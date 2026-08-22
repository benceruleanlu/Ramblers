using UnityEngine;

namespace Ramblers;

/// <summary>
/// One bounded primary interaction with an exact switch frozen from gaze,
/// held state, or private game context. World targets are approached before
/// the same game-owned reach and authority checks are crossed.
/// </summary>
internal sealed class CompanionInteractBehavior : ICompanionJob
{
    private const float MinimumLookSeconds = 0.25f;
    private const float MaximumLookSeconds = 2f;
    private const float AimSettleSeconds = 0.10f;
    private const float AimToleranceDegrees = 5f;
    private const float ConfirmationSeconds = 1f;
    private const float InteractionTimeoutSeconds = 25f;
    private const float ApproachCommitSeconds = 0.45f;
    private const float ApproachNavigationInterval = 0.1f;

    private enum InteractionState
    {
        Idle,
        Approaching,
        Aligning,
        AwaitingConfirmation
    }

    private readonly CompanionAttention _attention;
    private readonly CompanionLocomotion _locomotion;
    private readonly CompanionJumpActuator _jump;

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
    private int _approachRecoveries;
    private float _approachCommitUntil;
    private Vector3 _approachCommitDirection;
    private float _nextApproachTick;

    internal CompanionInteractBehavior(
        CompanionLocomotion locomotion,
        CompanionAttention attention,
        CompanionJumpActuator jump)
    {
        _locomotion = locomotion;
        _attention = attention;
        _jump = jump;
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
        return request?.PeckTarget?.IsWorldTarget == true
            ? JobResources.Locomotion | JobResources.Gaze | JobResources.Hands
            : JobResources.Gaze | JobResources.Hands;
    }

    public JobResources Held => !IsActive
        ? JobResources.None
        : _target?.IsWorldTarget == true
            ? JobResources.Locomotion | JobResources.Gaze | JobResources.Hands
            : JobResources.Gaze | JobResources.Hands;

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

        var canActivateNow = false;
        if (_target.IsWorldTarget)
        {
            CompanionPeckActivation ignoredActivation;
            string prepareError;
            canActivateNow = _target.TryPrepare(
                _body,
                out ignoredActivation,
                out prepareError);
            if (!canActivateNow && !string.Equals(
                    prepareError,
                    "interaction_out_of_reach",
                    System.StringComparison.Ordinal))
            {
                _target = null;
                failure = AgentToolResult.Failure(
                    prepareError ?? "interaction_unavailable");
                return false;
            }
        }
        else
        {
            canActivateNow = true;
        }

        _state = canActivateNow
            ? InteractionState.Aligning
            : InteractionState.Approaching;
        _stateStartedAt = now;
        _alignedAt = -1f;
        _authorityCrossed = false;
        _cancelRequested = false;
        _approachRecoveries = 0;
        _approachCommitUntil = 0f;
        _approachCommitDirection = Vector3.zero;
        if (_target.IsWorldTarget)
            _locomotion.ResetProgressObservation(now);
        _attention.SetTarget(GazeChannel.Inspection, _targetPoint);
        Plugin.Logger.LogInfo(
            $"[INTERACT] STARTED source={_target.SourceLabel}, " +
            $"referenceId={_target.ReferenceId}, " +
            $"netId={_target.NetworkId}, target={_targetPoint}, " +
            $"distance={Vector3.Distance(_body.Position, _targetPoint):F2}, " +
            $"phase={(_state == InteractionState.Approaching ? "approach" : "align")}.");
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

        if (_state == InteractionState.Approaching)
        {
            TickApproach(now);
            return;
        }
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
        {
            Plugin.Logger.LogWarning(
                $"[INTERACT] ALIGNMENT_TIMEOUT referenceId={_target.ReferenceId}, " +
                $"lookSeconds={elapsed:F2}; continuing with exact target.");
            Activate(now, elapsed);
        }
    }

    private void TickApproach(float now)
    {
        if (now < _nextApproachTick)
            return;
        _nextApproachTick = now + ApproachNavigationInterval;

        CompanionPeckActivation ignoredActivation;
        string prepareError;
        if (_target.TryPrepare(_body, out ignoredActivation, out prepareError))
        {
            _locomotion.Stop(now);
            _state = InteractionState.Aligning;
            _stateStartedAt = now;
            _alignedAt = -1f;
            Plugin.Logger.LogInfo(
                $"[INTERACT] APPROACH_REACHED referenceId={_target.ReferenceId}, " +
                $"distance={Vector3.Distance(_body.Position, _targetPoint):F2}.");
            return;
        }
        if (!string.Equals(
                prepareError,
                "interaction_out_of_reach",
                System.StringComparison.Ordinal))
        {
            CompleteFailure(prepareError ?? "interaction_unavailable");
            return;
        }

        var toTarget = _targetPoint - _body.Position;
        var horizontalDistance = new Vector3(toTarget.x, 0f, toTarget.z).magnitude;
        if (horizontalDistance < 0.05f)
        {
            _locomotion.Stop(now);
            CompleteFailure("interaction_path_blocked");
            return;
        }

        var direction = new Vector3(toTarget.x, 0f, toTarget.z) /
                        horizontalDistance;
        SteeringStatus status;
        if (now < _approachCommitUntil)
        {
            _locomotion.CommitTraversalDirection(
                _approachCommitDirection,
                horizontalDistance);
        }
        else if (!_locomotion.TrySteerToward(
                     direction,
                     horizontalDistance,
                     now,
                     out status))
        {
            _locomotion.Stop(now);
            if (!TryRecoverApproach(
                    now,
                    direction,
                    horizontalDistance,
                    "blocked_path"))
                CompleteFailure("interaction_path_blocked");
            return;
        }

        if (_locomotion.ObserveProgress(now) &&
            !TryRecoverApproach(now, direction, horizontalDistance, "stuck"))
        {
            CompleteFailure("interaction_path_blocked");
        }
    }

    private bool TryRecoverApproach(
        float now,
        Vector3 direction,
        float distance,
        string reason)
    {
        string jumpError;
        if (!_jump.TryRequestActionRecovery(
                now,
                _locomotion.Posture,
                AgentToolCatalog.InteractWithObject,
                reason,
                out jumpError))
        {
            if (CompanionJumpActuator.IsDeferredRecoveryError(jumpError))
            {
                _locomotion.ResetProgressObservation(now);
                Plugin.Logger.LogInfo(
                    $"[INTERACT] APPROACH_DEFERRED referenceId={_target.ReferenceId}, " +
                    $"reason={reason}, recovery={jumpError}.");
                return true;
            }

            Plugin.Logger.LogWarning(
                $"[INTERACT] APPROACH_BLOCKED referenceId={_target.ReferenceId}, " +
                $"reason={reason}, recovery={jumpError ?? "unavailable"}.");
            return false;
        }

        _approachRecoveries++;
        _approachCommitUntil = now + ApproachCommitSeconds;
        _approachCommitDirection = direction;
        _locomotion.CommitTraversalDirection(direction, distance);
        _locomotion.ResetProgressObservation(now);
        Plugin.Logger.LogInfo(
            $"[INTERACT] APPROACH_RECOVERY referenceId={_target.ReferenceId}, " +
            $"reason={reason}, attempt={_approachRecoveries}.");
        return true;
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
        _jump.CancelActionRecovery(AgentToolCatalog.InteractWithObject);
        if (_target?.IsWorldTarget == true)
            _locomotion.Stop(Time.realtimeSinceStartup);
        _state = InteractionState.Idle;
        _target = null;
        _activation = null;
        _targetPoint = Vector3.zero;
        _stateStartedAt = 0f;
        _alignedAt = -1f;
        _authorityCrossed = false;
        _cancelRequested = false;
        _approachRecoveries = 0;
        _approachCommitUntil = 0f;
        _approachCommitDirection = Vector3.zero;
        _nextApproachTick = 0f;
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
        _approachRecoveries = 0;
        _approachCommitUntil = 0f;
        _approachCommitDirection = Vector3.zero;
        _nextApproachTick = 0f;
    }
}
