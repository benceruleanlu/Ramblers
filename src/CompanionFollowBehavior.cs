using System;
using Mirror;
using UnityEngine;

namespace Ramblers;

/// <summary>
/// Long-running breadcrumb-follow goal. It owns navigation state but delegates
/// body motion and facing to reusable actuators.
/// </summary>
internal sealed class CompanionFollowBehavior
{
    internal const float NavigationInterval = 0.1f;

    private const float TrailSampleInterval = 0.1f;
    private const float BreadcrumbSpacing = 0.65f;
    private const float BreadcrumbArrivalTolerance = 0.8f;
    private const float BreadcrumbArrivalVerticalTolerance = 0.9f;
    private const float BreadcrumbPassLateralTolerance = 1.5f;
    private const float RouteShortcutHorizontalTolerance = 1.5f;
    private const float FollowDistance = 2.25f;
    private const float ResumeDistance = 2.5f;
    private const float HoldingVerticalTolerance = 1.0f;
    private const float TrailResetDistance = 8f;
    private const float JumpRiseThreshold = 0.45f;
    private const float MeaningfulJumpLandingRise = 0.35f;
    private const float JumpApproachDistance = 1.6f;
    private const float BlockedJumpApproachDistance = 1.8f;
    private const float TraversalLookaheadDistance = 1.8f;
    private const float TraversalLookaheadVerticalTolerance = 1.0f;
    private const float DropCommitDepth = 0.45f;
    private const float DropCommitApproachDistance = 1.8f;
    private const float DropDirectionCommitSeconds = 1.25f;
    private const float TraversalDirectionCommitSeconds = 1.25f;
    private const float RecoveryDirectionCommitSeconds = 0.45f;
    private const int MaximumJumpAttemptsPerBreadcrumb = 2;
    private const float StatusLogInterval = 1f;
    private const int MaximumBreadcrumbs = 1024;

    private enum FollowState
    {
        Idle,
        Waiting,
        Following,
        Holding,
        Carried,
        Suspended,
        Blocked,
        Failed
    }

    private readonly BreadcrumbTrail _trail = new BreadcrumbTrail(MaximumBreadcrumbs);
    private readonly CompanionLocomotion _locomotion;
    private readonly CompanionAttention _attention;
    private readonly CompanionJumpActuator _jump;

    private CompanionBody _body;
    private PlayerCharacter _humanAtSpawn;
    private bool _followRequested;
    private FollowState _state = FollowState.Idle;
    private float _followAt;
    private float _nextNavigationTick;
    private float _nextTrailSample;
    private float _lastTrailDistance;
    private Vector3 _currentTarget;
    private float _followStartedAt;
    private float _nextStatusLog;
    private string _suspensionReason;
    private bool _humanWasGrounded;
    private bool _humanJumpInProgress;
    private Vector3 _humanJumpTakeoffPosition;
    private float _humanJumpPeakY;
    private bool _pendingHumanDrop;
    private bool _bodyIsCarried;
    private bool _bodyCarriesHuman;
    private int _currentBreadcrumbSequence;
    private int _jumpCommittedSequence;
    private int _jumpAttemptsForBreadcrumb;
    private int _dropCommittedSequence;
    private float _directTraversalUntil;
    private Vector3 _committedTraversalDirection;
    private int _committedTraversalSequence;
    private Vector3 _lastRouteDirection;
    private string _lastRouteMode = "waypoint";
    private float _lastTargetHorizontalDistance;

    internal CompanionFollowBehavior(
        CompanionLocomotion locomotion,
        CompanionAttention attention,
        CompanionJumpActuator jump)
    {
        _locomotion = locomotion;
        _attention = attention;
        _jump = jump;
    }

    /// <summary>Whether a follow intent is outstanding, suspended or not.</summary>
    internal bool IsRequested => _followRequested;
    internal bool IsCarried => _bodyIsCarried;
    internal bool IsCarryingHuman => _bodyCarriesHuman;
    internal string StateLabel => _state.ToString().ToLowerInvariant();

    internal void Bind(
        CompanionBody body,
        PlayerCharacter human,
        float now,
        bool movementAllowed,
        string movementBlocker)
    {
        _body = body;
        _humanAtSpawn = human;
        _followRequested = false;
        _state = FollowState.Idle;
        _lastTrailDistance = 0f;
        _suspensionReason = null;
        _followAt = float.PositiveInfinity;
        _nextNavigationTick = _followAt;
        _nextTrailSample = now + TrailSampleInterval;
        _trail.Clear();
        _bodyIsCarried = false;
        _bodyCarriesHuman = false;
        ResetTraversalState(human);
        if (human == null)
            return;

        StartFollowIntent(human, now, movementAllowed, movementBlocker);
        Plugin.Logger.LogInfo(
            $"[FOLLOW] DEFAULT mode=follow status={(movementAllowed ? "started" : "suspended")}.");
    }

    internal void TickFrame(float now)
    {
        if (_body == null || !_body.IsAlive)
            return;

        if (UpdateCarryState(now))
            return;

        ObserveHumanTraversal();
        if (now < _nextTrailSample)
            return;
        _nextTrailSample = now + TrailSampleInterval;
        RecordHumanTrail();
    }

    internal void TickFixed(float now, bool movementAllowed, string movementBlocker)
    {
        if (_body == null || !_body.IsAlive)
            return;

        // A physical job may own locomotion even while follow is explicitly
        // set to stay. Yield before idle-follow cleanup can clear that job's
        // velocity; the action coordinator is the authority for this gate.
        if (!movementAllowed)
        {
            if (_followRequested)
                SetMovementAllowed(false, now, movementBlocker);
            else
                _attention.ClearTarget(GazeChannel.Follow);
            return;
        }

        if (!_followRequested)
        {
            _attention.ClearTarget(GazeChannel.Follow);
            if (_state != FollowState.Idle ||
                _locomotion.LastMovementIntent.sqrMagnitude > 0f)
            {
                StopForState(FollowState.Idle, now);
            }
            return;
        }

        if (_bodyIsCarried)
        {
            if (_state != FollowState.Carried ||
                _locomotion.LastMovementIntent.sqrMagnitude > 0f)
            {
                StopForState(FollowState.Carried, now);
            }
            return;
        }

        if (_state == FollowState.Suspended)
            SetMovementAllowed(true, now, null);
        if (_state == FollowState.Failed)
            return;
        if (now < _followAt)
            return;

        if (!NetworkServer.active ||
            !_body.Networking.isServer ||
            _body.Networking.isLocalPlayer)
        {
            Fail(
                $"authority invariant failed: serverActive={NetworkServer.active}, " +
                $"isServer={_body.Networking.isServer}, " +
                $"isLocalPlayer={_body.Networking.isLocalPlayer}");
            return;
        }

        if (_state == FollowState.Waiting)
            BeginFollowing(now);

        if (now < _nextNavigationTick)
            return;

        _nextNavigationTick = now + NavigationInterval;
        NavigateTowardHuman(now);
    }

    internal AgentToolResult SetMode(
        FollowMode mode,
        float now,
        bool movementAllowed,
        string movementBlocker)
    {
        if (_body == null || !_body.IsAlive)
            return AgentToolResult.Failure("bot_not_spawned");

        if (mode == FollowMode.Follow)
        {
            var human = GetHumanPlayer();
            if (human == null)
                return AgentToolResult.Failure("human_player_unavailable");

            StartFollowIntent(human, now, movementAllowed, movementBlocker);
            var status = movementAllowed ? "started" : "suspended";
            Plugin.Logger.LogInfo(
                $"[AGENT] TOOL {AgentToolCatalog.SetFollowMode} mode=follow status={status}.");
            return AgentToolResult.Success(
                AgentToolCatalog.SetFollowMode,
                status,
                "follow");
        }

        Stop(now);
        Plugin.Logger.LogInfo(
            $"[AGENT] TOOL {AgentToolCatalog.SetFollowMode} mode=stay status=stopped.");
        return AgentToolResult.Success(
            AgentToolCatalog.SetFollowMode,
            "stopped",
            "stay");
    }

    private void StartFollowIntent(
        PlayerCharacter human,
        float now,
        bool movementAllowed,
        string movementBlocker)
    {
        _followRequested = true;
        _state = movementAllowed ? FollowState.Waiting : FollowState.Suspended;
        _suspensionReason = movementAllowed ? null : movementBlocker;
        _followAt = now;
        _nextNavigationTick = now;
        _trail.Clear();
        _trail.Add(human.transform.position, false, false);
        ResetTraversalState(human);
        _attention.SetTarget(
            GazeChannel.Follow,
            CompanionBody.HeadPositionOf(human));
        _locomotion.ResetProgressObservation(now);
    }

    /// <summary>
    /// Drops the follow intent without reporting a <c>set_follow_mode</c> result,
    /// so a general cancel can stop navigation without pretending the model
    /// asked for stay.
    /// </summary>
    internal void Stop(float now)
    {
        _followRequested = false;
        _suspensionReason = null;
        _followAt = float.PositiveInfinity;
        _jump.CancelFollow("follow stopped");
        _attention.ClearTarget(GazeChannel.Follow);
        StopForState(FollowState.Idle, now);
    }

    internal void SetMovementAllowed(bool allowed, float now, string movementBlocker)
    {
        if (!_followRequested)
            return;

        if (!allowed)
        {
            var nextReason = movementBlocker ?? "companion_action";
            var reasonChanged = !string.Equals(
                _suspensionReason,
                nextReason,
                StringComparison.Ordinal);
            // Stop only when follow first yields locomotion. Once suspended,
            // any live movement intent belongs to the action holding that
            // resource and must not be cleared by the follow behaviour.
            if (_state != FollowState.Suspended)
            {
                StopForState(FollowState.Suspended, now);
                _suspensionReason = nextReason;
                Plugin.Logger.LogInfo(
                    $"[FOLLOW] SUSPENDED by {_suspensionReason}.");
            }
            else if (reasonChanged)
            {
                _suspensionReason = nextReason;
                Plugin.Logger.LogInfo(
                    $"[FOLLOW] SUSPENSION_BLOCKER changed to {_suspensionReason}.");
            }
            return;
        }

        if (_state != FollowState.Suspended)
            return;

        _state = FollowState.Waiting;
        _followAt = now;
        _nextNavigationTick = now;
        _attention.ResumeAt(now);
        _locomotion.ResetProgressObservation(now);
        Plugin.Logger.LogInfo(
            $"[FOLLOW] RESUMED after {_suspensionReason ?? "companion_action"} cleared.");
        _suspensionReason = null;
    }

    private void BeginFollowing(float now)
    {
        var human = GetHumanPlayer();
        if (human == null)
        {
            Fail("local human player was unavailable when follow began");
            return;
        }

        _state = FollowState.Following;
        _followStartedAt = now;
        _nextStatusLog = now;
        _attention.ResumeAt(now);
        _locomotion.ResetProgressObservation(now);

        var bodyCollider = _body.Character.collision == null
            ? null
            : _body.Character.collision.bodyCollider;
        var obstacleMask = CompanionLocomotion.GetObstacleMask(_body.Character);
        Plugin.Logger.LogInfo(
            "[FOLLOW] START " +
            $"bot={_body.Position}, human={human.transform.position}, " +
            $"followDistance={FollowDistance:F2}, breadcrumbSpacing={BreadcrumbSpacing:F2}, " +
            $"walkSpeed={_locomotion.WalkSpeed:F2}, runSpeed={_locomotion.RunSpeed:F2}, " +
            $"gaitSpeedsFromTunings={_locomotion.GaitSpeedsFromTunings}, " +
            $"posture={CompanionPostureActuator.Describe(_locomotion.Posture)}, " +
            $"runStartDistance={CompanionLocomotion.RunStartDistance:F2}, runLatchesUntilStop=true, " +
            $"bodyTurnSpeed={CompanionFacing.BodyTurnSpeed:F0}, lookLimitsFromTunings=true, " +
            "verticalAwareTrail=true, jumpReplay=landing_outcome, dropReplay=true, " +
            "traversalLookahead=true, carryRebase=true, slopeAwareSteering=true, " +
            "walkableSweepFiltering=true, " +
            $"navigationHz={1f / NavigationInterval:F0}, obstacleMask={obstacleMask}, " +
            $"bodyRadius={(bodyCollider == null ? -1f : bodyCollider.radius):F2}, " +
            $"bodyHeight={(bodyCollider == null ? -1f : bodyCollider.height):F2}.");
    }

    private void NavigateTowardHuman(float now)
    {
        var human = GetHumanPlayer();
        if (human == null)
        {
            StopForState(FollowState.Blocked, now);
            Plugin.Logger.LogWarning("[FOLLOW] BLOCKED local human player is unavailable.");
            return;
        }

        var botPosition = _body.Position;
        var humanPosition = human.transform.position;
        var routeEndpoint = _humanJumpInProgress
            ? _humanJumpTakeoffPosition
            : humanPosition;
        _attention.SetTarget(
            GazeChannel.Follow,
            CompanionBody.HeadPositionOf(human));
        var humanDistance = Vector3.Distance(botPosition, humanPosition);
        var routeVerticalDistance = Mathf.Abs(routeEndpoint.y - botPosition.y);
        var routeDistanceBeforePruning = _trail.MeasureDistance(
            botPosition,
            routeEndpoint);
        var holdDistance = _state == FollowState.Holding
            ? ResumeDistance
            : FollowDistance;
        if (routeDistanceBeforePruning <= holdDistance &&
            routeVerticalDistance <= HoldingVerticalTolerance)
        {
            StopForState(FollowState.Holding, now);
            LogFollowStatusIfDue(now, humanDistance, 0f);
            return;
        }

        BreadcrumbPoint lastRemoved;
        bool crossedPointPlane;
        var removedBreadcrumbs = _trail.RemoveReached(
            botPosition,
            BreadcrumbArrivalTolerance,
            BreadcrumbArrivalVerticalTolerance,
            BreadcrumbPassLateralTolerance,
            IsBodyGrounded,
            _jumpCommittedSequence,
            _dropCommittedSequence,
            out lastRemoved,
            out crossedPointPlane);
        if (crossedPointPlane)
        {
            Plugin.Logger.LogInfo(
                "[FOLLOW] ROUTE_ADVANCE reason=passed_plane " +
                $"removed={removedBreadcrumbs}, last={lastRemoved.Sequence}, " +
                $"position={botPosition}, waypoint={lastRemoved.Position}, " +
                $"travelDirection={lastRemoved.TravelDirection}.");
        }

        var shortcutFirst = default(BreadcrumbPoint);
        var shortcutLast = default(BreadcrumbPoint);
        var shortcutRemoved = IsBodyGrounded
            ? _trail.RemoveThroughLatestNearby(
                botPosition,
                RouteShortcutHorizontalTolerance,
                BreadcrumbArrivalVerticalTolerance,
                _jumpCommittedSequence,
                _dropCommittedSequence,
                IsRouteShortcutTraversable,
                out shortcutFirst,
                out shortcutLast)
            : 0;
        if (shortcutRemoved > 0)
        {
            _directTraversalUntil = 0f;
            _committedTraversalDirection = Vector3.zero;
            _committedTraversalSequence = 0;
            _locomotion.ResetProgressObservation(now);
            Plugin.Logger.LogInfo(
                "[FOLLOW] ROUTE_SHORTCUT reason=later_breadcrumb_nearby " +
                $"removed={shortcutRemoved}, first={shortcutFirst.Sequence}, " +
                $"last={shortcutLast.Sequence}, remaining={_trail.Count}, " +
                $"position={botPosition}.");
        }
        if (_trail.Count == 0)
            _trail.Add(routeEndpoint, false, false);

        var breadcrumb = SelectTraversalLookahead(botPosition);
        SelectBreadcrumb(breadcrumb, now);
        _currentTarget = breadcrumb.Position;
        var toTarget = _currentTarget - botPosition;
        var targetVerticalDelta = toTarget.y;
        toTarget.y = 0f;
        var targetHorizontalDistance = toTarget.magnitude;
        _lastTargetHorizontalDistance = targetHorizontalDistance;
        var targetDistance = Vector3.Distance(botPosition, _currentTarget);
        var desiredDirection = ResolveRouteDirection(
            botPosition,
            routeEndpoint,
            breadcrumb,
            now);
        if (desiredDirection.sqrMagnitude < 0.0001f)
        {
            StopForState(FollowState.Blocked, now);
            Plugin.Logger.LogWarning(
                "[FOLLOW] BLOCKED route has vertical separation but no " +
                $"horizontal exit direction. target={_currentTarget}, " +
                $"verticalDelta={targetVerticalDelta:F2}.");
            return;
        }

        desiredDirection.Normalize();
        _lastRouteDirection = desiredDirection;
        var trailDistance = _trail.MeasureDistance(botPosition, routeEndpoint);
        _lastTrailDistance = trailDistance;

        var previousState = _state;
        var previousAngle = _locomotion.LastSteeringAngle;
        SteeringStatus status;
        if (now >= _directTraversalUntil)
        {
            _committedTraversalDirection = Vector3.zero;
            _committedTraversalSequence = 0;
        }
        var usedTraversalCommit = now < _directTraversalUntil;
        if (usedTraversalCommit && !IsBodyGrounded)
            _lastRouteMode = "airborne_commit";
        if (!usedTraversalCommit)
        {
            var jumpReason = breadcrumb.RequiresJump &&
                             breadcrumb.Sequence != _jumpCommittedSequence &&
                             targetHorizontalDistance <= JumpApproachDistance
                ? "recorded_human_jump"
                : targetVerticalDelta >= JumpRiseThreshold &&
                  targetHorizontalDistance <= JumpApproachDistance
                    ? "rising_breadcrumb"
                    : null;
            if (jumpReason != null &&
                TryCommitTraversalJump(
                    now,
                    breadcrumb,
                    jumpReason,
                    desiredDirection,
                    targetHorizontalDistance,
                    targetVerticalDelta))
            {
                usedTraversalCommit = true;
            }
        }

        var recordedDrop = breadcrumb.RequiresDrop &&
                           breadcrumb.Sequence != _dropCommittedSequence;
        if (IsBodyGrounded &&
            (recordedDrop || targetVerticalDelta <= -DropCommitDepth) &&
            targetHorizontalDistance <= DropCommitApproachDistance)
        {
            _directTraversalUntil = Mathf.Max(
                _directTraversalUntil,
                now + DropDirectionCommitSeconds);
            _committedTraversalDirection = desiredDirection;
            _committedTraversalSequence = breadcrumb.Sequence;
            usedTraversalCommit = true;
            if (_dropCommittedSequence != breadcrumb.Sequence)
            {
                _dropCommittedSequence = breadcrumb.Sequence;
                Plugin.Logger.LogInfo(
                    "[FOLLOW] DROP_COMMIT " +
                    $"breadcrumb={breadcrumb.Sequence}, " +
                    $"reason={(recordedDrop ? "recorded_human_drop" : "descending_breadcrumb")}, " +
                    $"horizontalDistance={targetHorizontalDistance:F2}, " +
                    $"verticalDelta={targetVerticalDelta:F2}, " +
                    $"travelDirection={breadcrumb.TravelDirection}.");
            }
        }

        if (usedTraversalCommit)
        {
            status = _locomotion.CommitTraversalDirection(
                _committedTraversalDirection,
                trailDistance);
        }
        else if (!_locomotion.TrySteerToward(
                     desiredDirection,
                     trailDistance,
                     now,
                     out status))
        {
            if (targetHorizontalDistance <= BlockedJumpApproachDistance &&
                targetVerticalDelta >= -BreadcrumbArrivalVerticalTolerance &&
                TryCommitTraversalJump(
                    now,
                    breadcrumb,
                    "blocked_route",
                    desiredDirection,
                    targetHorizontalDistance,
                    targetVerticalDelta))
            {
                usedTraversalCommit = true;
                status = _locomotion.CommitTraversalDirection(
                    desiredDirection,
                    trailDistance);
            }
            else
            {
                var stateChanged = _state != FollowState.Blocked;
                StopForState(FollowState.Blocked, now);
                if (stateChanged)
                {
                    Plugin.Logger.LogWarning(
                        "[FOLLOW] BLOCKED " +
                        $"target={_currentTarget}, targetDistance={targetDistance:F2}, " +
                        $"targetVerticalDelta={targetVerticalDelta:F2}, " +
                        $"humanDistance={humanDistance:F2}; no steering candidate had " +
                        $"{CompanionLocomotion.MinimumClearance:F2}m clearance and " +
                        "no grounded traversal jump was available. No teleport attempted.");
                }
                LogFollowStatusIfDue(now, humanDistance, targetDistance);
                return;
            }
        }

        _state = FollowState.Following;
        if (!usedTraversalCommit &&
            status.DirectPathBlocked &&
            (previousState != FollowState.Following ||
             Mathf.Abs(previousAngle - status.SteeringAngle) >= 1f))
        {
            Plugin.Logger.LogInfo(
                "[FOLLOW] AVOID " +
                $"steeringAngle={status.SteeringAngle:F0}, clearance={status.Clearance:F2}, " +
                $"groundLimited={status.DirectGroundLimited}, " +
                $"groundResponse={status.GroundResponse:F2}, steepScalar={status.SteepScalar:F2}, " +
                $"target={_currentTarget}, targetDistance={targetDistance:F2}.");
        }
        else if (!status.DirectPathBlocked && previousState == FollowState.Blocked)
        {
            Plugin.Logger.LogInfo("[FOLLOW] Path clear; resuming breadcrumb follow.");
        }

        var stuck = _locomotion.ObserveProgress(now);
        if (stuck &&
            targetVerticalDelta >= -BreadcrumbArrivalVerticalTolerance &&
            TryCommitTraversalJump(
                now,
                breadcrumb,
                "stuck_recovery",
                desiredDirection,
                targetHorizontalDistance,
                targetVerticalDelta))
        {
            _locomotion.CommitTraversalDirection(
                desiredDirection,
                trailDistance);
            _locomotion.ResetProgressObservation(now);
        }
        LogFollowStatusIfDue(now, humanDistance, targetDistance);
    }

    private bool IsRouteShortcutTraversable(Vector3 point)
    {
        if (_body == null || !_body.IsAlive)
            return false;
        return _locomotion.CanShortcutSegment(point);
    }

    private BreadcrumbPoint SelectTraversalLookahead(Vector3 botPosition)
    {
        var current = _trail.Peek();
        if (current.RequiresJump || current.RequiresDrop)
            return current;

        BreadcrumbPoint next;
        if (!_trail.TryPeek(1, out next) ||
            (!next.RequiresJump && !next.RequiresDrop))
        {
            return current;
        }

        var horizontalDistance = BreadcrumbTrail.HorizontalDistance(
            botPosition,
            current.Position);
        var verticalDistance = Mathf.Abs(botPosition.y - current.Position.y);
        if (horizontalDistance > TraversalLookaheadDistance ||
            verticalDistance > TraversalLookaheadVerticalTolerance)
        {
            return current;
        }

        BreadcrumbPoint skipped;
        if (!_trail.TryRemoveFirst(out skipped))
            return current;

        Plugin.Logger.LogInfo(
            "[FOLLOW] TRAVERSAL_LOOKAHEAD " +
            $"skipped={skipped.Sequence}, selected={next.Sequence}, " +
            $"kind={(next.RequiresJump ? "jump" : "drop")}, " +
            $"approachDistance={horizontalDistance:F2}, " +
            $"travelDirection={next.TravelDirection}.");
        return next;
    }

    private Vector3 ResolveRouteDirection(
        Vector3 botPosition,
        Vector3 humanPosition,
        BreadcrumbPoint breadcrumb,
        float now)
    {
        var traversalCommitted =
            (breadcrumb.RequiresJump &&
             breadcrumb.Sequence == _jumpCommittedSequence) ||
            (breadcrumb.RequiresDrop &&
             breadcrumb.Sequence == _dropCommittedSequence);
        bool usingTravelDirection;
        var direction = BreadcrumbTrail.ResolveTraversalApproachDirection(
            botPosition,
            breadcrumb,
            traversalCommitted,
            traversalCommitted && now < _directTraversalUntil,
            JumpApproachDistance,
            DropCommitApproachDistance,
            out usingTravelDirection);
        _lastRouteMode = usingTravelDirection
            ? traversalCommitted ? "transition_commit" : "transition_tangent"
            : breadcrumb.RequiresJump || breadcrumb.RequiresDrop
                ? "transition_approach"
                : "waypoint";
        if (usingTravelDirection && !traversalCommitted)
        {
            var tangentDistance = BreadcrumbTrail.HorizontalDistance(
                botPosition,
                breadcrumb.Position);
            var tangentCorridor = breadcrumb.RequiresDrop
                ? DropCommitApproachDistance
                : JumpApproachDistance;
            Plugin.Logger.LogInfo(
                "[FOLLOW] ROUTE_TANGENT " +
                $"breadcrumb={breadcrumb.Sequence}, " +
                $"kind={(breadcrumb.RequiresDrop ? "drop" : "jump")}, " +
                $"horizontalDistance={tangentDistance.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}, " +
                $"corridor={tangentCorridor.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}.");
        }
        if (direction.sqrMagnitude >= 0.0001f)
            return direction;

        BreadcrumbPoint next;
        if (_trail.TryPeek(1, out next))
        {
            direction = next.Position - botPosition;
            direction.y = 0f;
            if (direction.sqrMagnitude >= 0.0001f)
            {
                _lastRouteMode = "lookahead";
                return direction;
            }
        }

        if (_lastRouteDirection.sqrMagnitude >= 0.0001f)
        {
            _lastRouteMode = "last_direction";
            return _lastRouteDirection;
        }

        direction = humanPosition - botPosition;
        direction.y = 0f;
        if (direction.sqrMagnitude >= 0.0001f)
        {
            _lastRouteMode = "human_fallback";
            return direction;
        }

        _lastRouteMode = "body_fallback";
        direction = _body.Transform.forward;
        direction.y = 0f;
        return direction;
    }

    private void SelectBreadcrumb(BreadcrumbPoint breadcrumb, float now)
    {
        if (_currentBreadcrumbSequence == breadcrumb.Sequence)
        {
            if (BreadcrumbTrail.ShouldReleasePriorTraversalCommit(
                    breadcrumb.Sequence,
                    _committedTraversalSequence,
                    IsBodyGrounded))
            {
                Plugin.Logger.LogInfo(
                    "[FOLLOW] TRAVERSAL_COMMIT_RELEASED " +
                    $"reason=landed_after_target_change, " +
                    $"owner={_committedTraversalSequence}, " +
                    $"target={breadcrumb.Sequence}.");
                _directTraversalUntil = 0f;
                _committedTraversalDirection = Vector3.zero;
                _committedTraversalSequence = 0;
            }
            return;
        }

        _currentBreadcrumbSequence = breadcrumb.Sequence;
        // A jump/drop marker can be retired while its body is still airborne.
        // Keep that exact bounded direction through landing, but never let a
        // grounded follower carry an old marker's tangent into the next target.
        var preserveAirborneCommit = !IsBodyGrounded &&
                                     now < _directTraversalUntil &&
                                     _committedTraversalDirection.sqrMagnitude >= 0.0001f;
        if (!preserveAirborneCommit)
        {
            _directTraversalUntil = 0f;
            _committedTraversalDirection = Vector3.zero;
            _committedTraversalSequence = 0;
        }
        _jumpCommittedSequence = 0;
        _jumpAttemptsForBreadcrumb = 0;
        _dropCommittedSequence = 0;
    }

    private bool TryCommitTraversalJump(
        float now,
        BreadcrumbPoint breadcrumb,
        string reason,
        Vector3 direction,
        float horizontalDistance,
        float verticalDelta)
    {
        if (_jumpAttemptsForBreadcrumb >= MaximumJumpAttemptsPerBreadcrumb)
            return false;

        string error;
        if (!_jump.TryRequestTraversal(
                now,
                _locomotion.Posture,
                reason,
                out error))
        {
            return false;
        }

        _jumpAttemptsForBreadcrumb++;
        _jumpCommittedSequence = breadcrumb.Sequence;
        var recovery = string.Equals(
                           reason,
                           "blocked_route",
                           StringComparison.Ordinal) ||
                       string.Equals(
                           reason,
                           "stuck_recovery",
                           StringComparison.Ordinal);
        var commitSeconds = recovery
            ? RecoveryDirectionCommitSeconds
            : TraversalDirectionCommitSeconds;
        _directTraversalUntil = now + commitSeconds;
        direction.y = 0f;
        _committedTraversalDirection = direction.sqrMagnitude < 0.0001f
            ? breadcrumb.TravelDirection
            : direction.normalized;
        _committedTraversalSequence = breadcrumb.Sequence;
        _locomotion.ResetProgressObservation(now);
        Plugin.Logger.LogInfo(
            "[FOLLOW] JUMP_COMMIT " +
            $"breadcrumb={breadcrumb.Sequence}, reason={reason}, " +
            $"attempt={_jumpAttemptsForBreadcrumb}, " +
            $"horizontalDistance={horizontalDistance:F2}, " +
            $"verticalDelta={verticalDelta:F2}, " +
            $"commitSeconds={commitSeconds:F2}.");
        return true;
    }

    private bool IsBodyGrounded =>
        _body?.Character?.ground != null &&
        _body.Character.ground.isGrounded;

    private void ObserveHumanTraversal()
    {
        var human = GetHumanPlayer();
        if (human == null)
            return;

        var grounded = human.ground != null && human.ground.isGrounded;
        var justJumped = human.jumper != null && human.jumper.justJumped;
        var position = human.transform.position;
        var verticalVelocity = human.rb == null
            ? 0f
            : human.rb.linearVelocity.y;

        if (_humanJumpInProgress)
        {
            _humanJumpPeakY = Mathf.Max(_humanJumpPeakY, position.y);
            if (grounded && !_humanWasGrounded)
            {
                RecordHumanJumpLanding(position);
                _humanJumpInProgress = false;
            }
        }
        else if (_humanWasGrounded && !grounded)
        {
            var startedJump = justJumped || verticalVelocity > 0.25f;
            if (startedJump)
            {
                _humanJumpInProgress = true;
                _humanJumpTakeoffPosition = position;
                _humanJumpPeakY = position.y;
                _pendingHumanDrop = false;
            }
            else
            {
                _pendingHumanDrop = true;
            }
        }

        _humanWasGrounded = grounded;
    }

    private void RecordHumanJumpLanding(Vector3 landingPosition)
    {
        var landingRise = landingPosition.y - _humanJumpTakeoffPosition.y;
        var peakRise = _humanJumpPeakY - _humanJumpTakeoffPosition.y;
        var horizontalTravel = BreadcrumbTrail.HorizontalDistance(
            _humanJumpTakeoffPosition,
            landingPosition);
        var requiresJump = landingRise >= MeaningfulJumpLandingRise;

        BreadcrumbPoint lastAdded;
        var hasLast = _trail.TryGetLastAdded(out lastAdded);
        var distanceFromTrail = hasLast
            ? Vector3.Distance(landingPosition, lastAdded.Position)
            : float.PositiveInfinity;
        BreadcrumbPoint added = default(BreadcrumbPoint);
        var addedLanding = requiresJump || distanceFromTrail >= BreadcrumbSpacing;
        if (addedLanding)
            added = _trail.Add(landingPosition, requiresJump, false);

        if (requiresJump)
        {
            Plugin.Logger.LogInfo(
                "[FOLLOW] TRAIL_JUMP_RETAINED " +
                $"breadcrumb={added.Sequence}, landingRise={landingRise:F2}, " +
                $"peakRise={peakRise:F2}, horizontalTravel={horizontalTravel:F2}, " +
                $"takeoff={_humanJumpTakeoffPosition}, landing={landingPosition}.");
        }
        else
        {
            Plugin.Logger.LogInfo(
                "[FOLLOW] TRAIL_JUMP_IGNORED reason=same_level_landing " +
                $"landingRise={landingRise:F2}, peakRise={peakRise:F2}, " +
                $"horizontalTravel={horizontalTravel:F2}, landingRecorded={addedLanding}.");
        }
    }

    private void ResetTraversalState(PlayerCharacter human)
    {
        _humanWasGrounded = human?.ground != null && human.ground.isGrounded;
        _humanJumpInProgress = false;
        _humanJumpTakeoffPosition = human == null
            ? Vector3.zero
            : human.transform.position;
        _humanJumpPeakY = _humanJumpTakeoffPosition.y;
        _pendingHumanDrop = false;
        _currentBreadcrumbSequence = 0;
        _jumpCommittedSequence = 0;
        _jumpAttemptsForBreadcrumb = 0;
        _dropCommittedSequence = 0;
        _directTraversalUntil = 0f;
        _committedTraversalDirection = Vector3.zero;
        _committedTraversalSequence = 0;
        _lastRouteDirection = Vector3.zero;
        _lastRouteMode = "waypoint";
        _lastTargetHorizontalDistance = 0f;
    }

    private void RecordHumanTrail()
    {
        var human = GetHumanPlayer();
        if (human == null)
            return;
        if (_humanJumpInProgress)
            return;

        var position = human.transform.position;
        BreadcrumbPoint lastAdded;
        if (!_trail.TryGetLastAdded(out lastAdded))
        {
            _trail.Add(position, false, false);
            _pendingHumanDrop = false;
            return;
        }

        var distance = Vector3.Distance(position, lastAdded.Position);
        if (distance >= TrailResetDistance)
        {
            _trail.Clear();
            _trail.Add(position, false, false);
            _pendingHumanDrop = false;
            Plugin.Logger.LogWarning(
                "[FOLLOW] TRAIL_RESET " +
                $"human moved {distance:F2}m between samples; refusing to invent a traversable segment.");
            return;
        }

        if (distance < BreadcrumbSpacing)
            return;

        var added = _trail.Add(
            position,
            false,
            _pendingHumanDrop);
        if (_pendingHumanDrop)
        {
            Plugin.Logger.LogInfo(
                "[FOLLOW] TRAIL_DROP " +
                $"breadcrumb={added.Sequence}, position={added.Position}, " +
                $"travelDirection={added.TravelDirection}.");
        }
        _pendingHumanDrop = false;
    }

    private bool UpdateCarryState(float now)
    {
        var human = GetHumanPlayer();
        var isCarried = IsHumanCarryingBody(_body, human);
        var isCarryingHuman = IsBodyCarryingHuman(_body, human);
        if (isCarried == _bodyIsCarried &&
            isCarryingHuman == _bodyCarriesHuman)
        {
            return isCarried || isCarryingHuman;
        }

        var wasCarryingHuman = _bodyCarriesHuman;
        var wasSuspended = _state == FollowState.Suspended;
        _bodyIsCarried = isCarried;
        _bodyCarriesHuman = isCarryingHuman;
        if (isCarried || isCarryingHuman)
        {
            var discardedBreadcrumbs = _trail.Count;
            _trail.Clear();
            _humanJumpInProgress = false;
            _pendingHumanDrop = false;
            _jump.CancelFollow(isCarried
                ? "companion was picked up"
                : "companion picked up the human");
            var carriedState = isCarried
                ? FollowState.Carried
                : FollowState.Suspended;
            if (!wasSuspended)
            {
                StopForState(carriedState, now);
            }
            else
            {
                // Follow had already yielded locomotion to a job. Update only
                // follow-owned state; the active job retains its movement and
                // any action-owned recovery jump.
                _directTraversalUntil = 0f;
                _committedTraversalDirection = Vector3.zero;
                _committedTraversalSequence = 0;
                _state = carriedState;
            }
            _attention.ClearTarget(GazeChannel.Follow);
            _suspensionReason = isCarryingHuman
                ? "carrying_player"
                : null;
            Plugin.Logger.LogInfo(
                (isCarried
                    ? "[FOLLOW] CARRY_STARTED carrier=local_human, "
                    : "[FOLLOW] PLAYER_CARRY_STARTED carrier=companion, ") +
                $"discardedBreadcrumbs={discardedBreadcrumbs}, " +
                "followIntentRetained=true, movementPaused=true.");
            return true;
        }

        if (_followRequested && human != null)
        {
            _trail.Clear();
            _trail.Add(human.transform.position, false, false);
            ResetTraversalState(human);
            // Keep the intent suspended until the coordinator re-evaluates its
            // live movement gate. A directed movement job may still own
            // locomotion at the exact frame a carry link clears.
            _state = FollowState.Suspended;
            _suspensionReason = wasCarryingHuman
                ? "carrying_player"
                : "carried";
        }
        else
        {
            _trail.Clear();
            ResetTraversalState(human);
        }

        Plugin.Logger.LogInfo(
            (wasCarryingHuman
                ? "[FOLLOW] PLAYER_CARRY_RELEASED "
                : "[FOLLOW] CARRY_RELEASED ") +
            $"bot={_body.Position}, human={human?.transform.position}, " +
            $"routeRebased={_followRequested && human != null}.");
        return false;
    }

    /// <summary>
    /// Exact inverse of the ordinary carry check: the companion's stock hands
    /// currently hold the controller-bound human. Follow intent remains latent
    /// during this relationship so the attached passenger cannot become a
    /// self-generated breadcrumb route.
    /// </summary>
    internal static bool IsBodyCarryingHuman(
        CompanionBody body,
        PlayerCharacter human)
    {
        if (body == null || !body.IsAlive || human == null ||
            human.gameObject == body.GameObject)
        {
            return false;
        }

        var grabPose = body.Character?.registry?.grabPose;
        var poser = human.poser;
        return body.Character?.hands?.heldCharacter == human ||
               (poser != null && poser.playerHoldingMe == body.Character) ||
               (grabPose != null && poser != null &&
                poser.currentPose == grabPose) ||
               (grabPose != null && grabPose.occupant == human);
    }

    /// <summary>
    /// The corresponding exact native relationship when the controller-bound
    /// human carries the companion. All stock links participate so navigation
    /// cannot resume during a partial pose teardown.
    /// </summary>
    internal static bool IsHumanCarryingBody(
        CompanionBody body,
        PlayerCharacter human)
    {
        if (body == null || !body.IsAlive || human == null ||
            human.gameObject == body.GameObject)
        {
            return false;
        }

        var grabPose = human.registry?.grabPose;
        var poser = body.Character?.poser;
        return human.hands?.heldCharacter == body.Character ||
               (poser != null && poser.playerHoldingMe == human) ||
               (grabPose != null && poser != null &&
                poser.currentPose == grabPose) ||
               (grabPose != null && grabPose.occupant == body.Character);
    }

    private PlayerCharacter GetHumanPlayer()
    {
        var human = WorldManager.localPlayerCharacter;
        if (human == null)
            human = _humanAtSpawn;
        if (human == null || (_body != null && human.gameObject == _body.GameObject))
            return null;
        return human;
    }

    private void StopForState(FollowState state, float now)
    {
        _locomotion.Stop(now);
        if (state != FollowState.Following)
        {
            _directTraversalUntil = 0f;
            _committedTraversalDirection = Vector3.zero;
            _committedTraversalSequence = 0;
        }
        _state = state;
    }

    internal void Fail(string reason)
    {
        if (_state == FollowState.Failed)
            return;

        _state = FollowState.Failed;
        _attention.ClearTarget(GazeChannel.Follow);
        _locomotion.StopQuietly();
        Plugin.Logger.LogError($"[FOLLOW] FAILED {reason}");
    }

    private void LogFollowStatusIfDue(
        float now,
        float humanDistance,
        float targetDistance)
    {
        if (now < _nextStatusLog)
            return;

        _nextStatusLog = now + StatusLogInterval;
        var rigidbody = _body.Character.rb;
        var rigidbodyVelocity = rigidbody == null
            ? Vector3.zero
            : rigidbody.linearVelocity;
        var ground = _body.Character.ground;
        var groundNormal = ground == null ? Vector3.zero : ground.normal;
        var groundSteepness = ground == null ? -1f : ground.GetSteepness();
        var correctedIntent = _body.Character.mover == null
            ? Vector3.zero
            : _body.Character.mover.correctedControlsVelocity;
        var sitting = _body.Character.sitter != null &&
                      _body.Character.sitter.isSittingCorrected;
        var rigidbodySleeping = rigidbody != null && rigidbody.IsSleeping();
        var human = WorldManager.localPlayerCharacter;
        var hostStillLocal = human != null &&
                             human.gameObject != _body.GameObject &&
                             human.playerNetworking != null &&
                             human.playerNetworking.isLocalPlayer;

        Plugin.Logger.LogInfo(
            "[FOLLOW] STATUS " +
            $"state={_state}, elapsed={now - _followStartedAt:F2}, " +
            $"position={_body.Position}, humanDistance={humanDistance:F2}, " +
            $"target={_currentTarget}, targetDistance={targetDistance:F2}, " +
            $"targetHorizontalDistance={_lastTargetHorizontalDistance:F2}, " +
            $"trailDistance={_lastTrailDistance:F2}, " +
            $"commandedSpeed={_locomotion.LastCommandedSpeed:F2}, " +
            $"gait={_locomotion.DescribeGait()}, posture={CompanionPostureActuator.Describe(_locomotion.Posture)}, " +
            $"bodyYaw={_attention.LastBodyYaw:F1}, targetYaw={_attention.LastTargetYaw:F1}, " +
            $"headState={_attention.HeadState}, breadcrumbs={_trail.Count}, " +
            $"breadcrumb={_currentBreadcrumbSequence}, " +
            $"routeMode={_lastRouteMode}, " +
            $"jumpTarget={_jumpCommittedSequence}, " +
            $"dropTarget={_dropCommittedSequence}, carried={_bodyIsCarried}, " +
            $"humanJumpInProgress={_humanJumpInProgress}, " +
            $"bodyGrounded={IsBodyGrounded}, " +
            $"jumpable={ground != null && ground.isOnJumpableGround}, " +
            $"groundNormal={groundNormal}, groundSteepness={groundSteepness:F2}, " +
            $"directBlocked={_locomotion.LastDirectPathBlocked}, " +
            $"groundLimited={_locomotion.LastDirectGroundLimited}, " +
            $"groundResponse={_locomotion.LastGroundResponse:F2}, " +
            $"slopeResponse={_locomotion.LastSlopeResponse:F2}, " +
            $"steeringAuthority={_locomotion.LastSteeringAuthority}, " +
            $"steepScalar={_locomotion.LastSteepScalar:F2}, " +
            $"steeringAngle={_locomotion.LastSteeringAngle:F0}, " +
            $"clearance={_locomotion.LastClearance:F2}, " +
            $"directHit={_locomotion.LastDirectHit}, " +
            $"probes={_locomotion.LastProbeSummary}, " +
            $"intent={_locomotion.LastMovementIntent}, rigidbodyVelocity={rigidbodyVelocity}, " +
            $"networkIntent={_body.Networking.controlsVelocity}, correctedIntent={correctedIntent}, " +
            $"sitting={sitting}, rigidbodySleeping={rigidbodySleeping}, " +
            $"botIsLocalPlayer={_body.Networking.isLocalPlayer}, hostStillLocal={hostStillLocal}.");
    }

    internal void Release()
    {
        _body = null;
        _humanAtSpawn = null;
        _followRequested = false;
        _bodyIsCarried = false;
        _bodyCarriesHuman = false;
        _suspensionReason = null;
        _state = FollowState.Idle;
        _lastTrailDistance = 0f;
        _trail.Clear();
        ResetTraversalState(null);
        _attention.ClearTarget(GazeChannel.Follow);
    }
}
