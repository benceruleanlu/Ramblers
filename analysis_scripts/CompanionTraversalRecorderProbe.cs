using System;

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

        internal static Vector3 zero => new Vector3(0f, 0f, 0f);
        internal float sqrMagnitude => x * x + y * y + z * z;
        internal float magnitude => (float)Math.Sqrt(sqrMagnitude);
        internal Vector3 normalized => magnitude > 0.000001f
            ? new Vector3(x / magnitude, y / magnitude, z / magnitude)
            : zero;

        public static Vector3 operator -(Vector3 left, Vector3 right)
        {
            return new Vector3(
                left.x - right.x,
                left.y - right.y,
                left.z - right.z);
        }
    }

    internal static class Mathf
    {
        internal static float Max(float left, float right)
        {
            return Math.Max(left, right);
        }
    }
}

namespace Ramblers
{
    using UnityEngine;

    internal static class CompanionTraversalRecorderProbe
    {
        private static int Main()
        {
            SameLevelJumpRetainsGroundedTakeoff();
            DownhillJumpRemainsJump();
            NativeJumpCanPrecedeAirborneFrame();
            MissedJumpFlagUsesObservedArc();
            WalkOffLedgeRecordsDrop();
            BriefFloorSeamAddsNoTraversal();
            FlatUnsupportedPlaneAddsNoJump();
            StartupAirborneAddsNoInventedTakeoff();
            ResetDiscardsCarriedTrajectory();
            RejectedGroundedJumpExpires();
            LandingCanBeginNextJump();
            JumpInPlaceHasFiniteDirection();
            ClockResetDiscardsTrajectory();
            Console.WriteLine("Traversal recorder probe passed (13 scenarios).");
            return 0;
        }

        private static void SameLevelJumpRetainsGroundedTakeoff()
        {
            var recorder = NewGrounded(0f, 2f);
            CompanionTraversalCompleted completed;
            Expect(!recorder.Sample(Point(0.4f, 2.3f), false, true, 4f, 0.1f, out completed),
                "airborne takeoff completed prematurely");
            Expect(recorder.TraversalInProgress, "native jump was not tracked");
            ExpectPoint(recorder.TakeoffPosition, 0f, 2f, "takeoff was already airborne");
            recorder.Sample(Point(1f, 3f), false, false, 0f, 0.3f, out completed);
            Expect(recorder.Sample(Point(2f, 2f), true, false, -1f, 0.6f, out completed),
                "same-level jump was discarded");
            Expect(completed.RequiresJump && !completed.RequiresDrop, "same-level jump was misclassified");
            ExpectPoint(completed.Takeoff, 0f, 2f, "completed jump lost grounded takeoff");
            ExpectPoint(completed.Landing, 2f, 2f, "jump landing changed");
            ExpectNear(completed.PeakRise, 1f, "jump peak changed");
            ExpectNear(completed.Duration, 0.6f, "jump duration changed");
            ExpectNear(completed.HorizontalDistance, 2f, "jump span changed");
            ExpectPoint(completed.TravelDirection, 1f, 0f, "jump direction changed");
            Expect(!recorder.TraversalInProgress, "completed jump remained active");
        }

        private static void DownhillJumpRemainsJump()
        {
            var recorder = NewGrounded(0f, 3f);
            CompanionTraversalCompleted completed;
            recorder.Sample(Point(0.3f, 3.2f), false, true, 4f, 0.1f, out completed);
            recorder.Sample(Point(1f, 3.8f), false, false, 0f, 0.3f, out completed);
            Expect(recorder.Sample(Point(2f, 1f), true, false, -4f, 0.8f, out completed),
                "downhill jump was discarded");
            Expect(completed.RequiresJump && !completed.RequiresDrop,
                "downhill jump became a walk-off drop");
        }

        private static void NativeJumpCanPrecedeAirborneFrame()
        {
            var recorder = NewGrounded(0f, 0f);
            CompanionTraversalCompleted completed;
            recorder.Sample(Point(0.1f, 0f), true, true, 4f, 0.02f, out completed);
            recorder.Sample(Point(0.2f, 0.08f), false, false, 2f, 0.04f, out completed);
            Expect(recorder.Sample(Point(0.5f, 0f), true, false, -1f, 0.10f, out completed),
                "native jump flag was lost before grounded state updated");
            Expect(completed.RequiresJump, "short native jump was classified as a floor seam");
            ExpectPoint(completed.Takeoff, 0f, 0f, "grounded jump flag moved the takeoff");
        }

        private static void MissedJumpFlagUsesObservedArc()
        {
            var recorder = NewGrounded(0f, 0f);
            CompanionTraversalCompleted completed;
            recorder.Sample(Point(0.2f, 0.2f), false, false, 3f, 0.06f, out completed);
            recorder.Sample(Point(0.8f, 0.7f), false, false, 0f, 0.3f, out completed);
            Expect(recorder.Sample(Point(1.5f, 0f), true, false, -2f, 0.5f, out completed),
                "clear jump arc was lost with a missed native flag");
            Expect(completed.RequiresJump, "observed upward arc became a drop");
        }

        private static void WalkOffLedgeRecordsDrop()
        {
            var recorder = NewGrounded(0f, 2f);
            CompanionTraversalCompleted completed;
            recorder.Sample(Point(0.3f, 1.98f), false, false, -0.3f, 0.04f, out completed);
            recorder.Sample(Point(0.7f, 1.5f), false, false, -3f, 0.3f, out completed);
            Expect(recorder.Sample(Point(1.2f, 0f), true, false, -4f, 0.5f, out completed),
                "ledge drop was discarded");
            Expect(completed.RequiresDrop && !completed.RequiresJump, "walk-off drop became a jump");
            ExpectPoint(completed.Takeoff, 0f, 2f, "drop approach starts beyond the ledge");
            ExpectPoint(completed.Landing, 1.2f, 0f, "drop landing changed");
        }

        private static void BriefFloorSeamAddsNoTraversal()
        {
            var recorder = NewGrounded(0f, 0f);
            CompanionTraversalCompleted completed;
            recorder.Sample(Point(0.1f, -0.01f), false, false, -0.4f, 0.02f, out completed);
            Expect(!recorder.Sample(Point(0.2f, 0f), true, false, 0f, 0.04f, out completed),
                "one missing-ground frame became a traversal");
            Expect(!recorder.TraversalInProgress, "floor seam stayed active");
            recorder.Sample(Point(0.3f, -0.01f), false, false, -0.4f, 0.06f, out completed);
            Expect(!recorder.Sample(Point(0.4f, 0f), true, false, 0f, 0.08f, out completed),
                "repeated seams accumulated into a jump");
            ExpectPoint(recorder.TakeoffPosition, 0.4f, 0f, "floor seam did not advance grounded anchor");
        }

        private static void FlatUnsupportedPlaneAddsNoJump()
        {
            var recorder = NewGrounded(0f, 0f);
            CompanionTraversalCompleted completed;
            recorder.Sample(Point(0.2f, -0.02f), false, false, -0.1f, 0.1f, out completed);
            recorder.Sample(Point(1f, -0.05f), false, false, -0.1f, 0.3f, out completed);
            Expect(!recorder.Sample(Point(1.5f, 0f), true, false, 0f, 0.5f, out completed),
                "duration alone converted an unsupported plane into a jump");
        }

        private static void StartupAirborneAddsNoInventedTakeoff()
        {
            var recorder = new CompanionTraversalRecorder();
            CompanionTraversalCompleted completed;
            recorder.Sample(Point(0f, 5f), false, true, 3f, 0f, out completed);
            recorder.Sample(Point(1f, 4f), false, false, -3f, 0.3f, out completed);
            Expect(!recorder.TraversalInProgress, "startup fabricated a grounded takeoff");
            Expect(!recorder.Sample(Point(2f, 0f), true, false, -5f, 0.7f, out completed),
                "startup fall emitted an invented drop");
            ExpectPoint(recorder.TakeoffPosition, 2f, 0f, "landing failed to establish first anchor");
        }

        private static void ResetDiscardsCarriedTrajectory()
        {
            var recorder = NewGrounded(0f, 0f);
            CompanionTraversalCompleted completed;
            recorder.Sample(Point(1f, 1f), false, true, 4f, 0.1f, out completed);
            recorder.Reset();
            Expect(!recorder.TraversalInProgress, "carry reset left a traversal active");
            recorder.Sample(Point(20f, 4f), false, false, -2f, 5f, out completed);
            Expect(!recorder.Sample(Point(21f, 0f), true, false, -3f, 5.2f, out completed),
                "carry reset connected unrelated positions");
            recorder.Sample(Point(22f, 0.3f), false, true, 3f, 5.3f, out completed);
            Expect(recorder.Sample(Point(23f, 0f), true, false, -3f, 5.6f, out completed),
                "recorder failed to recover after reset");
            ExpectPoint(completed.Takeoff, 21f, 0f, "post-reset jump used a stale anchor");
        }

        private static void RejectedGroundedJumpExpires()
        {
            var recorder = NewGrounded(0f, 0f);
            CompanionTraversalCompleted completed;
            recorder.Sample(Point(0f, 0f), true, true, 0f, 0.02f, out completed);
            Expect(!recorder.Sample(Point(0.2f, 0f), true, true, 0f, 0.3f, out completed),
                "grounded jump flag fabricated movement");
            Expect(!recorder.TraversalInProgress, "grounded jump flag blocked recording indefinitely");
            recorder.Sample(Point(0.4f, 0f), true, true, 0f, 0.4f, out completed);
            Expect(!recorder.TraversalInProgress, "persistent native flag continually restarted a jump");
        }

        private static void LandingCanBeginNextJump()
        {
            var recorder = NewGrounded(0f, 0f);
            CompanionTraversalCompleted completed;
            recorder.Sample(Point(0.2f, 0.2f), false, true, 3f, 0.1f, out completed);
            recorder.Sample(Point(0.7f, 0.1f), false, false, -3f, 0.3f, out completed);
            Expect(recorder.Sample(Point(1f, 0f), true, true, 3f, 0.4f, out completed),
                "landing with next jump lost the completed jump");
            Expect(recorder.TraversalInProgress, "next jump flag was lost at landing");
            recorder.Sample(Point(1.5f, 0.4f), false, false, 2f, 0.5f, out completed);
            Expect(recorder.Sample(Point(2f, 0f), true, false, -3f, 0.8f, out completed),
                "second jump was not recorded");
            ExpectPoint(completed.Takeoff, 1f, 0f, "second jump reused first takeoff");
        }

        private static void JumpInPlaceHasFiniteDirection()
        {
            var recorder = NewGrounded(2f, 0f);
            CompanionTraversalCompleted completed;
            recorder.Sample(Point(2f, 0.2f), false, true, 3f, 0.1f, out completed);
            Expect(recorder.Sample(Point(2f, 0f), true, false, -3f, 0.4f, out completed),
                "native jump in place was discarded");
            ExpectPoint(completed.TravelDirection, 0f, 0f, "vertical jump generated an invalid direction");
            ExpectNear(completed.HorizontalDistance, 0f, "vertical jump gained horizontal span");
        }

        private static void ClockResetDiscardsTrajectory()
        {
            var recorder = NewGrounded(0f, 0f);
            CompanionTraversalCompleted completed;
            recorder.Sample(Point(0f, 0f), true, false, 0f, 4f, out completed);
            recorder.Sample(Point(1f, 1f), false, true, 3f, 4.2f, out completed);
            Expect(!recorder.Sample(Point(20f, 0f), true, false, 0f, 0f, out completed),
                "clock reset completed an obsolete trajectory");
            Expect(!recorder.TraversalInProgress, "clock reset retained old active state");
        }

        private static CompanionTraversalRecorder NewGrounded(float x, float y)
        {
            var recorder = new CompanionTraversalRecorder();
            CompanionTraversalCompleted completed;
            recorder.Sample(Point(x, y), true, false, 0f, 0f, out completed);
            return recorder;
        }

        private static Vector3 Point(float x, float y)
        {
            return new Vector3(x, y, 0f);
        }

        private static void ExpectPoint(Vector3 actual, float x, float y, string message)
        {
            ExpectNear(actual.x, x, message + " (x)");
            ExpectNear(actual.y, y, message + " (y)");
            ExpectNear(actual.z, 0f, message + " (z)");
        }

        private static void ExpectNear(float actual, float expected, string message)
        {
            Expect(!float.IsNaN(actual) && Math.Abs(actual - expected) < 0.0001f,
                message + ": expected " + expected + ", got " + actual);
        }

        private static void Expect(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
    }
}
