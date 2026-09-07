using System;
using UnityEngine;

namespace Ramblers;

internal static class CompanionLocalRoutePlannerBudgetProbe
{
    private static int Main()
    {
        ResumeNativeGeometrySearch(1200);
        ResumeNativeGeometrySearch(100);
        InterruptedNeighborIsRetried();
        ChangedGoalStartsNewSearch();
        DriveBudgetedFollower();
        Console.WriteLine("Companion route search budget: 5 checks passed.");
        return 0;
    }

    private static void ResumeNativeGeometrySearch(int budget)
    {
        CompanionBody body;
        var geometry = CreateGeometry(out body);
        var planner = new CompanionLocalRoutePlanner();
        Vector3[] route = null;
        var found = false;
        var passes = 0;
        while (!found && passes++ < 100)
        {
            var before = geometry.NativeQueryCount;
            geometry.RunWithQueryBudget(budget, () => found = planner.TryPlan(
                Vector3.zero, new Vector3(0, 0, 5),
                candidate => geometry.TryGroundPoint(candidate, out var support)
                    ? (Vector3?)support : null,
                geometry.CanWalkSegment, () => !geometry.QueryBudgetExhausted, out route));
            Expect(geometry.NativeQueryCount - before <= budget, "native pass exceeded its budget");
            Expect(found || planner.LastStatus == "query_budget",
                "unfinished native search was classified as " + planner.LastStatus);
        }
        Expect(found && passes > 1, "native-budget search did not resume to a complete route");
        var anchor = Vector3.zero;
        foreach (var waypoint in route)
        {
            Expect(geometry.CanWalkSegment(anchor, waypoint), "resumed route contained an untested edge");
            anchor = waypoint;
        }
        Expect(Vector3.Distance(anchor, new Vector3(0, 0, 5)) < 0.001f,
            "resumed route did not end at requested goal");
        Console.WriteLine("  budget=" + budget + ", passes=" + passes);
    }

    private static CompanionNavigationGeometry CreateGeometry(out CompanionBody body)
    {
        body = new CompanionBody();
        body.Character.collision.bodyCollider.bounds = new Bounds
        {
            center = new Vector3(0f, 0.75f, 0f),
            size = new Vector3(0.5f, 1.5f, 0.5f)
        };
        PlayerGround.CastHits = Array.Empty<RaycastHit>();
        Physics.Ray = (origin, direction, distance) =>
        {
            if (direction.y < -0.9f)
                return new RaycastHit
                {
                    collider = new Collider(), normal = Vector3.up,
                    point = new Vector3(origin.x, 0f, origin.z), distance = origin.y
                };
            for (var sample = 0; sample <= 100; sample++)
            {
                var point = origin + direction * (distance * sample / 100f);
                if (InsideWall(point))
                    return new RaycastHit
                    {
                        collider = new Collider(), normal = new Vector3(1, 0, 0),
                        point = point, distance = distance * sample / 100f
                    };
            }
            return null;
        };
        var geometry = new CompanionNavigationGeometry();
        geometry.Bind(body);
        return geometry;
    }

    private static void DriveBudgetedFollower()
    {
        CompanionBody body;
        var geometry = CreateGeometry(out body);
        Func<Vector3, Vector3?> support = point => geometry.TryGroundPoint(point, out var ground)
            ? (Vector3?)ground : null;
        var navigation = new CompanionFollowNavigation(support, geometry.CanWalkSegment,
            geometry.IsSegmentClear, () => !geometry.QueryBudgetExhausted);
        var selector = new CompanionFollowRoutePlanner(support, navigation.IsRouteSegmentAvailable,
            () => !geometry.QueryBudgetExhausted);
        var history = new BreadcrumbTrail(20);
        var goal = new Vector3(0, 0, 5);
        var revision = -1;
        var pending = 0;
        var completedRoute = false;
        for (var tick = 0; tick < 300 && Vector3.Distance(body.Position, goal) > 0.3f; tick++)
        {
            CompanionFollowRoute route = null;
            var before = geometry.NativeQueryCount;
            geometry.RunWithQueryBudget(1200, () => route = selector.Select(body.Position, goal, history, tick * 0.1f));
            Expect(geometry.NativeQueryCount - before <= 1200, "selector exceeded production budget");
            if (route.SearchPending) pending++;
            completedRoute |= route.Kind == CompanionFollowRouteKind.Walking;
            if (revision != route.Revision)
            {
                revision = route.Revision;
                navigation.AcceptRoute(route.Destination, route.Hint.Sequence, route.Waypoints, tick * 0.1f,
                    route.SearchPending);
            }
            var step = default(CompanionNavigationStep);
            before = geometry.NativeQueryCount;
            geometry.RunWithQueryBudget(384, () => step = navigation.Tick(body.Position, route.Destination,
                route.Hint.Sequence, true, tick * 0.1f));
            Expect(geometry.NativeQueryCount - before <= 384, "execution exceeded production budget");
            if (step.GoalStalled && !route.SearchPending) selector.ReportFailure(tick * 0.1f);
            var next = body.Position + step.Direction * 0.2f;
            if (geometry.IsSegmentClear(body.Position, next)) body.Position = next;
        }
        Expect(pending > 0 && completedRoute && Vector3.Distance(body.Position, goal) <= 0.3f,
            "production selector/executor budgets failed to navigate the synthetic U");
    }

    private static void InterruptedNeighborIsRetried()
    {
        var planner = new CompanionLocalRoutePlanner();
        var remaining = 3;
        var sampled = 0;
        var edgeAttempts = 0;
        Func<Vector3, Vector3?> support = point => { remaining--; sampled++; return point; };
        Func<Vector3, Vector3, bool> clear = (from, to) =>
        {
            remaining--;
            if (Vector3.Distance(from, Vector3.zero) < 0.01f &&
                Vector3.Distance(to, new Vector3(1, 0, 0)) < 0.01f)
                edgeAttempts++;
            return to.z >= 1f || Vector3.Distance(from, to) < 1.5f;
        };
        Vector3[] route;
        Expect(!planner.TryPlan(Vector3.zero, new Vector3(3, 0, 0), support,
                clear, () => remaining > 0, out route) && planner.LastStatus == "query_budget",
            "edge interruption did not yield");
        Expect(sampled == 1 && edgeAttempts == 1, "fixture did not interrupt the first edge");
        remaining = 1000;
        Expect(planner.TryPlan(Vector3.zero, new Vector3(3, 0, 0), support,
                clear, () => remaining > 0, out route), "interrupted edge was treated as blocked");
        Expect(edgeAttempts == 2, "interrupted neighbor was not retried exactly once");
    }

    private static void ChangedGoalStartsNewSearch()
    {
        var planner = new CompanionLocalRoutePlanner();
        Vector3[] route;
        Expect(!planner.TryPlan(Vector3.zero, new Vector3(3, 0, 0), point => point,
                (from, to) => false, () => false, out route), "empty budget did not yield");
        Expect(planner.TryPlan(Vector3.zero, new Vector3(-3, 0, 0), point => point,
                (from, to) => true, () => true, out route), "new goal inherited exhausted search");
        Expect(route.Length == 1 && route[0].x == -3f, "new goal reused old destination");
    }

    private static bool InsideWall(Vector3 point) =>
        (point.x >= -1.8f && point.x <= -1.2f && point.z >= -1.5f && point.z <= 3.3f) ||
        (point.x >= 1.2f && point.x <= 1.8f && point.z >= -1.5f && point.z <= 3.3f) ||
        (point.x >= -1.8f && point.x <= 1.8f && point.z >= 2.7f && point.z <= 3.3f);

    private static void Expect(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
