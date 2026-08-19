using Mirror;
using UnityEngine;

namespace Ramblers;

/// <summary>
/// Picks up only the prop frozen at the originating utterance boundary. The
/// action revalidates that same object immediately before crossing host
/// authority and confirms that the bot's hands hold that exact prop afterward.
/// </summary>
internal sealed class CompanionPickupBehavior : ICompanionJob
{
    private const float MinimumTargetLookSeconds = 0.20f;
    private const float MaximumTargetLookSeconds = 1.50f;
    private const float TargetAimToleranceDegrees = 8f;
    private const float PickupReachDistance = 2.75f;
    private const float PickupConfirmationSeconds = 1.00f;
    private const float ReconciliationSettlementSeconds = 1.00f;
    private const float DropAbsentSettlementSeconds = 0.25f;
    private const float DropRetrySeconds = 0.50f;
    private const float PickupTimeoutSecondsValue = 5f;

    private enum PickupState
    {
        Idle,
        AligningTarget,
        AwaitingConfirmation,
        HoldingItem,
        DroppingItem,
        ReconcilingFailure,
        Cancelling,
        Faulted
    }

    private readonly CompanionAttention _attention;

    private CompanionBody _body;
    private CompanionInteractionTarget _target;
    private PickupState _state;
    private float _stateStartedAt;
    private bool _holdGaze;
    private bool _dropRequested;
    private float _dropIssuedAt = -1f;
    private float _dropAbsentSince = -1f;
    private string _activeActionName;
    private CompanionJobCompletion _completion;

    internal CompanionPickupBehavior(CompanionAttention attention)
    {
        _attention = attention;
    }

    public string Name => AgentToolCatalog.PickUpItem;

    public string ActiveName => _activeActionName ?? Name;

    public bool Handles(string actionName)
    {
        return string.Equals(
                   actionName,
                   AgentToolCatalog.PickUpItem,
                   System.StringComparison.Ordinal) ||
               string.Equals(
                   actionName,
                   AgentToolCatalog.DropItem,
                   System.StringComparison.Ordinal);
    }

    public JobResources RequiredFor(CompanionJobRequest request)
    {
        return request != null && string.Equals(
            request.ActionName,
            AgentToolCatalog.DropItem,
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
                case PickupState.Idle:
                    return JobResources.None;
                case PickupState.HoldingItem:
                    return _holdGaze
                        ? JobResources.Gaze | JobResources.Hands
                        : JobResources.Hands;
                case PickupState.DroppingItem:
                case PickupState.ReconcilingFailure:
                case PickupState.Cancelling:
                case PickupState.Faulted:
                    return JobResources.Hands;
                default:
                    return JobResources.Locomotion |
                           JobResources.Gaze |
                           JobResources.Hands;
            }
        }
    }

    public bool IsActive => _state != PickupState.Idle;

    public float TimeoutSeconds => PickupTimeoutSecondsValue;

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
                AgentToolCatalog.DropItem,
                System.StringComparison.Ordinal))
        {
            return TryBeginDrop(now, out failure);
        }

        if (request != null &&
            !string.IsNullOrEmpty(request.ActionName) &&
            !string.Equals(
                request.ActionName,
                AgentToolCatalog.PickUpItem,
                System.StringComparison.Ordinal))
        {
            failure = AgentToolResult.Failure("unknown_tool");
            return false;
        }

        if (_state != PickupState.Idle || _completion != null)
        {
            failure = AgentToolResult.Failure("pick_up_item_in_progress");
            return false;
        }

        _target = request == null ? null : request.InteractionTarget;
        if (_target == null)
        {
            failure = AgentToolResult.Failure("human_reference_not_captured");
            return false;
        }

        Vector3 targetPoint;
        string validationError;
        if (!TryValidateWorldTarget(out targetPoint, out validationError))
        {
            _target = null;
            failure = AgentToolResult.Failure(validationError);
            return false;
        }

        if (!IsWithinPickupReach(targetPoint))
        {
            _target = null;
            failure = AgentToolResult.Failure("item_out_of_reach");
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

        if (!hands.IsSafeToPickUp(_target.Prop))
        {
            _target = null;
            failure = AgentToolResult.Failure("item_unavailable");
            return false;
        }

        _state = PickupState.AligningTarget;
        _stateStartedAt = now;
        _activeActionName = AgentToolCatalog.PickUpItem;
        _holdGaze = true;
        ResetDropTracking();
        _attention.SetTarget(GazeChannel.Manipulation, targetPoint);
        Plugin.Logger.LogInfo(
            $"[ACTION] PICKUP_STARTED referenceId={_target.ReferenceId}, " +
            $"netId={_target.NetworkId}, turnId={(request == null ? 0 : request.TurnId)}.");
        return true;
    }

    private bool TryBeginDrop(float now, out AgentToolResult failure)
    {
        failure = null;
        if (_completion != null)
        {
            failure = AgentToolResult.Failure(ActiveName + "_in_progress");
            return false;
        }

        if (_state != PickupState.Idle && _state != PickupState.HoldingItem)
        {
            failure = AgentToolResult.Failure(ActiveName + "_in_progress");
            return false;
        }

        var hands = GetHands();
        if (hands == null)
        {
            failure = AgentToolResult.Failure("hands_unavailable");
            return false;
        }

        var heldProp = hands.heldProp;
        if (heldProp == null)
        {
            if (_state == PickupState.HoldingItem)
                EndAction();
            failure = AgentToolResult.Failure(
                hands.heldCharacter == null
                    ? "hands_empty"
                    : "held_item_not_droppable");
            return false;
        }

        if (_state == PickupState.HoldingItem)
        {
            if (_target == null || !_target.IsStillTheSameProp(heldProp))
            {
                EnterIdentityFault("held_target_mismatch", false);
                failure = AgentToolResult.Failure(
                    "held_item_identity_mismatch");
                return false;
            }
        }
        else if (!CompanionInteractionTarget.TryCaptureHeldProp(
                     heldProp,
                     out _target))
        {
            failure = AgentToolResult.Failure("held_item_unavailable");
            return false;
        }

        if (!HasDropAuthority)
        {
            if (_state == PickupState.Idle)
                _target = null;
            failure = AgentToolResult.Failure("bot_authority_unavailable");
            return false;
        }

        _state = PickupState.DroppingItem;
        _stateStartedAt = now;
        _activeActionName = AgentToolCatalog.DropItem;
        _holdGaze = false;
        ResetDropTracking();
        _attention.ClearTarget(GazeChannel.Manipulation);
        Plugin.Logger.LogInfo(
            $"[ACTION] DROP_STARTED referenceId={ReferenceIdForLog}, " +
            $"netId={_target.NetworkId}.");

        // The operation remains pending even if the host call throws: once the
        // call boundary is crossed its outcome is ambiguous, so Tick reconciles
        // against this same held prop and retries only while it remains exact.
        TryIssueExactDrop(now, true);
        return true;
    }

    public void Tick(float now)
    {
        switch (_state)
        {
            case PickupState.Idle:
            case PickupState.Faulted:
                return;
            case PickupState.HoldingItem:
                TickHolding();
                return;
            case PickupState.DroppingItem:
                TickExplicitDrop(now);
                return;
            case PickupState.AwaitingConfirmation:
                TickAwaitingConfirmation(now);
                return;
            case PickupState.ReconcilingFailure:
            case PickupState.Cancelling:
                TickReconciliation(now);
                return;
        }

        TickAlignment(now);
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
        if (_state != PickupState.HoldingItem)
            return;

        _holdGaze = false;
        _attention.ClearTarget(GazeChannel.Manipulation);
    }

    public void Cancel(float now)
    {
        _completion = null;
        if (_state == PickupState.Idle)
            return;

        var explicitDrop = IsExplicitDrop;

        if (_state == PickupState.AligningTarget)
        {
            Plugin.Logger.LogInfo(
                $"[ACTION] PICKUP_CANCELLED phase=before_authority, " +
                $"referenceId={ReferenceIdForLog}.");
            EndAction();
            return;
        }

        if (_state == PickupState.Faulted)
        {
            Plugin.Logger.LogWarning(explicitDrop
                ? "[ACTION] DROP_CANCEL_BLOCKED reason=target_identity_fault."
                : "[ACTION] PICKUP_CANCEL_BLOCKED reason=target_identity_fault.");
            return;
        }

        _state = PickupState.Cancelling;
        _stateStartedAt = now;
        _holdGaze = false;
        ResetDropTracking();
        _attention.ClearTarget(GazeChannel.Manipulation);
        Plugin.Logger.LogInfo(explicitDrop
            ? $"[ACTION] DROP_CANCEL_RECONCILE_STARTED " +
              $"referenceId={ReferenceIdForLog}."
            : $"[ACTION] PICKUP_CANCEL_RECONCILE_STARTED " +
              $"referenceId={ReferenceIdForLog}.");
    }

    public void Fail(string error, float now)
    {
        if (_state == PickupState.Idle || _state == PickupState.Faulted)
            return;

        if (_state == PickupState.AligningTarget)
        {
            CompleteFailure(error ?? "action_execution_failed");
            return;
        }

        _completion = CompanionJobCompletion.Failed(
            error ?? "action_execution_failed");
        _state = PickupState.ReconcilingFailure;
        _stateStartedAt = now;
        _holdGaze = false;
        ResetDropTracking();
        _attention.ClearTarget(GazeChannel.Manipulation);
    }

    public void Release()
    {
        _body = null;
        ResetState();
        _attention.ClearTarget(GazeChannel.Manipulation);
    }

    private void TickAlignment(float now)
    {
        if (_body == null || !_body.IsAlive)
        {
            CompleteFailure("bot_not_spawned");
            return;
        }

        Vector3 targetPoint;
        string validationError;
        if (!TryValidateWorldTarget(out targetPoint, out validationError))
        {
            CompleteFailure(validationError);
            return;
        }

        if (!IsWithinPickupReach(targetPoint))
        {
            CompleteFailure("item_out_of_reach");
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

        if (!hands.IsSafeToPickUp(_target.Prop))
        {
            CompleteFailure("item_unavailable");
            return;
        }

        _attention.SetTarget(GazeChannel.Manipulation, targetPoint);
        var lookSeconds = now - _stateStartedAt;
        if (lookSeconds < MinimumTargetLookSeconds)
            return;

        if (_attention.IsAimWithin(
                GazeChannel.Manipulation,
                TargetAimToleranceDegrees,
                TargetAimToleranceDegrees))
        {
            ExecutePickup(now, targetPoint);
            return;
        }

        if (lookSeconds >= MaximumTargetLookSeconds)
            CompleteFailure("target_alignment_failed");
    }

    private void ExecutePickup(float now, Vector3 targetPoint)
    {
        var hands = GetHands();
        if (hands == null)
        {
            CompleteFailure("hands_unavailable");
            return;
        }

        Vector3 currentPoint;
        string validationError;
        if (!TryValidateWorldTarget(out currentPoint, out validationError))
        {
            CompleteFailure(validationError);
            return;
        }

        if (!IsWithinPickupReach(currentPoint))
        {
            CompleteFailure("item_out_of_reach");
            return;
        }

        if (hands.heldProp != null || hands.heldCharacter != null)
        {
            CompleteFailure("hands_occupied");
            return;
        }

        if (!hands.IsSafeToPickUp(_target.Prop))
        {
            CompleteFailure("item_unavailable");
            return;
        }

        if (_body.Networking == null || !NetworkServer.active ||
            !_body.Networking.isServer || _body.Networking.isLocalPlayer)
        {
            CompleteFailure("bot_authority_unavailable");
            return;
        }

        var authorityCallStarted = false;
        try
        {
            authorityCallStarted = true;
            _body.Networking.ServerPickUpPropAutomatic(_target.Prop);
        }
        catch (System.Exception exception)
        {
            Plugin.Logger.LogError(
                $"[ACTION] PICKUP_FAILED exception={exception}");
            if (authorityCallStarted)
                BeginPostDispatchFailure("pickup_execution_failed", now);
            else
                CompleteFailure("pickup_execution_failed");
            return;
        }

        _state = PickupState.AwaitingConfirmation;
        _stateStartedAt = now;
        _attention.SetTarget(GazeChannel.Manipulation, targetPoint);
        Plugin.Logger.LogInfo(
            $"[ACTION] PICKUP_REQUESTED referenceId={ReferenceIdForLog}, " +
            $"netId={(_target == null ? 0u : _target.NetworkId)}.");
        TickAwaitingConfirmation(now);
    }

    private void TickAwaitingConfirmation(float now)
    {
        if (_body == null || !_body.IsAlive)
        {
            BeginPostDispatchFailure("bot_not_spawned", now);
            return;
        }

        var hands = GetHands();
        if (hands == null)
        {
            EnterIdentityFault("hands_unavailable", true);
            return;
        }

        var heldProp = hands.heldProp;
        if (heldProp != null)
        {
            if (!_target.IsStillTheSameProp(heldProp))
            {
                EnterIdentityFault("pickup_target_mismatch", true);
                return;
            }

            _state = PickupState.HoldingItem;
            _stateStartedAt = now;
            _completion = new CompanionJobCompletion
            {
                Result = AgentToolResult.Success(
                    AgentToolCatalog.PickUpItem,
                    "picked_up",
                    "holding_item")
            };
            Plugin.Logger.LogInfo(
                $"[ACTION] PICKUP_CONFIRMED referenceId={ReferenceIdForLog}, " +
                $"netId={_target.NetworkId}.");
            return;
        }

        Vector3 targetPoint;
        string validationError;
        if (!TryValidateWorldTarget(out targetPoint, out validationError))
        {
            BeginPostDispatchFailure("pickup_not_confirmed", now);
            return;
        }

        _attention.SetTarget(GazeChannel.Manipulation, targetPoint);
        if (now - _stateStartedAt >= PickupConfirmationSeconds)
            BeginPostDispatchFailure("pickup_not_confirmed", now);
    }

    private void TickHolding()
    {
        var hands = GetHands();
        if (hands == null)
        {
            EnterIdentityFault("hands_unavailable", false);
            return;
        }

        var heldProp = hands.heldProp;
        if (heldProp == null)
        {
            Plugin.Logger.LogInfo(
                $"[ACTION] PICKUP_RELEASED referenceId={ReferenceIdForLog}.");
            EndAction();
            return;
        }

        if (!_target.IsStillTheSameProp(heldProp))
        {
            EnterIdentityFault("held_target_mismatch", false);
            return;
        }

        if (_holdGaze && _target.Prop != null)
        {
            _attention.SetTarget(
                GazeChannel.Manipulation,
                _target.Prop.transform.position);
        }
    }

    private void TickExplicitDrop(float now)
    {
        if (_body == null || !_body.IsAlive)
        {
            CompleteDropFailure("bot_not_spawned");
            return;
        }

        var hands = GetHands();
        if (hands == null)
        {
            EnterIdentityFault("hands_unavailable", true);
            return;
        }

        var heldProp = hands.heldProp;
        if (heldProp != null)
        {
            _dropAbsentSince = -1f;
            if (_target == null || !_target.IsStillTheSameProp(heldProp))
            {
                EnterIdentityFault("drop_target_mismatch", true);
                return;
            }

            if (_dropIssuedAt < 0f ||
                now - _dropIssuedAt >= DropRetrySeconds)
            {
                TryIssueExactDrop(now, true);
            }
            return;
        }

        if (_dropAbsentSince < 0f)
            _dropAbsentSince = now;
        if (now - _dropAbsentSince < DropAbsentSettlementSeconds)
            return;

        _completion = new CompanionJobCompletion
        {
            Result = AgentToolResult.Success(
                AgentToolCatalog.DropItem,
                "dropped",
                "hands_empty")
        };
        Plugin.Logger.LogInfo(
            $"[ACTION] DROP_CONFIRMED referenceId={ReferenceIdForLog}.");
        EndAction();
    }

    private void BeginPostDispatchFailure(string error, float now)
    {
        if (_completion == null)
            _completion = CompanionJobCompletion.Failed(error);
        _state = PickupState.ReconcilingFailure;
        _stateStartedAt = now;
        _holdGaze = false;
        ResetDropTracking();
        _attention.ClearTarget(GazeChannel.Manipulation);
        Plugin.Logger.LogWarning(
            $"[ACTION] PICKUP_RECONCILIATION_STARTED error={error}, " +
            $"referenceId={ReferenceIdForLog}.");
    }

    private void TickReconciliation(float now)
    {
        if (_body == null || !_body.IsAlive || _target == null)
        {
            EndAction();
            return;
        }

        var hands = GetHands();
        if (hands == null)
        {
            EnterIdentityFault("hands_unavailable", _completion == null);
            return;
        }

        var heldProp = hands.heldProp;
        if (heldProp != null)
        {
            _dropAbsentSince = -1f;
            if (!_target.IsStillTheSameProp(heldProp))
            {
                EnterIdentityFault(
                    "pickup_target_mismatch",
                    _completion == null);
                return;
            }

            if (_dropIssuedAt < 0f ||
                now - _dropIssuedAt >= DropRetrySeconds)
            {
                TryIssueExactDrop(now, IsExplicitDrop);
            }
            return;
        }

        if (_dropRequested)
        {
            if (_dropAbsentSince < 0f)
                _dropAbsentSince = now;
            if (now - _dropAbsentSince < DropAbsentSettlementSeconds)
                return;

            Plugin.Logger.LogInfo(IsExplicitDrop
                ? $"[ACTION] DROP_RECONCILED disposition=dropped, " +
                  $"referenceId={ReferenceIdForLog}."
                : $"[ACTION] PICKUP_RECONCILED disposition=dropped, " +
                  $"referenceId={ReferenceIdForLog}.");
            EndAction();
            return;
        }

        if (IsTargetGone() ||
            now - _stateStartedAt >= ReconciliationSettlementSeconds)
        {
            Plugin.Logger.LogInfo(IsExplicitDrop
                ? $"[ACTION] DROP_RECONCILED disposition=not_held, " +
                  $"referenceId={ReferenceIdForLog}."
                : $"[ACTION] PICKUP_RECONCILED disposition=not_held, " +
                  $"referenceId={ReferenceIdForLog}.");
            EndAction();
        }
    }

    private bool TryIssueExactDrop(float now, bool explicitDrop = false)
    {
        var hands = GetHands();
        if (hands == null || _target == null ||
            !_target.IsStillTheSameProp(hands.heldProp))
        {
            return false;
        }

        _dropIssuedAt = now;
        if (!HasDropAuthority)
        {
            return false;
        }

        try
        {
            // ServerDropPropAutomatic has no Prop parameter. The exact held-prop
            // check immediately above is therefore the identity boundary for
            // this compensating command.
            _body.Networking.ServerDropPropAutomatic(false);
            _dropRequested = true;
            _dropAbsentSince = -1f;
            Plugin.Logger.LogInfo(explicitDrop
                ? $"[ACTION] DROP_REQUESTED referenceId={ReferenceIdForLog}."
                : $"[ACTION] PICKUP_DROP_REQUESTED referenceId={ReferenceIdForLog}.");
            return true;
        }
        catch (System.Exception exception)
        {
            Plugin.Logger.LogError(explicitDrop
                ? $"[ACTION] DROP_FAILED exception={exception}"
                : $"[ACTION] PICKUP_DROP_FAILED exception={exception}");
            return false;
        }
    }

    private bool HasDropAuthority =>
        _body != null && _body.IsAlive && _body.Networking != null &&
        NetworkServer.active && _body.Networking.isServer &&
        !_body.Networking.isLocalPlayer;

    private bool TryValidateWorldTarget(
        out Vector3 point,
        out string error)
    {
        point = Vector3.zero;
        error = null;
        if (_target == null || !_target.IsStillTheSameProp(_target.Prop))
        {
            error = "item_reference_lost";
            return false;
        }

        if (!_target.TryGetCurrentPoint(out point))
        {
            error = "item_unavailable";
            return false;
        }

        return true;
    }

    private bool IsWithinPickupReach(Vector3 point)
    {
        return _body != null &&
               Vector3.Distance(_body.Position, point) <= PickupReachDistance;
    }

    private PlayerHands GetHands()
    {
        return _body == null || _body.Character == null
            ? null
            : _body.Character.hands;
    }

    private bool IsTargetGone()
    {
        return _target == null || _target.Prop == null ||
               _target.Prop.gameObject == null;
    }

    private void CompleteFailure(string error)
    {
        _completion = CompanionJobCompletion.Failed(error);
        Plugin.Logger.LogWarning(
            $"[ACTION] PICKUP_FAILED error={error}, " +
            $"referenceId={ReferenceIdForLog}.");
        EndAction();
    }

    private void CompleteDropFailure(string error)
    {
        _completion = CompanionJobCompletion.Failed(error);
        Plugin.Logger.LogWarning(
            $"[ACTION] DROP_FAILED error={error}, " +
            $"referenceId={ReferenceIdForLog}.");
        EndAction();
    }

    private void EnterIdentityFault(string error, bool reportFailure)
    {
        if (reportFailure && _completion == null)
            _completion = CompanionJobCompletion.Failed(error);
        _state = PickupState.Faulted;
        _holdGaze = false;
        ResetDropTracking();
        _attention.ClearTarget(GazeChannel.Manipulation);
        Plugin.Logger.LogError(IsExplicitDrop
            ? $"[ACTION] DROP_IDENTITY_FAULT error={error}, " +
              $"referenceId={ReferenceIdForLog}. Hands remain blocked; " +
              "no command will target a different prop."
            : $"[ACTION] PICKUP_IDENTITY_FAULT error={error}, " +
              $"referenceId={ReferenceIdForLog}. Hands remain blocked; " +
              "no command will target a different prop.");
    }

    private int ReferenceIdForLog => _target == null ? 0 : _target.ReferenceId;

    private bool IsExplicitDrop => string.Equals(
        _activeActionName,
        AgentToolCatalog.DropItem,
        System.StringComparison.Ordinal);

    private void EndAction()
    {
        _state = PickupState.Idle;
        _target = null;
        _activeActionName = null;
        _holdGaze = false;
        ResetDropTracking();
        _attention.ClearTarget(GazeChannel.Manipulation);
    }

    private void ResetDropTracking()
    {
        _dropRequested = false;
        _dropIssuedAt = -1f;
        _dropAbsentSince = -1f;
    }

    private void ResetState()
    {
        _target = null;
        _state = PickupState.Idle;
        _stateStartedAt = 0f;
        _activeActionName = null;
        _holdGaze = false;
        _completion = null;
        ResetDropTracking();
    }
}
