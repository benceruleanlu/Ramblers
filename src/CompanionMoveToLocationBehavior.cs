using UnityEngine;

namespace Ramblers;

/// <summary>
/// Walks toward one utterance-frozen world point. The model chooses the intent,
/// while deterministic locomotion owns movement, bounded recovery, arrival,
/// and cancellation. This composes with native player carry because it neither
/// picks up nor drops anyone and never changes the persistent follow intent.
/// </summary>
internal sealed class CompanionMoveToLocationBehavior :
    ICompanionJob,
    ICompanionStandingJob
{
    private const float ArrivalHorizontalDistance = 1.0f;
    private const float ArrivalVerticalTolerance = 2.0f;
    private const float TimeoutSecondsValue = 30f;

    private readonly CompanionLocomotion _locomotion;
    private readonly CompanionAttention _attention;
    private readonly CompanionApproachController _approach;

    private CompanionBody _body;
    private CompanionInspectionReferent _destination;
    private CompanionJobCompletion _completion;
    private Vector3 _destinationPoint;
    private bool _active;
    private bool _requiresCarriedHuman;
    private string _destinationReferenceId;
    private string _callId;
    private long _turnId;

    internal CompanionMoveToLocationBehavior(
        CompanionLocomotion locomotion,
        CompanionAttention attention,
        CompanionJumpActuator jump)
    {
        _locomotion = locomotion;
        _attention = attention;
        _approach = new CompanionApproachController(
            locomotion,
            jump,
            AgentToolCatalog.GoToLocation);
    }

    public string Name => AgentToolCatalog.GoToLocation;

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
        return JobResources.Locomotion | JobResources.Gaze;
    }

    public JobResources Held => _active
        ? JobResources.Locomotion | JobResources.Gaze
        : JobResources.None;

    public bool IsActive => _active;

    public bool MayPublishCompletionWhileActive => false;

    public float TimeoutSeconds => TimeoutSecondsValue;

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
        if (_active || _completion != null)
        {
            failure = AgentToolResult.Failure("go_to_location_in_progress");
            return false;
        }

        var destination = request == null ? null : request.MoveDestination;
        Vector3 point;
        if (destination == null ||
            destination.Source != CompanionInspectionSource.HumanGaze ||
            !destination.GazeRayHit ||
            !destination.TryGetCurrentPoint(out point) ||
            !IsFinite(point))
        {
            failure = AgentToolResult.Failure("location_not_found");
            return false;
        }

        _destination = destination;
        _destinationPoint = point;
        _destinationReferenceId =
            CompanionInspectionReferent.GetFrozenPointReferenceId(point);
        _callId = request.CallId;
        _turnId = request.TurnId;
        _active = true;
        _requiresCarriedHuman = IsCarryingHuman;
        _approach.Begin(now);
        _attention.SetTarget(GazeChannel.Navigation, _destinationPoint);
        Plugin.Logger.LogInfo(
            $"[ACTION] GO_TO_LOCATION_STARTED " +
            $"referenceId={_destinationReferenceId}, " +
            $"destinationPoint={_destinationPoint}, " +
            $"source={destination.SourceLabel}, " +
            $"carryingPlayer={_requiresCarriedHuman}, " +
            $"callId={CallIdForLog}, turnId={_turnId}.");
        return true;
    }

    public void Tick(float now)
    {
        if (!_active || !_approach.TryBeginTick(now))
            return;

        if (_body == null || !_body.IsAlive)
        {
            CompleteFailure("bot_not_spawned", now);
            return;
        }
        if (_requiresCarriedHuman && !IsCarryingHuman)
        {
            CompleteFailure("player_carry_lost", now);
            return;
        }

        _attention.SetTarget(GazeChannel.Navigation, _destinationPoint);
        var offset = _destinationPoint - _body.Position;
        var verticalDistance = Mathf.Abs(offset.y);
        offset.y = 0f;
        var horizontalDistance = offset.magnitude;
        if (horizontalDistance <= ArrivalHorizontalDistance &&
            verticalDistance <= ArrivalVerticalTolerance)
        {
            CompleteSuccess(now, horizontalDistance, verticalDistance);
            return;
        }
        if (horizontalDistance <= 0.0001f)
        {
            // A point directly above or below is not an arrival and has no
            // valid planar steering direction. Keep the job lifecycle-bound
            // without introducing a NaN movement vector or inventing a route.
            _locomotion.Stop(now);
            _locomotion.ResetProgressObservation(now);
            return;
        }

        var direction = offset / horizontalDistance;
        var approachStep = _approach.Advance(
            now,
            direction,
            horizontalDistance);
        if (approachStep.Kind == CompanionApproachStepKind.RecoveryCommitted)
        {
            Plugin.Logger.LogInfo(
                $"[ACTION] GO_TO_LOCATION_RECOVERY reason={approachStep.Reason}, " +
                $"attempt={approachStep.RecoveryAttempt}, " +
                $"carryingPlayer={IsCarryingHuman}.");
        }
        else if (approachStep.Kind == CompanionApproachStepKind.Blocked)
        {
            Plugin.Logger.LogWarning(
                $"[ACTION] GO_TO_LOCATION_BLOCKED reason={approachStep.Reason}, " +
                $"recovery={approachStep.RecoveryError ?? "unavailable"}.");
            CompleteFailure("location_path_blocked", now);
        }
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
        End(now);
    }

    public void Cancel(float now)
    {
        _completion = null;
        if (!_active)
            return;
        Plugin.Logger.LogInfo(
            $"[ACTION] GO_TO_LOCATION_CANCELLED " +
            $"referenceId={_destinationReferenceId}, " +
            $"callId={CallIdForLog}, turnId={_turnId}.");
        End(now);
    }

    public void Fail(string error, float now)
    {
        if (_active)
            CompleteFailure(error ?? "action_execution_failed", now);
    }

    public void Release()
    {
        _body = null;
        ResetState();
        _attention.ClearTarget(GazeChannel.Navigation);
    }

    private void CompleteSuccess(
        float now,
        float horizontalDistance,
        float verticalDistance)
    {
        _completion = new CompanionJobCompletion
        {
            Result = AgentToolResult.Success(
                Name,
                "arrived",
                IsCarryingHuman ? "holding_player" : "at_destination")
        };
        Plugin.Logger.LogInfo(
            $"[ACTION] GO_TO_LOCATION_ARRIVED " +
            $"referenceId={_destinationReferenceId}, " +
            $"destinationPoint={_destinationPoint}, " +
            $"horizontalDistance={horizontalDistance:F2}, " +
            $"verticalDistance={verticalDistance:F2}, " +
            $"carryingPlayer={IsCarryingHuman}, " +
            $"callId={CallIdForLog}, turnId={_turnId}.");
        End(now);
    }

    private void CompleteFailure(string error, float now)
    {
        _completion = CompanionJobCompletion.Failed(error);
        Plugin.Logger.LogWarning(
            $"[ACTION] GO_TO_LOCATION_FAILED error={error}, " +
            $"referenceId={_destinationReferenceId}, " +
            $"destinationPoint={_destinationPoint}, " +
            $"callId={CallIdForLog}, turnId={_turnId}.");
        End(now);
    }

    private void End(float now)
    {
        _approach.CancelRecovery();
        _locomotion.Stop(now);
        _active = false;
        _destination = null;
        _destinationPoint = Vector3.zero;
        _approach.Reset();
        _requiresCarriedHuman = false;
        _destinationReferenceId = null;
        _callId = null;
        _turnId = 0;
        _attention.ClearTarget(GazeChannel.Navigation);
    }

    private bool IsCarryingHuman
    {
        get
        {
            var human = WorldManager.localPlayerCharacter;
            return CompanionFollowBehavior.IsBodyCarryingHuman(_body, human);
        }
    }

    private string CallIdForLog => string.IsNullOrWhiteSpace(_callId)
        ? "none"
        : _callId;

    private static bool IsFinite(Vector3 point)
    {
        return !float.IsNaN(point.x) && !float.IsInfinity(point.x) &&
               !float.IsNaN(point.y) && !float.IsInfinity(point.y) &&
               !float.IsNaN(point.z) && !float.IsInfinity(point.z);
    }

    private void ResetState()
    {
        _destination = null;
        _completion = null;
        _destinationPoint = Vector3.zero;
        _approach.Reset();
        _active = false;
        _requiresCarriedHuman = false;
        _destinationReferenceId = null;
        _callId = null;
        _turnId = 0;
    }
}
