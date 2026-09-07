using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ramblers;

internal readonly struct CompanionNavigationStep
{
    internal CompanionNavigationStep(Vector3 direction, string mode, bool stalled, bool requestJump,
        bool goalStalled = false)
    {
        Direction = direction;
        Mode = mode;
        Stalled = stalled;
        RequestJump = requestJump;
        GoalStalled = goalStalled;
    }

    internal Vector3 Direction { get; }
    internal string Mode { get; }
    internal bool Stalled { get; }
    internal bool RequestJump { get; }
    internal bool GoalStalled { get; }
}

internal sealed class CompanionFollowNavigation
{
    private const float GoalProgressDistance = 0.25f;
    private const float GoalStallInterval = 6f;
    private const float FailedApproachRadius = 0.65f;
    private const float FailureMemoryDuration = 12f;
    private readonly CompanionLocalRoutePlanner _planner = new CompanionLocalRoutePlanner();
    private readonly Func<Vector3, Vector3?> _sampleGround;
    private readonly Func<Vector3, Vector3, bool> _clearSegment;
    private readonly Func<Vector3, Vector3, bool> _attemptSegment;
    private readonly List<FailedDirection> _failures = new List<FailedDirection>();
    private Vector3[] _route = Array.Empty<Vector3>();
    private int _cursor;
    private int _targetSequence;
    private Vector3 _goal;
    private bool _hasGoal;
    private float _nextPlanAt;
    private float _nextJumpAt;
    private Vector3 _progressAnchor;
    private Vector3 _progressTarget;
    private float _bestDistance;
    private float _progressAt;
    private Vector3 _motionAnchor;
    private float _motionAt;
    private bool _observingProgress;
    private Vector3 _lastDirection;
    private int _recoveryAttempts;
    private bool _escapeRoute;
    private bool _observingGoal;
    private Vector3 _observedGoal;
    private float _bestGoalDistance;
    private float _goalProgressAt;
    private bool _hasApproach;
    private Vector3 _approachOrigin;
    private Vector3 _approachDirection;
    private float _approachGoalDistance;
    private float _approachAt;
    private bool _leftApproach;

    internal CompanionFollowNavigation(
        Func<Vector3, Vector3?> sampleGround,
        Func<Vector3, Vector3, bool> clearSegment,
        Func<Vector3, Vector3, bool> attemptSegment = null)
    {
        _sampleGround = sampleGround;
        _clearSegment = clearSegment;
        _attemptSegment = attemptSegment;
    }

    internal string PlanStatus => _planner.LastStatus;
    internal int ExpandedNodes => _planner.LastExpandedNodeCount;
    internal int QueryCount => _planner.LastQueryCount;
    internal int WaypointsRemaining => _route.Length - _cursor;
    internal int RememberedFailures => _failures.Count;
    internal int PlanCount { get; private set; }
    internal int GoalStallCount { get; private set; }
    internal bool IsRouteSegmentAvailable(Vector3 from, Vector3 to) => SegmentClear(from, to);

    internal void Reset()
    {
        _route = Array.Empty<Vector3>();
        _cursor = 0;
        _hasGoal = false;
        _observingProgress = false;
        _lastDirection = Vector3.zero;
        _failures.Clear();
        _nextPlanAt = 0f;
        _nextJumpAt = 0f;
        _recoveryAttempts = 0;
        _escapeRoute = false;
        _observingGoal = false;
        _hasApproach = false;
        PlanCount = 0;
        GoalStallCount = 0;
    }

    internal void Pause()
    {
        _observingProgress = false;
        _route = Array.Empty<Vector3>();
        _cursor = 0;
        _lastDirection = Vector3.zero;
        _nextPlanAt = 0f;
        _observingGoal = false;
        _hasApproach = false;
    }

    internal bool TryWalkingDetour(
        Vector3 position, Vector3 goal, int targetSequence, float now,
        Func<Vector3, Vector3?> sampleGround,
        Func<Vector3, Vector3, bool> canWalkSegment)
    {
        PlanCount++;
        if (!_planner.TryPlan(position, goal, sampleGround,
                (from, to) => canWalkSegment(from, to) && SegmentClear(from, to),
                out var route))
            return false;
        _route = route;
        _cursor = 0;
        _targetSequence = targetSequence;
        _goal = goal;
        _hasGoal = true;
        _escapeRoute = false;
        _nextPlanAt = now + 0.8f;
        return true;
    }

    internal void AcceptRoute(Vector3 goal, int targetSequence, Vector3[] route, float now)
    {
        _route = route;
        _cursor = 0;
        _targetSequence = targetSequence;
        _goal = goal;
        _hasGoal = true;
        _escapeRoute = false;
        _nextPlanAt = route.Length == 0 ? now : now + 0.8f;
    }

    internal CompanionNavigationStep Tick(
        Vector3 position,
        Vector3 goal,
        int targetSequence,
        bool grounded,
        float now)
    {
        _failures.RemoveAll(failure => failure.Until <= now);
        if (!_hasGoal || targetSequence != _targetSequence ||
            Vector3.Distance(goal, _goal) > 0.75f)
        {
            _route = Array.Empty<Vector3>();
            _cursor = 0;
            _goal = goal;
            _targetSequence = targetSequence;
            _hasGoal = true;
        }

        var goalStalled = ObserveGoalProgress(position, goal, grounded, now);
        if (!grounded)
        {
            _observingProgress = false;
            var airborneDirection = _lastDirection.sqrMagnitude > 0.001f
                ? _lastDirection : Flat(goal - position).normalized;
            return new CompanionNavigationStep(airborneDirection, "airborne", false, false);
        }

        var stalled = ObserveProgress(position, now);
        if (stalled || goalStalled)
        {
            if (goalStalled && _hasApproach)
                RememberFailure(_approachOrigin, _approachDirection, now);
            else
                RememberFailure(position, _lastDirection, now);
            _route = Array.Empty<Vector3>();
            _cursor = 0;
            _nextPlanAt = 0f;
            _recoveryAttempts++;
        }

        while (_cursor < _route.Length &&
               Flat(_route[_cursor] - position).magnitude <= 0.45f &&
               Mathf.Abs(_route[_cursor].y - position.y) <= 0.9f)
        {
            _cursor++;
            _observingProgress = false;
        }

        if (_cursor < _route.Length &&
            !SegmentClear(position, _route[_cursor]))
        {
            _route = Array.Empty<Vector3>();
            _cursor = 0;
        }

        if (_cursor >= _route.Length && now >= _nextPlanAt)
        {
            _nextPlanAt = now + 0.8f;
            var offset = goal - position;
            var horizontal = Flat(offset).magnitude;
            var localGoal = horizontal > 5.5f
                ? position + offset * (5.5f / horizontal) : goal;
            var sampledGoal = _sampleGround(localGoal);
            if (sampledGoal.HasValue && horizontal > 5.5f)
                localGoal = sampledGoal.Value;
            PlanCount++;
            _escapeRoute = !_planner.TryPlan(
                position, localGoal, _sampleGround, SegmentClear, out _route);
            _cursor = 0;
            if (_escapeRoute)
                FindEscape(position, goal);
        }

        var target = _cursor < _route.Length ? _route[_cursor] : goal;
        var direction = _cursor >= _route.Length && _escapeRoute
            ? _lastDirection : Flat(target - position);
        if (direction.sqrMagnitude < 0.0001f)
        {
            direction = _lastDirection.sqrMagnitude > 0.0001f
                ? _lastDirection : Vector3.forward;
        }
        direction.Normalize();
        var requestJump = stalled && now >= _nextJumpAt;
        if (requestJump)
            _nextJumpAt = now + 3f;
        SetProgressTarget(position, target, now);
        _lastDirection = direction;
        RememberApproach(position, goal, direction, now);
        var mode = _cursor >= _route.Length ? "attempt"
            : _escapeRoute ? "escape" : _route.Length > 1 ? "detour" : "direct";
        return new CompanionNavigationStep(direction, mode, stalled, requestJump, goalStalled);
    }

    private bool ObserveGoalProgress(Vector3 position, Vector3 goal, bool grounded, float now)
    {
        var distance = Vector3.Distance(position, goal);
        if (!_observingGoal || Vector3.Distance(goal, _observedGoal) > 0.75f || now < _goalProgressAt)
        {
            _observingGoal = true;
            _observedGoal = goal;
            _bestGoalDistance = distance;
            _goalProgressAt = now;
            _hasApproach = false;
            return false;
        }
        if (distance < _bestGoalDistance - GoalProgressDistance)
        {
            _bestGoalDistance = distance;
            _goalProgressAt = now;
        }
        if (_hasApproach && Vector3.Distance(position, _approachOrigin) > 1.5f)
            _leftApproach = true;
        if (!grounded)
            return false;
        var repeatingApproach = _hasApproach && _leftApproach && now - _approachAt >= 2f &&
            Vector3.Distance(position, _approachOrigin) <= 1f &&
            distance >= _approachGoalDistance - GoalProgressDistance &&
            Vector3.Dot(_lastDirection, _approachDirection) > 0.75f;
        if (!repeatingApproach && now - _goalProgressAt < GoalStallInterval)
            return false;
        _goalProgressAt = now;
        _approachAt = now;
        _leftApproach = false;
        GoalStallCount++;
        return true;
    }

    private void RememberApproach(Vector3 position, Vector3 goal, Vector3 direction, float now)
    {
        if (_escapeRoute || direction.sqrMagnitude < 0.001f ||
            Vector3.Dot(direction, Flat(goal - position).normalized) < 0.25f)
            return;
        var distance = Vector3.Distance(position, goal);
        if (distance > _bestGoalDistance + GoalProgressDistance)
            return;
        if (_hasApproach && distance >= _approachGoalDistance - GoalProgressDistance)
            return;
        _hasApproach = true;
        _approachOrigin = position;
        _approachDirection = direction;
        _approachGoalDistance = distance;
        _approachAt = now;
        _leftApproach = false;
    }

    private bool ObserveProgress(Vector3 position, float now)
    {
        if (!_observingProgress)
            return false;
        if (Vector3.Distance(position, _motionAnchor) >= 0.2f)
        {
            _motionAnchor = position;
            _motionAt = now;
        }
        if (now - _motionAt >= 1.25f)
        {
            _motionAt = now;
            _progressAt = now;
            return true;
        }
        var distance = Vector3.Distance(position, _progressTarget);
        if (distance < _bestDistance - 0.15f)
        {
            _bestDistance = distance;
            _progressAnchor = position;
            _progressAt = now;
            return false;
        }
        var moving = Vector3.Distance(position, _progressAnchor) >= 0.2f;
        if (now - _progressAt < (moving ? 3f : 1.25f))
            return false;
        _progressAt = now;
        _progressAnchor = position;
        _bestDistance = distance;
        return true;
    }

    private void SetProgressTarget(Vector3 position, Vector3 target, float now)
    {
        if (!_observingProgress || Vector3.Distance(_progressTarget, target) > 0.5f)
        {
            if (!_observingProgress)
            {
                _motionAnchor = position;
                _motionAt = now;
            }
            _progressAnchor = position;
            _progressTarget = target;
            _bestDistance = Vector3.Distance(position, target);
            _progressAt = now;
            _observingProgress = true;
        }
    }

    private bool SegmentClear(Vector3 from, Vector3 to)
    {
        return !CrossesFailedApproach(from, to) && _clearSegment(from, to);
    }

    private bool CrossesFailedApproach(Vector3 from, Vector3 to)
    {
        var direction = Flat(to - from).normalized;
        for (var i = 0; i < _failures.Count; i++)
        {
            var failure = _failures[i];
            var delta = to - from;
            var fraction = delta.sqrMagnitude < 0.0001f ? 0f :
                Mathf.Clamp(Vector3.Dot(failure.Origin - from, delta) / delta.sqrMagnitude, 0f, 1f);
            if (Vector3.Distance(from + delta * fraction, failure.Origin) <= FailedApproachRadius &&
                Vector3.Dot(direction, failure.Direction) > 0.75f)
                return true;
        }
        return false;
    }

    private void RememberFailure(Vector3 position, Vector3 direction, float now)
    {
        if (direction.sqrMagnitude < 0.001f)
            return;
        for (var i = 0; i < _failures.Count; i++)
        {
            if (Vector3.Distance(position, _failures[i].Origin) <= 0.4f &&
                Vector3.Dot(direction.normalized, _failures[i].Direction) > 0.85f)
            {
                _failures[i] = new FailedDirection(position, direction.normalized, now + FailureMemoryDuration);
                return;
            }
        }
        if (_failures.Count >= 16)
            _failures.RemoveAt(0);
        _failures.Add(new FailedDirection(position, direction.normalized, now + FailureMemoryDuration));
    }

    private void FindEscape(Vector3 position, Vector3 goal)
    {
        var forward = Flat(goal - position).normalized;
        if (forward.sqrMagnitude < 0.001f)
            forward = Vector3.forward;
        var attempt = position + forward * 1.2f;
        if (_attemptSegment != null && !CrossesFailedApproach(position, attempt) &&
            _attemptSegment(position, attempt))
        {
            _route = Array.Empty<Vector3>();
            _lastDirection = forward;
            return;
        }
        var side = new Vector3(-forward.z, 0f, forward.x);
        if ((_recoveryAttempts & 1) != 0)
            side = -side;
        var directions = new[] { side, -forward, -side, (side - forward).normalized,
            (-side - forward).normalized, forward };
        for (var i = 0; i < directions.Length; i++)
        {
            var candidate = position + directions[i] * 1.2f;
            var sampled = _sampleGround(candidate);
            if (sampled.HasValue)
                candidate = sampled.Value;
            if (!SegmentClear(position, candidate))
                continue;
            _route = new[] { candidate };
            return;
        }
        var fallback = directions[_recoveryAttempts % directions.Length];
        _route = Array.Empty<Vector3>();
        _goal = goal;
        _lastDirection = fallback;
    }

    private static Vector3 Flat(Vector3 vector)
    {
        vector.y = 0f;
        return vector;
    }

    private readonly struct FailedDirection
    {
        internal FailedDirection(Vector3 origin, Vector3 direction, float until)
        {
            Origin = origin;
            Direction = direction;
            Until = until;
        }

        internal Vector3 Origin { get; }
        internal Vector3 Direction { get; }
        internal float Until { get; }
    }
}
