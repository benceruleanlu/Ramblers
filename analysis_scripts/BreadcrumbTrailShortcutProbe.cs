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
            PreservesUncommittedJump();
            PreservesUncommittedDrop();
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
                out firstRemoved,
                out lastRemoved);

            Expect(removed == 0, "stacked-floor point was incorrectly collapsed");
            Expect(trail.Count == 2, "vertical rejection mutated the route");
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
