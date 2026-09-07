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

        internal static Vector3 zero => new Vector3(0, 0, 0);
        internal static Vector3 forward => new Vector3(0, 0, 1);
        internal float sqrMagnitude => x * x + y * y + z * z;
        internal float magnitude => (float)Math.Sqrt(sqrMagnitude);
        internal Vector3 normalized => magnitude < 0.000001f ? zero : this / magnitude;
        internal void Normalize() { this = normalized; }
        internal static float Distance(Vector3 first, Vector3 second) => (first - second).magnitude;
        internal static float Dot(Vector3 first, Vector3 second) => first.x * second.x + first.y * second.y + first.z * second.z;
        public static Vector3 operator +(Vector3 first, Vector3 second) => new Vector3(first.x + second.x, first.y + second.y, first.z + second.z);
        public static Vector3 operator -(Vector3 first, Vector3 second) => new Vector3(first.x - second.x, first.y - second.y, first.z - second.z);
        public static Vector3 operator -(Vector3 value) => new Vector3(-value.x, -value.y, -value.z);
        public static Vector3 operator *(Vector3 value, float scale) => new Vector3(value.x * scale, value.y * scale, value.z * scale);
        public static Vector3 operator /(Vector3 value, float scale) => new Vector3(value.x / scale, value.y / scale, value.z / scale);
    }

    internal static class Mathf
    {
        internal static float Abs(float value) => Math.Abs(value);
        internal static float Max(float first, float second) => Math.Max(first, second);
        internal static float Min(float first, float second) => Math.Min(first, second);
        internal static float Clamp(float value, float minimum, float maximum) => Math.Min(maximum, Math.Max(minimum, value));
    }
}

namespace Ramblers
{
    internal static class CompanionFollowNavigationProbe
    {
        private static int Main()
        {
            var failures = 0;
            Run("persistent U detour", PersistentDetourMovesThroughU, ref failures);
            Run("measured stall changes direction", ActualStallRejectsFailedDirection, ref failures);
            Run("ten seconds blocked keeps recovering", CompleteBlockageDoesNotDeadlock, ref failures);
            Run("uncertain floor samples", FloorUncertaintyCanPassThrough, ref failures);
            Run("moving target retains stall memory", MovingGoalDoesNotEraseStalls, ref failures);
            Run("target churn respects plan cadence", TargetChurnRespectsPlanCadence, ref failures);
            Run("airborne retains direction", AirborneRetainsDirectionWithoutGroundedStall, ref failures);
            Run("pause and reset lifecycle", PauseAndResetOwnTheirState, ref failures);
            if (failures != 0)
            {
                Console.Error.WriteLine("Companion follow navigation: " + failures + " behavioral checks failed.");
                return 1;
            }
            Console.WriteLine("Companion follow navigation: 8 behavioral checks passed.");
            return 0;
        }

        private static void PersistentDetourMovesThroughU()
        {
            Func<Vector3, bool> open = point =>
                !Inside(point, -1.8f, -1.2f, -1.5f, 3.3f) &&
                !Inside(point, 1.2f, 1.8f, -1.5f, 3.3f) &&
                !Inside(point, -1.8f, 1.8f, 2.7f, 3.3f);
            var clear = SegmentPredicate(open);
            var navigator = new CompanionFollowNavigation(candidate => candidate, clear);
            var position = Point(0, 0);
            var goal = Point(0, 5);
            var movedAway = false;
            var detourTicks = 0;
            for (var tick = 0; tick < 400 && Vector3.Distance(position, goal) > 0.3f; tick++)
            {
                var step = navigator.Tick(position, goal, 1, true, tick * 0.05f);
                if (step.Mode == "detour") detourTicks++;
                var candidate = position + step.Direction * 0.1f;
                Expect(clear(position, candidate), "commanded detour movement crossed a solid wall");
                position = candidate;
                movedAway |= position.z < -1.5f;
            }
            Expect(Vector3.Distance(position, goal) <= 0.3f, "U detour did not reach the goal");
            Expect(movedAway && detourTicks > 10, "detour was discarded instead of being followed");
            Expect(navigator.PlanCount < 15, "persistent detour was needlessly replanned every tick");
            Console.WriteLine("  U traversal plans=" + navigator.PlanCount + ", detour ticks=" + detourTicks);
        }

        private static void ActualStallRejectsFailedDirection()
        {
            var navigator = new CompanionFollowNavigation(candidate => candidate, (from, to) => true);
            var origin = Point(0, 0);
            var first = navigator.Tick(origin, Point(5, 0), 1, true, 0);
            var foundStall = false;
            for (var tick = 1; tick <= 20; tick++)
            {
                var step = navigator.Tick(origin, Point(5, 0), 1, true, tick * 0.1f);
                if (!step.Stalled)
                    continue;
                foundStall = true;
                Expect(step.RequestJump, "first real stall did not request native recovery");
                Expect(Vector3.Dot(step.Direction, first.Direction) <= 0.75f,
                    "route continued selecting the physically failed direction");
                Expect(navigator.RememberedFailures > 0 && navigator.PlanCount >= 2,
                    "physical failure did not enter route-search memory");
                break;
            }
            Expect(foundStall, "zero movement was mistaken for route progress");
        }

        private static void CompleteBlockageDoesNotDeadlock()
        {
            var navigator = new CompanionFollowNavigation(candidate => candidate, (from, to) => false);
            var stalls = 0;
            var jumps = new List<float>();
            var directions = new List<Vector3>();
            for (var tick = 0; tick <= 100; tick++)
            {
                var now = tick * 0.1f;
                var step = navigator.Tick(Point(0, 0), Point(5, 0), 1, true, now);
                Expect(step.Direction.sqrMagnitude > 0.9f, "blocked navigation stopped sending recovery movement");
                if (step.Stalled)
                {
                    stalls++;
                    directions.Add(step.Direction);
                }
                if (step.RequestJump) jumps.Add(now);
            }
            Expect(stalls >= 4 && jumps.Count >= 2, "blocked navigation reset itself into a permanent idle state");
            for (var i = 1; i < jumps.Count; i++)
                Expect(jumps[i] - jumps[i - 1] >= 2.99f, "blocked navigation spammed jump recovery");
            Expect(directions.Exists(direction => Vector3.Dot(direction, directions[0]) < 0.5f),
                "fully blocked recovery kept pushing the same failed direction");
        }

        private static void FloorUncertaintyCanPassThrough()
        {
            var uncertainSamples = 0;
            Func<Vector3, Vector3?> sample = candidate =>
            {
                if (Math.Abs(candidate.x - 1f) < 0.01f) uncertainSamples++;
                return candidate;
            };
            Func<Vector3, bool> open = point => !Inside(point, 1.4f, 2.6f, -0.8f, 0.8f);
            var navigator = new CompanionFollowNavigation(sample, SegmentPredicate(open));
            var step = navigator.Tick(Point(0, 0), Point(4, 0), 1, true, 0);
            Expect(uncertainSamples > 0 && navigator.WaypointsRemaining > 0 && step.Mode == "detour",
                "tolerated support seams were dropped from navigation");
        }

        private static void MovingGoalDoesNotEraseStalls()
        {
            var navigator = new CompanionFollowNavigation(candidate => candidate, (from, to) => true);
            var stalls = 0;
            for (var tick = 0; tick < 50; tick++)
            {
                var goal = Point(5, (tick & 1) == 0 ? 1 : -1);
                var step = navigator.Tick(Point(0, 0), goal, tick + 1, true, tick * 0.1f);
                if (step.Stalled) stalls++;
            }
            Expect(stalls >= 2 && navigator.RememberedFailures >= 2,
                "moving human or breadcrumb sequence churn erased a motionless companion's failures");
        }

        private static void TargetChurnRespectsPlanCadence()
        {
            var navigator = new CompanionFollowNavigation(candidate => candidate, (from, to) => true);
            var position = Point(0, 0);
            for (var tick = 0; tick < 100; tick++)
            {
                position.x += 0.02f;
                navigator.Tick(position, Point(5, 0), tick + 1, true, tick * 0.02f);
            }
            Expect(navigator.PlanCount <= 5,
                "breadcrumb key churn bypassed the local search cadence: plans=" + navigator.PlanCount);
        }

        private static void AirborneRetainsDirectionWithoutGroundedStall()
        {
            var navigator = new CompanionFollowNavigation(candidate => candidate, (from, to) => true);
            var first = navigator.Tick(Point(0, 0), Point(5, 0), 1, true, 0);
            var plans = navigator.PlanCount;
            for (var tick = 1; tick <= 80; tick++)
            {
                var step = navigator.Tick(new Vector3(0, 2, 0), Point(0, -5), 2, false, tick * 0.1f);
                Expect(Vector3.Dot(step.Direction, first.Direction) > 0.999f,
                    "airborne navigation discarded its established travel direction");
                Expect(step.Mode == "airborne" && !step.Stalled && !step.RequestJump,
                    "airborne time was counted as a grounded stall");
            }
            Expect(navigator.PlanCount == plans && navigator.RememberedFailures == 0,
                "airborne navigation planned routes or accumulated failures");
            var landing = navigator.Tick(Point(0, 0), Point(0, -5), 2, true, 8.1f);
            Expect(!landing.Stalled && !landing.RequestJump, "landing immediately inherited an airborne stall timer");
        }

        private static void PauseAndResetOwnTheirState()
        {
            var navigator = new CompanionFollowNavigation(candidate => candidate, (from, to) => true);
            navigator.Tick(Point(0, 0), Point(5, 0), 1, true, 0);
            navigator.Tick(Point(0, 0), Point(5, 0), 1, true, 2);
            Expect(navigator.RememberedFailures > 0, "lifecycle test never generated a failure");
            navigator.Pause();
            Expect(navigator.WaypointsRemaining == 0, "pause retained a live route");
            var resumed = navigator.Tick(Point(0, 0), Point(5, 0), 1, true, 5);
            Expect(!resumed.Stalled && !resumed.RequestJump, "pause duration was counted as stalled movement");
            navigator.Reset();
            Expect(navigator.PlanCount == 0 && navigator.RememberedFailures == 0 && navigator.WaypointsRemaining == 0,
                "reset retained state from an old following lifecycle");
        }

        private static Func<Vector3, Vector3, bool> SegmentPredicate(Func<Vector3, bool> open) =>
            (from, to) =>
            {
                var steps = Math.Max(1, (int)Math.Ceiling(Vector3.Distance(from, to) / 0.025));
                for (var i = 0; i <= steps; i++)
                    if (!open(from + (to - from) * ((float)i / steps)))
                        return false;
                return true;
            };

        private static bool Inside(Vector3 point, float minimumX, float maximumX,
            float minimumZ, float maximumZ) => point.x >= minimumX && point.x <= maximumX &&
            point.z >= minimumZ && point.z <= maximumZ;

        private static Vector3 Point(float x, float z) => new Vector3(x, 0, z);

        private static void Run(string name, Action check, ref int failures)
        {
            try { check(); Console.WriteLine("PASS " + name); }
            catch (Exception error) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + error.Message); }
        }

        private static void Expect(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
