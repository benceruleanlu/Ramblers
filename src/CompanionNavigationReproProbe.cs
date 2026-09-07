using System;
using System.Text;
using UnityEngine;

namespace Ramblers;

internal static class CompanionNavigationReproProbe
{
    private const int NativeQueryBudget = 1500;
    private static readonly Vector3 RecordedGoal = new Vector3(-216.30f, 33.93f, -509.11f);
    private static readonly Vector3 LaterHuman = new Vector3(-223.67f, 33.01f, -513.98f);
    private static readonly Vector3[] Approaches =
    {
        new Vector3(-214.21f, 33.09f, -511.50f),
        new Vector3(-215.35f, 32.68f, -510.14f),
        new Vector3(-215.63f, 32.77f, -509.82f),
        new Vector3(-215.48f, 32.73f, -510.93f)
    };

    internal static void Run(CompanionNavigationGeometry geometry, Vector3 bodyPosition)
    {
        if (geometry == null)
            return;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var initialQueries = geometry.NativeQueryCount;
        var exhausted = false;
        Plugin.Logger.LogInfo("[FOLLOW_REPRO] START " +
            $"body={bodyPosition}, recordedBreadcrumb=81, rawGoal={RecordedGoal}, " +
            $"laterHuman={LaterHuman}, queryBudget={NativeQueryBudget}, movementRequested=false.");
        try
        {
            geometry.RunWithQueryBudget(NativeQueryBudget, () =>
            {
                Plugin.Logger.LogInfo("[FOLLOW_REPRO] BODY " + geometry.DescribeBodyGeometry());
                Vector3 bodySupport;
                Plugin.Logger.LogInfo("[FOLLOW_REPRO] SUPPORT id=current_body, " +
                    geometry.DescribeGroundSample(bodyPosition, false, out bodySupport));
                Vector3 unrestrictedGoal;
                Plugin.Logger.LogInfo("[FOLLOW_REPRO] SUPPORT id=goal_unrestricted, " +
                    geometry.DescribeGroundSample(RecordedGoal, false, out unrestrictedGoal));
                Vector3 projectedGoal;
                Plugin.Logger.LogInfo("[FOLLOW_REPRO] SUPPORT id=goal_ordinary, " +
                    geometry.DescribeGroundSample(RecordedGoal, true, out projectedGoal));

                for (var index = 0; index < Approaches.Length && !geometry.QueryBudgetExhausted; index++)
                {
                    var position = Approaches[index];
                    Vector3 supported;
                    Plugin.Logger.LogInfo($"[FOLLOW_REPRO] SUPPORT id=approach{index}, " +
                        geometry.DescribeGroundSample(position, false, out supported));
                    LogSegment(geometry, $"approach{index}_raw", position, RecordedGoal);
                    LogSegment(geometry, $"approach{index}_projected", supported, projectedGoal);
                }

                for (var x = -1; x <= 1 && !geometry.QueryBudgetExhausted; x++)
                {
                    for (var z = -1; z <= 1 && !geometry.QueryBudgetExhausted; z++)
                    {
                        var candidate = new Vector3(-215.5f + x, 33.0f, -510.0f + z);
                        Vector3 supported;
                        Plugin.Logger.LogInfo($"[FOLLOW_REPRO] SUPPORT id=grid_{x}_{z}, " +
                            geometry.DescribeGroundSample(candidate, false, out supported));
                    }
                }

                var start = Approaches[1];
                if (geometry.TryGroundPoint(start, out var projectedStart))
                    start = projectedStart;
                RunPlan(geometry, "projected_breadcrumb81", start, projectedGoal, 650);
                var offset = LaterHuman - start;
                var horizontal = offset;
                horizontal.y = 0f;
                var localHuman = horizontal.magnitude > 5.5f
                    ? start + offset * (5.5f / horizontal.magnitude)
                    : LaterHuman;
                if (geometry.TryGroundRoutePoint(localHuman, out var projectedHuman))
                    localHuman = projectedHuman;
                RunPlan(geometry, "toward_later_human", start, localHuman, 650);
                exhausted = geometry.QueryBudgetExhausted;
            });
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning("[FOLLOW_REPRO] FAILED " +
                $"type={exception.GetType().Name}, message={exception.Message}.");
        }
        var milliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - started) *
                           1000.0 / System.Diagnostics.Stopwatch.Frequency;
        Plugin.Logger.LogInfo("[FOLLOW_REPRO] COMPLETE " +
            $"nativeQueries={geometry.NativeQueryCount - initialQueries}, " +
            $"elapsedMs={milliseconds:F2}, budgetExhausted={exhausted}, movementRequested=false.");
    }

    private static void LogSegment(
        CompanionNavigationGeometry geometry,
        string label,
        Vector3 from,
        Vector3 to)
    {
        if (geometry.QueryBudgetExhausted)
            return;
        var delta = to - from;
        var distance = delta.magnitude;
        string hit;
        var clearance = geometry.MeasureClearance(from, delta, distance, out hit);
        Plugin.Logger.LogInfo("[FOLLOW_REPRO] SEGMENT " +
            $"id={label}, from={from}, to={to}, distance={distance:F3}, " +
            $"clearance={clearance:F3}, clear={clearance >= distance - 0.06f}, hit={hit}, " +
            $"queryBudgetExhausted={geometry.QueryBudgetExhausted}.");
    }

    private static void RunPlan(
        CompanionNavigationGeometry geometry,
        string label,
        Vector3 start,
        Vector3 goal,
        int queryBudget)
    {
        if (geometry.QueryBudgetExhausted)
            return;
        var initialQueries = geometry.NativeQueryCount;
        var planner = new CompanionLocalRoutePlanner(64, 200, 8f);
        var planned = false;
        var exhausted = false;
        var route = Array.Empty<Vector3>();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        geometry.RunWithQueryBudget(queryBudget, () =>
        {
            planned = planner.TryPlan(start, goal,
                candidate =>
                {
                    if (geometry.QueryBudgetExhausted)
                        return null;
                    var found = geometry.TryGroundPoint(candidate, out var supported);
                    return geometry.QueryBudgetExhausted
                        ? (Vector3?)null : found ? supported : candidate;
                },
                (from, to) => !geometry.QueryBudgetExhausted &&
                              geometry.CanWalkSegment(from, to) &&
                              !geometry.QueryBudgetExhausted,
                out route);
            exhausted = geometry.QueryBudgetExhausted;
        });
        var milliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - started) *
                           1000.0 / System.Diagnostics.Stopwatch.Frequency;
        var points = new StringBuilder();
        for (var index = 0; index < route.Length; index++)
        {
            if (index > 0)
                points.Append(" -> ");
            points.Append(route[index]);
        }
        Plugin.Logger.LogInfo("[FOLLOW_REPRO] PLAN " +
            $"id={label}, start={start}, goal={goal}, complete={planned && !exhausted}, " +
            $"status={planner.LastStatus}, nodes={planner.LastExpandedNodeCount}, " +
            $"plannerQueries={planner.LastQueryCount}, " +
            $"nativeQueries={geometry.NativeQueryCount - initialQueries}, elapsedMs={milliseconds:F2}, " +
            $"budgetExhausted={exhausted}, route=[{points}].");
    }
}
