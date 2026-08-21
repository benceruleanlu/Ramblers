using System;
using UnityEngine;

namespace Ramblers;

/// <summary>
/// One recorded point on the human's proven route. A jump hint belongs to the
/// segment ending at this point, so the follower can replay the takeoff instead
/// of treating every pair of breadcrumbs as flat walking geometry.
/// </summary>
internal readonly struct BreadcrumbPoint
{
    internal BreadcrumbPoint(
        int sequence,
        Vector3 position,
        bool requiresJump,
        bool requiresDrop,
        Vector3 travelDirection)
    {
        Sequence = sequence;
        Position = position;
        RequiresJump = requiresJump;
        RequiresDrop = requiresDrop;
        TravelDirection = travelDirection;
    }

    internal int Sequence { get; }
    internal Vector3 Position { get; }
    internal bool RequiresJump { get; }
    internal bool RequiresDrop { get; }
    internal Vector3 TravelDirection { get; }
}

/// <summary>
/// A bounded FIFO of world positions describing a walked route. This is pure
/// storage and geometry: when to sample a new point, and what to log when the
/// route is discarded, belong to the behaviour that owns the trail.
/// </summary>
internal sealed class BreadcrumbTrail
{
    private readonly BreadcrumbPoint[] _points;
    private int _head;
    private int _count;
    private int _nextSequence;
    private BreadcrumbPoint _lastAdded;
    private bool _hasLastAdded;

    internal BreadcrumbTrail(int capacity)
    {
        _points = new BreadcrumbPoint[capacity];
    }

    internal int Count => _count;

    internal void Clear()
    {
        _head = 0;
        _count = 0;
        _hasLastAdded = false;
    }

    /// <summary>
    /// Appends a point, evicting the oldest once the trail is full.
    /// </summary>
    internal BreadcrumbPoint Add(
        Vector3 position,
        bool requiresJump,
        bool requiresDrop)
    {
        if (_count == _points.Length)
        {
            _head = (_head + 1) % _points.Length;
            _count--;
        }

        var travelDirection = Vector3.zero;
        if (_hasLastAdded)
        {
            travelDirection = position - _lastAdded.Position;
            travelDirection.y = 0f;
            if (travelDirection.sqrMagnitude >= 0.0025f)
                travelDirection.Normalize();
            else
                travelDirection = _lastAdded.TravelDirection;
        }

        var point = new BreadcrumbPoint(
            ++_nextSequence,
            position,
            requiresJump,
            requiresDrop,
            travelDirection);
        _points[(_head + _count) % _points.Length] = point;
        _count++;
        _lastAdded = point;
        _hasLastAdded = true;
        return point;
    }

    /// <summary>
    /// The oldest remaining point, which is the next one to walk toward.
    /// </summary>
    internal BreadcrumbPoint Peek()
    {
        return _points[_head];
    }

    internal bool TryPeek(int offset, out BreadcrumbPoint point)
    {
        point = default(BreadcrumbPoint);
        if (offset < 0 || offset >= _count)
            return false;

        point = _points[(_head + offset) % _points.Length];
        return true;
    }

    internal bool TryRemoveFirst(out BreadcrumbPoint point)
    {
        point = default(BreadcrumbPoint);
        if (_count == 0)
            return false;

        point = Peek();
        _head = (_head + 1) % _points.Length;
        _count--;
        return true;
    }

    /// <summary>
    /// The most recently appended point, used to decide whether the next sample
    /// is far enough along to be worth recording. Cleared with the trail.
    /// </summary>
    internal bool TryGetLastAdded(out BreadcrumbPoint point)
    {
        point = _lastAdded;
        return _hasLastAdded;
    }

    /// <summary>
    /// Removes points reached in both the horizontal and vertical axes. A point
    /// can also be retired after the body crosses its route-normal plane while
    /// still inside the route corridor. That second condition prevents a fast
    /// body from orbiting a waypoint it has already passed without allowing a
    /// point on another floor to disappear merely because its X/Z projection
    /// happens to be nearby.
    ///
    /// An uncommitted jump marker can pin the head even when its horizontal
    /// coordinate is already close, preventing the route from deleting its own
    /// takeoff instruction.
    /// </summary>
    internal int RemoveReached(
        Vector3 from,
        float horizontalTolerance,
        float verticalTolerance,
        float passLateralTolerance,
        bool preserveTraversalHints,
        int committedJumpSequence,
        int committedDropSequence,
        out BreadcrumbPoint lastRemoved,
        out bool crossedPointPlane)
    {
        var removed = 0;
        lastRemoved = default(BreadcrumbPoint);
        crossedPointPlane = false;
        while (_count > 0)
        {
            var point = Peek();
            if (preserveTraversalHints &&
                ((point.RequiresJump &&
                  point.Sequence != committedJumpSequence) ||
                 (point.RequiresDrop &&
                  point.Sequence != committedDropSequence)))
            {
                return removed;
            }

            var verticallyNear = Mathf.Abs(from.y - point.Position.y) <= verticalTolerance;
            var reached = verticallyNear &&
                          HorizontalDistance(from, point.Position) <= horizontalTolerance;
            var passed = !reached &&
                         _count > 1 &&
                         verticallyNear &&
                         HasCrossedPointPlane(from, point, passLateralTolerance);
            if (!reached && !passed)
            {
                return removed;
            }

            lastRemoved = point;
            crossedPointPlane |= passed;
            _head = (_head + 1) % _points.Length;
            _count--;
            removed++;
        }

        return removed;
    }

    /// <summary>
    /// Collapses an obsolete route prefix when the body is already inside the
    /// arrival corridor of a later breadcrumb. This is the route equivalent of
    /// taking a proven loop shortcut: the later point was occupied by the human,
    /// and the body has independently reached the same place and level.
    ///
    /// An uncommitted traversal marker stops the scan. Geometric proximity must
    /// never erase a jump or drop that the body has not performed.
    /// </summary>
    internal int RemoveThroughLatestNearby(
        Vector3 from,
        float horizontalTolerance,
        float verticalTolerance,
        int committedJumpSequence,
        int committedDropSequence,
        Func<Vector3, bool> canShortcut,
        out BreadcrumbPoint firstRemoved,
        out BreadcrumbPoint lastRemoved)
    {
        firstRemoved = default(BreadcrumbPoint);
        lastRemoved = default(BreadcrumbPoint);
        if (_count < 2)
            return 0;

        var latestNearbyOffset = -1;
        for (var offset = 0; offset < _count; offset++)
        {
            var point = _points[(_head + offset) % _points.Length];
            if ((point.RequiresJump &&
                 point.Sequence != committedJumpSequence) ||
                (point.RequiresDrop &&
                 point.Sequence != committedDropSequence))
            {
                break;
            }

            if (offset > 0 &&
                Mathf.Abs(from.y - point.Position.y) <= verticalTolerance &&
                HorizontalDistance(from, point.Position) <= horizontalTolerance &&
                (canShortcut == null || canShortcut(point.Position)))
            {
                latestNearbyOffset = offset;
            }
        }

        if (latestNearbyOffset < 1)
            return 0;

        var removed = 0;
        for (var offset = 0; offset <= latestNearbyOffset; offset++)
        {
            BreadcrumbPoint point;
            if (!TryRemoveFirst(out point))
                break;
            if (removed == 0)
                firstRemoved = point;
            lastRemoved = point;
            removed++;
        }
        return removed;
    }

    private static bool HasCrossedPointPlane(
        Vector3 from,
        BreadcrumbPoint point,
        float lateralTolerance)
    {
        var direction = point.TravelDirection;
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.0001f)
            return false;

        direction.Normalize();
        var delta = from - point.Position;
        delta.y = 0f;
        var forwardDistance = Vector3.Dot(delta, direction);
        if (forwardDistance < 0f)
            return false;

        var lateralOffset = delta - direction * forwardDistance;
        return lateralOffset.magnitude <= lateralTolerance;
    }

    /// <summary>
    /// Three-dimensional length of the route <paramref name="from"/> -> every
    /// remaining breadcrumb -> <paramref name="to"/>. Vertical separation is
    /// intentionally retained so stacked floors and ledges cannot collapse into
    /// the same apparent position.
    /// </summary>
    internal float MeasureDistance(Vector3 from, Vector3 to)
    {
        if (_count == 0)
            return Vector3.Distance(from, to);

        var previous = from;
        var total = 0f;
        for (var offset = 0; offset < _count; offset++)
        {
            var current = _points[(_head + offset) % _points.Length].Position;
            total += Vector3.Distance(previous, current);
            previous = current;
        }

        return total + Vector3.Distance(previous, to);
    }

    internal static float HorizontalDistance(Vector3 from, Vector3 to)
    {
        var delta = to - from;
        delta.y = 0f;
        return delta.magnitude;
    }
}
