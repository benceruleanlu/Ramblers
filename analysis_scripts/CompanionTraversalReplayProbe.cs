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
        internal Vector3 normalized
        {
            get
            {
                var copy = this;
                copy.Normalize();
                return copy;
            }
        }

        internal void Normalize()
        {
            var length = magnitude;
            if (length < 0.000001f)
                return;
            x /= length;
            y /= length;
            z /= length;
        }

        public static Vector3 operator -(Vector3 left, Vector3 right)
        {
            return new Vector3(left.x - right.x, left.y - right.y, left.z - right.z);
        }

        public static Vector3 operator *(Vector3 value, float scalar)
        {
            return new Vector3(value.x * scalar, value.y * scalar, value.z * scalar);
        }

        internal static float Distance(Vector3 left, Vector3 right)
        {
            return (left - right).magnitude;
        }

        internal static float Dot(Vector3 left, Vector3 right)
        {
            return left.x * right.x + left.y * right.y + left.z * right.z;
        }
    }

    internal static class Mathf
    {
        internal static float Abs(float value)
        {
            return Math.Abs(value);
        }
    }
}

namespace Ramblers
{
    using UnityEngine;

    internal static class CompanionTraversalReplayProbe
    {
        private static int Main()
        {
            ApproachesTakeoffBeforeJumping();
            AnotherFloorDoesNotCountAsTakeoff();
            JumpCommitsOnceThroughLongFlight();
            RejectedJumpRetriesWithoutMovementCommit();
            GroundedLaunchExpiresAndRetriesWithoutLimit();
            FloorSeamDoesNotCompleteJump();
            SustainedUnsupportedMotionConfirmsFlight();
            DropDoesNotRequestJump();
            TargetChangeRetainsAirborneDirection();
            TargetChangeRetainsPendingNativeLaunch();
            MissedLandingRetainsCrossingForRetry();
            RevisitingCompletedSequenceDoesNotRejump();
            ResetClearsCommitOwnership();
            ClockResetClearsOldFlight();
            LegacyBreadcrumbRemainsOrdinaryApproach();
            InPlaceJumpKeepsFiniteDirection();
            CancelActivePreservesCompletedTraversal();
            CancelAirborneDoesNotSatisfyItsMarker();
            CancelNextLaunchKeepsPreviousCompletion();
            AirborneCorrectionTargetsOriginalLanding();
            LongDropStopsHorizontalIntentAboveLanding();
            AirborneOvershootCanCorrectBackToLanding();
            LoggedShortDropCompletesWithoutAirborne();
            GroundedOvershootCompletesDrop();
            GroundedArrivalBeforeLaunchSatisfiesHint();
            WalkedJumpSpanDoesNotRequireJumpAnimation();
            RaisedPlatformAtSameXZDoesNotCompleteAtLaunch();
            InPlaceJumpDoesNotCompleteWhileGrounded();
            VerticalDropRequiresRealProgress();
            SidewaysOrDistantGroundDoesNotCountAsArrival();
            PendingDropReaimsTowardLanding();
            ArrivalAfterLaunchTimeoutDoesNotReturnToTakeoff();
            Console.WriteLine("Traversal replay probe passed (32 scenarios).");
            return 0;
        }

        private static void ApproachesTakeoffBeforeJumping()
        {
            var replay = new CompanionTraversalReplay();
            var point = Jump(1, Point(3f, 0f), Point(5f, 0f));
            var requests = 0;
            Vector3 direction;
            Expect(!replay.Tick(point, Point(1f, 0f), true, 0f,
                delegate { requests++; return true; }, out direction), "jump launched before takeoff approach");
            ExpectNear(replay.ApproachPosition.x, 3f, "approach target was landing instead of takeoff");
            Expect(requests == 0 && !replay.Active, "approach requested a jump");
            Expect(replay.Tick(point, Point(2.5f, 0f), true, 0.1f,
                delegate { requests++; return true; }, out direction), "jump did not launch near takeoff");
            Expect(requests == 1 && replay.Active, "native jump was not committed once");
            ExpectNear(direction.x, 1f, "launch direction did not point toward landing");
            Expect(replay.JumpCommittedSequence == 0, "jump was considered satisfied before flight");
        }

        private static void AnotherFloorDoesNotCountAsTakeoff()
        {
            var replay = new CompanionTraversalReplay();
            var point = Jump(1, Point(0f, 3f), Point(2f, 4f));
            Vector3 direction;
            Expect(!replay.Tick(point, Point(0f, 0f), true, 0f,
                delegate { throw new InvalidOperationException("jump requested from another floor"); }, out direction),
                "another floor started a traversal");
        }

        private static void JumpCommitsOnceThroughLongFlight()
        {
            var replay = new CompanionTraversalReplay();
            var point = Jump(1, Point(0f, 0f), Point(3f, -2f));
            var requests = 0;
            Func<bool> jump = delegate { requests++; return true; };
            Vector3 direction;
            replay.Tick(point, Point(0f, 0f), true, 0f, jump, out direction);
            Expect(replay.Tick(point, Point(0.2f, 0.3f), false, 0.1f, jump, out direction),
                "jump stopped during ascent");
            Expect(replay.Tick(point, Point(2f, -1f), false, 10f, jump, out direction),
                "committed direction expired in midair");
            ExpectNear(direction.x, 1f, "long flight lost direction");
            Expect(!replay.Tick(point, Point(3f, -2f), true, 10.1f, jump, out direction),
                "landed traversal kept active movement ownership");
            Expect(requests == 1, "active flight repeatedly requested native jump");
            Expect(replay.JumpCommittedSequence == 1 && !replay.Active,
                "landing did not satisfy the jump");
            replay.Tick(point, Point(3f, -2f), true, 11f, jump, out direction);
            Expect(requests == 1, "completed marker triggered another jump");
        }

        private static void RejectedJumpRetriesWithoutMovementCommit()
        {
            var replay = new CompanionTraversalReplay();
            var point = Jump(1, Point(0f, 0f), Point(2f, 0f));
            var requests = 0;
            Func<bool> jump = delegate { requests++; return requests > 1; };
            Vector3 direction;
            Expect(!replay.Tick(point, Point(0f, 0f), true, 0f, jump, out direction),
                "native refusal committed movement");
            Expect(!replay.Active, "native refusal retained launch ownership");
            replay.Tick(point, Point(0f, 0f), true, 0.1f, jump, out direction);
            Expect(requests == 1, "native refusal retried every frame");
            Expect(replay.Tick(point, Point(0f, 0f), true, 0.4f, jump, out direction),
                "native refusal never retried");
            Expect(requests == 2, "retry count changed");
        }

        private static void GroundedLaunchExpiresAndRetriesWithoutLimit()
        {
            var replay = new CompanionTraversalReplay();
            var point = Jump(1, Point(0f, 0f), Point(2f, 0f));
            var requests = 0;
            Func<bool> jump = delegate { requests++; return true; };
            Vector3 direction;
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var now = attempt * 2f;
                Expect(replay.Tick(point, Point(0f, 0f), true, now, jump, out direction),
                    "retry stopped after repeated launch failures");
                Expect(!replay.Tick(point, Point(0.1f, 0f), true, now + 0.9f, jump, out direction),
                    "failed native launch retained movement forever");
                Expect(!replay.Active && replay.JumpCommittedSequence == 0,
                    "failed launch was marked completed");
                Expect(!replay.Tick(point, Point(0f, 0f), true, now + 1f, jump, out direction),
                    "launch failure skipped its retry cooldown");
            }
            Expect(requests == 8, "failed launches were requested more than once per attempt");
        }

        private static void FloorSeamDoesNotCompleteJump()
        {
            var replay = new CompanionTraversalReplay();
            var point = Jump(1, Point(0f, 0f), Point(2f, 0f));
            Vector3 direction;
            replay.Tick(point, Point(0f, 0f), true, 0f, Yes, out direction);
            replay.Tick(point, Point(0.1f, -0.01f), false, 0.02f, Yes, out direction);
            Expect(replay.Tick(point, Point(0.2f, 0f), true, 0.04f, Yes, out direction),
                "one-frame floor seam ended a pending jump");
            Expect(replay.JumpCommittedSequence == 0, "floor seam satisfied a jump marker");
            replay.Tick(point, Point(0.3f, 0.3f), false, 0.1f, Yes, out direction);
            replay.Tick(point, Point(2f, 0f), true, 0.5f, Yes, out direction);
            Expect(replay.JumpCommittedSequence == 1, "real jump following a floor seam was lost");
        }

        private static void SustainedUnsupportedMotionConfirmsFlight()
        {
            var replay = new CompanionTraversalReplay();
            var point = Jump(1, Point(0f, 0f), Point(2f, 0f));
            Vector3 direction;
            replay.Tick(point, Point(0f, 0f), true, 0f, Yes, out direction);
            replay.Tick(point, Point(0.2f, 0.01f), false, 0.1f, Yes, out direction);
            replay.Tick(point, Point(1f, 0.01f), false, 0.2f, Yes, out direction);
            Expect(!replay.Tick(point, Point(2f, 0f), true, 0.3f, Yes, out direction),
                "sustained unsupported movement did not complete");
            Expect(replay.JumpCommittedSequence == 1, "flight required a fixed height threshold");
        }

        private static void DropDoesNotRequestJump()
        {
            var replay = new CompanionTraversalReplay();
            var point = new BreadcrumbPoint(7, Point(1.5f, 0f), false, true,
                Point(1f, 0f), true, Point(0f, 2f), 0.5f);
            Func<bool> noJump = delegate { throw new InvalidOperationException("drop requested a jump"); };
            Vector3 direction;
            Expect(replay.Tick(point, Point(0f, 2f), true, 0f, noJump, out direction),
                "drop failed to begin at takeoff");
            replay.Tick(point, Point(0.5f, 1.5f), false, 0.1f, noJump, out direction);
            replay.Tick(point, Point(1.5f, 0f), true, 0.5f, noJump, out direction);
            Expect(replay.DropCommittedSequence == 7 && replay.JumpCommittedSequence == 0,
                "drop completion changed jump state");
        }

        private static void TargetChangeRetainsAirborneDirection()
        {
            var replay = new CompanionTraversalReplay();
            var first = Jump(1, Point(0f, 0f), Point(2f, 0f));
            var second = Jump(2, Point(2f, 0f), Point(-2f, 0f));
            Vector3 direction;
            replay.Tick(first, Point(0f, 0f), true, 0f, Yes, out direction);
            replay.Tick(first, Point(0.3f, 0.3f), false, 0.1f, Yes, out direction);
            Expect(replay.Tick(second, Point(1f, 0.5f), false, 0.3f, Yes, out direction),
                "target change ended active flight");
            ExpectNear(direction.x, 1f, "target change reversed airborne movement");
            Expect(replay.TargetSequence == 1, "target change stole active traversal ownership");
            replay.Tick(second, Point(2f, 0f), true, 0.5f, Yes, out direction);
            Expect(replay.JumpCommittedSequence == 1, "landing credited the replacement target");
            Expect(replay.Tick(second, Point(2f, 0f), true, 0.6f, Yes, out direction),
                "next traversal was not adopted after landing");
            Expect(replay.TargetSequence == 2, "replacement target was never adopted");
            ExpectNear(direction.x, -1f, "next traversal retained obsolete movement");
        }

        private static void TargetChangeRetainsPendingNativeLaunch()
        {
            var replay = new CompanionTraversalReplay();
            var first = Jump(1, Point(0f, 0f), Point(2f, 0f));
            var second = Jump(2, Point(0f, 0f), Point(-2f, 0f));
            var requests = 0;
            Func<bool> jump = delegate { requests++; return true; };
            Vector3 direction;
            replay.Tick(first, Point(0f, 0f), true, 0f, jump, out direction);
            replay.Tick(second, Point(0.1f, 0f), true, 0.05f, jump, out direction);
            Expect(replay.TargetSequence == 1 && requests == 1,
                "target replacement requested another jump before first launched");
            ExpectNear(direction.x, 1f, "pending native jump lost its launch direction");
            replay.Tick(second, Point(0.2f, 0.2f), false, 0.1f, jump, out direction);
            Expect(replay.TargetSequence == 1, "airborne onset adopted replacement target");
        }

        private static void MissedLandingRetainsCrossingForRetry()
        {
            var replay = new CompanionTraversalReplay();
            var point = Jump(1, Point(0f, 0f), Point(3f, 0f));
            var requests = 0;
            Func<bool> jump = delegate { requests++; return true; };
            Vector3 direction;
            replay.Tick(point, Point(0f, 0f), true, 0f, jump, out direction);
            replay.Tick(point, Point(0.2f, 0.2f), false, 0.1f, jump, out direction);
            replay.Tick(point, Point(1f, -1f), true, 0.5f, jump, out direction);
            ExpectNear(replay.ApproachPosition.x, 0f, "missed landing lost the required takeoff");
            ExpectNear(replay.ApproachPosition.y, 0f, "missed landing substituted the current ground");
            Expect(!replay.Tick(point, Point(0f, 0f), true, 0.6f, jump, out direction),
                "missed jump bypassed its retry cooldown");
            Expect(requests == 1 && replay.JumpCommittedSequence == 0,
                "missed landing was marked successful");
            Expect(replay.Tick(point, Point(0f, 0f), true, 0.9f, jump, out direction),
                "missed landing could not retry from takeoff");
            Expect(requests == 2, "missed landing failed to request a paced retry");
        }

        private static void RevisitingCompletedSequenceDoesNotRejump()
        {
            var replay = new CompanionTraversalReplay();
            var first = Jump(1, Point(0f, 0f), Point(2f, 0f));
            var later = Jump(2, Point(10f, 0f), Point(12f, 0f));
            var requests = 0;
            Func<bool> jump = delegate { requests++; return true; };
            Vector3 direction;
            replay.Tick(first, Point(0f, 0f), true, 0f, jump, out direction);
            replay.Tick(first, Point(0.5f, 0.5f), false, 0.1f, jump, out direction);
            replay.Tick(first, Point(2f, 0f), true, 0.5f, jump, out direction);
            replay.Tick(later, Point(2f, 0f), true, 0.6f, jump, out direction);
            Expect(!replay.Tick(first, Point(0f, 0f), true, 0.7f, jump, out direction),
                "reselecting a completed traversal relaunched it");
            Expect(requests == 1, "revisited completed sequence requested another jump");
        }

        private static void ResetClearsCommitOwnership()
        {
            var replay = new CompanionTraversalReplay();
            var point = Jump(1, Point(0f, 0f), Point(2f, 0f));
            Vector3 direction;
            replay.Tick(point, Point(0f, 0f), true, 0f, Yes, out direction);
            replay.Tick(point, Point(0.5f, 0.5f), false, 0.1f, Yes, out direction);
            replay.Reset();
            Expect(!replay.Active && replay.TargetSequence == 0,
                "reset retained active traversal ownership");
            Expect(replay.JumpCommittedSequence == 0 && replay.DropCommittedSequence == 0,
                "reset retained completed markers");
            Expect(!replay.Tick(point, Point(1f, 0.5f), false, 0.2f, Yes, out direction),
                "reset revived canceled airborne movement");
        }

        private static void ClockResetClearsOldFlight()
        {
            var replay = new CompanionTraversalReplay();
            var point = Jump(1, Point(0f, 0f), Point(2f, 0f));
            Vector3 direction;
            replay.Tick(point, Point(0f, 0f), true, 5f, Yes, out direction);
            replay.Tick(point, Point(0.5f, 0.5f), false, 5.1f, Yes, out direction);
            Expect(!replay.Tick(point, Point(1f, 0f), true, 0f, Yes, out direction),
                "clock reset retained obsolete flight");
            Expect(replay.JumpCommittedSequence == 0 && !replay.Active,
                "clock reset satisfied an obsolete jump");
        }

        private static void LegacyBreadcrumbRemainsOrdinaryApproach()
        {
            var replay = new CompanionTraversalReplay();
            var point = new BreadcrumbPoint(1, Point(2f, 0f), true, false, Point(1f, 0f));
            Vector3 direction;
            Expect(!replay.Tick(point, Point(0f, 0f), true, 0f,
                delegate { throw new InvalidOperationException("legacy marker invented a takeoff"); }, out direction),
                "legacy marker launched through replay");
            ExpectNear(replay.ApproachPosition.x, 2f, "legacy marker changed its destination");
        }

        private static void InPlaceJumpKeepsFiniteDirection()
        {
            var replay = new CompanionTraversalReplay();
            var point = Jump(1, Point(2f, 0f), Point(2f, 0f));
            Vector3 direction;
            Expect(replay.Tick(point, Point(2f, 0f), true, 0f, Yes, out direction),
                "in-place jump did not launch");
            ExpectNear(direction.sqrMagnitude, 0f, "in-place jump acquired invalid movement");
        }

        private static void CancelActivePreservesCompletedTraversal()
        {
            var replay = new CompanionTraversalReplay();
            var point = Jump(1, Point(0f, 0f), Point(2f, 0f));
            Vector3 direction;
            replay.Tick(point, Point(0f, 0f), true, 0f, Yes, out direction);
            replay.Tick(point, Point(0.5f, 0.5f), false, 0.1f, Yes, out direction);
            replay.Tick(point, Point(2f, 0f), true, 0.5f, Yes, out direction);
            replay.CancelActive();
            Expect(replay.JumpCommittedSequence == 1, "holding erased completed traversal identity");
            ExpectNear(replay.ApproachPosition.x, 2f, "holding changed landing approach back to takeoff");
            Expect(!replay.Tick(point, Point(0f, 0f), true, 1f,
                delegate { throw new InvalidOperationException("holding restarted a completed jump"); }, out direction),
                "holding restarted movement for a completed traversal");
        }

        private static void CancelAirborneDoesNotSatisfyItsMarker()
        {
            var replay = new CompanionTraversalReplay();
            var point = Jump(1, Point(0f, 0f), Point(2f, 0f));
            Vector3 direction;
            replay.Tick(point, Point(0f, 0f), true, 0f, Yes, out direction);
            replay.Tick(point, Point(0.5f, 0.5f), false, 0.1f, Yes, out direction);
            replay.CancelActive();
            Expect(!replay.Active && replay.JumpCommittedSequence == 0,
                "canceling airborne movement credited an unfinished traversal");
            Expect(!replay.Tick(point, Point(1f, 0.5f), false, 0.2f, Yes, out direction),
                "canceled airborne direction kept movement ownership");
            Expect(!replay.Tick(point, Point(2f, 0f), true, 0.5f, Yes, out direction),
                "landing automatically resumed canceled movement");
            ExpectNear(replay.ApproachPosition.x, 2f, "physical arrival after cancellation was ignored");
            Expect(replay.JumpCommittedSequence == 1, "physical arrival after cancellation did not satisfy hint");
        }

        private static void CancelNextLaunchKeepsPreviousCompletion()
        {
            var replay = new CompanionTraversalReplay();
            var first = Jump(1, Point(0f, 0f), Point(2f, 0f));
            var second = Jump(2, Point(2f, 0f), Point(4f, 0f));
            Vector3 direction;
            replay.Tick(first, Point(0f, 0f), true, 0f, Yes, out direction);
            replay.Tick(first, Point(0.5f, 0.5f), false, 0.1f, Yes, out direction);
            replay.Tick(first, Point(2f, 0f), true, 0.5f, Yes, out direction);
            replay.Tick(second, Point(2f, 0f), true, 0.6f, Yes, out direction);
            replay.CancelActive();
            Expect(replay.JumpCommittedSequence == 1 && replay.TargetSequence == 2 && !replay.Active,
                "canceling a pending launch erased previous completion or kept active movement");
            Expect(replay.Tick(second, Point(2f, 0f), true, 1f, Yes, out direction),
                "canceled traversal could not retry after movement resumed");
        }

        private static void AirborneCorrectionTargetsOriginalLanding()
        {
            var replay = new CompanionTraversalReplay();
            var first = Jump(1, Point(0f, 0f), Point(2f, 0f));
            var other = Jump(2, Point(2f, 0f), Point(-2f, 0f));
            Vector3 direction;
            replay.Tick(first, Point(0f, 0f), true, 0f, Yes, out direction);
            replay.Tick(first, Point(0.5f, 0.5f), false, 0.1f, Yes, out direction);
            Expect(replay.Tick(other, new Vector3(1f, 0.5f, 1f), false, 0.2f, Yes, out direction),
                "lateral airborne correction released traversal ownership");
            ExpectNear(direction.x, (float)Math.Sqrt(0.5), "air control did not approach original landing x");
            ExpectNear(direction.z, -(float)Math.Sqrt(0.5), "air control ignored lateral drift");
            Expect(replay.TargetSequence == 1, "air correction adopted the replacement target");
        }

        private static void LongDropStopsHorizontalIntentAboveLanding()
        {
            var replay = new CompanionTraversalReplay();
            var point = new BreadcrumbPoint(1, Point(2f, -8f), false, true,
                Point(1f, 0f), true, Point(0f, 0f), 2f);
            Vector3 direction;
            replay.Tick(point, Point(0f, 0f), true, 0f, Yes, out direction);
            replay.Tick(point, Point(0.5f, -0.5f), false, 0.1f, Yes, out direction);
            Expect(replay.Tick(point, Point(1.9f, -2f), false, 1f, Yes, out direction),
                "reaching landing horizontally ended the drop before grounding");
            ExpectNear(direction.sqrMagnitude, 0f, "long drop kept driving past its landing");
            Expect(replay.Active && replay.DropCommittedSequence == 0,
                "horizontal arrival prematurely satisfied a drop");
            Expect(replay.Tick(point, Point(2.1f, -4f), false, 2f, Yes, out direction),
                "small overshoot released airborne control");
            ExpectNear(direction.sqrMagnitude, 0f, "minor overshoot flipped airborne movement");
            replay.Tick(point, Point(2.1f, -8f), true, 3f, Yes, out direction);
            Expect(replay.DropCommittedSequence == 1, "drop completion was lost after zero lateral intent");
        }

        private static void AirborneOvershootCanCorrectBackToLanding()
        {
            var replay = new CompanionTraversalReplay();
            var point = Jump(1, Point(0f, 0f), Point(2f, 0f));
            Vector3 direction;
            replay.Tick(point, Point(0f, 0f), true, 0f, Yes, out direction);
            replay.Tick(point, Point(0.5f, 0.5f), false, 0.1f, Yes, out direction);
            Expect(replay.Tick(point, Point(2.5f, 0.2f), false, 0.4f, Yes, out direction),
                "overshoot ended airborne control");
            ExpectNear(direction.x, -1f, "air control continued increasing landing overshoot");
        }

        private static void LoggedShortDropCompletesWithoutAirborne()
        {
            var replay = new CompanionTraversalReplay();
            var takeoff = new Vector3(-215.21f, 33.35f, -508.62f);
            var landing = new Vector3(-214.83f, 32.68f, -508.93f);
            var point = Drop(80, takeoff, landing);
            Vector3 direction;
            Expect(replay.Tick(point, takeoff, true, 33.7f, NeverJump, out direction),
                "logged short drop did not start");
            var actualArrival = new Vector3(-214.94f, 32.54f, -508.88f);
            Expect(!replay.Tick(point, actualArrival, true, 33.85f, NeverJump, out direction),
                "logged grounded arrival kept driving toward the old launch heading");
            Expect(replay.DropCommittedSequence == 80 && !replay.Active,
                "logged grounded arrival was not recognized as a completed hint");
            ExpectNear(direction.sqrMagnitude, 0f, "logged arrival retained movement intent");
            replay.Tick(point, actualArrival, true, 35f, NeverJump, out direction);
            Expect(!replay.Active && replay.DropCommittedSequence == 80,
                "logged arrival later retried the same short drop");
        }

        private static void GroundedOvershootCompletesDrop()
        {
            var replay = new CompanionTraversalReplay();
            var takeoff = new Vector3(-215.21f, 33.35f, -508.62f);
            var landing = new Vector3(-214.83f, 32.68f, -508.93f);
            var point = Drop(80, takeoff, landing);
            Vector3 direction;
            replay.Tick(point, takeoff, true, 32.6f, NeverJump, out direction);
            Expect(!replay.Tick(point, new Vector3(-214.44f, 32.84f, -509.25f), true,
                32.78f, NeverJump, out direction), "grounded overstep was sent back to takeoff");
            Expect(replay.DropCommittedSequence == 80, "grounded endpoint crossing did not satisfy hint");
        }

        private static void GroundedArrivalBeforeLaunchSatisfiesHint()
        {
            var replay = new CompanionTraversalReplay();
            var point = Drop(1, Point(0f, 1f), Point(0.5f, 0f));
            Vector3 direction;
            Expect(!replay.Tick(point, Point(0.5f, -0.1f), true, 0f, NeverJump, out direction),
                "already reached hint acquired movement ownership");
            Expect(replay.DropCommittedSequence == 1 && !replay.Active,
                "already reached hint required a trip back to takeoff");
            ExpectNear(replay.ApproachPosition.x, 0.5f, "already reached hint retained takeoff target");
        }

        private static void WalkedJumpSpanDoesNotRequireJumpAnimation()
        {
            var replay = new CompanionTraversalReplay();
            var point = Jump(1, Point(0f, 0f), Point(2f, 0f));
            Vector3 direction;
            replay.Tick(point, Point(0f, 0f), true, 0f, Yes, out direction);
            Expect(!replay.Tick(point, Point(1.9f, 0f), true, 0.6f, NeverJump, out direction),
                "walking across a jump hint still demanded airborne evidence");
            Expect(replay.JumpCommittedSequence == 1 && !replay.Active,
                "walkable jump span was not satisfied by physical progress");
        }

        private static void RaisedPlatformAtSameXZDoesNotCompleteAtLaunch()
        {
            var replay = new CompanionTraversalReplay();
            var point = Jump(1, Point(0f, 0f), Point(0f, 0.9f));
            Vector3 direction;
            Expect(replay.Tick(point, Point(0f, 0f), true, 0f, Yes, out direction),
                "standing beneath a raised platform satisfied the landing");
            replay.Tick(point, Point(0f, 0f), true, 0.3f, Yes, out direction);
            Expect(replay.JumpCommittedSequence == 0 && replay.Active,
                "same XZ below platform was classified as arrival");

            var smallRise = new CompanionTraversalReplay();
            var shortPoint = Jump(2, Point(0f, 0f), Point(0f, 0.2f));
            Expect(smallRise.Tick(shortPoint, Point(0f, 0f), true, 0f, Yes, out direction),
                "landing tolerance completed a short vertical step before any progress");
            Expect(smallRise.JumpCommittedSequence == 0,
                "short vertical step was satisfied while still at takeoff");
        }

        private static void InPlaceJumpDoesNotCompleteWhileGrounded()
        {
            var replay = new CompanionTraversalReplay();
            var point = Jump(1, Point(0f, 0f), Point(0f, 0f));
            Vector3 direction;
            Expect(replay.Tick(point, Point(0f, 0f), true, 0f, Yes, out direction),
                "in-place jump self-completed on its launch tick");
            replay.Tick(point, Point(0f, 0f), true, 0.4f, Yes, out direction);
            Expect(replay.JumpCommittedSequence == 0,
                "unchanged grounded position satisfied an in-place jump");
        }

        private static void VerticalDropRequiresRealProgress()
        {
            var replay = new CompanionTraversalReplay();
            var point = Drop(1, Point(0f, 0.2f), Point(0f, 0f));
            Vector3 direction;
            Expect(replay.Tick(point, Point(0f, 0.2f), true, 0f, NeverJump, out direction),
                "vertical drop self-completed within landing height tolerance");
            Expect(!replay.Tick(point, Point(0f, 0f), true, 0.3f, NeverJump, out direction),
                "grounded downward progress failed to satisfy vertical drop");
            Expect(replay.DropCommittedSequence == 1, "completed vertical drop retained old hint");
        }

        private static void SidewaysOrDistantGroundDoesNotCountAsArrival()
        {
            var point = Drop(1, Point(0f, 1f), Point(1f, 0f));
            Vector3 direction;
            var sideways = new CompanionTraversalReplay();
            sideways.Tick(point, Point(0f, 1f), true, 0f, NeverJump, out direction);
            Expect(sideways.Tick(point, new Vector3(1.1f, 0f, 1f), true, 0.3f, NeverJump, out direction),
                "unrelated side ground was credited as endpoint crossing");
            Expect(sideways.DropCommittedSequence == 0, "sideways endpoint-plane crossing satisfied hint");
            var distant = new CompanionTraversalReplay();
            distant.Tick(point, Point(0f, 1f), true, 0f, NeverJump, out direction);
            Expect(distant.Tick(point, Point(3f, 0f), true, 0.3f, NeverJump, out direction),
                "unbounded ground beyond endpoint was treated as a local landing");
            Expect(distant.DropCommittedSequence == 0, "distant ground satisfied endpoint arrival");
            var lowerFloor = new CompanionTraversalReplay();
            lowerFloor.Tick(point, Point(0f, 1f), true, 0f, NeverJump, out direction);
            Expect(lowerFloor.Tick(point, Point(1f, -0.8f), true, 0.3f, NeverJump, out direction),
                "another floor counted as the expected landing height");
            Expect(lowerFloor.DropCommittedSequence == 0, "vertical tolerance matched another floor");
        }

        private static void PendingDropReaimsTowardLanding()
        {
            var replay = new CompanionTraversalReplay();
            var point = Drop(1, Point(0f, 1f), Point(1f, 0f));
            Vector3 direction;
            replay.Tick(point, Point(0f, 1f), true, 0f, NeverJump, out direction);
            Expect(replay.Tick(point, new Vector3(0.5f, 0.7f, 0.5f), true, 0.2f, NeverJump, out direction),
                "pending drop lost ownership during sideways drift");
            ExpectNear(direction.x, (float)Math.Sqrt(0.5), "pending drop retained stale launch x");
            ExpectNear(direction.z, -(float)Math.Sqrt(0.5), "pending drop ignored lateral drift");
            Expect(replay.Tick(point, Point(1.5f, 0.7f), true, 0.3f, NeverJump, out direction),
                "pending drop completed despite wrong landing height");
            ExpectNear(direction.x, -1f, "pending drop continued outward after passing landing XZ");
        }

        private static void ArrivalAfterLaunchTimeoutDoesNotReturnToTakeoff()
        {
            var replay = new CompanionTraversalReplay();
            var point = Drop(1, Point(0f, 1f), Point(1f, 0f));
            Vector3 direction;
            replay.Tick(point, Point(0f, 1f), true, 0f, NeverJump, out direction);
            Expect(!replay.Tick(point, Point(0f, 1f), true, 0.9f, NeverJump, out direction),
                "unchanged failed launch did not release ownership");
            Expect(!replay.Tick(point, Point(1f, 0f), true, 1f, NeverJump, out direction),
                "arrival during cooldown requested another launch");
            Expect(replay.DropCommittedSequence == 1,
                "arrival during retry cooldown was ignored until a return to takeoff");
            ExpectNear(replay.ApproachPosition.x, 1f, "cooldown arrival retained wrong approach target");
        }

        private static BreadcrumbPoint Drop(int sequence, Vector3 takeoff, Vector3 landing)
        {
            var direction = landing - takeoff;
            direction.y = 0f;
            return new BreadcrumbPoint(sequence, landing, false, true,
                direction.normalized, true, takeoff, 0.14f);
        }

        private static bool NeverJump()
        {
            throw new InvalidOperationException("drop or completed hint requested a native jump");
        }

        private static BreadcrumbPoint Jump(int sequence, Vector3 takeoff, Vector3 landing)
        {
            var direction = landing - takeoff;
            direction.y = 0f;
            return new BreadcrumbPoint(sequence, landing, true, false,
                direction.normalized, true, takeoff, 0.5f);
        }

        private static Vector3 Point(float x, float y)
        {
            return new Vector3(x, y, 0f);
        }

        private static bool Yes()
        {
            return true;
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
