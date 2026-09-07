using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Ramblers;

internal static class CompanionFollowRoutePlannerProbe
{
    private static int Main()
    {
        var failures = 0;
        Run("arrived history cannot ping-pong", ArrivedHistoryCannotPingPong, ref failures);
        Run("walking route survives periodic selection", WalkingRoutePersists, ref failures);
        Run("traversal approach survives periodic selection", TraversalRoutePersists, ref failures);
        Run("uncertain attempts reconsider new evidence", UncertainAttemptsReconsider, ref failures);
        Run("connector arrival respects height", ConnectorArrivalRespectsHeight, ref failures);
        Run("failed hint memory expires and stays bounded", FailedHintMemoryIsBounded, ref failures);
        Run("human search owns its pending frontier", PendingHumanSearchContinues, ref failures);
        Run("candidate search does not restart human search", PendingCandidateSearchContinues, ref failures);
        Run("pending search cancels on drift and failure", PendingSearchCancels, ref failures);
        return failures == 0 ? 0 : 1;
    }

    private static void ArrivedHistoryCannotPingPong()
    {
        var history = new BreadcrumbTrail(20);
        var old = history.Add(Point(0), false, false);
        var newest = history.Add(Point(2), false, false);
        Func<Vector3, Vector3, bool> walk = (from, to) => from.x <= 2.01f && to.x <= 2.01f;
        var selector = new CompanionFollowRoutePlanner(point => point, walk);
        var navigation = new CompanionFollowNavigation(point => point, walk);
        var body = Point(1);
        var revision = -1;
        var sawNewest = false;
        for (var tick = 0; tick < 200; tick++)
        {
            var now = tick * 0.1f;
            var route = selector.Select(body, Point(10), history, now);
            if (route.Hint.Sequence == newest.Sequence)
                sawNewest = true;
            Expect(!sawNewest || route.Hint.Sequence != old.Sequence,
                "arrival selected an older historical destination again");
            if (route.Revision != revision)
            {
                navigation.AcceptRoute(route.Destination, route.Hint.Sequence, route.Waypoints, now);
                revision = route.Revision;
            }
            var step = navigation.Tick(body, route.Destination, route.Hint.Sequence, true, now);
            if (step.GoalStalled)
                selector.ReportFailure(now);
            var next = body + step.Direction * 0.2f;
            if (walk(body, next))
                body = next;
        }
        Expect(sawNewest && history.Count == 0, "arrived ordinary connector prefix survived");
    }

    private static void WalkingRoutePersists()
    {
        var selector = new CompanionFollowRoutePlanner(point => point, (from, to) => true);
        var history = new BreadcrumbTrail(20);
        var first = selector.Select(Point(0), Point(5), history, 0);
        var retained = selector.Select(Point(1), Point(5), history, 10);
        Expect(retained.Revision == first.Revision, "periodic selection replaced an executing walking route");
        selector.ReportFailure(10);
        var reconsidered = selector.Select(Point(1), Point(5), history, 10.1f);
        Expect(reconsidered.Revision != first.Revision, "execution failure did not reconsider the route");
        var movedHuman = selector.Select(Point(1), Point(7), history, 10.2f);
        Expect(movedHuman.Revision != reconsidered.Revision, "human movement did not update the goal");
    }

    private static void TraversalRoutePersists()
    {
        var selector = new CompanionFollowRoutePlanner(point => point,
            (from, to) => from.x <= 0.1f && to.x <= 0.1f);
        var history = new BreadcrumbTrail(20);
        var jump = history.AddTraversal(Point(0), Point(3), true, false, 0.8f);
        var first = selector.Select(Point(0), Point(10), history, 0);
        var retained = selector.Select(Point(0), Point(10), history, 10);
        Expect(first.Kind == CompanionFollowRouteKind.Traversal && first.Hint.Sequence == jump.Sequence,
            "explicit jump route was not available");
        Expect(retained.Revision == first.Revision && history.Count == 2,
            "takeoff arrival consumed or periodically replaced the traversal");
    }

    private static void UncertainAttemptsReconsider()
    {
        var selector = new CompanionFollowRoutePlanner(point => null, (from, to) => false);
        var history = new BreadcrumbTrail(20);
        var first = selector.Select(Point(0), Point(10), history, 0);
        Expect(selector.Select(Point(0), Point(10), history, 0.5f).Revision == first.Revision,
            "uncertain attempt replanned before its cadence");
        history.Add(Point(3), false, false);
        var newHint = selector.Select(Point(0), Point(10), history, 0.6f);
        Expect(newHint.Revision != first.Revision, "new hint did not reconsider an uncertain attempt");
        Expect(selector.Select(Point(0), Point(10), history, 1.7f).Revision != newHint.Revision,
            "uncertain attempt never reconsidered");
    }

    private static void ConnectorArrivalRespectsHeight()
    {
        var history = new BreadcrumbTrail(20);
        history.Add(new Vector3(1, 2, 0), false, false);
        var selector = new CompanionFollowRoutePlanner(point => point,
            (from, to) => to.x <= 1.01f);
        var first = selector.Select(Point(0), Point(10), history, 0);
        var retained = selector.Select(Point(1), Point(10), history, 2);
        Expect(first.Hint.Sequence != 0 && retained.Hint.Sequence == first.Hint.Sequence && history.Count == 1,
            "horizontal proximity consumed a connector on another level");
    }

    private static void FailedHintMemoryIsBounded()
    {
        var history = new BreadcrumbTrail(40);
        var selector = new CompanionFollowRoutePlanner(point => point,
            (from, to) => from.x <= 2.01f && to.x <= 2.01f);
        for (var index = 0; index < 30; index++)
        {
            history.Add(Point(1 + index * 0.02f), false, false);
            var route = selector.Select(Point(0), Point(10), history, index * 0.1f);
            Expect(route.Hint.Sequence != 0, "failed hint fixture did not select a connector");
            selector.ReportFailure(index * 0.1f);
        }
        var field = typeof(CompanionFollowRoutePlanner).GetField("_failedHints", BindingFlags.Instance | BindingFlags.NonPublic);
        var failures = (Dictionary<int, float>)field.GetValue(selector);
        Expect(failures.Count <= 16, "failed hint memory grew without a bound");
        selector.Select(Point(0), Point(10), history, 20);
        Expect(failures.Count == 0, "expired failed hints were retained");
    }

    private static void PendingHumanSearchContinues()
    {
        var budget = 0;
        var human = new Vector3(0, 0, 5);
        var directRequests = 0;
        Func<Vector3, bool> open = point =>
            !Inside(point, -1.8f, -1.2f, -1.5f, 3.3f) &&
            !Inside(point, 1.2f, 1.8f, -1.5f, 3.3f) &&
            !Inside(point, -1.8f, 1.8f, 2.7f, 3.3f);
        var selector = new CompanionFollowRoutePlanner(
            point => { budget--; return point; },
            (from, to) =>
            {
                budget--;
                if (Vector3.Distance(from, Point(0)) < 0.001f && Vector3.Distance(to, human) < 0.001f)
                    directRequests++;
                return Clear(from, to, open);
            }, () => budget > 0);
        var history = new BreadcrumbTrail(20);
        history.AddTraversal(Point(0), new Vector3(0, 0, 4), true, false, 1);
        var yielded = false;
        for (var tick = 0; tick < 200; tick++)
        {
            budget = 12;
            var route = selector.Select(new Vector3(tick * 0.001f, 0, 0), human, history, tick * 0.1f);
            if (route.SearchPending)
            {
                yielded = true;
                Expect(route.Kind == CompanionFollowRouteKind.Attempt && route.Hint.Sequence == 0,
                    "pending human search exposed a traversal before planning completed");
                continue;
            }
            Expect(yielded && route.Kind == CompanionFollowRouteKind.Walking && route.Hint.Sequence == 0,
                "human detour was not completed across bounded search ticks");
            Expect(directRequests <= 2, "pending human search repeatedly restarted with the moving body");
            return;
        }
        throw new InvalidOperationException("human frontier never finished across query budgets");
    }

    private static void PendingCandidateSearchContinues()
    {
        var budget = 0;
        var humanRequests = 0;
        var history = new BreadcrumbTrail(20);
        var hint = history.Add(Point(4), false, false);
        var selector = new CompanionFollowRoutePlanner(
            point => { budget--; return point; },
            (from, to) =>
            {
                budget--;
                if (Vector3.Distance(from, Point(0)) < 0.001f && Vector3.Distance(to, Point(10)) < 0.001f)
                    humanRequests++;
                return Clear(from, to, point => !Inside(point, 1.2f, 1.8f, -1.3f, 1.3f));
            }, () => budget > 0);
        var yielded = false;
        for (var tick = 0; tick < 100; tick++)
        {
            budget = 10;
            var route = selector.Select(Point(0), Point(10), history, tick * 0.1f);
            if (route.SearchPending)
            {
                yielded = true;
                Expect(route.Hint.Sequence == 0, "unproven candidate became the movement target");
                continue;
            }
            Expect(yielded && route.Kind == CompanionFollowRouteKind.Walking && route.Hint.Sequence == hint.Sequence,
                "candidate frontier did not resume to completion");
            Expect(humanRequests == 1, "completed human failure was restarted instead of resuming the candidate");
            return;
        }
        throw new InvalidOperationException("candidate frontier never finished across query budgets");
    }

    private static void PendingSearchCancels()
    {
        var available = false;
        var lastStart = Point(-1);
        var selector = new CompanionFollowRoutePlanner(point => point,
            (from, to) => { lastStart = from; return true; }, () => available);
        var history = new BreadcrumbTrail(20);
        var first = selector.Select(Point(0), Point(5), history, 0);
        var movedHuman = selector.Select(Point(0), Point(7), history, 0.1f);
        Expect(first.SearchPending && movedHuman.SearchPending &&
            movedHuman.Revision != first.Revision && movedHuman.HumanGoal.x == 7,
            "material human drift did not cancel pending search");
        var movedBody = selector.Select(Point(3), Point(7), history, 0.2f);
        Expect(movedBody.SearchPending && movedBody.Revision != movedHuman.Revision,
            "material body drift did not cancel pending search");
        selector.ReportFailure(0.3f);
        available = true;
        var completed = selector.Select(Point(3.5f), Point(7), history, 0.4f);
        Expect(!completed.SearchPending && lastStart.x == 3.5f,
            "execution failure retained an obsolete pending request");
        selector.Reset();
        var reset = selector.Select(Point(4), Point(8), history, 0.5f);
        Expect(!reset.SearchPending && lastStart.x == 4, "reset retained pending search ownership");
    }

    private static bool Inside(Vector3 point, float minimumX, float maximumX,
        float minimumZ, float maximumZ) => point.x >= minimumX && point.x <= maximumX &&
        point.z >= minimumZ && point.z <= maximumZ;

    private static bool Clear(Vector3 from, Vector3 to, Func<Vector3, bool> open)
    {
        var steps = Math.Max(1, (int)Math.Ceiling(Vector3.Distance(from, to) / 0.025));
        for (var index = 0; index <= steps; index++)
            if (!open(from + (to - from) * ((float)index / steps)))
                return false;
        return true;
    }

    private static Vector3 Point(float x) => new Vector3(x, 0, 0);

    private static void Expect(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void Run(string name, Action action, ref int failures)
    {
        try
        {
            action();
            Console.WriteLine("PASS " + name);
        }
        catch (Exception error)
        {
            failures++;
            Console.Error.WriteLine("FAIL " + name + ": " + error.Message);
        }
    }
}
