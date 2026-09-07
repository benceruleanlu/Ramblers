using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ramblers;

internal sealed class CompanionLocalRoutePlanner
{
    private const float HeightCellSize = 0.5f;
    private static readonly int[] NeighborX = { 1, 0, -1, 0, 1, -1, -1, 1 };
    private static readonly int[] NeighborZ = { 0, 1, 0, -1, 1, 1, -1, -1 };
    private readonly int _maximumExpandedNodes;
    private readonly int _maximumQueries;
    private readonly float _radius;
    private readonly float _spacing;
    private readonly List<Node> _nodes = new List<Node>();
    private readonly Dictionary<Cell, int> _nodeIndices = new Dictionary<Cell, int>();
    private readonly Dictionary<Cell, Vector3?> _groundSamples =
        new Dictionary<Cell, Vector3?>();
    private Func<Vector3, Vector3?> _sampleGround;
    private Func<Vector3, Vector3, bool> _clearSegment;
    private Func<bool> _budgetAvailable;
    private Vector3 _start;
    private Vector3 _goal;
    private bool _searchPending;
    private bool _directPending;
    private int _expandingNode = -1;
    private int _neighbor;
    private bool _goalCheckPending;
    private int _expandedTotal;

    internal CompanionLocalRoutePlanner(
        int maximumExpandedNodes = 160,
        int maximumQueries = 640,
        float radius = 8f,
        float spacing = 1f)
    {
        if (maximumExpandedNodes < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumExpandedNodes));
        if (maximumQueries < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumQueries));
        if (!Finite(spacing) || spacing <= 0f)
            throw new ArgumentOutOfRangeException(nameof(spacing));
        if (!Finite(radius) || radius < spacing)
            throw new ArgumentOutOfRangeException(nameof(radius));
        _maximumExpandedNodes = maximumExpandedNodes;
        _maximumQueries = maximumQueries;
        _radius = radius;
        _spacing = spacing;
    }

    internal string LastStatus { get; private set; } = "not_planned";
    internal int LastExpandedNodeCount { get; private set; }
    internal int LastSampleCount { get; private set; }
    internal int LastSegmentCheckCount { get; private set; }
    internal int LastQueryCount => LastSampleCount + LastSegmentCheckCount;

    internal void Reset()
    {
        _searchPending = false;
        _nodes.Clear();
        _nodeIndices.Clear();
        _groundSamples.Clear();
        _directPending = true;
        _expandingNode = -1;
        _neighbor = 0;
        _expandedTotal = 0;
    }

    internal bool TryPlan(
        Vector3 start,
        Vector3 goal,
        Func<Vector3, Vector3?> sampleGround,
        Func<Vector3, Vector3, bool> clearSegment,
        out Vector3[] route)
    {
        _searchPending = false;
        return TryPlan(start, goal, sampleGround, clearSegment, null, out route);
    }

    internal bool TryPlan(
        Vector3 start,
        Vector3 goal,
        Func<Vector3, Vector3?> sampleGround,
        Func<Vector3, Vector3, bool> clearSegment,
        Func<bool> budgetAvailable,
        out Vector3[] route)
    {
        route = Array.Empty<Vector3>();
        LastExpandedNodeCount = 0;
        LastSampleCount = 0;
        LastSegmentCheckCount = 0;
        LastStatus = "searching";
        if (sampleGround == null || clearSegment == null)
            throw new ArgumentNullException(sampleGround == null
                ? nameof(sampleGround) : nameof(clearSegment));
        if (!Finite(start) || !Finite(goal))
        {
            _searchPending = false;
            LastStatus = "invalid_position";
            return false;
        }

        if (!_searchPending || Distance(start, _start) > 0.35f || Distance(goal, _goal) > 0.25f)
        {
            Reset();
            _start = start;
            _goal = goal;
        }
        _sampleGround = sampleGround;
        _clearSegment = clearSegment;
        _budgetAvailable = budgetAvailable;
        try
        {
            var found = Search(out route);
            _searchPending = !found && LastStatus == "query_budget";
            return found;
        }
        finally
        {
            _sampleGround = null;
            _clearSegment = null;
            _budgetAvailable = null;
        }
    }

    private bool Search(out Vector3[] route)
    {
        route = Array.Empty<Vector3>();
        if (_directPending)
        {
            if (!HasQueryBudget)
                return YieldSearch();
            var direct = SegmentClear(_start, _goal);
            if (!ExternalBudgetAvailable)
                return YieldSearch();
            if (direct)
            {
                route = new[] { _goal };
                LastStatus = "direct";
                return true;
            }
            _directPending = false;
            var goalDx = _goal.x - _start.x;
            var goalDz = _goal.z - _start.z;
            if (goalDx * goalDx + goalDz * goalDz > _radius * _radius)
            {
                LastStatus = "goal_outside_horizon";
                return false;
            }

            var initial = new Node(new Cell(0, 0, 0), _start, 0f, Distance(_start, _goal), -1);
            _nodes.Add(initial);
            _nodeIndices.Add(initial.Cell, 0);
        }
        while (HasQueryBudget)
        {
            if (_expandingNode < 0)
            {
                if (_expandedTotal >= _maximumExpandedNodes)
                {
                    LastStatus = "node_budget";
                    return false;
                }
                _expandingNode = SelectNext();
                if (_expandingNode < 0)
                {
                    LastStatus = "no_route";
                    return false;
                }
                _nodes[_expandingNode].Closed = true;
                LastExpandedNodeCount++;
                _expandedTotal++;
                _neighbor = 0;
                _goalCheckPending = true;
            }

            var currentIndex = _expandingNode;
            var current = _nodes[currentIndex];
            if (_goalCheckPending)
            {
                if (Distance(current.Position, _goal) <= _spacing * 2.25f)
                {
                    var connects = SegmentClear(current.Position, _goal);
                    if (!ExternalBudgetAvailable)
                        return YieldSearch();
                    if (connects)
                    {
                        route = BuildRoute(currentIndex);
                        LastStatus = "detour";
                        return true;
                    }
                }
                _goalCheckPending = false;
            }

            for (; _neighbor < NeighborX.Length; _neighbor++)
            {
                if (!HasQueryBudget)
                    return YieldSearch();
                var direction = _neighbor;
                var x = current.Cell.X + NeighborX[direction];
                var z = current.Cell.Z + NeighborZ[direction];
                if ((x * x + z * z) * _spacing * _spacing > _radius * _radius)
                    continue;
                var sampleCell = new Cell(x, z, HeightBand(current.Position.y));
                if (!_groundSamples.TryGetValue(sampleCell, out var grounded))
                {
                    LastSampleCount++;
                    grounded = _sampleGround(new Vector3(
                        _start.x + x * _spacing,
                        current.Position.y,
                        _start.z + z * _spacing));
                    if (!ExternalBudgetAvailable)
                        return YieldSearch();
                    if (grounded.HasValue && !Finite(grounded.Value))
                        grounded = null;
                    _groundSamples.Add(sampleCell, grounded);
                }
                if (!grounded.HasValue)
                    continue;

                var position = grounded.Value;
                var cell = new Cell(x, z, HeightBand(position.y));
                var exists = _nodeIndices.TryGetValue(cell, out var candidateIndex);
                if (exists)
                    position = _nodes[candidateIndex].Position;
                var cost = current.Cost + Distance(current.Position, position) +
                    Math.Abs(position.y - current.Position.y) * 0.4f;
                if (exists && _nodes[candidateIndex].Cost <= cost + 0.0001f)
                    continue;
                if (!HasQueryBudget)
                    return YieldSearch();
                var edgeClear = SegmentClear(current.Position, position);
                if (!ExternalBudgetAvailable)
                    return YieldSearch();
                if (!edgeClear)
                    continue;

                if (exists)
                {
                    var candidate = _nodes[candidateIndex];
                    candidate.Position = position;
                    candidate.Cost = cost;
                    candidate.Estimate = cost + Distance(position, _goal);
                    candidate.Parent = currentIndex;
                    candidate.Closed = false;
                }
                else
                {
                    _nodeIndices.Add(cell, _nodes.Count);
                    _nodes.Add(new Node(
                        cell, position, cost, cost + Distance(position, _goal), currentIndex));
                }
            }
            _expandingNode = -1;
        }

        return YieldSearch();
    }

    private bool YieldSearch()
    {
        LastStatus = "query_budget";
        return false;
    }

    private int SelectNext()
    {
        var selected = -1;
        for (var i = 0; i < _nodes.Count; i++)
        {
            var node = _nodes[i];
            if (node.Closed)
                continue;
            if (selected < 0 || node.Estimate < _nodes[selected].Estimate - 0.0001f ||
                (Math.Abs(node.Estimate - _nodes[selected].Estimate) <= 0.0001f &&
                 node.Cost > _nodes[selected].Cost))
                selected = i;
        }
        return selected;
    }

    private Vector3[] BuildRoute(int destinationIndex)
    {
        var reversed = new List<Vector3> { _goal };
        for (var index = destinationIndex; index > 0; index = _nodes[index].Parent)
        {
            var position = _nodes[index].Position;
            if (Distance(position, reversed[reversed.Count - 1]) > 0.001f)
                reversed.Add(position);
        }
        reversed.Reverse();
        var smoothed = new List<Vector3>();
        var anchor = _start;
        var next = 0;
        while (next < reversed.Count)
        {
            var selected = next;
            for (var later = reversed.Count - 1; later > next && HasQueryBudget; later--)
            {
                if (SegmentClear(anchor, reversed[later]))
                {
                    selected = later;
                    break;
                }
            }
            anchor = reversed[selected];
            smoothed.Add(anchor);
            next = selected + 1;
        }
        return smoothed.ToArray();
    }

    private bool ExternalBudgetAvailable => _budgetAvailable == null || _budgetAvailable();
    private bool HasQueryBudget => LastQueryCount < _maximumQueries && ExternalBudgetAvailable;

    private bool SegmentClear(Vector3 from, Vector3 to)
    {
        if (!HasQueryBudget)
            return false;
        LastSegmentCheckCount++;
        var clear = _clearSegment(from, to);
        return clear && ExternalBudgetAvailable;
    }

    private int HeightBand(float height) =>
        (int)Math.Round((height - _start.y) / HeightCellSize, MidpointRounding.AwayFromZero);

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);

    private static float Distance(Vector3 from, Vector3 to)
    {
        var x = from.x - to.x;
        var y = from.y - to.y;
        var z = from.z - to.z;
        return (float)Math.Sqrt(x * x + y * y + z * z);
    }

    private sealed class Node
    {
        internal readonly Cell Cell;
        internal Vector3 Position;
        internal float Cost;
        internal float Estimate;
        internal int Parent;
        internal bool Closed;

        internal Node(Cell cell, Vector3 position, float cost, float estimate, int parent)
        {
            Cell = cell;
            Position = position;
            Cost = cost;
            Estimate = estimate;
            Parent = parent;
        }
    }

    private readonly struct Cell : IEquatable<Cell>
    {
        internal readonly int X;
        internal readonly int Z;
        private readonly int _height;

        internal Cell(int x, int z, int height)
        {
            X = x;
            Z = z;
            _height = height;
        }

        public bool Equals(Cell other) => X == other.X && Z == other.Z && _height == other._height;
        public override bool Equals(object other) => other is Cell cell && Equals(cell);
        public override int GetHashCode() => unchecked((X * 397 ^ Z) * 397 ^ _height);
    }
}
