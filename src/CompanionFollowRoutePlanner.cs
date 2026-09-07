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
    internal bool SearchPending;
}

internal sealed class CompanionFollowRoutePlanner
{
    private readonly CompanionLocalRoutePlanner _search = new CompanionLocalRoutePlanner();
    private readonly Func<Vector3, Vector3?> _support;
    private readonly Func<Vector3, Vector3, bool> _walk;
    private readonly Func<bool> _budgetAvailable;
    private readonly List<Candidate> _candidates = new List<Candidate>();
    private readonly Dictionary<int, float> _failedHints = new Dictionary<int, float>();
    private readonly List<int> _expiredHints = new List<int>();
    private CompanionFollowRoute _selected;
    private float _nextSelection;
    private int _revision;
    private int _lastObservedHintSequence;
    private bool _selectionPending;
    private bool _searchingHuman;
    private Vector3 _selectionBody;
    private Vector3 _selectionHuman;
    private int _candidateIndex;
    private Candidate? _traversalFallback;

    internal CompanionFollowRoutePlanner(
        Func<Vector3, Vector3?> support,
        Func<Vector3, Vector3, bool> walk,
        Func<bool> budgetAvailable = null)
    {
        _support = support;
        _walk = walk;
        _budgetAvailable = budgetAvailable;
    }

    internal void Reset()
    {
        Reconsider();
        _failedHints.Clear();
        _lastObservedHintSequence = 0;
    }

    internal void Reconsider()
    {
        _selected = null;
        _nextSelection = 0f;
        CancelSearch();
    }

    internal void ReportFailure(float now)
    {
        PruneFailures(now);
        if (_selected != null && _selected.Hint.Sequence != 0)
        {
            if (_failedHints.Count >= 16)
            {
                var oldestSequence = 0;
                var oldestExpiry = float.PositiveInfinity;
                foreach (var failure in _failedHints)
                {
                    if (failure.Value >= oldestExpiry)
                        continue;
                    oldestSequence = failure.Key;
                    oldestExpiry = failure.Value;
                }
                _failedHints.Remove(oldestSequence);
            }
            _failedHints[_selected.Hint.Sequence] = now + 12f;
        }
        Reconsider();
    }

    internal CompanionFollowRoute Select(
        Vector3 body, Vector3 human, BreadcrumbTrail history, float now)
    {
        PruneFailures(now);
        if (_selectionPending)
        {
            if (Vector3.Distance(body, _selectionBody) < 2f &&
                Vector3.Distance(human, _selectionHuman) < 1.5f)
                return ContinueSelection(history, now);
            CancelSearch();
            _selected = null;
        }
        if (_selected != null && _selected.Kind == CompanionFollowRouteKind.Walking &&
            _selected.Hint.Sequence != 0 &&
            Vector3.Distance(body, _selected.Destination) <= 0.8f)
        {
            history.RemoveThrough(_selected.Hint.Sequence);
            _selected = null;
        }
        var newestSequence = history.TryGetLastAdded(out var newest) ? newest.Sequence : 0;
        if (_selected != null && Vector3.Distance(human, _selected.HumanGoal) < 1.5f)
        {
            var uncertain = _selected.Kind == CompanionFollowRouteKind.Attempt ||
                _selected.Waypoints.Length == 0;
            if (!uncertain || (now < _nextSelection && newestSequence == _lastObservedHintSequence))
                return _selected;
        }

        _nextSelection = now + 1f;
        _lastObservedHintSequence = newestSequence;
        _selectionPending = true;
        _searchingHuman = true;
        _selectionBody = body;
        _selectionHuman = human;
        _candidateIndex = 0;
        _traversalFallback = null;
        _search.Reset();
        return ContinueSelection(history, now);
    }

    private CompanionFollowRoute ContinueSelection(BreadcrumbTrail history, float now)
    {
        if (_searchingHuman)
        {
            if (_search.TryPlan(_selectionBody, _selectionHuman, _support, _walk,
                    _budgetAvailable, out var directRoute))
                return CompleteSelection(_selectionHuman, directRoute,
                    CompanionFollowRouteKind.Walking, default(BreadcrumbPoint),
                    Length(_selectionBody, directRoute), now);
            if (_search.LastStatus == "query_budget")
                return PendingSelection();
            _searchingHuman = false;
            BuildCandidates(history, now);
        }

        while (_candidateIndex < _candidates.Count && _candidateIndex < 4)
        {
            var candidate = _candidates[_candidateIndex];
            if (_search.TryPlan(_selectionBody, candidate.Destination, _support, _walk,
                    _budgetAvailable, out var route))
                return CompleteSelection(candidate.Destination, route,
                    candidate.IsTraversal ? CompanionFollowRouteKind.Traversal : CompanionFollowRouteKind.Walking,
                    candidate.Point, Length(_selectionBody, route) + candidate.Tail, now);
            if (_search.LastStatus == "query_budget")
                return PendingSelection();
            _candidateIndex++;
        }
        if (_traversalFallback.HasValue)
        {
            var candidate = _traversalFallback.Value;
            return CompleteSelection(candidate.Destination, Array.Empty<Vector3>(),
                CompanionFollowRouteKind.Traversal, candidate.Point, candidate.Cost, now);
        }
        return CompleteSelection(_selectionHuman, Array.Empty<Vector3>(), CompanionFollowRouteKind.Attempt,
            default(BreadcrumbPoint), Vector3.Distance(_selectionBody, _selectionHuman), now);
    }

    private void BuildCandidates(BreadcrumbTrail history, float now)
    {

        _candidates.Clear();
        var remaining = 0f;
        var previous = _selectionHuman;
        for (var offset = history.Count - 1; offset >= 0; offset--)
        {
            history.TryPeek(offset, out var point);
            remaining += Vector3.Distance(point.Position, previous);
            previous = point.Position;
            var traversal = point.HasTakeoff && (point.RequiresJump || point.RequiresDrop);
            var destination = traversal ? point.TakeoffPosition : point.Position;
            if (Vector3.Distance(_selectionBody, destination) > 8f ||
                (!traversal && Vector3.Distance(_selectionBody, destination) <= 0.8f) ||
                (_failedHints.TryGetValue(point.Sequence, out var until) && now < until))
                continue;
            var tail = remaining + (traversal ? Vector3.Distance(point.TakeoffPosition, point.Position) : 0f);
            _candidates.Add(new Candidate(point, destination, tail,
                Vector3.Distance(_selectionBody, destination) + tail));
        }
        _candidates.Sort((first, second) => first.Cost.CompareTo(second.Cost));

        for (var index = 0; index < _candidates.Count; index++)
        {
            var candidate = _candidates[index];
            if (!candidate.IsTraversal)
                continue;
            _traversalFallback = candidate;
            break;
        }
    }

    private CompanionFollowRoute PendingSelection()
    {
        if (_selected != null && _selected.SearchPending)
            return _selected;
        var route = Choose(_selectionHuman, _selectionHuman, Array.Empty<Vector3>(),
            CompanionFollowRouteKind.Attempt, default(BreadcrumbPoint),
            Vector3.Distance(_selectionBody, _selectionHuman));
        route.SearchPending = true;
        return route;
    }

    private CompanionFollowRoute CompleteSelection(Vector3 destination, Vector3[] waypoints,
        CompanionFollowRouteKind kind, BreadcrumbPoint hint, float remaining, float now)
    {
        _selectionPending = false;
        _nextSelection = now + 1f;
        return Choose(_selectionHuman, destination, waypoints, kind, hint, remaining);
    }

    private void CancelSearch()
    {
        _selectionPending = false;
        _search.Reset();
        _candidates.Clear();
        _traversalFallback = null;
    }

    private void PruneFailures(float now)
    {
        _expiredHints.Clear();
        foreach (var failure in _failedHints)
            if (failure.Value <= now)
                _expiredHints.Add(failure.Key);
        for (var index = 0; index < _expiredHints.Count; index++)
            _failedHints.Remove(_expiredHints[index]);
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
