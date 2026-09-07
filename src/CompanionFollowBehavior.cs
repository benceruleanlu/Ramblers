using System;
using Mirror;
using UnityEngine;

namespace Ramblers;

internal sealed class CompanionFollowBehavior
{
    internal const float NavigationInterval = 0.1f;

    private const float TrailSampleInterval = 0.1f;
    private const float BreadcrumbSpacing = 0.65f;
    private const float FollowDistance = 2.25f;
    private const float ResumeDistance = 2.5f;
    private const float HoldingVerticalTolerance = 1.0f;
    private const float TrailResetDistance = 8f;
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
    private readonly CompanionTraversalRecorder _traversalRecorder = new CompanionTraversalRecorder();
    private readonly CompanionTraversalReplay _replay = new CompanionTraversalReplay();
    private readonly CompanionFollowNavigation _navigation;
    private readonly CompanionFollowRoutePlanner _routePlanner;
    private int _routeRevision;
    private BreadcrumbPoint _activeTraversalPoint;

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
    private bool _humanJumpInProgress;
    private Vector3 _humanJumpTakeoffPosition;
    private bool _bodyIsCarried;
    private bool _bodyCarriesHuman;
    private int _currentBreadcrumbSequence;
    private int _jumpCommittedSequence;
    private int _dropCommittedSequence;
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
        _navigation = new CompanionFollowNavigation(
            candidate => _locomotion.Geometry.TryGroundPoint(candidate, out var grounded)
                ? (Vector3?)grounded : null,
            (from, to) => !_locomotion.Geometry.QueryBudgetExhausted &&
                _locomotion.Geometry.CanWalkSegment(from, to),
            (from, to) => _locomotion.Geometry.IsSegmentClear(from, to),
            () => !_locomotion.Geometry.QueryBudgetExhausted);
        _routePlanner = new CompanionFollowRoutePlanner(
            candidate => !_locomotion.Geometry.QueryBudgetExhausted &&
                _locomotion.Geometry.TryGroundPoint(candidate, out var grounded)
                ? (Vector3?)grounded : null,
            (from, to) => !_locomotion.Geometry.QueryBudgetExhausted &&
                _navigation.IsRouteSegmentAvailable(from, to),
            () => !_locomotion.Geometry.QueryBudgetExhausted);
    }

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

        if (Plugin.NavigationReproEnabled)
            CompanionNavigationReproProbe.Run(_locomotion.Geometry, _body.Position);

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

        ObserveHumanTraversal(now);
        if (now < _nextTrailSample)
            return;
        _nextTrailSample = now + TrailSampleInterval;
        RecordHumanTrail();
    }

    internal void TickFixed(float now, bool movementAllowed, string movementBlocker)
    {
        if (_body == null || !_body.IsAlive)
            return;

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
        _trail.Add(WalkingPosition(human.transform.position), false, false);
        ResetTraversalState(human);
        _attention.SetTarget(
            GazeChannel.Follow,
            CompanionBody.HeadPositionOf(human));
        _locomotion.ResetProgressObservation(now);
    }

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

    internal void RebaseAfterExternalReposition(
        PlayerCharacter human,
        float now,
        bool movementAllowed,
        string movementBlocker)
    {
        _locomotion.StopQuietly();
        _jump.CancelFollow("external reposition");
        _bodyIsCarried = false;
        _bodyCarriesHuman = false;
        _trail.Clear();
        _nextTrailSample = now + TrailSampleInterval;
        ResetTraversalState(human);

        if (!_followRequested || human == null)
        {
            Plugin.Logger.LogInfo(
                "[FOLLOW] EXTERNAL_REPOSITION_REBASED " +
                $"followRequested={_followRequested}, breadcrumbs=0.");
            return;
        }

        _trail.Add(WalkingPosition(human.transform.position), false, false);
        _state = movementAllowed ? FollowState.Waiting : FollowState.Suspended;
        _suspensionReason = movementAllowed ? null : movementBlocker ?? "companion_action";
        _followAt = now;
        _nextNavigationTick = now;
        _attention.SetTarget(GazeChannel.Follow, CompanionBody.HeadPositionOf(human));
        _attention.ResumeAt(now);
        _locomotion.ResetProgressObservation(now);
        Plugin.Logger.LogInfo(
            "[FOLLOW] EXTERNAL_REPOSITION_REBASED " +
            $"followRequested=true, movementAllowed={movementAllowed}, breadcrumbs=1.");
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
            $"runStartDistance={CompanionLocomotion.RunStartDistance:F2}, recordedTraversalPace=true, " +
            $"bodyTurnSpeed={CompanionFacing.BodyTurnSpeed:F0}, lookLimitsFromTunings=true, " +
            "followTarget=current_human, trailRole=route_memory, jumpPolicy=terrain_needed, " +
            "localRouteSearch=true, failedDirectionMemory=true, carryRebase=true, " +
            "allHitCollisionQueries=true, footprintSupport=true, " +
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
            return;
        }

        var position = _body.Position;
        var humanGoal = ResolveHumanFollowPosition(human);
        var humanDistance = Vector3.Distance(position, human.transform.position);
        _attention.SetTarget(GazeChannel.Follow, CompanionBody.HeadPositionOf(human));
        _lastTrailDistance = humanDistance;

        if (_replay.Active && TryReplayTraversal(_activeTraversalPoint, now))
        {
            LogFollowStatusIfDue(now, humanDistance, Vector3.Distance(position, _currentTarget));
            return;
        }

        var holdDistance = _state == FollowState.Holding ? ResumeDistance : FollowDistance;
        if (BreadcrumbTrail.HorizontalDistance(position, humanGoal) <= holdDistance &&
            Mathf.Abs(humanGoal.y - position.y) <= HoldingVerticalTolerance &&
            _locomotion.Geometry.IsSegmentClear(position, humanGoal))
        {
            if (_state != FollowState.Holding)
            {
                _trail.Clear();
                _trail.Add(humanGoal, false, false);
                _routePlanner.Reset();
            }
            _currentTarget = humanGoal;
            _currentBreadcrumbSequence = 0;
            _lastRouteMode = "human:holding";
            StopForState(FollowState.Holding, now);
            LogFollowStatusIfDue(now, humanDistance, humanDistance);
            return;
        }

        CompanionFollowRoute route = null;
        var queriesBefore = _locomotion.Geometry.NativeQueryCount;
        _locomotion.Geometry.RunWithQueryBudget(1200, () =>
            route = _routePlanner.Select(position, humanGoal, _trail, now));
        _lastTrailDistance = route.RemainingDistance;
        _currentTarget = route.Destination;
        _currentBreadcrumbSequence = route.Hint.Sequence;
        if (_routeRevision != route.Revision)
        {
            _routeRevision = route.Revision;
            _navigation.AcceptRoute(route.Destination, route.Hint.Sequence, route.Waypoints, now,
                route.SearchPending);
            if (Plugin.FollowDiagnosticsEnabled)
            {
                Plugin.Logger.LogInfo("[FOLLOW] ROUTE_SELECTED " +
                    $"goal={humanGoal}, destination={route.Destination}, kind={route.Kind}, " +
                    $"hint={route.Hint.Sequence}, waypoints={route.Waypoints.Length}, " +
                    $"remaining={route.RemainingDistance:F2}, " +
                    $"searchPending={route.SearchPending}, " +
                    $"nativeQueries={_locomotion.Geometry.NativeQueryCount - queriesBefore}.");
            }
        }
        if (route.Kind == CompanionFollowRouteKind.Traversal && TryReplayTraversal(route.Hint, now))
        {
            LogFollowStatusIfDue(now, humanDistance, Vector3.Distance(position, _currentTarget));
            return;
        }
        MoveToward(route.Destination, route.Hint.Sequence, humanDistance, now,
            route.Kind.ToString().ToLowerInvariant(), route.SearchPending);
    }
    private Vector3 ResolveHumanFollowPosition(PlayerCharacter human)
    {
        var position = human.transform.position;
        if (human.ground != null && human.ground.isGrounded)
            return WalkingPosition(position);
        if (_locomotion.Geometry.TryGroundPoint(position, out var supported))
            return supported;
        if (_humanJumpInProgress)
            position.y = _humanJumpTakeoffPosition.y;
        return position;
    }

    private Vector3 WalkingPosition(Vector3 position)
    {
        return _locomotion.Geometry.TryGroundRoutePoint(position, out var supported)
            ? supported : position;
    }

    private void MoveToward(Vector3 destination, int sequence, float humanDistance, float now, string source,
        bool searchPending)
    {
        var position = _body.Position;
        _lastTargetHorizontalDistance = BreadcrumbTrail.HorizontalDistance(position, destination);
        var previousPlan = _navigation.PlanCount;
        var queriesBefore = _locomotion.Geometry.NativeQueryCount;
        var navigationStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        var step = default(CompanionNavigationStep);
        _locomotion.Geometry.RunWithQueryBudget(384, () =>
            step = _navigation.Tick(position, destination, sequence, IsBodyGrounded, now));
        var navigationMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - navigationStarted) *
                                     1000.0 / System.Diagnostics.Stopwatch.Frequency;
        var nativeQueries = _locomotion.Geometry.NativeQueryCount - queriesBefore;
        if (step.GoalStalled && !searchPending)
        {
            _routePlanner.ReportFailure(now);
            Plugin.Logger.LogInfo("[FOLLOW] ROUTE_RECONSIDERED " +
                $"reason=no_progress, destination={destination}, hint={sequence}.");
        }
        _lastRouteDirection = step.Direction;
        _lastRouteMode = source + ":" + step.Mode;
        _locomotion.MoveAlongRoute(step.Direction, _lastTrailDistance, now);
        _state = FollowState.Following;
        if (step.RequestJump)
            RequestTraversalJump(now, "navigation_recovery");
        if ((Plugin.FollowDiagnosticsEnabled && previousPlan != _navigation.PlanCount) ||
            step.Stalled || step.GoalStalled)
        {
            Plugin.Logger.LogInfo("[FOLLOW] NAVIGATION " +
                $"source={source}, mode={step.Mode}, stalled={step.Stalled}, target={destination}, " +
                $"goalStalled={step.GoalStalled}, goalStalls={_navigation.GoalStallCount}, " +
                $"plan={_navigation.PlanStatus}, nodes={_navigation.ExpandedNodes}, " +
                $"queries={_navigation.QueryCount}, remaining={_navigation.WaypointsRemaining}, " +
                $"nativeQueries={nativeQueries}, navigationMs={navigationMilliseconds:F2}, " +
                $"failedDirections={_navigation.RememberedFailures}, direction={step.Direction}.");
        }
        LogFollowStatusIfDue(now, humanDistance, Vector3.Distance(position, destination));
    }

    private bool TryReplayTraversal(BreadcrumbPoint point, float now)
    {
        var wasActive = _replay.Active;
        var committed = _replay.Tick(point, _body.Position, IsBodyGrounded, now,
            () => RequestTraversalJump(now, "terrain_traversal"), out var direction);
        _jumpCommittedSequence = _replay.JumpCommittedSequence;
        _dropCommittedSequence = _replay.DropCommittedSequence;
        if (!committed)
        {
            var completed = _jumpCommittedSequence == point.Sequence ||
                            _dropCommittedSequence == point.Sequence;
            if (completed)
            {
                _trail.RemoveThrough(point.Sequence);
                _routePlanner.Reset();
                _locomotion.Stop(now);
            }
            else if (wasActive)
            {
                _routePlanner.Reconsider();
            }
            if (wasActive)
            {
                Plugin.Logger.LogInfo("[FOLLOW] TRAVERSAL_SETTLED " +
                    $"outcome={(completed ? "landed" : "retry")}, " +
                    $"breadcrumb={point.Sequence}, jump={_jumpCommittedSequence}, drop={_dropCommittedSequence}, " +
                    $"position={_body.Position}, landing={point.Position}.");
            }
            return completed;
        }

        _activeTraversalPoint = point;
        _currentTarget = point.Position;
        _currentBreadcrumbSequence = point.Sequence;
        _lastTargetHorizontalDistance = BreadcrumbTrail.HorizontalDistance(_body.Position, point.Position);
        _lastRouteDirection = direction;
        _lastRouteMode = IsBodyGrounded ? "traversal_launch" : "traversal_airborne";
        _navigation.Pause();
        var recordedSpeed = point.TraversalDuration > 0.05f
            ? BreadcrumbTrail.HorizontalDistance(point.TakeoffPosition, point.Position) /
              point.TraversalDuration : 0f;
        _locomotion.CommitTraversalDirection(direction, _lastTrailDistance, recordedSpeed);
        _state = FollowState.Following;
        if (!wasActive)
        {
            Plugin.Logger.LogInfo("[FOLLOW] TRAVERSAL_LAUNCH " +
                $"breadcrumb={point.Sequence}, kind={(point.RequiresJump ? "jump" : "drop")}, " +
                $"takeoff={point.TakeoffPosition}, landing={point.Position}, direction={direction}, " +
                $"recordedSpeed={recordedSpeed:F2}, commandedSpeed={_locomotion.LastCommandedSpeed:F2}.");
        }
        return true;
    }

    private bool RequestTraversalJump(float now, string reason)
    {
        return _jump.TryRequestTraversal(now, _locomotion.Posture, reason, out var error);
    }

    private bool IsBodyGrounded =>
        _body?.Character?.ground != null &&
        _body.Character.ground.isGrounded;

    private void ObserveHumanTraversal(float now)
    {
        var human = GetHumanPlayer();
        if (human == null)
            return;
        var completed = _traversalRecorder.Sample(human.transform.position,
            human.ground != null && human.ground.isGrounded,
            human.jumper != null && human.jumper.justJumped,
            human.rb == null ? 0f : human.rb.linearVelocity.y, now, out var traversal);
        _humanJumpInProgress = _traversalRecorder.TraversalInProgress;
        _humanJumpTakeoffPosition = _traversalRecorder.TakeoffPosition;
        if (!completed)
            return;
        var walkable = !traversal.RequiresJump ||
                       _locomotion.Geometry.CanWalkSegment(traversal.Takeoff, traversal.Landing);
        if (walkable)
        {
            _trail.Add(WalkingPosition(traversal.Landing), false, false);
            if (Plugin.FollowDiagnosticsEnabled)
            {
                Plugin.Logger.LogInfo("[FOLLOW] TRAVERSAL_HINT_SKIPPED " +
                    $"reason={(traversal.RequiresJump ? "walkable_connection" : "ordinary_descent")}, " +
                    $"takeoff={traversal.Takeoff}, landing={traversal.Landing}.");
            }
            return;
        }
        var landing = _trail.AddTraversal(traversal.Takeoff, traversal.Landing,
            traversal.RequiresJump, traversal.RequiresDrop, traversal.Duration);
        Plugin.Logger.LogInfo("[FOLLOW] TRAIL_TRAVERSAL " +
            $"breadcrumb={landing.Sequence}, kind={(traversal.RequiresJump ? "jump" : "drop")}, " +
            $"takeoff={traversal.Takeoff}, landing={traversal.Landing}, " +
            $"duration={traversal.Duration:F2}, peakRise={traversal.PeakRise:F2}, " +
            $"horizontalDistance={traversal.HorizontalDistance:F2}.");
    }

    private void ResetTraversalState(PlayerCharacter human)
    {
        _traversalRecorder.Reset();
        _replay.Reset();
        _navigation.Reset();
        _routePlanner.Reset();
        _routeRevision = 0;
        _humanJumpInProgress = false;
        _humanJumpTakeoffPosition = human == null ? Vector3.zero : human.transform.position;
        _currentBreadcrumbSequence = 0;
        _jumpCommittedSequence = 0;
        _dropCommittedSequence = 0;
        _lastRouteDirection = Vector3.zero;
        _lastRouteMode = "waypoint";
        _lastTargetHorizontalDistance = 0f;
    }

    private void RecordHumanTrail()
    {
        var human = GetHumanPlayer();
        if (human == null || _humanJumpInProgress)
            return;
        var position = WalkingPosition(human.transform.position);
        if (!_trail.TryGetLastAdded(out var lastAdded))
        {
            _trail.Add(position, false, false);
            return;
        }
        var distance = Vector3.Distance(position, lastAdded.Position);
        if (distance >= TrailResetDistance)
        {
            _trail.Clear();
            ResetTraversalState(human);
            _trail.Add(position, false, false);
            Plugin.Logger.LogInfo($"[FOLLOW] TRAIL_REBASED humanDisplacement={distance:F2}.");
            return;
        }
        if (distance >= BreadcrumbSpacing)
            _trail.Add(position, false, false);
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
            ResetTraversalState(human);
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
            _trail.Add(WalkingPosition(human.transform.position), false, false);
            ResetTraversalState(human);

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
        _navigation.Pause();
        _replay.CancelActive();
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
        if (!Plugin.FollowDiagnosticsEnabled || now < _nextStatusLog)
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
