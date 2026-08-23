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

        internal void Normalize()
        {
            var length = magnitude;
            if (length <= 0.000001f)
                return;
            x /= length;
            y /= length;
            z /= length;
        }

        public static Vector3 operator -(Vector3 left, Vector3 right)
        {
            return new Vector3(
                left.x - right.x,
                left.y - right.y,
                left.z - right.z);
        }

        public static Vector3 operator *(Vector3 value, float scalar)
        {
            return new Vector3(
                value.x * scalar,
                value.y * scalar,
                value.z * scalar);
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
    internal static class BreadcrumbTrailShortcutProbe
    {
        private static int Main()
        {
            CollapsesThroughLatestNearbyPoint();
            RejectsAnotherVerticalLevel();
            RejectsBlockedNearbyPoint();
            PreservesUncommittedJump();
            PreservesUncommittedDrop();
            ApproachesDistantTraversalMarker();
            UsesTangentInsideTraversalCorridor();
            UsesTangentForActiveExactCommit();
            StopsUsingTangentAfterCommitExpires();
            ReleasesPriorCommitOnFirstGroundedTick();
            Console.WriteLine("Breadcrumb shortcut probe passed.");
            return 0;
        }

        private static void CollapsesThroughLatestNearbyPoint()
        {
            var trail = new BreadcrumbTrail(8);
            var first = trail.Add(Point(0f, 0f), false, false);
            trail.Add(Point(1f, 0f), false, false);
            var latestNearby = trail.Add(Point(1.2f, 0f), false, false);
            var remaining = trail.Add(Point(3f, 0f), false, false);

            BreadcrumbPoint firstRemoved;
            BreadcrumbPoint lastRemoved;
            var removed = trail.RemoveThroughLatestNearby(
                Point(1.1f, 0f),
                0.25f,
                0.5f,
                0,
                0,
                null,
                out firstRemoved,
                out lastRemoved);

            Expect(removed == 3, "latest nearby point should collapse the full prefix");
            Expect(firstRemoved.Sequence == first.Sequence, "first removed sequence changed");
            Expect(lastRemoved.Sequence == latestNearby.Sequence, "shortcut chose an older nearby point");
            Expect(trail.Count == 1, "shortcut removed later route points");
            Expect(trail.Peek().Sequence == remaining.Sequence, "remaining route head changed");
        }

        private static void RejectsAnotherVerticalLevel()
        {
            var trail = new BreadcrumbTrail(4);
            trail.Add(Point(0f, 0f), false, false);
            trail.Add(Point(1f, 2f), false, false);

            BreadcrumbPoint firstRemoved;
            BreadcrumbPoint lastRemoved;
            var removed = trail.RemoveThroughLatestNearby(
                Point(1f, 0f),
                0.25f,
                0.5f,
                0,
                0,
                null,
                out firstRemoved,
                out lastRemoved);

            Expect(removed == 0, "stacked-floor point was incorrectly collapsed");
            Expect(trail.Count == 2, "vertical rejection mutated the route");
        }

        private static void RejectsBlockedNearbyPoint()
        {
            var trail = new BreadcrumbTrail(4);
            trail.Add(Point(0f, 0f), false, false);
            trail.Add(Point(1f, 0f), false, false);

            BreadcrumbPoint firstRemoved;
            BreadcrumbPoint lastRemoved;
            var removed = trail.RemoveThroughLatestNearby(
                Point(1f, 0f),
                0.25f,
                0.5f,
                0,
                0,
                delegate { return false; },
                out firstRemoved,
                out lastRemoved);

            Expect(removed == 0, "blocked nearby point was incorrectly collapsed");
            Expect(trail.Count == 2, "blocked shortcut mutated the route");
        }

        private static void PreservesUncommittedJump()
        {
            var trail = new BreadcrumbTrail(4);
            trail.Add(Point(0f, 0f), false, false);
            var jump = trail.Add(Point(1f, 0f), true, false);
            trail.Add(Point(2f, 0f), false, false);

            ExpectShortcutBlocked(trail, Point(2f, 0f), "jump");

            BreadcrumbPoint firstRemoved;
            BreadcrumbPoint lastRemoved;
            var removed = trail.RemoveThroughLatestNearby(
                Point(2f, 0f),
                0.25f,
                0.5f,
                jump.Sequence,
                0,
                null,
                out firstRemoved,
                out lastRemoved);
            Expect(removed == 3, "committed jump should permit a proven loop shortcut");
        }

        private static void PreservesUncommittedDrop()
        {
            var trail = new BreadcrumbTrail(4);
            trail.Add(Point(0f, 0f), false, false);
            trail.Add(Point(1f, 0f), false, true);
            trail.Add(Point(2f, 0f), false, false);

            ExpectShortcutBlocked(trail, Point(2f, 0f), "drop");
        }

        private static void ApproachesDistantTraversalMarker()
        {
            var marker = new BreadcrumbPoint(
                1,
                new UnityEngine.Vector3(0f, 0f, 10f),
                true,
                false,
                new UnityEngine.Vector3(1f, 0f, 0f));
            bool usingTangent;
            var direction = BreadcrumbTrail.ResolveTraversalApproachDirection(
                Point(0f, 0f), marker, false, false, 1.6f, 1.8f,
                out usingTangent);

            Expect(!usingTangent, "distant jump marker replayed its tangent");
            Expect(direction.z > 9.9f && Math.Abs(direction.x) < 0.01f,
                "distant jump marker was not approached by waypoint vector");
        }

        private static void UsesTangentInsideTraversalCorridor()
        {
            var marker = new BreadcrumbPoint(
                1,
                new UnityEngine.Vector3(0f, 0f, 10f),
                true,
                false,
                new UnityEngine.Vector3(1f, 0f, 0f));
            bool usingTangent;
            var direction = BreadcrumbTrail.ResolveTraversalApproachDirection(
                new UnityEngine.Vector3(0f, 0f, 8.8f), marker,
                false, false, 1.6f, 1.8f, out usingTangent);

            Expect(usingTangent, "near jump marker did not use its traversal tangent");
            Expect(direction.x > 0.99f && Math.Abs(direction.z) < 0.01f,
                "near jump marker returned the wrong tangent");
        }

        private static void UsesTangentForActiveExactCommit()
        {
            var marker = new BreadcrumbPoint(
                1,
                new UnityEngine.Vector3(0f, 0f, 10f),
                false,
                true,
                new UnityEngine.Vector3(-1f, 0f, 0f));
            bool usingTangent;
            var direction = BreadcrumbTrail.ResolveTraversalApproachDirection(
                Point(0f, 0f), marker, true, true, 1.6f, 1.8f,
                out usingTangent);

            Expect(usingTangent, "active exact drop commit lost its tangent");
            Expect(direction.x < -0.99f, "active exact drop commit changed direction");
        }

        private static void StopsUsingTangentAfterCommitExpires()
        {
            var marker = new BreadcrumbPoint(
                1,
                new UnityEngine.Vector3(0f, 0f, 10f),
                true,
                false,
                new UnityEngine.Vector3(1f, 0f, 0f));
            bool usingTangent;
            var direction = BreadcrumbTrail.ResolveTraversalApproachDirection(
                new UnityEngine.Vector3(0f, 0f, 9f), marker,
                true, false, 1.6f, 1.8f, out usingTangent);

            Expect(!usingTangent, "expired traversal commit kept replaying its tangent");
            Expect(direction.z > 0.99f && Math.Abs(direction.x) < 0.01f,
                "expired traversal commit did not return to waypoint approach");
        }

        private static void ReleasesPriorCommitOnFirstGroundedTick()
        {
            Expect(!BreadcrumbTrail.ShouldReleasePriorTraversalCommit(8, 7, false),
                "airborne target change discarded its landing direction");
            Expect(!BreadcrumbTrail.ShouldReleasePriorTraversalCommit(7, 7, true),
                "current marker commitment was treated as stale");
            Expect(!BreadcrumbTrail.ShouldReleasePriorTraversalCommit(8, 0, true),
                "missing commitment was treated as stale");
            Expect(BreadcrumbTrail.ShouldReleasePriorTraversalCommit(8, 7, true),
                "first grounded tick retained the previous marker commitment");
        }

        private static void ExpectShortcutBlocked(
            BreadcrumbTrail trail,
            UnityEngine.Vector3 from,
            string marker)
        {
            BreadcrumbPoint firstRemoved;
            BreadcrumbPoint lastRemoved;
            var removed = trail.RemoveThroughLatestNearby(
                from,
                0.25f,
                0.5f,
                0,
                0,
                null,
                out firstRemoved,
                out lastRemoved);
            Expect(removed == 0, "uncommitted " + marker + " marker was skipped");
            Expect(trail.Count == 3, "blocked shortcut mutated the route");
        }

        private static UnityEngine.Vector3 Point(float x, float y)
        {
            return new UnityEngine.Vector3(x, y, 0f);
        }

        private static void Expect(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
    }
}
