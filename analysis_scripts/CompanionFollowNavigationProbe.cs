using System;
using System.Collections.Generic;
using System.Reflection;
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
            Run("recorded breadcrumb eighty-one loop", RecordedBreadcrumbEightyOneReportsNoGoalProgress, ref failures);
            Run("brief airborne frames preserve goal clock", BriefAirborneFramesPreserveGoalProgress, ref failures);
            Run("remembered approach blocks long return segment", RememberedApproachRejectsLongReturnSegment, ref failures);
            Run("revisited approach detected before timeout", RepeatedApproachReportsGoalStall, ref failures);
            if (failures != 0)
            {
                Console.Error.WriteLine("Companion follow navigation: " + failures + " behavioral checks failed.");
                return 1;
            }
            Console.WriteLine("Companion follow navigation: 12 behavioral checks passed.");
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
                Expect(!HasGoalStall(step), "legitimate U-shaped detour was mistaken for a repeated failed approach");
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
            Expect(stalls >= 2 && navigator.RememberedFailures > 0,
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

        private static void RecordedBreadcrumbEightyOneReportsNoGoalProgress()
        {
            var goal = new Vector3(-216.30f, 33.93f, -509.11f);
            var times = new[] { 38.42f, 39.43f, 40.56f, 41.66f, 42.66f, 43.79f,
                44.79f, 45.79f, 46.79f, 47.83f };
            var positions = new[]
            {
                new Vector3(-215.48f, 32.73f, -510.93f),
                new Vector3(-214.71f, 33.01f, -512.86f),
                new Vector3(-214.87f, 32.63f, -511.40f),
                new Vector3(-215.41f, 32.71f, -509.26f),
                new Vector3(-214.22f, 33.04f, -511.12f),
                new Vector3(-215.35f, 32.70f, -510.06f),
                new Vector3(-214.89f, 32.80f, -510.60f),
                new Vector3(-214.70f, 32.62f, -510.83f),
                new Vector3(-215.35f, 32.68f, -510.14f),
                new Vector3(-214.21f, 33.09f, -511.50f)
            };
            var navigator = new CompanionFollowNavigation(candidate => candidate,
                (from, to) => Vector3.Distance(to, goal) > 0.2f || from.z < -511.7f);
            var goalStalls = 0;
            var localStalls = 0;
            for (var segment = 0; segment < times.Length - 1; segment++)
            {
                var steps = (int)Math.Ceiling((times[segment + 1] - times[segment]) / 0.1f);
                for (var sample = 0; sample <= steps; sample++)
                {
                    var fraction = (float)sample / steps;
                    var now = times[segment] + (times[segment + 1] - times[segment]) * fraction;
                    var position = positions[segment] + (positions[segment + 1] - positions[segment]) * fraction;
                    var grounded = Math.Abs(now - 44.79f) > 0.06f;
                    var step = navigator.Tick(position, goal, 81, grounded, now);
                    if (HasGoalStall(step)) goalStalls++;
                    if (step.Stalled) localStalls++;
                }
            }
            Console.WriteLine("  recorded loop goal stalls=" + goalStalls + ", local stalls=" + localStalls +
                ", remembered=" + navigator.RememberedFailures);
            Expect(goalStalls > 0 && navigator.RememberedFailures > 0,
                "recorded escape-return trajectory made no global progress but was never reported");
        }

        private static void BriefAirborneFramesPreserveGoalProgress()
        {
            var navigator = new CompanionFollowNavigation(candidate => candidate, (from, to) => true);
            var goalStalls = 0;
            for (var tick = 0; tick <= 75; tick++)
            {
                var grounded = tick % 4 != 3;
                var step = navigator.Tick(Point(0, 0), Point(5, 0), 81, grounded, tick * 0.1f);
                if (HasGoalStall(step)) goalStalls++;
            }
            Expect(goalStalls > 0 && navigator.RememberedFailures > 0,
                "brief unsupported frames repeatedly erased the stalled goal clock");
        }

        private static void RememberedApproachRejectsLongReturnSegment()
        {
            var navigator = new CompanionFollowNavigation(candidate => candidate, (from, to) => true);
            var goal = Point(5, 0);
            navigator.Tick(Point(0, 0), goal, 81, true, 0);
            navigator.Tick(Point(0, 0), goal, 81, true, 1.3f);
            Expect(navigator.RememberedFailures > 0, "test never recorded the physically failed approach");
            Expect(navigator.TryWalkingDetour(Point(-2, 0), goal, 81, 2.2f,
                candidate => candidate, (from, to) => true), "alternative route around the failed approach was not found");
            var step = navigator.Tick(Point(-2, 0), goal, 81, true, 2.2f);
            Expect(navigator.PlanStatus != "direct" && Math.Abs(step.Direction.z) > 0.1f,
                "a long direct segment crossed the remembered failed approach from outside its origin radius");
        }

        private static void RepeatedApproachReportsGoalStall()
        {
            var navigator = new CompanionFollowNavigation(candidate => candidate, (from, to) => true);
            var goal = Point(5, 0);
            navigator.Tick(Point(0, 0), goal, 81, true, 0);
            navigator.Tick(Point(-2, 0), goal, 81, true, 1.0f);
            navigator.Tick(Point(-1.2f, 0), goal, 81, true, 1.5f);
            var returned = navigator.Tick(Point(-0.2f, 0), goal, 81, true, 2.5f);
            Expect(HasGoalStall(returned) && navigator.RememberedFailures > 0,
                "returning to the same unsuccessful approach was counted as fresh route progress");
        }

        private static bool HasGoalStall(CompanionNavigationStep step)
        {
            var property = typeof(CompanionNavigationStep).GetProperty("GoalStalled",
                BindingFlags.Instance | BindingFlags.NonPublic);
            return property != null && (bool)property.GetValue(step);
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
