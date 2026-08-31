using UnityEngine;

namespace Ramblers;

internal sealed class CompanionInteractBehavior : ICompanionJob
{
    private const float MinimumLookSeconds = 0.25f;
    private const float MaximumLookSeconds = 2f;
    private const float AimSettleSeconds = 0.10f;
    private const float AimToleranceDegrees = 5f;
    private const float ConfirmationSeconds = 1f;
    private const float InteractionTimeoutSeconds = 25f;

    private enum InteractionState
    {
        Idle,
        Approaching,
        Aligning,
        AwaitingConfirmation
    }

    private readonly CompanionAttention _attention;
    private readonly CompanionLocomotion _locomotion;
    private readonly CompanionApproachController _approach;

    private CompanionBody _body;
    private InteractionState _state;
    private CompanionAffordanceTarget _target;
    private CompanionAffordanceActivation _activation;
    private CompanionJobCompletion _completion;
    private Vector3 _targetPoint;
    private float _stateStartedAt;
    private float _alignedAt;
    private bool _authorityCrossed;
    private bool _cancelRequested;
    private string _postAuthorityError;
    private bool _requiresLocomotion;
    private string _callId;
    private long _turnId;
    private CompanionInteractionIntent _intent;

    internal CompanionInteractBehavior(
        CompanionLocomotion locomotion,
        CompanionAttention attention,
        CompanionJumpActuator jump)
    {
        _locomotion = locomotion;
        _attention = attention;
        _approach = new CompanionApproachController(
            locomotion,
            jump,
            AgentToolCatalog.InteractWithObject);
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
        var readiness = request?.AffordanceTarget?.GetReadiness(
            _body,
            request.InteractionIntent);
        return readiness?.State ==
               CompanionAffordanceReadinessState.NeedsApproach
            ? JobResources.Locomotion | JobResources.Gaze | JobResources.Hands
            : JobResources.Gaze | JobResources.Hands;
    }

    public JobResources Held => !IsActive
        ? JobResources.None
        : _requiresLocomotion
            ? JobResources.Locomotion | JobResources.Gaze | JobResources.Hands
            : JobResources.Gaze | JobResources.Hands;

    public bool IsActive => _state != InteractionState.Idle;

    public bool MayPublishCompletionWhileActive => false;

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

        _target = request == null ? null : request.AffordanceTarget;
        _callId = request == null ? null : request.CallId;
        _turnId = request == null ? 0L : request.TurnId;
        _intent = request == null
            ? CompanionInteractionIntent.Use
            : request.InteractionIntent;
        if (_target == null)
        {
            _target = null;
            failure = AgentToolResult.Failure(
                "interaction_reference_unavailable");
            return false;
        }

        var readiness = _target.GetReadiness(_body, _intent);
        if (readiness.State == CompanionAffordanceReadinessState.Unavailable)
        {
            _target = null;
            failure = AgentToolResult.Failure(
                readiness.Error ?? "interaction_unavailable");
            return false;
        }

        _targetPoint = readiness.Point;
        _requiresLocomotion = readiness.State ==
                              CompanionAffordanceReadinessState.NeedsApproach;
        _state = _requiresLocomotion
            ? InteractionState.Approaching
            : InteractionState.Aligning;
        _stateStartedAt = now;
        _alignedAt = -1f;
        _authorityCrossed = false;
        _cancelRequested = false;
        _postAuthorityError = null;
        if (_target.IsWorldTarget)
            _approach.Begin(now);
        else
            _approach.Reset();
        _attention.SetTarget(GazeChannel.Inspection, _targetPoint);
        Plugin.Logger.LogInfo(
            $"[INTERACT] STARTED kind={_target.KindLabel}, source={_target.SourceLabel}, " +
            $"referenceId={_target.ReferenceId}, " +
            $"netId={_target.NetworkId}, target={_targetPoint}, " +
            $"distance={Vector3.Distance(_body.Position, _targetPoint):F2}, " +
            $"phase={(_state == InteractionState.Approaching ? "approach" : "align")}, " +
            $"callId={CallIdForLog}, turnId={_turnId}.");
        return true;
    }

    public void Tick(float now)
    {
        if (_state == InteractionState.Idle)
            return;
        if (_body == null || !_body.IsAlive)
        {
            RefreshAuthorityCrossing();
            if (!_authorityCrossed)
            {
                CompleteFailure("bot_not_spawned");
                return;
            }
            RecordPostAuthorityFailure("bot_not_spawned", "body");
            if (now - _stateStartedAt >= ConfirmationSeconds)
                CompleteFailure(_postAuthorityError);
            return;
        }

        if (_state == InteractionState.Approaching ||
            _state == InteractionState.Aligning)
        {
            string pointError;
            if (!_target.TryGetCurrentPoint(out _targetPoint, out pointError))
            {
                CompleteFailure(pointError ?? "interaction_target_unavailable");
                return;
            }
            _attention.SetTarget(GazeChannel.Inspection, _targetPoint);
            if (_state == InteractionState.Approaching)
                TickApproach(now);
            else
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
        RefreshAuthorityCrossing();
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
        if (!IsActive)
            return;
        RefreshAuthorityCrossing();
        if (!_authorityCrossed)
        {
            CompleteFailure(error ?? "action_execution_failed");
            return;
        }
        RecordPostAuthorityFailure(
            error ?? "action_execution_failed",
            "job_failure");
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
        if (!_approach.TryBeginTick(now))
            return;

        var readiness = _target.GetReadiness(_body, _intent);
        if (readiness.State == CompanionAffordanceReadinessState.Ready)
        {
            _targetPoint = readiness.Point;
            _locomotion.Stop(now);
            _approach.CancelRecovery();
            _state = InteractionState.Aligning;
            _stateStartedAt = now;
            _alignedAt = -1f;
            Plugin.Logger.LogInfo(
                $"[INTERACT] APPROACH_REACHED referenceId={_target.ReferenceId}, " +
                $"distance={Vector3.Distance(_body.Position, _targetPoint):F2}.");
            return;
        }
        if (readiness.State == CompanionAffordanceReadinessState.Unavailable)
        {
            CompleteFailure(readiness.Error ?? "interaction_unavailable");
            return;
        }
        _targetPoint = readiness.Point;

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
        var approachStep = _approach.Advance(
            now,
            direction,
            horizontalDistance);
        if (approachStep.Kind == CompanionApproachStepKind.RecoveryDeferred)
        {
            Plugin.Logger.LogInfo(
                $"[INTERACT] APPROACH_DEFERRED referenceId={_target.ReferenceId}, " +
                $"reason={approachStep.Reason}, " +
                $"recovery={approachStep.RecoveryError}.");
        }
        else if (approachStep.Kind == CompanionApproachStepKind.RecoveryCommitted)
        {
            Plugin.Logger.LogInfo(
                $"[INTERACT] APPROACH_RECOVERY referenceId={_target.ReferenceId}, " +
                $"reason={approachStep.Reason}, " +
                $"attempt={approachStep.RecoveryAttempt}.");
        }
        else if (approachStep.Kind == CompanionApproachStepKind.Blocked)
        {
            Plugin.Logger.LogWarning(
                $"[INTERACT] APPROACH_BLOCKED referenceId={_target.ReferenceId}, " +
                $"reason={approachStep.Reason}, " +
                $"recovery={approachStep.RecoveryError ?? "unavailable"}.");
            CompleteFailure("interaction_path_blocked");
        }
    }

    private void Activate(float now, float lookSeconds)
    {
        var readiness = _target.GetReadiness(_body, _intent);
        if (readiness.State ==
                CompanionAffordanceReadinessState.NeedsApproach &&
            _requiresLocomotion && _target.IsWorldTarget)
        {
            _targetPoint = readiness.Point;
            _state = InteractionState.Approaching;
            _stateStartedAt = now;
            _alignedAt = -1f;
            _approach.Resume(now);
            Plugin.Logger.LogInfo(
                $"[INTERACT] APPROACH_RESUMED " +
                $"referenceId={_target.ReferenceId}, " +
                "reason=target_moved_out_of_reach.");
            return;
        }
        if (readiness.State != CompanionAffordanceReadinessState.Ready)
        {
            CompleteFailure(readiness.Error ?? "interaction_unavailable");
            return;
        }
        _targetPoint = readiness.Point;
        _activation = readiness.Activation;

        Plugin.Logger.LogInfo(
            $"[INTERACT] AUTHORITY_REQUEST kind={_target.KindLabel}, " +
            $"referenceId={_target.ReferenceId}, " +
            $"{_target.DescribeActivation(_activation)}, " +
            $"lookSeconds={lookSeconds:F2}.");
        string error;
        var activated =
            _target.TryActivate(_body, _activation, now, out error);
        RefreshAuthorityCrossing();
        if (!activated && !_authorityCrossed)
        {
            CompleteFailure(error ?? "interaction_authority_failed");
            return;
        }

        _state = InteractionState.AwaitingConfirmation;
        _stateStartedAt = now;
        if (!activated)
        {
            RecordPostAuthorityFailure(
                error ?? "interaction_authority_failed",
                "activation");
        }
        TickConfirmation(now);
    }

    private void TickConfirmation(float now)
    {
        bool observed;
        string observation;
        string error;
        var progressed = _target.TryProgressActivation(
                _body,
                _activation,
                now,
                out observed,
                out observation,
                out error);
        RefreshAuthorityCrossing();
        if (!progressed)
        {
            if (!_authorityCrossed)
            {
                CompleteFailure(
                    error ?? "interaction_confirmation_unavailable");
                return;
            }
            RecordPostAuthorityFailure(
                error ?? "interaction_confirmation_unavailable",
                "confirmation");
            observed = false;
        }

        if (observed)
        {
            var referenceId = _target.ReferenceId;
            var kind = _target.KindLabel;
            var successState = _target.SuccessState(_activation);
            if (_cancelRequested)
            {
                Plugin.Logger.LogInfo(
                    $"[INTERACT] CANCEL_RECONCILED kind={kind}, " +
                    $"referenceId={referenceId}, observation={observation}, " +
                    $"callId={CallIdForLog}, turnId={_turnId}.");
                EndInteraction();
                return;
            }
            _completion = new CompanionJobCompletion
            {
                Result = AgentToolResult.Success(
                    AgentToolCatalog.InteractWithObject,
                    "interacted",
                    successState),
                HandsTransition = _target.Kind ==
                    CompanionAffordanceKind.PropHome
                        ? CompanionTurnHandsTransition.HandsEmpty
                        : CompanionTurnHandsTransition.None
            };
            Plugin.Logger.LogInfo(
                $"[INTERACT] CONFIRMED kind={kind}, referenceId={referenceId}, " +
                $"observation={observation}, " +
                $"cancelRequested={_cancelRequested}, callId={CallIdForLog}, " +
                $"turnId={_turnId}.");
            EndInteraction();
            return;
        }

        if (now - _stateStartedAt >= ConfirmationSeconds)
        {
            CompleteFailure(
                _postAuthorityError ?? "interaction_not_confirmed");
        }
    }

    private void RefreshAuthorityCrossing()
    {
        _authorityCrossed |= _activation?.AuthorityCrossed == true;
    }

    private void RecordPostAuthorityFailure(string error, string phase)
    {
        if (_postAuthorityError != null)
            return;
        _postAuthorityError = error;
        Plugin.Logger.LogWarning(
            $"[INTERACT] RECONCILIATION_PENDING referenceId={_target?.ReferenceId ?? "none"}, " +
            $"phase={phase}, error={error}, callId={CallIdForLog}, " +
            $"turnId={_turnId}.");
    }

    private void CompleteFailure(string error)
    {
        var referenceId = _target == null ? "none" : _target.ReferenceId;
        if (_cancelRequested)
        {
            Plugin.Logger.LogWarning(
                $"[INTERACT] CANCEL_RECONCILIATION_FAILED " +
                $"referenceId={referenceId}, error={error}, " +
                $"callId={CallIdForLog}, turnId={_turnId}.");
            EndInteraction();
            return;
        }
        _completion = CompanionJobCompletion.Failed(error);
        Plugin.Logger.LogWarning(
            $"[INTERACT] FAILED referenceId={referenceId}, error={error}, " +
            $"authorityCrossed={_authorityCrossed}, callId={CallIdForLog}, " +
            $"turnId={_turnId}.");
        EndInteraction();
    }

    private void EndInteraction()
    {
        _approach.CancelRecovery();
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
        _postAuthorityError = null;
        _requiresLocomotion = false;
        _approach.Reset();
        _callId = null;
        _turnId = 0L;
        _intent = CompanionInteractionIntent.Use;
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
        _postAuthorityError = null;
        _requiresLocomotion = false;
        _approach.Reset();
        _callId = null;
        _turnId = 0L;
    }

    private string CallIdForLog =>
        string.IsNullOrEmpty(_callId) ? "none" : _callId;
}
