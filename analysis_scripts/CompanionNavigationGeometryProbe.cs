using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace Il2CppInterop.Runtime.InteropTypes.Arrays
{
    internal sealed class Il2CppStructArray<T>
    {
        private readonly T[] _values;
        internal Il2CppStructArray(int count) { _values = new T[count]; }
        internal T this[int index] { get => _values[index]; set => _values[index] = value; }
    }
}

namespace UnityEngine
{
    internal struct Vector3
    {
        internal float x;
        internal float y;
        internal float z;
        internal Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        internal static Vector3 zero => new Vector3(0f, 0f, 0f);
        internal static Vector3 forward => new Vector3(0f, 0f, 1f);
        internal static Vector3 up => new Vector3(0f, 1f, 0f);
        internal static Vector3 down => new Vector3(0f, -1f, 0f);
        internal float sqrMagnitude => x * x + y * y + z * z;
        internal float magnitude => (float)Math.Sqrt(sqrMagnitude);
        internal Vector3 normalized => magnitude < 0.00001f ? zero : this / magnitude;
        internal static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
        internal void Normalize() { var m = magnitude; if (m > 0.00001f) { x /= m; y /= m; z /= m; } }
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator -(Vector3 value) => new Vector3(-value.x, -value.y, -value.z);
        public static Vector3 operator *(Vector3 a, float b) => new Vector3(a.x * b, a.y * b, a.z * b);
        public static Vector3 operator /(Vector3 a, float b) => new Vector3(a.x / b, a.y / b, a.z / b);
        internal static float Distance(Vector3 a, Vector3 b) => (a - b).magnitude;
    }

    internal struct Quaternion
    {
        private float _yaw;
        internal static Quaternion AngleAxis(float degrees, Vector3 axis) => new Quaternion { _yaw = degrees };
        public static Vector3 operator *(Quaternion q, Vector3 v)
        {
            var rad = q._yaw * Math.PI / 180.0;
            return new Vector3((float)(v.x * Math.Cos(rad) + v.z * Math.Sin(rad)), v.y,
                (float)(-v.x * Math.Sin(rad) + v.z * Math.Cos(rad)));
        }
    }

    internal static class Mathf
    {
        internal static float Min(float a, float b) => Math.Min(a, b);
        internal static int Min(int a, int b) => Math.Min(a, b);
        internal static float Max(float a, float b) => Math.Max(a, b);
        internal static float Abs(float v) => Math.Abs(v);
        internal static float Clamp(float value, float minimum, float maximum) => Math.Min(maximum, Math.Max(minimum, value));
    }

    internal sealed class Transform
    {
        internal string name = "test";
        internal Transform parent = null;
        internal float Yaw;
        internal Vector3 InverseTransformDirection(Vector3 v) => Quaternion.AngleAxis(-Yaw, Vector3.up) * v;
    }

    internal sealed class GameObject { internal int layer = 10; }
    internal struct Bounds
    {
        internal Vector3 center;
        internal Vector3 size;
        internal Vector3 extents => size * 0.5f;
        internal Vector3 min => center - extents;
    }

    internal class Collider
    {
        internal Transform transform = new Transform();
        internal GameObject gameObject = new GameObject();
        internal Bounds bounds;
    }
    internal sealed class CapsuleCollider : Collider { }
    internal struct RaycastHit
    {
        internal Collider collider;
        internal float distance;
        internal Vector3 point;
        internal Vector3 normal;
    }
    internal enum QueryTriggerInteraction { Ignore }
    internal struct LayerMask
    {
        internal int value;
        public static implicit operator LayerMask(int v) => new LayerMask { value = v };
    }
    internal static class Time { internal static float time; }
    internal static class Physics
    {
        internal const int DefaultRaycastLayers = -1;
        internal static Func<Vector3, Vector3, float, RaycastHit?> Ray;
        internal static bool Raycast(Vector3 origin, Vector3 direction, out RaycastHit hit,
            float distance, int mask, QueryTriggerInteraction trigger)
        {
            var result = Ray == null ? null : Ray(origin, direction, distance);
            hit = result.GetValueOrDefault();
            return result.HasValue;
        }
    }
}

internal sealed class PlayerGround
{
    internal static RaycastHit[] CastHits = new RaycastHit[0];
    internal static Vector3 LastOriginOffset;
    internal static Vector3 LastCastDirection;
    internal static int CastCalls;
    internal bool isGrounded = true;
    internal LayerMask layerMask = -1;
    internal Vector3 LastSlopeInput;
    internal Vector3 GetSlopedMoveForce(Vector3 input, out float scalar)
    {
        LastSlopeInput = input;
        scalar = 1f;
        return input;
    }
    internal static int ColliderCastNonAlloc(Collider collider, Vector3 offset, Vector3 direction,
        Il2CppStructArray<RaycastHit> hits, float distance, LayerMask mask, QueryTriggerInteraction trigger)
    {
        LastOriginOffset = offset;
        LastCastDirection = direction;
        CastCalls++;
        for (var index = 0; index < CastHits.Length && index < 32; index++) hits[index] = CastHits[index];
        return Math.Min(CastHits.Length, 32);
    }
}
internal sealed class PlayerTunings
{
    internal float forwardSpeed = 3f;
    internal float forwardSprintSpeed = 5.5f;
    internal float crouchForwardSpeed = 2f;
    internal float crouchForwardSprintSpeed = 3f;
}
internal sealed class PlayerCollision { internal CapsuleCollider bodyCollider = new CapsuleCollider(); }
internal sealed class PlayerSprinter { internal bool isSprinting; internal bool sprintIsToggledOn; }
internal sealed class PlayerCharacter
{
    internal PlayerGround ground = new PlayerGround();
    internal PlayerCollision collision = new PlayerCollision();
    internal PlayerTunings tunings = new PlayerTunings();
    internal PlayerSprinter sprinter = new PlayerSprinter();
    internal Transform kernal = new Transform();
}

namespace Ramblers
{
    internal enum CompanionPosture { Standing, Crouching }
    internal sealed class PlayerNetworking { internal Vector3 NetworkcontrolsVelocity; }
    internal sealed class CompanionBody
    {
        internal PlayerCharacter Character = new PlayerCharacter();
        internal PlayerNetworking Networking = new PlayerNetworking();
        internal Vector3 Position;
        internal bool IsAlive = true;
        internal bool Contains(Transform t) => t == Character.collision.bodyCollider.transform;
    }
    internal sealed class LogLatch
    {
        private bool _set;
        internal bool ShouldLog() { var prior = _set; _set = true; return !prior; }
        internal void Reset() { _set = false; }
    }
    internal static class Plugin { internal static LoggerStub Logger = new LoggerStub(); }
    internal sealed class LoggerStub
    {
        internal readonly System.Collections.Generic.List<string> Entries = new System.Collections.Generic.List<string>();
        internal void LogInfo(string value) { Entries.Add(value); }
        internal void LogWarning(string value) { Entries.Add(value); }
    }

    internal static class CompanionNavigationGeometryProbe
    {
        private static readonly Vector3 Forward = new Vector3(0f, 0f, 1f);
        private static int Main()
        {
            WalkableHitDoesNotHideWall();
            HitOrderDoesNotChangeClearance();
            SupportAloneIsClear();
            HypotheticalOriginOffsetsTheNativeCollider();
            SameTerrainWallIsFoundBySupplementaryRay();
            NeighboringFootprintBridgesMissingCenter();
            UnknownSupportPreservesCandidate();
            GroundedPointKeepsBodyOriginOffset();
            UpperPlaneDoesNotHideExpectedSupportLayer();
            SlopeInputUsesCharacterCoordinates();
            VerticalTraversalClearsPreviousWalkingIntent();
            TraversalSpeedUsesRecordedPaceWithinNativeBounds();
            PhysicsQueryCountTracksActualCalls();
            FlatWalkingDoesNotRequireReplayingAJump();
            RealGapRemainsUncertainWithoutBlockingNativeMovement();
            TinyFloorSeamKeepsWalkingConnection();
            ContinuousRampKeepsWalkingConnection();
            AbruptFloorLayerSwitchIsNotAConfirmedWalk();
            WalkingConnectionReportsMeasuredWall();
            WalkingProbeBudgetIsBoundedAndScoped();
            WalkingConnectionPreservesBodyOriginHeight();
            WalkingConnectionFollowsHillContour();
            GroundContactHeightDifferencesStayWithinTolerance();
            FootprintHitIsProjectedBackToTheCandidateCenter();
            OrdinaryRouteProjectionKeepsTheUpperPlatform();
            OrdinaryRouteProjectionDropsRawAirborneHeight();
            OrdinaryRouteProjectionPreservesUnknownCandidate();
            ReproQueryBudgetIsScoped();
            NativeReproLeavesBodiesAndMovementUnchanged();
            ReleaseClearsNativeReferences();
            Console.WriteLine("Companion navigation geometry probe passed (30 cases).");
            return 0;
        }

        private static CompanionBody Create(out CompanionNavigationGeometry geometry)
        {
            Physics.Ray = null;
            PlayerGround.CastHits = new RaycastHit[0];
            PlayerGround.CastCalls = 0;
            Time.time = 0f;
            var body = new CompanionBody();
            body.Character.collision.bodyCollider.bounds = new Bounds
            {
                center = new Vector3(0f, 0.75f, 0f),
                size = new Vector3(0.5f, 1.5f, 0.5f)
            };
            geometry = new CompanionNavigationGeometry();
            geometry.Bind(body);
            return body;
        }

        private static RaycastHit Hit(float distance, Vector3 normal, Collider collider = null)
        {
            return new RaycastHit
            {
                distance = distance,
                normal = normal,
                collider = collider ?? new Collider(),
                point = new Vector3(0f, 0f, distance)
            };
        }

        private static void WalkableHitDoesNotHideWall()
        {
            CompanionNavigationGeometry geometry;
            var body = Create(out geometry);
            PlayerGround.CastHits = new[]
            {
                Hit(0.05f, Vector3.up),
                Hit(0.9f, new Vector3(0f, 0f, -1f)),
                Hit(0f, new Vector3(0f, 0f, -1f), body.Character.collision.bodyCollider)
            };
            string description;
            Equal(0.9f, geometry.MeasureClearance(Vector3.zero, Forward, 3f, out description),
                "ignored first support/self hid the wall");
        }

        private static void HitOrderDoesNotChangeClearance()
        {
            CompanionNavigationGeometry geometry;
            Create(out geometry);
            PlayerGround.CastHits = new[]
            {
                Hit(2f, new Vector3(0f, 0f, -1f)),
                Hit(0.05f, Vector3.up),
                Hit(0.8f, new Vector3(0f, 0f, -1f))
            };
            string description;
            Equal(0.8f, geometry.MeasureClearance(Vector3.zero, Forward, 3f, out description),
                "unsorted hit list did not choose nearest wall");
        }

        private static void SupportAloneIsClear()
        {
            CompanionNavigationGeometry geometry;
            Create(out geometry);
            PlayerGround.CastHits = new[] { Hit(0f, Vector3.up), Hit(0.8f, new Vector3(0.3f, 0.95f, 0f)) };
            string description;
            Equal(3f, geometry.MeasureClearance(Vector3.zero, Forward, 3f, out description),
                "support surface blocked walking");
        }

        private static void HypotheticalOriginOffsetsTheNativeCollider()
        {
            CompanionNavigationGeometry geometry;
            var body = Create(out geometry);
            body.Position = new Vector3(10f, 2f, 4f);
            var origin = new Vector3(12f, 3f, 8f);
            Expect(geometry.IsSegmentClear(origin, origin + new Vector3(0f, 0.6f, 2f)),
                "clear hypothetical segment blocked");
            Equal(2f, PlayerGround.LastOriginOffset.x, "native offset x");
            Equal(1.06f, PlayerGround.LastOriginOffset.y, "native offset y and contact skin");
            Equal(4f, PlayerGround.LastOriginOffset.z, "native offset z");
            Expect(PlayerGround.LastCastDirection.y > 0.1f, "segment discarded its vertical direction");
        }

        private static void SameTerrainWallIsFoundBySupplementaryRay()
        {
            CompanionNavigationGeometry geometry;
            Create(out geometry);
            var terrain = new Collider();
            PlayerGround.CastHits = new[] { Hit(0.02f, Vector3.up, terrain) };
            Physics.Ray = (origin, direction, distance) =>
                direction.z > 0.9f ? (RaycastHit?)Hit(1.25f, new Vector3(0f, 0f, -1f), terrain) : null;
            string description;
            Equal(1f, geometry.MeasureClearance(Vector3.zero, Forward, 3f, out description),
                "same-collider wall was hidden by terrain support");
        }

        private static void NeighboringFootprintBridgesMissingCenter()
        {
            CompanionNavigationGeometry geometry;
            Create(out geometry);
            Physics.Ray = (origin, direction, distance) =>
            {
                if (Math.Abs(origin.x) < 0.01f && Math.Abs(origin.z) < 0.01f) return null;
                return new RaycastHit { collider = new Collider(), normal = Vector3.up,
                    point = new Vector3(origin.x, 0f, origin.z), distance = origin.y };
            };
            Vector3 grounded;
            Expect(geometry.TryGroundPoint(Vector3.zero, out grounded),
                "small center seam lost the entire footprint");
            Equal(0f, grounded.y, "seam support height");
        }

        private static void UnknownSupportPreservesCandidate()
        {
            CompanionNavigationGeometry geometry;
            Create(out geometry);
            var candidate = new Vector3(5f, 4f, 3f);
            Vector3 grounded;
            Expect(!geometry.TryGroundPoint(candidate, out grounded), "invented ground support");
            Equal(0f, Vector3.Distance(candidate, grounded), "unknown support changed candidate");
            Expect(geometry.IsSegmentClear(candidate, candidate + Forward),
                "missing support unconditionally blocked native movement");
        }

        private static void GroundedPointKeepsBodyOriginOffset()
        {
            CompanionNavigationGeometry geometry;
            var body = Create(out geometry);
            body.Position = new Vector3(0f, 0.4f, 0f);
            Physics.Ray = (origin, direction, distance) => new RaycastHit
            {
                collider = new Collider(), normal = Vector3.up,
                point = new Vector3(origin.x, 0.2f, origin.z), distance = origin.y - 0.2f
            };
            Vector3 grounded;
            Expect(geometry.TryGroundPoint(new Vector3(1f, 0.4f, 2f), out grounded), "missing raised support");
            Equal(0.6f, grounded.y, "body origin offset discarded");
        }

        private static void SlopeInputUsesCharacterCoordinates()
        {
            CompanionNavigationGeometry ignoredGeometry;
            var body = Create(out ignoredGeometry);
            var locomotion = new CompanionLocomotion();
            locomotion.Bind(body, 0f);
            SteeringStatus status;
            body.Character.kernal.Yaw = 0f;
            Expect(locomotion.TrySteerToward(Forward, 2f, 0f, out status), "yaw zero movement failed");
            Equal(1f, body.Character.ground.LastSlopeInput.z, "yaw zero local forward");
            body.Character.kernal.Yaw = 90f;
            Expect(locomotion.TrySteerToward(Forward, 2f, 0.2f, out status), "rotated movement failed");
            Equal(-1f, body.Character.ground.LastSlopeInput.x, "world direction was sent to stock local slope API");
            Equal(0f, body.Character.ground.LastSlopeInput.z, "rotated local slope axis");
            Equal(3f, body.Networking.NetworkcontrolsVelocity.z, "network movement stopped being world-space");
        }

        private static void UpperPlaneDoesNotHideExpectedSupportLayer()
        {
            CompanionNavigationGeometry geometry;
            Create(out geometry);
            Physics.Ray = (origin, direction, distance) =>
            {
                var supportHeight = origin.y > 0.6f ? 0.6f : 0f;
                return new RaycastHit
                {
                    collider = new Collider(), normal = Vector3.up,
                    point = new Vector3(origin.x, supportHeight, origin.z),
                    distance = origin.y - supportHeight
                };
            };
            Vector3 grounded;
            Expect(geometry.TryGroundPoint(Vector3.zero, out grounded), "missing layered support");
            Equal(0f, grounded.y, "upper slab hid the expected lower floor");
        }

        private static void ReleaseClearsNativeReferences()
        {
            CompanionNavigationGeometry geometry;
            Create(out geometry);
            geometry.Release();
            string description;
            Equal(2f, geometry.MeasureClearance(Vector3.zero, Forward, 2f, out description),
                "released geometry did not degrade to uncertainty");
            Expect(PlayerGround.CastCalls == 0, "released geometry accessed the old native collider");
        }

        private static void VerticalTraversalClearsPreviousWalkingIntent()
        {
            CompanionNavigationGeometry ignoredGeometry;
            var body = Create(out ignoredGeometry);
            var locomotion = new CompanionLocomotion();
            locomotion.Bind(body, 0f);
            locomotion.CommitTraversalDirection(Forward, 8f);
            Expect(body.Networking.NetworkcontrolsVelocity.z > 0f, "test did not start walking");
            var status = locomotion.CommitTraversalDirection(Vector3.up, 8f);
            Equal(0f, body.Networking.NetworkcontrolsVelocity.magnitude, "vertical jump retained stale walking input");
            Equal(0f, locomotion.LastCommandedSpeed, "vertical jump retained stale speed");
            Expect(!status.Moving && locomotion.Gait == MovementGait.Stopped,
                "vertical jump retained running gait");
        }

        private static void TraversalSpeedUsesRecordedPaceWithinNativeBounds()
        {
            CompanionNavigationGeometry ignoredGeometry;
            var body = Create(out ignoredGeometry);
            var locomotion = new CompanionLocomotion();
            locomotion.Bind(body, 0f);
            locomotion.CommitTraversalDirection(Forward, 10f, 0.75f);
            Equal(0.75f, body.Networking.NetworkcontrolsVelocity.z, "recorded slow pace became a sprint");
            Expect(locomotion.Gait == MovementGait.Walk, "slow traversal selected sprint gait");
            locomotion.CommitTraversalDirection(Forward, 1f, 4.5f);
            Equal(4.5f, body.Networking.NetworkcontrolsVelocity.z, "recorded sprint pace became a walk");
            Expect(locomotion.Gait == MovementGait.Run, "fast traversal did not enable sprint gait");
            locomotion.CommitTraversalDirection(Forward, 1f, 100f);
            Equal(locomotion.RunSpeed, body.Networking.NetworkcontrolsVelocity.z,
                "recorded pace exceeded native run tuning");
        }

        private static void PhysicsQueryCountTracksActualCalls()
        {
            CompanionNavigationGeometry geometry;
            Create(out geometry);
            geometry.IsSegmentClear(Vector3.zero, Forward);
            Expect(geometry.NativeQueryCount == 3, "segment should count one capsule query and two ray queries");
            Physics.Ray = (origin, direction, distance) => new RaycastHit
            {
                collider = new Collider(), normal = Vector3.up,
                point = new Vector3(origin.x, 0f, origin.z), distance = origin.y
            };
            Vector3 grounded;
            Expect(geometry.TryGroundPoint(Vector3.zero, out grounded), "test flat support missing");
            Expect(geometry.NativeQueryCount == 4, "matching center support should require only one query");
        }

        private static void SampleFloor(Func<Vector3, float?> height, Vector3? normal = null)
        {
            Physics.Ray = (origin, direction, distance) =>
            {
                if (direction.y > -0.9f)
                    return null;
                var floor = height(origin);
                if (!floor.HasValue || floor.Value > origin.y || origin.y - floor.Value > distance)
                    return null;
                return new RaycastHit
                {
                    collider = new Collider(),
                    normal = normal ?? Vector3.up,
                    point = new Vector3(origin.x, floor.Value, origin.z),
                    distance = origin.y - floor.Value
                };
            };
        }

        private static void FlatWalkingDoesNotRequireReplayingAJump()
        {
            CompanionNavigationGeometry geometry;
            Create(out geometry);
            SampleFloor(origin => 0f);
            Expect(geometry.CanWalkSegment(Vector3.zero, new Vector3(3f, 0f, 0f)),
                "flat route between jump takeoff and landing was not walkable");
            Expect(geometry.CanWalkSegment(Vector3.zero, Vector3.zero),
                "in-place jump invented an obstacle");
            Expect(geometry.LastWalkingConnectionReason == "connected", "successful walk reason missing");
        }

        private static void RealGapRemainsUncertainWithoutBlockingNativeMovement()
        {
            CompanionNavigationGeometry geometry;
            Create(out geometry);
            SampleFloor(origin => origin.x > 1f && origin.x < 2f ? (float?)null : 0f);
            var destination = new Vector3(3f, 0f, 0f);
            Expect(geometry.ProbeWalkingConnection(Vector3.zero, destination) == CompanionWalkingConnection.Uncertain,
                "unsupported gap was certified as an ordinary walk");
            Expect(geometry.LastWalkingConnectionReason.StartsWith("sample_support_missing:"),
                "gap uncertainty did not identify the missing support sample");
            Expect(geometry.IsSegmentClear(Vector3.zero, destination),
                "uncertain walking support blocked an otherwise clear native motion segment");
        }

        private static void TinyFloorSeamKeepsWalkingConnection()
        {
            CompanionNavigationGeometry geometry;
            Create(out geometry);
            SampleFloor(origin => origin.x > 1.01f && origin.x < 1.09f ? (float?)null : 0f);
            Expect(geometry.CanWalkSegment(Vector3.zero, new Vector3(2.1f, 0f, 0f)),
                "tiny mesh seam erased support under the character footprint");
        }

        private static void ContinuousRampKeepsWalkingConnection()
        {
            CompanionNavigationGeometry geometry;
            Create(out geometry);
            SampleFloor(origin => origin.x * 0.4f, new Vector3(-0.3713907f, 0.9284767f, 0f));
            Expect(geometry.CanWalkSegment(Vector3.zero, new Vector3(3f, 1.2f, 0f)),
                "supported ordinary ramp was treated as a gap or floor switch");
        }

        private static void AbruptFloorLayerSwitchIsNotAConfirmedWalk()
        {
            CompanionNavigationGeometry geometry;
            Create(out geometry);
            SampleFloor(origin => origin.x < 1.5f ? 0f : 1f);
            Expect(geometry.ProbeWalkingConnection(Vector3.zero, new Vector3(3f, 1f, 0f)) ==
                CompanionWalkingConnection.Uncertain,
                "diagonal air clearance was mistaken for a connection between flat floor layers");
        }

        private static void WalkingConnectionReportsMeasuredWall()
        {
            CompanionNavigationGeometry geometry;
            Create(out geometry);
            SampleFloor(origin => 0f);
            PlayerGround.CastHits = new[] { Hit(0.1f, new Vector3(-1f, 0f, 0f)) };
            Expect(geometry.ProbeWalkingConnection(Vector3.zero, new Vector3(2f, 0f, 0f)) ==
                CompanionWalkingConnection.Obstructed, "measured wall contact was not reported");
            Expect(geometry.LastWalkingConnectionReason.StartsWith("collision:"), "wall reason missing");
        }

        private static void WalkingProbeBudgetIsBoundedAndScoped()
        {
            CompanionNavigationGeometry geometry;
            Create(out geometry);
            Expect(geometry.ProbeWalkingConnection(Vector3.zero, new Vector3(20f, 0f, 0f)) ==
                CompanionWalkingConnection.Uncertain, "long walk bypassed the sensing horizon");
            Expect(geometry.NativeQueryCount == 0, "out-of-horizon probe spent physics work");
            SampleFloor(origin => origin.y > 0.6f ? 0.6f : origin.y > 0.4f ? 0.4f :
                origin.y > 0.2f ? 0.2f : 0f);
            Expect(geometry.ProbeWalkingConnection(Vector3.zero, new Vector3(16f, 0f, 0f)) ==
                CompanionWalkingConnection.Uncertain, "expensive layered walk ignored the query budget");
            Expect(geometry.NativeQueryCount <= 256, "walking query budget exceeded 256 native calls");
            Expect(geometry.LastWalkingConnectionReason.StartsWith("query_budget:"), "query budget reason missing");
            var queries = geometry.NativeQueryCount;
            geometry.IsSegmentClear(Vector3.zero, Forward);
            Expect(geometry.NativeQueryCount == queries + 3, "walking budget leaked into ordinary motion sensing");
        }

        private static void WalkingConnectionPreservesBodyOriginHeight()
        {
            CompanionNavigationGeometry geometry;
            var body = Create(out geometry);
            body.Position = new Vector3(0f, 0.4f, 0f);
            SampleFloor(origin => 0f);
            Expect(geometry.CanWalkSegment(body.Position, new Vector3(3f, 0.4f, 0f)),
                "body origin height was confused with the contact surface height");
        }

        private static void WalkingConnectionFollowsHillContour()
        {
            CompanionNavigationGeometry geometry;
            Create(out geometry);
            Physics.Ray = (origin, direction, distance) =>
            {
                if (direction.y > -0.9f)
                    return null;
                var height = (float)(1.2 * Math.Sin(origin.x * Math.PI / 4.0));
                if (origin.y < height || origin.y - height > distance)
                    return null;
                var gradient = (float)(1.2 * Math.PI / 4.0 * Math.Cos(origin.x * Math.PI / 4.0));
                var normal = new Vector3(-gradient, 1f, 0f);
                normal.Normalize();
                return new RaycastHit
                {
                    collider = new Collider(), normal = normal,
                    point = new Vector3(origin.x, height, origin.z), distance = origin.y - height
                };
            };
            Expect(geometry.CanWalkSegment(Vector3.zero, new Vector3(4f, 0f, 0f)),
                "equal-height endpoints hid a continuously supported hill");
        }

        private static void GroundContactHeightDifferencesStayWithinTolerance()
        {
            CompanionNavigationGeometry geometry;
            var body = Create(out geometry);
            body.Position = new Vector3(0f, 32.54f, 0f);
            body.Character.collision.bodyCollider.bounds = new Bounds
            {
                center = new Vector3(0f, 33.29f, 0f), size = new Vector3(0.5f, 1.5f, 0.5f)
            };
            SampleFloor(origin => 32.54f);
            Expect(geometry.CanWalkSegment(body.Position, new Vector3(3f, 32.68f, 0f)),
                "observed 0.14m human/bot contact difference rejected an ordinary walk");
            Expect(geometry.CanWalkSegment(new Vector3(0f, 32.64f, 0f), new Vector3(3f, 32.74f, 0f)),
                "0.1m start/0.2m end contact differences rejected by float rounding");
        }

        private static void FootprintHitIsProjectedBackToTheCandidateCenter()
        {
            CompanionNavigationGeometry geometry;
            Create(out geometry);
            SampleFloor(origin => Math.Abs(origin.x) < 0.01f && Math.Abs(origin.z) < 0.01f
                ? (float?)null : origin.x * 0.75f, new Vector3(-0.6f, 0.8f, 0f));
            Vector3 supported;
            Expect(geometry.TryGroundPoint(new Vector3(0f, 0.15f, 0f), out supported),
                "footprint slope support missing");
            Equal(0f, supported.y, "side-ray height was copied to a different x/z position");
        }

        private static void OrdinaryRouteProjectionKeepsTheUpperPlatform()
        {
            CompanionNavigationGeometry geometry;
            Create(out geometry);
            SampleFloor(origin => origin.y > 0.95f ? 0.95f : 0f);
            Vector3 supported;
            Expect(geometry.TryGroundRoutePoint(new Vector3(0f, 1f, 0f), out supported),
                "existing upper-platform support missing");
            Equal(0.95f, supported.y, "ordinary route projection jumped down to a different floor");
        }

        private static void OrdinaryRouteProjectionDropsRawAirborneHeight()
        {
            CompanionNavigationGeometry geometry;
            Create(out geometry);
            SampleFloor(origin => origin.y > 34.3f ? 34.3f : 32.73f);
            Vector3 supported;
            Expect(geometry.TryGroundRoutePoint(new Vector3(-216.3f, 33.93f, -509.11f), out supported),
                "lower support under raw breadcrumb81 height was not found");
            Equal(32.73f, supported.y, "raw airborne point snapped upward onto another platform");
        }

        private static void OrdinaryRouteProjectionPreservesUnknownCandidate()
        {
            CompanionNavigationGeometry geometry;
            Create(out geometry);
            SampleFloor(origin => 0.8f);
            var candidate = new Vector3(1f, 0.5f, 2f);
            Vector3 supported;
            Expect(!geometry.TryGroundRoutePoint(candidate, out supported),
                "higher-only surface was accepted as downward route support");
            Equal(0f, Vector3.Distance(candidate, supported), "missing route support changed the candidate");
        }

        private static void ReproQueryBudgetIsScoped()
        {
            CompanionNavigationGeometry geometry;
            Create(out geometry);
            geometry.RunWithQueryBudget(3, () =>
            {
                geometry.IsSegmentClear(Vector3.zero, Forward);
                Expect(geometry.QueryBudgetExhausted, "repro query budget did not exhaust");
                Vector3 supported;
                geometry.TryGroundPoint(Vector3.zero, out supported);
                Expect(geometry.NativeQueryCount == 3, "exhausted repro kept making native queries");
            });
            Expect(!geometry.QueryBudgetExhausted, "repro budget remained active after return");
            geometry.IsSegmentClear(Vector3.zero, Forward);
            Expect(geometry.NativeQueryCount == 6, "repro budget leaked into gameplay sensing");
        }

        private static void NativeReproLeavesBodiesAndMovementUnchanged()
        {
            CompanionNavigationGeometry geometry;
            var body = Create(out geometry);
            body.Position = new Vector3(4f, 33f, 8f);
            body.Character.collision.bodyCollider.bounds = new Bounds
            {
                center = new Vector3(4f, 33.75f, 8f), size = new Vector3(0.5f, 1.5f, 0.5f)
            };
            var priorPosition = body.Position;
            var priorIntent = new Vector3(0.5f, 0f, 1f);
            body.Networking.NetworkcontrolsVelocity = priorIntent;
            SampleFloor(origin => 33f);
            Plugin.Logger.Entries.Clear();
            CompanionNavigationReproProbe.Run(geometry, body.Position);
            Equal(0f, Vector3.Distance(priorPosition, body.Position), "repro repositioned a body");
            Equal(0f, Vector3.Distance(priorIntent, body.Networking.NetworkcontrolsVelocity),
                "repro changed native movement intent");
            Expect(geometry.NativeQueryCount <= 1500, "repro exceeded its overall native query budget");
            Expect(!geometry.QueryBudgetExhausted, "repro retained a query limit after returning");
            Expect(Plugin.Logger.Entries.Exists(line => line.StartsWith("[FOLLOW_REPRO] SUPPORT id=goal_ordinary")),
                "repro omitted projected breadcrumb evidence");
            Expect(Plugin.Logger.Entries.Exists(line => line.StartsWith("[FOLLOW_REPRO] PLAN id=toward_later_human")),
                "repro omitted current-human route evidence");
            Expect(!Plugin.Logger.Entries.Exists(line => line.StartsWith("[FOLLOW_REPRO] FAILED")),
                "repro threw under controlled geometry");
            Expect(Plugin.Logger.Entries.Exists(line => line.StartsWith("[FOLLOW_REPRO] COMPLETE")),
                "repro omitted completion/query summary");
        }

        private static void Expect(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void Equal(float expected, float actual, string message)
        {
            Expect(Math.Abs(expected - actual) < 0.001f, message + ": expected=" + expected + ", actual=" + actual);
        }
    }
}
