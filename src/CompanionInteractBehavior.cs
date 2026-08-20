using UnityEngine;

namespace Ramblers;

/// <summary>
/// One bounded primary interaction with the exact switch frozen under the
/// human's gaze. This deliberately stops at already-reachable switches; travel
/// to a remote object belongs to a later navigation/action composition slice.
/// </summary>
internal sealed class CompanionInteractBehavior : ICompanionJob
{
    private const float MinimumLookSeconds = 0.25f;
    private const float MaximumLookSeconds = 2f;
    private const float AimSettleSeconds = 0.10f;
    private const float AimToleranceDegrees = 5f;
    private const float ConfirmationSeconds = 1f;
    private const float InteractionTimeoutSeconds = 4f;

    private enum InteractionState
    {
        Idle,
        Aligning,
        AwaitingConfirmation
    }

    private readonly CompanionAttention _attention;

    private CompanionBody _body;
    private InteractionState _state;
    private CompanionPeckTarget _target;
    private CompanionPeckActivation _activation;
    private CompanionJobCompletion _completion;
    private Vector3 _targetPoint;
    private float _stateStartedAt;
    private float _alignedAt;
    private bool _authorityCrossed;
    private bool _cancelRequested;

    internal CompanionInteractBehavior(CompanionAttention attention)
    {
        _attention = attention;
    }

    public string Name => AgentToolCatalog.InteractWithObject;

    public string ActiveName => Name;

    public bool Handles(string actionName)
    {
        return string.Equals(
            actionName,
            Name,
            System.StringComparison.Ordinal);
    }

    public JobResources RequiredFor(CompanionJobRequest request)
    {
        return JobResources.Locomotion |
               JobResources.Gaze |
               JobResources.Hands;
    }

    public JobResources Held => IsActive
        ? JobResources.Locomotion | JobResources.Gaze | JobResources.Hands
        : JobResources.None;

    public bool IsActive => _state != InteractionState.Idle;

    public float TimeoutSeconds => InteractionTimeoutSeconds;

    public void Bind(CompanionBody body, PlayerCharacter human)
    {
        _body = body;
        Reset();
    }

    public bool TryBegin(
        float now,
        CompanionJobRequest request,
        out AgentToolResult failure)
    {
        failure = null;
        if (_body == null || !_body.IsAlive)
        {
            failure = AgentToolResult.Failure("bot_not_spawned");
            return false;
        }
        if (IsActive || _completion != null)
        {
            failure = AgentToolResult.Failure("interaction_in_progress");
            return false;
        }

        _target = request == null ? null : request.PeckTarget;
        string error = null;
        if (_target == null ||
            !_target.TryGetCurrentPoint(out _targetPoint, out error))
        {
            _target = null;
            failure = AgentToolResult.Failure(
                error ?? "interaction_reference_unavailable");
            return false;
        }

        _state = InteractionState.Aligning;
        _stateStartedAt = now;
        _alignedAt = -1f;
        _authorityCrossed = false;
        _cancelRequested = false;
        _attention.SetTarget(GazeChannel.Inspection, _targetPoint);
        Plugin.Logger.LogInfo(
            $"[INTERACT] STARTED source={_target.SourceLabel}, " +
            $"referenceId={_target.ReferenceId}, " +
            $"netId={_target.NetworkId}, target={_targetPoint}.");
        return true;
    }

    public void Tick(float now)
    {
        if (_state == InteractionState.Idle)
            return;
        if (_body == null || !_body.IsAlive)
        {
            CompleteFailure("bot_not_spawned");
            return;
        }

        string pointError;
        if (!_target.TryGetCurrentPoint(out _targetPoint, out pointError))
        {
            CompleteFailure(pointError ?? "interaction_target_unavailable");
            return;
        }
        _attention.SetTarget(GazeChannel.Inspection, _targetPoint);

        if (_state == InteractionState.Aligning)
        {
            TickAlignment(now);
            return;
        }

        TickConfirmation(now);
    }

    public bool TryTakeCompletion(out CompanionJobCompletion completion)
    {
        completion = _completion;
        if (completion == null)
            return false;
        _completion = null;
        return true;
    }

    public void Conclude(float now)
    {
        if (IsActive)
            EndInteraction();
    }

    public void Cancel(float now)
    {
        _completion = null;
        if (!IsActive)
            return;
        if (_authorityCrossed)
        {
            _cancelRequested = true;
            Plugin.Logger.LogInfo(
                $"[INTERACT] CANCEL_PENDING referenceId={_target.ReferenceId}, " +
                "reason=authority_already_crossed.");
            return;
        }
        EndInteraction();
    }

    public void Fail(string error, float now)
    {
        if (_completion != null)
        {
            EndInteraction();
            return;
        }
        if (IsActive)
            CompleteFailure(error ?? "action_execution_failed");
    }

    public void Release()
    {
        _body = null;
        Reset();
        _attention.ClearTarget(GazeChannel.Inspection);
    }

    private void TickAlignment(float now)
    {
        var elapsed = now - _stateStartedAt;
        if (elapsed < MinimumLookSeconds)
            return;

        if (_attention.IsAimWithin(
                GazeChannel.Inspection,
                AimToleranceDegrees,
                AimToleranceDegrees))
        {
            if (_alignedAt < 0f)
                _alignedAt = now;
            if (now - _alignedAt >= AimSettleSeconds)
            {
                Activate(now, elapsed);
                return;
            }
        }
        else
        {
            _alignedAt = -1f;
        }

        if (elapsed >= MaximumLookSeconds)
            CompleteFailure("interaction_alignment_failed");
    }

    private void Activate(float now, float lookSeconds)
    {
        string error;
        if (!_target.TryPrepare(_body, out _activation, out error))
        {
            CompleteFailure(error ?? "interaction_unavailable");
            return;
        }

        Plugin.Logger.LogInfo(
            $"[INTERACT] AUTHORITY_REQUEST referenceId={_target.ReferenceId}, " +
            $"previousState={_activation.PreviousState}, " +
            $"expectedState={_activation.ExpectedState}, " +
            $"lookSeconds={lookSeconds:F2}.");
        if (!_target.TryActivate(_activation, out error))
        {
            CompleteFailure(error ?? "interaction_authority_failed");
            return;
        }

        _authorityCrossed = true;
        _state = InteractionState.AwaitingConfirmation;
        _stateStartedAt = now;
        TickConfirmation(now);
    }

    private void TickConfirmation(float now)
    {
        bool observed;
        int currentState;
        int currentActionNumber;
        string error;
        if (!_target.TryObserveActivation(
                _activation,
                out observed,
                out currentState,
                out currentActionNumber,
                out error))
        {
            CompleteFailure(error ?? "interaction_confirmation_unavailable");
            return;
        }

        if (observed)
        {
            var referenceId = _target.ReferenceId;
            var expectedState = _activation.ExpectedState;
            _completion = new CompanionJobCompletion
            {
                Result = AgentToolResult.Success(
                    AgentToolCatalog.InteractWithObject,
                    _cancelRequested
                        ? "interaction_completed_before_cancel"
                        : "interacted",
                    "switch_state_changed")
            };
            Plugin.Logger.LogInfo(
                $"[INTERACT] CONFIRMED referenceId={referenceId}, " +
                $"expectedState={expectedState}, currentState={currentState}, " +
                $"actionNumber={currentActionNumber}, " +
                $"cancelRequested={_cancelRequested}.");
            EndInteraction();
            return;
        }

        if (now - _stateStartedAt >= ConfirmationSeconds)
        {
            CompleteFailure("interaction_not_confirmed");
        }
    }

    private void CompleteFailure(string error)
    {
        var referenceId = _target == null ? "none" : _target.ReferenceId;
        _completion = CompanionJobCompletion.Failed(error);
        Plugin.Logger.LogWarning(
            $"[INTERACT] FAILED referenceId={referenceId}, error={error}, " +
            $"authorityCrossed={_authorityCrossed}.");
        EndInteraction();
    }

    private void EndInteraction()
    {
        _state = InteractionState.Idle;
        _target = null;
        _activation = null;
        _targetPoint = Vector3.zero;
        _stateStartedAt = 0f;
        _alignedAt = -1f;
        _authorityCrossed = false;
        _cancelRequested = false;
        _attention.ClearTarget(GazeChannel.Inspection);
    }

    private void Reset()
    {
        _state = InteractionState.Idle;
        _target = null;
        _activation = null;
        _completion = null;
        _targetPoint = Vector3.zero;
        _stateStartedAt = 0f;
        _alignedAt = -1f;
        _authorityCrossed = false;
        _cancelRequested = false;
    }
}
