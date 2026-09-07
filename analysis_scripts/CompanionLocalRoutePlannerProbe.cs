using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityEngine
{
    internal struct Vector3
    {
        internal float x;
        internal float y;
        internal float z;

        internal Vector3(float x, float y, float z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }
    }
}

namespace Ramblers
{
    internal static class CompanionLocalRoutePlannerProbe
    {
        private static int Main()
        {
            try
            {
                DirectRouteCostsOneQuery();
                ConcaveObstacleRequiresMovingAway();
                BendingCorridorIsTraversable();
                UnreachableDestinationNeverReturnsAFrontier();
                SeparateFloorsRemainSeparateNodes();
                SmallSupportSeamDoesNotEraseTheRoute();
                BudgetsBoundActualCallbacks();
                CompletedRouteSurvivesSmoothingBudgetExhaustion();
                PlanningStateIsResetAcrossAttempts();
                TranslationDoesNotChangeTheDetour();
                Console.WriteLine("Companion local route planner: 10 behavioral checks passed.");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 1;
            }
        }

        private static void DirectRouteCostsOneQuery()
        {
            var planner = new CompanionLocalRoutePlanner();
            var start = Point(0, 0);
            var goal = Point(5, 3);
            var sampleCalls = 0;
            var segmentCalls = 0;
            Expect(planner.TryPlan(start, goal,
                candidate => { sampleCalls++; return candidate; },
                (from, to) => { segmentCalls++; return true; }, out var route), "open ground failed");
            Expect(route.Length == 1 && Same(route[0], goal), "direct route changed the goal");
            Expect(sampleCalls == 0 && segmentCalls == 1 && planner.LastQueryCount == 1,
                "open ground did unnecessary physics work");
        }

        private static void ConcaveObstacleRequiresMovingAway()
        {
            Func<Vector3, bool> open = point =>
                !Inside(point, -1.8f, -1.2f, -1.5f, 3.3f) &&
                !Inside(point, 1.2f, 1.8f, -1.5f, 3.3f) &&
                !Inside(point, -1.8f, 1.8f, 2.7f, 3.3f);
            var start = Point(0, 0);
            var goal = Point(0, 5);
            var planner = new CompanionLocalRoutePlanner();
            var clear = SegmentPredicate(open);
            Expect(planner.TryPlan(start, goal, FlatSampler(open), clear, out var route),
                "U-shaped enclosure had no detour: " + planner.LastStatus);
            ValidateRoute(start, goal, route, clear);
            Expect(Array.Exists(route, point => point.z < -1.5f),
                "U-shaped detour never exited through the opening behind the start");
            Expect(planner.LastQueryCount <= 640 && planner.LastExpandedNodeCount <= 160,
                "U-shaped detour exceeded its default work budget");
            Console.WriteLine("U detour: nodes=" + planner.LastExpandedNodeCount +
                ", queries=" + planner.LastQueryCount + ", waypoints=" + route.Length);
        }

        private static void BendingCorridorIsTraversable()
        {
            Func<Vector3, bool> open = point =>
                Inside(point, -0.65f, 3.65f, -0.65f, 0.65f) ||
                Inside(point, 2.35f, 3.65f, -0.65f, 4.65f) ||
                Inside(point, 2.35f, 5.65f, 3.35f, 4.65f);
            var start = Point(0, 0);
            var goal = Point(5, 4);
            var clear = SegmentPredicate(open);
            var planner = new CompanionLocalRoutePlanner();
            Expect(planner.TryPlan(start, goal, FlatSampler(open), clear, out var route),
                "bending corridor failed: " + planner.LastStatus);
            ValidateRoute(start, goal, route, clear);
            Expect(route.Length >= 3, "corridor smoothing cut a solid corner");
        }

        private static void UnreachableDestinationNeverReturnsAFrontier()
        {
            Func<Vector3, bool> open = point =>
                !Inside(point, 1.5f, 4.5f, -1.5f, 1.5f);
            var planner = new CompanionLocalRoutePlanner();
            Expect(!planner.TryPlan(Point(0, 0), Point(3, 0), FlatSampler(open),
                SegmentPredicate(open), out var route), "blocked destination was reported reachable");
            Expect(route.Length == 0, "blocked destination returned a misleading partial route");
        }

        private static void SeparateFloorsRemainSeparateNodes()
        {
            var ramp = new[]
            {
                new Vector3(0, 0, 0),
                new Vector3(1, 0.5f, 0),
                new Vector3(2, 1, 0),
                new Vector3(2, 1.5f, 1),
                new Vector3(2, 2, 2),
                new Vector3(1, 2.5f, 2),
                new Vector3(0, 3, 2),
                new Vector3(0, 3.5f, 1),
                new Vector3(0, 4, 0),
                new Vector3(-1, 4, 0),
                new Vector3(-2, 4, 0)
            };
            Func<Vector3, Vector3?> sample = candidate =>
            {
                Vector3? selected = null;
                var heightDistance = float.MaxValue;
                foreach (var surface in ramp)
                {
                    var distance = Math.Abs(surface.y - candidate.y);
                    if (Math.Abs(surface.x - candidate.x) < 0.01f &&
                        Math.Abs(surface.z - candidate.z) < 0.01f && distance < heightDistance)
                    {
                        selected = surface;
                        heightDistance = distance;
                    }
                }
                return selected;
            };
            Func<Vector3, Vector3, bool> clear = (from, to) =>
            {
                var fromIndex = Array.FindIndex(ramp, point => Same(point, from));
                var toIndex = Array.FindIndex(ramp, point => Same(point, to));
                return fromIndex >= 0 && toIndex >= 0 && Math.Abs(fromIndex - toIndex) <= 1;
            };
            var planner = new CompanionLocalRoutePlanner();
            Expect(planner.TryPlan(ramp[0], ramp[ramp.Length - 1], sample, clear, out var route),
                "spiral ramp lost its upper-floor return through the start column: " + planner.LastStatus);
            ValidateRoute(ramp[0], ramp[ramp.Length - 1], route, clear);
            Expect(Array.Exists(route, point => Same(point, ramp[8])),
                "upper floor node at the start column was collapsed into the lower floor");
        }

        private static void SmallSupportSeamDoesNotEraseTheRoute()
        {
            Func<Vector3, bool> open = point => !Inside(point, 1.4f, 2.6f, -0.8f, 0.8f);
            var seamFallbacks = 0;
            Func<Vector3, Vector3?> sample = candidate =>
            {
                if (!open(candidate))
                    return null;
                if (Math.Abs(candidate.x - 1f) < 0.01f)
                {
                    seamFallbacks++;
                    return candidate;
                }
                return new Vector3(candidate.x, 0f, candidate.z);
            };
            var planner = new CompanionLocalRoutePlanner();
            var start = Point(0, 0);
            var goal = Point(4, 0);
            var clear = SegmentPredicate(open);
            Expect(planner.TryPlan(start, goal, sample, clear, out var route),
                "tolerated support seam prevented a detour: " + planner.LastStatus);
            ValidateRoute(start, goal, route, clear);
            Expect(seamFallbacks > 0, "seam test never exercised uncertain support");
        }

        private static void BudgetsBoundActualCallbacks()
        {
            var queries = 0;
            var planner = new CompanionLocalRoutePlanner(maximumExpandedNodes: 160, maximumQueries: 17);
            Func<Vector3, Vector3?> sample = candidate => { queries++; return candidate; };
            Func<Vector3, Vector3, bool> clear = (from, to) =>
            {
                queries++;
                return !Same(to, Point(5, 0));
            };
            Expect(!planner.TryPlan(Point(0, 0), Point(5, 0), sample, clear, out var route),
                "budgeted search unexpectedly reached the unavailable goal");
            Expect(queries <= 17 && queries == planner.LastQueryCount && route.Length == 0,
                "query budget did not bound actual callback invocations");
            Expect(planner.LastStatus == "query_budget", "query exhaustion was not observable");

            var nodeBound = new CompanionLocalRoutePlanner(maximumExpandedNodes: 2, maximumQueries: 640);
            Expect(!nodeBound.TryPlan(Point(0, 0), Point(5, 0), candidate => candidate,
                (from, to) => !Same(to, Point(5, 0)), out route), "node-bounded search reached a blocked goal");
            Expect(nodeBound.LastExpandedNodeCount == 2 && nodeBound.LastStatus == "node_budget",
                "expanded-node budget was exceeded or obscured");
        }

        private static void PlanningStateIsResetAcrossAttempts()
        {
            var planner = new CompanionLocalRoutePlanner(maximumQueries: 19);
            Expect(!planner.TryPlan(Point(0, 0), Point(4, 0), candidate => candidate,
                (from, to) => false, out var failed), "sealed start unexpectedly escaped");
            Expect(planner.TryPlan(Point(100, 100), Point(104, 100), candidate => candidate,
                (from, to) => true, out var succeeded), "failed search contaminated the next attempt");
            Expect(planner.LastStatus == "direct" && planner.LastQueryCount == 1 &&
                succeeded.Length == 1 && Same(succeeded[0], Point(104, 100)),
                "new search retained previous nodes or telemetry");
        }

        private static void CompletedRouteSurvivesSmoothingBudgetExhaustion()
        {
            Func<Vector3, bool> open = point =>
                !Inside(point, -1.8f, -1.2f, -1.5f, 3.3f) &&
                !Inside(point, 1.2f, 1.8f, -1.5f, 3.3f) &&
                !Inside(point, -1.8f, 1.8f, 2.7f, 3.3f);
            var clear = SegmentPredicate(open);
            var exhaustedAfterFindingRoute = false;
            for (var budget = 10; budget <= 200; budget++)
            {
                var planner = new CompanionLocalRoutePlanner(maximumQueries: budget);
                if (!planner.TryPlan(Point(0, 0), Point(0, 5), FlatSampler(open), clear, out var route))
                    continue;
                ValidateRoute(Point(0, 0), Point(0, 5), route, clear);
                Expect(planner.LastQueryCount <= budget, "smoothing exceeded the shared physics budget");
                if (planner.LastQueryCount == budget)
                {
                    exhaustedAfterFindingRoute = true;
                    break;
                }
            }
            Expect(exhaustedAfterFindingRoute,
                "no complete route survived query exhaustion during smoothing");
        }

        private static void TranslationDoesNotChangeTheDetour()
        {
            Vector3[] reference = null;
            foreach (var translation in new[] { new Vector3(0, 0, 0), new Vector3(-237.31f, 37.61f, -492.76f) })
            {
                Func<Vector3, bool> open = point =>
                {
                    var local = new Vector3(point.x - translation.x, 0, point.z - translation.z);
                    return !Inside(local, 1.4f, 2.6f, -0.8f, 0.8f);
                };
                var start = translation;
                var goal = new Vector3(start.x + 4, start.y, start.z);
                var planner = new CompanionLocalRoutePlanner();
                var clear = SegmentPredicate(open);
                Expect(planner.TryPlan(start, goal, candidate => open(candidate) ?
                    new Vector3(candidate.x, translation.y, candidate.z) : (Vector3?)null,
                    clear, out var route), "translated detour failed");
                ValidateRoute(start, goal, route, clear);
                if (reference == null)
                    reference = route;
                else
                {
                    Expect(reference.Length == route.Length, "world offset changed route shape");
                    for (var i = 0; i < route.Length; i++)
                        Expect(Same(reference[i], new Vector3(route[i].x - translation.x,
                            route[i].y - translation.y, route[i].z - translation.z)),
                            "world offset changed the local search lattice");
                }
            }
        }

        private static Func<Vector3, Vector3?> FlatSampler(Func<Vector3, bool> open) =>
            candidate => open(candidate) ? new Vector3(candidate.x, 0, candidate.z) : (Vector3?)null;

        private static Func<Vector3, Vector3, bool> SegmentPredicate(Func<Vector3, bool> open) =>
            (from, to) =>
            {
                var distance = Math.Sqrt((to.x - from.x) * (to.x - from.x) +
                    (to.z - from.z) * (to.z - from.z));
                var steps = Math.Max(1, (int)Math.Ceiling(distance / 0.025));
                for (var i = 0; i <= steps; i++)
                {
                    var fraction = (float)i / steps;
                    if (!open(new Vector3(from.x + (to.x - from.x) * fraction,
                        from.y + (to.y - from.y) * fraction, from.z + (to.z - from.z) * fraction)))
                        return false;
                }
                return true;
            };

        private static void ValidateRoute(Vector3 start, Vector3 goal, Vector3[] route,
            Func<Vector3, Vector3, bool> clear)
        {
            Expect(route.Length > 0 && Same(route[route.Length - 1], goal), "route lost the exact destination");
            var previous = start;
            foreach (var point in route)
            {
                Expect(clear(previous, point), "route crossed a blocked segment");
                previous = point;
            }
        }

        private static bool Inside(Vector3 point, float minimumX, float maximumX,
            float minimumZ, float maximumZ) => point.x >= minimumX && point.x <= maximumX &&
            point.z >= minimumZ && point.z <= maximumZ;

        private static Vector3 Point(float x, float z) => new Vector3(x, 0, z);

        private static bool Same(Vector3 first, Vector3 second) =>
            Math.Abs(first.x - second.x) < 0.001f && Math.Abs(first.y - second.y) < 0.001f &&
            Math.Abs(first.z - second.z) < 0.001f;

        private static void Expect(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
    }
}
