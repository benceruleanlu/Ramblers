using UnityEngine;

namespace Ramblers;

/// <summary>
/// A bounded FIFO of world positions describing a walked route. This is pure
/// storage and geometry: when to sample a new point, and what to log when the
/// route is discarded, belong to the behaviour that owns the trail.
/// </summary>
internal sealed class BreadcrumbTrail
{
    private readonly Vector3[] _points;
    private int _head;
    private int _count;
    private Vector3 _lastAdded;
    private bool _hasLastAdded;

    internal BreadcrumbTrail(int capacity)
    {
        _points = new Vector3[capacity];
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
    internal void Add(Vector3 position)
    {
        if (_count == _points.Length)
        {
            _head = (_head + 1) % _points.Length;
            _count--;
        }

        _points[(_head + _count) % _points.Length] = position;
        _count++;
        _lastAdded = position;
        _hasLastAdded = true;
    }

    /// <summary>
    /// The oldest remaining point, which is the next one to walk toward.
    /// </summary>
    internal Vector3 Peek()
    {
        return _points[_head];
    }

    /// <summary>
    /// The most recently appended point, used to decide whether the next sample
    /// is far enough along to be worth recording. Cleared with the trail.
    /// </summary>
    internal bool TryGetLastAdded(out Vector3 position)
    {
        position = _lastAdded;
        return _hasLastAdded;
    }

    internal void RemoveReached(Vector3 from, float tolerance)
    {
        while (_count > 0 && HorizontalDistance(from, Peek()) <= tolerance)
        {
            _head = (_head + 1) % _points.Length;
            _count--;
        }
    }

    /// <summary>
    /// Horizontal length of the route <paramref name="from"/> -> every remaining
    /// breadcrumb -> <paramref name="to"/>. With no breadcrumbs left this is the
    /// straight-line horizontal distance.
    /// </summary>
    internal float MeasureDistance(Vector3 from, Vector3 to)
    {
        if (_count == 0)
            return HorizontalDistance(from, to);

        var previous = Peek();
        var total = HorizontalDistance(from, previous);
        for (var offset = 1; offset < _count; offset++)
        {
            var current = _points[(_head + offset) % _points.Length];
            total += HorizontalDistance(previous, current);
            previous = current;
        }

        return total + HorizontalDistance(previous, to);
    }

    internal static float HorizontalDistance(Vector3 from, Vector3 to)
    {
        var delta = to - from;
        delta.y = 0f;
        return delta.magnitude;
    }
}
