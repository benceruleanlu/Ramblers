using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ramblers;

internal enum CompanionFollowRouteKind
{
    Walking,
    Traversal,
    Attempt
}

internal sealed class CompanionFollowRoute
{
    internal int Revision;
    internal Vector3 HumanGoal;
    internal Vector3 Destination;
    internal Vector3[] Waypoints = Array.Empty<Vector3>();
    internal CompanionFollowRouteKind Kind;
    internal BreadcrumbPoint Hint;
    internal float RemainingDistance;
}

internal sealed class CompanionFollowRoutePlanner
{
    private readonly CompanionLocalRoutePlanner _search = new CompanionLocalRoutePlanner();
    private readonly Func<Vector3, Vector3?> _support;
    private readonly Func<Vector3, Vector3, bool> _walk;
    private readonly List<Candidate> _candidates = new List<Candidate>();
    private readonly Dictionary<int, float> _failedHints = new Dictionary<int, float>();
    private CompanionFollowRoute _selected;
    private float _nextSelection;
    private int _revision;

    internal CompanionFollowRoutePlanner(
        Func<Vector3, Vector3?> support,
        Func<Vector3, Vector3, bool> walk)
    {
        _support = support;
        _walk = walk;
    }

    internal void Reset()
    {
        _selected = null;
        _nextSelection = 0f;
        _failedHints.Clear();
    }

    internal void ReportFailure(float now)
    {
        if (_selected != null && _selected.Hint.Sequence != 0)
            _failedHints[_selected.Hint.Sequence] = now + 12f;
        _selected = null;
        _nextSelection = now;
    }

    internal CompanionFollowRoute Select(
        Vector3 body, Vector3 human, BreadcrumbTrail history, float now)
    {
        if (_selected != null && now < _nextSelection &&
            Vector3.Distance(human, _selected.HumanGoal) < 1.5f &&
            Vector3.Distance(body, _selected.Destination) > 0.65f)
            return _selected;

        _nextSelection = now + 1f;
        if (_search.TryPlan(body, human, _support, _walk, out var directRoute))
            return Choose(human, human, directRoute, CompanionFollowRouteKind.Walking,
                default(BreadcrumbPoint), Length(body, directRoute));

        _candidates.Clear();
        var remaining = 0f;
        var previous = human;
        for (var offset = history.Count - 1; offset >= 0; offset--)
        {
            history.TryPeek(offset, out var point);
            remaining += Vector3.Distance(point.Position, previous);
            previous = point.Position;
            var traversal = point.HasTakeoff && (point.RequiresJump || point.RequiresDrop);
            var destination = traversal ? point.TakeoffPosition : point.Position;
            if (Vector3.Distance(body, destination) > 8f ||
                (!traversal && Vector3.Distance(body, destination) <= 0.8f) ||
                (_failedHints.TryGetValue(point.Sequence, out var until) && now < until))
                continue;
            var tail = remaining + (traversal ? Vector3.Distance(point.TakeoffPosition, point.Position) : 0f);
            _candidates.Add(new Candidate(point, destination, tail,
                Vector3.Distance(body, destination) + tail));
        }
        _candidates.Sort((first, second) => first.Cost.CompareTo(second.Cost));

        Candidate? traversalFallback = null;
        var attempted = 0;
        for (var index = 0; index < _candidates.Count; index++)
        {
            var candidate = _candidates[index];
            if (candidate.IsTraversal && !traversalFallback.HasValue)
                traversalFallback = candidate;
            if (attempted >= 4)
                continue;
            attempted++;
            if (!_search.TryPlan(body, candidate.Destination, _support, _walk, out var route))
                continue;
            return Choose(human, candidate.Destination, route,
                candidate.IsTraversal ? CompanionFollowRouteKind.Traversal : CompanionFollowRouteKind.Walking,
                candidate.Point, Length(body, route) + candidate.Tail);
        }

        if (traversalFallback.HasValue)
        {
            var candidate = traversalFallback.Value;
            return Choose(human, candidate.Destination, Array.Empty<Vector3>(),
                CompanionFollowRouteKind.Traversal, candidate.Point, candidate.Cost);
        }
        return Choose(human, human, Array.Empty<Vector3>(), CompanionFollowRouteKind.Attempt,
            default(BreadcrumbPoint), Vector3.Distance(body, human));
    }

    private CompanionFollowRoute Choose(
        Vector3 human, Vector3 destination, Vector3[] waypoints,
        CompanionFollowRouteKind kind, BreadcrumbPoint hint, float remaining)
    {
        _selected = new CompanionFollowRoute
        {
            Revision = ++_revision,
            HumanGoal = human,
            Destination = destination,
            Waypoints = waypoints,
            Kind = kind,
            Hint = hint,
            RemainingDistance = remaining
        };
        return _selected;
    }

    private static float Length(Vector3 start, Vector3[] route)
    {
        var length = 0f;
        for (var index = 0; index < route.Length; index++)
        {
            length += Vector3.Distance(start, route[index]);
            start = route[index];
        }
        return length;
    }

    private readonly struct Candidate
    {
        internal Candidate(BreadcrumbPoint point, Vector3 destination, float tail, float cost)
        {
            Point = point;
            Destination = destination;
            Tail = tail;
            Cost = cost;
        }
        internal BreadcrumbPoint Point { get; }
        internal Vector3 Destination { get; }
        internal float Tail { get; }
        internal float Cost { get; }
        internal bool IsTraversal => Point.HasTakeoff && (Point.RequiresJump || Point.RequiresDrop);
    }
}
