using Mirror;
using UnityEngine;

namespace Ramblers;

/// <summary>
/// Picks up and drops the exact local human frozen at the utterance boundary.
/// Dispatch uses Big Walk's connectionless server-body command path and
/// confirms the stock carry-pose state before reporting success.
/// </summary>
internal sealed class CompanionPlayerCarryBehavior :
    ICompanionJob,
    ICompanionStandingJob
{
    private const float MinimumTargetLookSeconds = 0.20f;
    private const float MaximumTargetLookSeconds = 1.50f;
    private const float TargetAimToleranceDegrees = 10f;
    private const float PickupReachTolerance = 0.15f;
    private const float PickupConfirmationSeconds = 1.25f;
    private const float ReleaseSettlementSeconds = 0.25f;
    private const float DropRetrySeconds = 0.50f;
    private const float ReconciliationSettlementSeconds = 1.00f;
    private const float ReconciliationMaximumSeconds = 3.00f;
    private const float PickupTimeoutSecondsValue = 25f;
    private const float DropTimeoutSecondsValue = 5f;

    private enum CarryState
    {
        Idle,
        ApproachingTarget,
        AligningTarget,
        AwaitingPickupConfirmation,
        HoldingPlayer,
        DroppingPlayer,
        Reconciling
    }

    private readonly CompanionLocomotion _locomotion;
    private readonly CompanionAttention _attention;
    private readonly CompanionApproachController _approach;

    private CompanionBody _body;
    private CompanionPlayerTarget _target;
    private CarryState _state;
    private float _stateStartedAt;
    private float _reconciliationStartedAt;
    private float _dropIssuedAt = -1f;
    private float _releasedSince = -1f;
    private bool _holdGaze;
    private string _activeActionName;
    private CompanionJobCompletion _completion;

    internal CompanionPlayerCarryBehavior(
        CompanionLocomotion locomotion,
        CompanionAttention attention,
        CompanionJumpActuator jump)
    {
        _locomotion = locomotion;
        _attention = attention;
        _approach = new CompanionApproachController(
            locomotion,
            jump,
            AgentToolCatalog.PickUpPlayer);
    }

    public string Name => AgentToolCatalog.PickUpPlayer;

    public string ActiveName => _activeActionName ?? Name;

    public bool Handles(string actionName)
    {
        return string.Equals(
                   actionName,
                   AgentToolCatalog.PickUpPlayer,
                   System.StringComparison.Ordinal) ||
               string.Equals(
                   actionName,
                   AgentToolCatalog.DropPlayer,
                   System.StringComparison.Ordinal);
    }

    public JobResources RequiredFor(CompanionJobRequest request)
    {
        return request != null && string.Equals(
            request.ActionName,
            AgentToolCatalog.DropPlayer,
            System.StringComparison.Ordinal)
            ? JobResources.Hands
            : JobResources.Locomotion | JobResources.Gaze | JobResources.Hands;
    }

    public JobResources Held
    {
        get
        {
            switch (_state)
            {
                case CarryState.Idle:
                    return JobResources.None;
                case CarryState.HoldingPlayer:
                    return _holdGaze
                        ? JobResources.Gaze | JobResources.Hands
                        : JobResources.Hands;
                case CarryState.DroppingPlayer:
                case CarryState.Reconciling:
                    return JobResources.Hands;
                default:
                    return JobResources.Locomotion |
                           JobResources.Gaze |
                           JobResources.Hands;
            }
        }
    }

    public bool IsActive => _state != CarryState.Idle;

    public bool MayPublishCompletionWhileActive =>
        _state == CarryState.HoldingPlayer &&
        _completion?.Result?.Ok == true;

    public float TimeoutSeconds => IsExplicitDrop
        ? DropTimeoutSecondsValue
        : PickupTimeoutSecondsValue;

    public void Bind(CompanionBody body, PlayerCharacter human)
    {
        _body = body;
        ResetState();
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

        if (request != null && string.Equals(
                request.ActionName,
                AgentToolCatalog.DropPlayer,
                System.StringComparison.Ordinal))
        {
            return TryBeginDrop(now, request.PlayerTarget, out failure);
        }

        if (request != null &&
            !string.IsNullOrEmpty(request.ActionName) &&
            !string.Equals(
                request.ActionName,
                AgentToolCatalog.PickUpPlayer,
                System.StringComparison.Ordinal))
        {
            failure = AgentToolResult.Failure("unknown_tool");
            return false;
        }

        if (_state != CarryState.Idle || _completion != null)
        {
            failure = AgentToolResult.Failure("pick_up_player_in_progress");
            return false;
        }

        _target = request == null ? null : request.PlayerTarget;
        Vector3 targetPosition;
        Vector3 lookPoint;
        string validationError;
        if (!TryValidateTarget(
                out targetPosition,
                out lookPoint,
                out validationError))
        {
            _target = null;
            failure = AgentToolResult.Failure(validationError);
            return false;
        }

        var hands = GetHands();
        if (hands == null)
        {
            _target = null;
            failure = AgentToolResult.Failure("hands_unavailable");
            return false;
        }
        if (hands.heldProp != null || hands.heldCharacter != null)
        {
            _target = null;
            failure = AgentToolResult.Failure("hands_occupied");
            return false;
        }

        bool withinPickupReach;
        if (!TryIsWithinPickupReach(
                targetPosition,
                out withinPickupReach,
                out validationError))
        {
            _target = null;
            failure = AgentToolResult.Failure(validationError);
            return false;
        }
        if (withinPickupReach &&
            !_target.IsPickupAdmittedByGame(_body, out validationError))
        {
            _target = null;
            failure = AgentToolResult.Failure(validationError);
            return false;
        }

        _state = withinPickupReach
            ? CarryState.AligningTarget
            : CarryState.ApproachingTarget;
        _stateStartedAt = now;
        _activeActionName = AgentToolCatalog.PickUpPlayer;
        _holdGaze = true;
        ResetDropTracking();
        _approach.Begin(now);
        _attention.SetTarget(GazeChannel.Manipulation, lookPoint);
        Plugin.Logger.LogInfo(
            $"[ACTION] PLAYER_PICKUP_STARTED referenceId={ReferenceIdForLog}, " +
            $"netId={_target.NetworkId}, stableId={_target.StableId}, " +
            $"distance={Vector3.Distance(_body.Position, targetPosition):F2}, " +
            $"phase={(_state == CarryState.ApproachingTarget ? "approach" : "align")}, " +
            $"turnId={(request == null ? 0 : request.TurnId)}.");
        return true;
    }

    private bool TryBeginDrop(
        float now,
        CompanionPlayerTarget requestedTarget,
        out AgentToolResult failure)
    {
        failure = null;
        if (_completion != null)
        {
            failure = AgentToolResult.Failure(ActiveName + "_in_progress");
            return false;
        }
        if (_state != CarryState.Idle &&
            _state != CarryState.HoldingPlayer)
        {
            failure = AgentToolResult.Failure(ActiveName + "_in_progress");
            return false;
        }
        if (requestedTarget == null)
        {
            failure = AgentToolResult.Failure(
                "human_player_reference_not_captured");
            return false;
        }

        var hands = GetHands();
        if (hands == null)
        {
            failure = AgentToolResult.Failure("hands_unavailable");
            return false;
        }
        if (hands.heldCharacter == null)
        {
            if (_state == CarryState.HoldingPlayer)
                EndAction();
            failure = AgentToolResult.Failure(
                hands.heldProp == null ? "hands_empty" : "held_item_not_player");
            return false;
        }
        if (!requestedTarget.IsStillTheSamePlayer(hands.heldCharacter))
        {
            failure = AgentToolResult.Failure(
                "held_player_identity_mismatch");
            return false;
        }
        if (_state == CarryState.HoldingPlayer &&
            (_target == null ||
             !_target.IsStillTheSamePlayer(hands.heldCharacter)))
        {
            EnterIdentityFault("held_player_identity_mismatch", false);
            failure = AgentToolResult.Failure(
                "held_player_identity_mismatch");
            return false;
        }
        if (!HasAuthority)
        {
            failure = AgentToolResult.Failure("bot_authority_unavailable");
            return false;
        }

        _target = requestedTarget;
        _state = CarryState.DroppingPlayer;
        _stateStartedAt = now;
        _activeActionName = AgentToolCatalog.DropPlayer;
        _holdGaze = false;
        ResetDropTracking();
        _attention.ClearTarget(GazeChannel.Manipulation);
        Plugin.Logger.LogInfo(
            $"[ACTION] PLAYER_DROP_STARTED referenceId={ReferenceIdForLog}, " +
            $"netId={_target.NetworkId}.");
        TryIssueExactDrop(now, true);
        return true;
    }

    public void Tick(float now)
    {
        switch (_state)
        {
            case CarryState.Idle:
                return;
            case CarryState.ApproachingTarget:
                TickApproach(now);
                return;
            case CarryState.AligningTarget:
                TickAlignment(now);
                return;
            case CarryState.AwaitingPickupConfirmation:
                TickAwaitingPickupConfirmation(now);
                return;
            case CarryState.HoldingPlayer:
                TickHolding();
                return;
            case CarryState.DroppingPlayer:
                TickDrop(now);
                return;
            case CarryState.Reconciling:
                TickReconciliation(now);
                return;
        }
    }

    public bool TryTakeCompletion(out CompanionJobCompletion completion)
    {
        RevalidateQueuedHoldCompletion();
        completion = _completion;
        if (completion == null)
            return false;
        _completion = null;
        return true;
    }

    private void RevalidateQueuedHoldCompletion()
    {
        if (_completion?.Result?.Ok != true ||
            _completion.HandsTransition !=
                CompanionTurnHandsTransition.HoldingExactPlayer)
        {
            return;
        }

        if (IsFullyHeldByCompanion())
            return;

        Plugin.Logger.LogWarning(
            $"[ACTION] PLAYER_PICKUP_COMPLETION_INVALIDATED " +
            $"referenceId={ReferenceIdForLog}, " +
            "reason=exact_carry_links_not_held_at_consumption.");
        _completion = CompanionJobCompletion.Failed(
            "player_pickup_not_retained");
        EndAction();
    }

    public void Conclude(float now)
    {
        if (_state == CarryState.Idle)
            return;

        var referenceId = ReferenceIdForLog;
        var stillHeld = IsFullyHeldByCompanion();
        EndAction();
        Plugin.Logger.LogInfo(
            $"[ACTION] PLAYER_PICKUP_JOB_CONCLUDED referenceId={referenceId}, " +
            $"stillHeld={stillHeld}.");
    }

    public void Cancel(float now)
    {
        _completion = null;
        if (_state == CarryState.Idle)
            return;
        if (_state == CarryState.ApproachingTarget ||
            _state == CarryState.AligningTarget)
        {
            Plugin.Logger.LogInfo(
                $"[ACTION] PLAYER_PICKUP_CANCELLED phase=before_authority, " +
                $"referenceId={ReferenceIdForLog}.");
            EndAction();
            return;
        }

        BeginReconciliation(now, null);
        Plugin.Logger.LogInfo(IsExplicitDrop
            ? $"[ACTION] PLAYER_DROP_CANCEL_RECONCILE_STARTED " +
              $"referenceId={ReferenceIdForLog}."
            : $"[ACTION] PLAYER_PICKUP_CANCEL_RECONCILE_STARTED " +
              $"referenceId={ReferenceIdForLog}.");
    }

    public void Fail(string error, float now)
    {
        if (_state == CarryState.Idle)
            return;
        if (_state == CarryState.ApproachingTarget ||
            _state == CarryState.AligningTarget)
        {
            CompleteFailure(error ?? "action_execution_failed");
            return;
        }

        BeginReconciliation(
            now,
            error ?? "action_execution_failed");
    }

    public void Release()
    {
        _body = null;
        ResetState();
        _attention.ClearTarget(GazeChannel.Manipulation);
    }

    private void TickApproach(float now)
    {
        if (!_approach.TryBeginTick(now))
            return;

        Vector3 targetPosition;
        Vector3 lookPoint;
        string validationError;
        if (!TryValidateTarget(
                out targetPosition,
                out lookPoint,
                out validationError))
        {
            CompleteFailure(validationError);
            return;
        }
        var hands = GetHands();
        if (hands == null)
        {
            CompleteFailure("hands_unavailable");
            return;
        }
        if (hands.heldProp != null || hands.heldCharacter != null)
        {
            CompleteFailure("hands_occupied");
            return;
        }

        _attention.SetTarget(GazeChannel.Manipulation, lookPoint);
        var toTarget = targetPosition - _body.Position;
        var verticalDelta = toTarget.y;
        toTarget.y = 0f;
        var horizontalDistance = toTarget.magnitude;
        bool withinPickupReach;
        if (!TryIsWithinPickupReach(
                targetPosition,
                out withinPickupReach,
                out validationError))
        {
            _locomotion.Stop(now);
            CompleteFailure(validationError);
            return;
        }
        if (withinPickupReach)
        {
            _locomotion.Stop(now);
            _approach.CancelRecovery();
            _state = CarryState.AligningTarget;
            _stateStartedAt = now;
            Plugin.Logger.LogInfo(
                $"[ACTION] PLAYER_PICKUP_APPROACH_REACHED " +
                $"referenceId={ReferenceIdForLog}, " +
                $"horizontalDistance={horizontalDistance:F2}, " +
                $"verticalDelta={verticalDelta:F2}.");
            return;
        }
        if (horizontalDistance < 0.05f)
        {
            _locomotion.Stop(now);
            CompleteFailure("player_vertical_reach_unavailable");
            return;
        }

        var direction = toTarget / horizontalDistance;
        var approachStep = _approach.Advance(
            now,
            direction,
            horizontalDistance);
        if (approachStep.Kind == CompanionApproachStepKind.RecoveryDeferred)
        {
            Plugin.Logger.LogInfo(
                $"[ACTION] PLAYER_PICKUP_APPROACH_DEFERRED " +
                $"referenceId={ReferenceIdForLog}, " +
                $"reason={approachStep.Reason}, " +
                $"recovery={approachStep.RecoveryError}.");
        }
        else if (approachStep.Kind == CompanionApproachStepKind.RecoveryCommitted)
        {
            Plugin.Logger.LogInfo(
                $"[ACTION] PLAYER_PICKUP_APPROACH_RECOVERY " +
                $"referenceId={ReferenceIdForLog}, " +
                $"reason={approachStep.Reason}, " +
                $"attempt={approachStep.RecoveryAttempt}.");
        }
        else if (approachStep.Kind == CompanionApproachStepKind.Blocked)
        {
            Plugin.Logger.LogWarning(
                $"[ACTION] PLAYER_PICKUP_APPROACH_BLOCKED " +
                $"referenceId={ReferenceIdForLog}, " +
                $"reason={approachStep.Reason}, " +
                $"recovery={approachStep.RecoveryError ?? "unavailable"}.");
            CompleteFailure("player_path_blocked");
        }
    }

    private void TickAlignment(float now)
    {
        Vector3 targetPosition;
        Vector3 lookPoint;
        string validationError;
        if (!TryValidateTarget(
                out targetPosition,
                out lookPoint,
                out validationError))
        {
            CompleteFailure(validationError);
            return;
        }
        bool withinPickupReach;
        if (!TryIsWithinPickupReach(
                targetPosition,
                out withinPickupReach,
                out validationError))
        {
            CompleteFailure(validationError);
            return;
        }
        if (!withinPickupReach)
        {
            _state = CarryState.ApproachingTarget;
            _stateStartedAt = now;
            _approach.ResetProgressObservation(now);
            return;
        }

        var hands = GetHands();
        if (hands == null)
        {
            CompleteFailure("hands_unavailable");
            return;
        }
        if (hands.heldProp != null || hands.heldCharacter != null)
        {
            CompleteFailure("hands_occupied");
            return;
        }

        _attention.SetTarget(GazeChannel.Manipulation, lookPoint);
        var lookSeconds = now - _stateStartedAt;
        if (lookSeconds < MinimumTargetLookSeconds)
            return;
        if (_attention.IsAimWithin(
                GazeChannel.Manipulation,
                TargetAimToleranceDegrees,
                TargetAimToleranceDegrees))
        {
            ExecutePickup(now);
            return;
        }
        if (lookSeconds >= MaximumTargetLookSeconds)
        {
            Plugin.Logger.LogWarning(
                $"[ACTION] PLAYER_PICKUP_ALIGNMENT_TIMEOUT " +
                $"referenceId={ReferenceIdForLog}, " +
                $"lookSeconds={lookSeconds:F2}; continuing with exact target.");
            ExecutePickup(now);
        }
    }

    private void ExecutePickup(float now)
    {
        Vector3 targetPosition;
        Vector3 lookPoint;
        string validationError;
        if (!TryValidateTarget(
                out targetPosition,
                out lookPoint,
                out validationError))
        {
            CompleteFailure(validationError);
            return;
        }
        bool withinPickupReach;
        if (!TryIsWithinPickupReach(
                targetPosition,
                out withinPickupReach,
                out validationError))
        {
            CompleteFailure(validationError);
            return;
        }
        if (!withinPickupReach)
        {
            _state = CarryState.ApproachingTarget;
            _stateStartedAt = now;
            _approach.ResetProgressObservation(now);
            return;
        }

        var hands = GetHands();
        if (hands == null)
        {
            CompleteFailure("hands_unavailable");
            return;
        }
        if (hands.heldProp != null || hands.heldCharacter != null)
        {
            CompleteFailure("hands_occupied");
            return;
        }
        if (!_target.IsPickupAdmittedByGame(_body, out validationError))
        {
            CompleteFailure(validationError);
            return;
        }
        if (!HasAuthority)
        {
            CompleteFailure("bot_authority_unavailable");
            return;
        }

        try
        {
            // This is the server-side body of Big Walk's stock command. The
            // companion has no client connection, so the generated Cmd wrapper
            // cannot be its authority boundary.
            _body.Networking.UserCode_CmdPickUpPlayer__PlayerCharacter(
                _target.Player);
        }
        catch (System.Exception exception)
        {
            Plugin.Logger.LogError(
                $"[ACTION] PLAYER_PICKUP_FAILED exception={exception}");
            BeginReconciliation(now, "player_pickup_execution_failed");
            return;
        }

        _state = CarryState.AwaitingPickupConfirmation;
        _stateStartedAt = now;
        _attention.SetTarget(GazeChannel.Manipulation, lookPoint);
        Plugin.Logger.LogInfo(
            $"[ACTION] PLAYER_PICKUP_REQUESTED " +
            $"referenceId={ReferenceIdForLog}, netId={_target.NetworkId}.");
        TickAwaitingPickupConfirmation(now);
    }

    private void TickAwaitingPickupConfirmation(float now)
    {
        string mismatchError;
        if (HasHeldPlayerMismatch(out mismatchError))
        {
            EnterIdentityFault(mismatchError, true);
            return;
        }
        if (IsFullyHeldByCompanion())
        {
            _state = CarryState.HoldingPlayer;
            _stateStartedAt = now;
            _completion = new CompanionJobCompletion
            {
                Result = AgentToolResult.Success(
                    AgentToolCatalog.PickUpPlayer,
                    "picked_up",
                    "holding_player"),
                HandsTransition =
                    CompanionTurnHandsTransition.HoldingExactPlayer,
                ExactPlayer = _target
            };
            Plugin.Logger.LogInfo(
                $"[ACTION] PLAYER_PICKUP_CONFIRMED " +
                $"referenceId={ReferenceIdForLog}, netId={_target.NetworkId}.");
            return;
        }

        Vector3 unusedPosition;
        Vector3 lookPoint;
        string validationError;
        if (TryValidateTarget(
                out unusedPosition,
                out lookPoint,
                out validationError))
        {
            _attention.SetTarget(GazeChannel.Manipulation, lookPoint);
        }
        if (now - _stateStartedAt >= PickupConfirmationSeconds)
            BeginReconciliation(now, "player_pickup_not_confirmed");
    }

    private void TickHolding()
    {
        string mismatchError;
        if (HasHeldPlayerMismatch(out mismatchError))
        {
            EnterIdentityFault(mismatchError, false);
            return;
        }
        if (!HasAnyExactCarryLink())
        {
            // The success is allowed to publish while the exact player is
            // held. If every exact carry link disappears before publication,
            // replace that queued result rather than advancing the turn with
            // a stale holding-player transition.
            if (_completion?.Result?.Ok == true)
                _completion = CompanionJobCompletion.Failed(
                    "player_pickup_not_retained");
            Plugin.Logger.LogInfo(
                $"[ACTION] PLAYER_PICKUP_RELEASED " +
                $"referenceId={ReferenceIdForLog}.");
            EndAction();
            return;
        }

        Vector3 lookPoint;
        if (_holdGaze && _target != null &&
            _target.TryGetCurrentLookPoint(out lookPoint))
        {
            _attention.SetTarget(GazeChannel.Manipulation, lookPoint);
        }
    }

    private void TickDrop(float now)
    {
        string mismatchError;
        if (HasHeldPlayerMismatch(out mismatchError))
        {
            EnterIdentityFault(mismatchError, true);
            return;
        }
        if (IsFullyReleasedFromCompanion())
        {
            if (_releasedSince < 0f)
                _releasedSince = now;
            if (now - _releasedSince < ReleaseSettlementSeconds)
                return;

            _completion = new CompanionJobCompletion
            {
                Result = AgentToolResult.Success(
                    AgentToolCatalog.DropPlayer,
                    "dropped",
                    "hands_empty"),
                HandsTransition = CompanionTurnHandsTransition.HandsEmpty
            };
            Plugin.Logger.LogInfo(
                $"[ACTION] PLAYER_DROP_CONFIRMED " +
                $"referenceId={ReferenceIdForLog}.");
            EndAction();
            return;
        }

        _releasedSince = -1f;
        if (_dropIssuedAt < 0f || now - _dropIssuedAt >= DropRetrySeconds)
            TryIssueExactDrop(now, true);
    }

    private void BeginReconciliation(float now, string error)
    {
        if (!string.IsNullOrEmpty(error) && _completion == null)
            _completion = CompanionJobCompletion.Failed(error);
        _state = CarryState.Reconciling;
        _stateStartedAt = now;
        _reconciliationStartedAt = now;
        _holdGaze = false;
        _releasedSince = -1f;
        _attention.ClearTarget(GazeChannel.Manipulation);
        Plugin.Logger.LogWarning(
            $"[ACTION] PLAYER_CARRY_RECONCILIATION_STARTED " +
            $"action={ActiveName}, error={error ?? "cancelled"}, " +
            $"referenceId={ReferenceIdForLog}.");
    }

    private void TickReconciliation(float now)
    {
        if (_body == null || !_body.IsAlive || _target == null)
        {
            EndAction();
            return;
        }

        string mismatchError;
        if (HasHeldPlayerMismatch(out mismatchError))
        {
            EnterIdentityFault(mismatchError, _completion == null);
            return;
        }
        if (HasAnyExactCarryLink())
        {
            _releasedSince = -1f;
            if (_dropIssuedAt < 0f ||
                now - _dropIssuedAt >= DropRetrySeconds)
            {
                TryIssueExactDrop(now, IsExplicitDrop);
            }

            if (now - _reconciliationStartedAt >=
                ReconciliationMaximumSeconds)
            {
                Plugin.Logger.LogWarning(
                    $"[ACTION] PLAYER_CARRY_RECONCILIATION_BOUNDED " +
                    $"referenceId={ReferenceIdForLog}, " +
                    "disposition=still_held; returning control to stock hands state.");
                EndAction();
            }
            return;
        }

        if (_releasedSince < 0f)
            _releasedSince = now;
        if (now - _releasedSince < ReconciliationSettlementSeconds)
            return;

        Plugin.Logger.LogInfo(
            $"[ACTION] PLAYER_CARRY_RECONCILED disposition=released, " +
            $"referenceId={ReferenceIdForLog}.");
        EndAction();
    }

    private bool TryIssueExactDrop(float now, bool explicitDrop)
    {
        if (_target == null || !HasAnyExactCarryLink())
            return false;
        string mismatchError;
        if (HasHeldPlayerMismatch(out mismatchError) || !HasAuthority)
            return false;

        _dropIssuedAt = now;
        try
        {
            // The native drop command has no target argument. Every carry link
            // is checked against the frozen player immediately above, making
            // this the exact-identity boundary for the parameterless command.
            _body.Networking.UserCode_CmdDropHeldPlayer();
            _releasedSince = -1f;
            Plugin.Logger.LogInfo(explicitDrop
                ? $"[ACTION] PLAYER_DROP_REQUESTED " +
                  $"referenceId={ReferenceIdForLog}."
                : $"[ACTION] PLAYER_PICKUP_COMPENSATING_DROP_REQUESTED " +
                  $"referenceId={ReferenceIdForLog}.");
            return true;
        }
        catch (System.Exception exception)
        {
            Plugin.Logger.LogError(explicitDrop
                ? $"[ACTION] PLAYER_DROP_FAILED exception={exception}"
                : $"[ACTION] PLAYER_PICKUP_COMPENSATING_DROP_FAILED " +
                  $"exception={exception}");
            return false;
        }
    }

    private bool TryValidateTarget(
        out Vector3 targetPosition,
        out Vector3 lookPoint,
        out string error)
    {
        targetPosition = Vector3.zero;
        lookPoint = Vector3.zero;
        error = null;
        if (_body == null || !_body.IsAlive)
        {
            error = "bot_not_spawned";
            return false;
        }
        if (_target == null || !_target.IsAvailable)
        {
            error = "human_player_reference_lost";
            return false;
        }
        if (!_target.TryGetCurrentPosition(out targetPosition) ||
            !_target.TryGetCurrentLookPoint(out lookPoint))
        {
            error = "human_player_unavailable";
            return false;
        }
        return true;
    }

    private bool TryIsWithinPickupReach(
        Vector3 targetPosition,
        out bool withinReach,
        out string error)
    {
        withinReach = false;
        error = null;
        if (_body == null || !_body.IsAlive ||
            _body.Character == null || _body.Character.caster == null)
        {
            error = "player_carry_capability_unavailable";
            return false;
        }

        var toTarget = targetPosition - _body.Position;
        var distance = toTarget.magnitude;
        if (distance < 0.01f)
        {
            withinReach = true;
            return true;
        }

        float nativeReach;
        try
        {
            nativeReach = _body.Character.caster.GetMaxDistanceForDirection(
                toTarget / distance);
        }
        catch (System.Exception exception)
        {
            Plugin.Logger.LogWarning(
                $"[ACTION] PLAYER_PICKUP_REACH_UNAVAILABLE " +
                $"exception={exception.GetType().Name}.");
            error = "player_carry_capability_unavailable";
            return false;
        }
        if (float.IsNaN(nativeReach) || float.IsInfinity(nativeReach) ||
            nativeReach <= 0f)
        {
            error = "player_carry_capability_unavailable";
            return false;
        }

        withinReach = distance <= nativeReach + PickupReachTolerance;
        return true;
    }

    private bool IsFullyHeldByCompanion()
    {
        if (_body == null || !_body.IsAlive || _target == null ||
            !_target.IsAvailable)
        {
            return false;
        }

        PlayerPose grabPose;
        string error;
        if (!_target.TryGetCarrierGrabPose(_body, out grabPose, out error))
            return false;
        var hands = GetHands();
        var poser = _target.Player.poser;
        return hands != null &&
               _target.IsStillTheSamePlayer(hands.heldCharacter) &&
               poser != null && poser.playerHoldingMe == _body.Character &&
               poser.currentPose == grabPose &&
               grabPose.occupant != null &&
               _target.IsStillTheSamePlayer(grabPose.occupant);
    }

    private bool IsFullyReleasedFromCompanion()
    {
        if (_body == null || !_body.IsAlive || _target == null)
            return true;

        var hands = GetHands();
        var poser = _target.Player == null ? null : _target.Player.poser;
        var grabPose = GetCarrierGrabPoseForLinkCheck();
        return (hands == null || hands.heldCharacter == null) &&
               (poser == null || poser.playerHoldingMe != _body.Character) &&
               (grabPose == null || poser == null ||
                poser.currentPose != grabPose) &&
               (grabPose == null || grabPose.occupant == null ||
                !_target.IsStillTheSamePlayer(grabPose.occupant));
    }

    private bool HasAnyExactCarryLink()
    {
        if (_body == null || !_body.IsAlive || _target == null)
            return false;

        var hands = GetHands();
        if (hands != null &&
            _target.IsStillTheSamePlayer(hands.heldCharacter))
        {
            return true;
        }

        var poser = _target.Player == null ? null : _target.Player.poser;
        if (poser != null && poser.playerHoldingMe == _body.Character)
            return true;

        var grabPose = GetCarrierGrabPoseForLinkCheck();
        if (grabPose == null)
            return false;
        return (poser != null &&
                poser.currentPose == grabPose) ||
               (grabPose.occupant != null &&
                _target.IsStillTheSamePlayer(grabPose.occupant));
    }

    private bool HasHeldPlayerMismatch(out string error)
    {
        error = null;
        if (_target == null)
        {
            error = "human_player_reference_lost";
            return true;
        }

        var hands = GetHands();
        if (hands != null && hands.heldCharacter != null &&
            !_target.IsStillTheSamePlayer(hands.heldCharacter))
        {
            error = "held_player_identity_mismatch";
            return true;
        }

        var grabPose = GetCarrierGrabPoseForLinkCheck();
        if (grabPose != null && grabPose.occupant != null &&
            !_target.IsStillTheSamePlayer(grabPose.occupant))
        {
            error = "held_player_identity_mismatch";
            return true;
        }
        return false;
    }

    private PlayerPose GetCarrierGrabPoseForLinkCheck()
    {
        // Teardown may deactivate the pose object before all exact native
        // backlinks clear. Link reconciliation needs the frozen pose identity,
        // not admission-time activeInHierarchy state.
        try
        {
            return _body?.Character?.registry?.grabPose;
        }
        catch (System.Exception)
        {
            return null;
        }
    }

    private PlayerHands GetHands()
    {
        return _body == null || _body.Character == null
            ? null
            : _body.Character.hands;
    }

    private bool HasAuthority =>
        _body != null && _body.IsAlive && _body.Networking != null &&
        NetworkServer.active && _body.Networking.isServer &&
        !_body.Networking.isLocalPlayer;

    private void CompleteFailure(string error)
    {
        _completion = CompanionJobCompletion.Failed(error);
        Plugin.Logger.LogWarning(
            $"[ACTION] PLAYER_PICKUP_FAILED error={error}, " +
            $"referenceId={ReferenceIdForLog}.");
        EndAction();
    }

    private void EnterIdentityFault(string error, bool reportFailure)
    {
        if ((reportFailure && _completion == null) ||
            _completion?.Result?.Ok == true)
            _completion = CompanionJobCompletion.Failed(error);
        Plugin.Logger.LogError(
            $"[ACTION] PLAYER_CARRY_IDENTITY_FAULT error={error}, " +
            $"referenceId={ReferenceIdForLog}. No parameterless drop command " +
            "ran against a different player; ownership returned to stock state.");
        EndAction();
    }

    private int ReferenceIdForLog =>
        _target == null ? 0 : _target.ReferenceId;

    private bool IsExplicitDrop => string.Equals(
        _activeActionName,
        AgentToolCatalog.DropPlayer,
        System.StringComparison.Ordinal);

    private void EndAction()
    {
        _approach.CancelRecovery();
        if (_locomotion != null && !IsExplicitDrop)
            _locomotion.Stop(Time.realtimeSinceStartup);
        _state = CarryState.Idle;
        _target = null;
        _stateStartedAt = 0f;
        _reconciliationStartedAt = 0f;
        _activeActionName = null;
        _holdGaze = false;
        _approach.Reset();
        ResetDropTracking();
        _attention.ClearTarget(GazeChannel.Manipulation);
    }

    private void ResetDropTracking()
    {
        _dropIssuedAt = -1f;
        _releasedSince = -1f;
    }

    private void ResetState()
    {
        _target = null;
        _state = CarryState.Idle;
        _stateStartedAt = 0f;
        _reconciliationStartedAt = 0f;
        _activeActionName = null;
        _holdGaze = false;
        _completion = null;
        _approach.Reset();
        ResetDropTracking();
    }
}
