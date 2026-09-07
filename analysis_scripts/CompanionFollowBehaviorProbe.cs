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
        internal Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        internal static Vector3 zero => new Vector3(0, 0, 0);
        internal static Vector3 forward => new Vector3(0, 0, 1);
        internal static Vector3 up => new Vector3(0, 1, 0);
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
        public override string ToString() => "(" + x + "," + y + "," + z + ")";
    }

    internal static class Mathf
    {
        internal static float Abs(float value) => Math.Abs(value);
        internal static float Max(float first, float second) => Math.Max(first, second);
        internal static float Min(float first, float second) => Math.Min(first, second);
        internal static float Clamp(float value, float minimum, float maximum) => Math.Min(maximum, Math.Max(minimum, value));
    }
    internal sealed class Transform { internal Vector3 position; }
    internal sealed class Rigidbody
    {
        internal Vector3 linearVelocity;
        internal bool IsSleeping() => false;
    }
    internal sealed class CapsuleCollider { internal float radius = 0.4f; internal float height = 1.8f; }
}

namespace Mirror { internal static class NetworkServer { internal static bool active = true; } }

internal sealed class PlayerGround
{
    internal bool isGrounded = true;
    internal bool isOnJumpableGround = true;
    internal Vector3 normal = Vector3.up;
    internal float GetSteepness() => 0f;
}
internal sealed class PlayerJumper { internal bool justJumped; }
internal sealed class PlayerCollision { internal CapsuleCollider bodyCollider = new CapsuleCollider(); }
internal sealed class PlayerNetworking
{
    internal bool isServer = true;
    internal bool isLocalPlayer;
    internal Vector3 controlsVelocity = Vector3.zero;
}
internal sealed class PlayerMover { internal Vector3 correctedControlsVelocity = Vector3.zero; }
internal sealed class PlayerSitter { internal bool isSittingCorrected = false; }
internal sealed class PlayerHands { internal PlayerCharacter heldCharacter; }
internal sealed class PlayerPose { internal PlayerCharacter occupant = null; }
internal sealed class PlayerPoser { internal PlayerCharacter playerHoldingMe = null; internal PlayerPose currentPose = null; }
internal sealed class PlayerRegistry { internal PlayerPose grabPose = null; }
internal sealed class PlayerCharacter
{
    internal readonly object gameObject = new object();
    internal readonly Transform transform = new Transform();
    internal readonly PlayerGround ground = new PlayerGround();
    internal readonly PlayerJumper jumper = new PlayerJumper();
    internal readonly Rigidbody rb = new Rigidbody();
    internal readonly PlayerCollision collision = new PlayerCollision();
    internal readonly PlayerNetworking playerNetworking = new PlayerNetworking();
    internal readonly PlayerMover mover = new PlayerMover();
    internal readonly PlayerSitter sitter = new PlayerSitter();
    internal readonly PlayerHands hands = new PlayerHands();
    internal readonly PlayerPoser poser = new PlayerPoser();
    internal readonly PlayerRegistry registry = new PlayerRegistry();
}
internal static class WorldManager { internal static PlayerCharacter localPlayerCharacter; }

namespace Ramblers
{
    internal enum FollowMode { Follow, Stay }
    internal enum CompanionPosture { Standing }
    internal enum GazeChannel { Follow }
    internal sealed class AgentToolResult
    {
        internal static AgentToolResult Failure(string reason) => new AgentToolResult();
        internal static AgentToolResult Success(string tool, string status, string mode) => new AgentToolResult();
    }
    internal static class AgentToolCatalog { internal const string SetFollowMode = "set_follow_mode"; }
    internal static class CompanionFacing { internal const float BodyTurnSpeed = 120f; }
    internal static class CompanionPostureActuator { internal static string Describe(CompanionPosture posture) => "standing"; }
    internal sealed class ProbeLogger
    {
        internal readonly List<string> Lines = new List<string>();
        internal void LogInfo(string value) { Lines.Add(value); }
        internal void LogError(string value) { Lines.Add(value); }
    }
    internal static class Plugin
    {
        internal static readonly ProbeLogger Logger = new ProbeLogger();
        internal static bool FollowDiagnosticsEnabled => true;
    }
    internal sealed class CompanionBody
    {
        internal readonly PlayerCharacter Character = new PlayerCharacter();
        internal bool IsAlive = true;
        internal Vector3 Position => Character.transform.position;
        internal object GameObject => Character.gameObject;
        internal PlayerNetworking Networking => Character.playerNetworking;
        internal static Vector3 HeadPositionOf(PlayerCharacter player) => player.transform.position + Vector3.up;
    }
    internal enum CompanionWalkingConnection { Walkable, Uncertain, Obstructed }
    internal sealed class CompanionNavigationGeometry
    {
        internal string LastWalkingConnectionReason => "probe";
        internal Func<Vector3, Vector3, bool> Clear = (from, to) => true;
        internal Func<Vector3, Vector3, CompanionWalkingConnection> Walking =
            (from, to) => CompanionWalkingConnection.Walkable;
        internal Func<Vector3, Vector3?> Support = candidate => new Vector3(candidate.x, 0, candidate.z);
        internal int NativeQueryCount;
        internal bool TryGroundPoint(Vector3 candidate, out Vector3 grounded)
        {
            NativeQueryCount++;
            var supported = Support(candidate);
            grounded = supported ?? candidate;
            return supported.HasValue;
        }
        internal bool IsSegmentClear(Vector3 from, Vector3 to)
        { NativeQueryCount++; return Clear(from, to); }
        internal CompanionWalkingConnection ProbeWalkingConnection(Vector3 from, Vector3 to)
        { NativeQueryCount++; return Walking(from, to); }
        internal bool CanWalkSegment(Vector3 from, Vector3 to) =>
            ProbeWalkingConnection(from, to) == CompanionWalkingConnection.Walkable;
    }
    internal sealed class CompanionLocomotion
    {
        internal const float RunStartDistance = 6.75f;
        internal readonly CompanionNavigationGeometry Geometry = new CompanionNavigationGeometry();
        internal Vector3 LastMovementIntent;
        internal float LastCommandedSpeed;
        internal float WalkSpeed => 3;
        internal float RunSpeed => 5.5f;
        internal bool GaitSpeedsFromTunings => true;
        internal CompanionPosture Posture => CompanionPosture.Standing;
        internal bool LastDirectPathBlocked => false;
        internal bool LastDirectGroundLimited => false;
        internal float LastGroundResponse => 1;
        internal float LastSlopeResponse => 1;
        internal string LastSteeringAuthority => "probe";
        internal float LastSteepScalar => 1;
        internal float LastSteeringAngle => 0;
        internal float LastClearance => 10;
        internal string LastDirectHit => "clear";
        internal string LastProbeSummary => "probe";
        internal int MoveCalls;
        internal int TraversalCalls;
        internal float LastTraversalSpeed;
        internal int StopCalls;
        internal static int GetObstacleMask(PlayerCharacter player) => -1;
        internal string DescribeGait() => LastCommandedSpeed == 0 ? "stopped" : "walk";
        internal void ResetProgressObservation(float now) { }
        internal void MoveAlongRoute(Vector3 direction, float distance, float now)
        { MoveCalls++; SetIntent(direction); }
        internal void CommitTraversalDirection(Vector3 direction, float distance, float targetSpeed = 0f)
        { TraversalCalls++; LastTraversalSpeed = targetSpeed; SetIntent(direction); }
        internal void Stop(float now) { StopCalls++; StopQuietly(); }
        internal void StopQuietly() { LastMovementIntent = Vector3.zero; LastCommandedSpeed = 0; }
        private void SetIntent(Vector3 direction)
        { LastMovementIntent = direction; LastCommandedSpeed = direction.sqrMagnitude > 0.0001f ? 3 : 0; }
    }
    internal sealed class CompanionAttention
    {
        internal float LastBodyYaw => 0;
        internal float LastTargetYaw => 0;
        internal string HeadState => "following";
        internal void SetTarget(GazeChannel channel, Vector3 target) { }
        internal void ClearTarget(GazeChannel channel) { }
        internal void ResumeAt(float now) { }
    }
    internal sealed class CompanionJumpActuator
    {
        internal bool Accept = true;
        internal readonly List<float> RequestTimes = new List<float>();
        internal readonly List<string> Reasons = new List<string>();
        internal int Cancellations;
        internal bool TryRequestTraversal(float now, CompanionPosture posture, string reason, out string error)
        { RequestTimes.Add(now); Reasons.Add(reason); error = Accept ? null : "not_ready"; return Accept; }
        internal void CancelFollow(string reason) { Cancellations++; }
    }

    internal static class CompanionFollowBehaviorProbe
    {
        private static int Main()
        {
            var failures = 0;
            Run("default following and explicit stay", DefaultFollowAndStay, ref failures);
            Run("walkable level jump is not mirrored", LevelJumpPipeline, ref failures);
            Run("real gap retains useful jump hint", RealGapJumpPipeline, ref failures);
            Run("short wall uses walking detour", ShortWallUsesWalkingDetour, ref failures);
            Run("fun jumps and zigzag do not drag follower backward", FunJumpsAndZigzagFollowCurrentHuman, ref failures);
            Run("airborne human projects to walking ground", AirborneHumanKeepsHorizontalChase, ref failures);
            Run("grounded drop eighty does not replay", GroundedDropEightyContinuesForward, ref failures);
            Run("vertical jump does not vanish at landing", VerticalJumpPipeline, ref failures);
            Run("grounded launch retries remain paced", FailedLaunchPacing, ref failures);
            Run("carry and action ownership", CarryAndSuspensionStopMovement, ref failures);
            Run("reposition clears old traversal", RepositionRebasesTraversal, ref failures);
            Run("native authority loss stops movement", AuthorityLossStopsMovement, ref failures);
            Run("thin wall prevents trail pruning", ThinWallPreventsPassedBreadcrumbPruning, ref failures);
            if (failures != 0)
            {
                Console.Error.WriteLine("Companion follow integration: " + failures + " checks failed.");
                return 1;
            }
            Console.WriteLine("Companion follow integration: 13 behavioral checks passed.");
            return 0;
        }

        private static void DefaultFollowAndStay()
        {
            var world = new World(Point(5), Point(0));
            world.Tick(0);
            Expect(world.Follow.IsRequested && world.Motion.LastMovementIntent.x > 0.9f,
                "spawn did not default to following");
            world.Follow.SetMode(FollowMode.Stay, 0.1f, true, null);
            world.Human.transform.position = Point(10);
            for (var i = 2; i <= 20; i++) world.Tick(i * 0.1f);
            Expect(!world.Follow.IsRequested && world.Follow.StateLabel == "idle" &&
                world.Motion.LastMovementIntent.sqrMagnitude == 0 && world.Jump.RequestTimes.Count == 0,
                "explicit stay was undone by trail updates");
            world.Follow.SetMode(FollowMode.Follow, 2.1f, true, null);
            world.Tick(2.1f);
            Expect(world.Follow.IsRequested && world.Motion.LastMovementIntent.x > 0.9f,
                "explicit resume did not restart following");
        }

        private static void LevelJumpPipeline()
        {
            var world = RecordedJump(Point(3), walkingPossible: true);
            world.Body.Character.transform.position = Point(0);
            world.Tick(0.7f);
            Expect(world.Jump.RequestTimes.Count == 0 && world.Motion.TraversalCalls == 0,
                "follower copied a human jump despite confirmed walking ground");
            Expect(world.Motion.LastMovementIntent.x > 0.9f,
                "follower did not walk toward the human's current position");
        }

        private static void RealGapJumpPipeline()
        {
            var world = RecordedJump(new Vector3(3, 0, 0));
            var landing = FindTraversal(world);
            Expect(landing.RequiresJump && landing.HasTakeoff && landing.TakeoffPosition.x == 0,
                "recorded level jump lost its native takeoff");
            world.Body.Character.transform.position = Point(0);
            world.Tick(0.7f);
            Expect(world.Jump.RequestTimes.Count == 1,
                "follower did not request the recorded jump at takeoff");
            Expect(world.Motion.TraversalCalls > 0 && world.Motion.LastMovementIntent.x > 0.9f,
                "follower did not commit motion toward the recorded landing");
            Expect(Math.Abs(world.Motion.LastTraversalSpeed - 5f) < 0.001f,
                "recorded 3 meter / 0.6 second traversal did not request 5 meters per second");
            Expect(ContainsTraversal(world, landing.Sequence), "jump was pruned before native airborne confirmation");
            world.Body.Character.transform.position = new Vector3(0.75f, 0.8f, 0);
            world.Body.Character.ground.isGrounded = false;
            world.Tick(0.85f);
            world.Body.Character.transform.position = new Vector3(2.25f, 0.8f, 0);
            world.Tick(1.15f);
            Expect(ContainsTraversal(world, landing.Sequence) && world.Jump.RequestTimes.Count == 1,
                "airborne proximity discarded the landing or retriggered the jump");
            world.Body.Character.transform.position = Point(3);
            world.Body.Character.ground.isGrounded = true;
            world.Tick(1.3f);
            world.Tick(1.45f);
            Expect(world.Follow.StateLabel == "holding" && world.Jump.RequestTimes.Count == 1,
                "settled landing did not hold beside the human without replaying the jump");
            world.Human.transform.position = Point(6);
            world.Tick(1.6f);
            Expect(!ContainsTraversal(world, landing.Sequence),
                "confirmed grounded landing never released the traversal breadcrumb");
            Expect(world.Jump.RequestTimes.Count == 1 && world.Motion.LastMovementIntent.x > 0.9f,
                "landing replayed a completed jump or stopped following the next route");
        }

        private static void FunJumpsAndZigzagFollowCurrentHuman()
        {
            var world = RecordedJump(new Vector3(3, 0, 3), walkingPossible: true);
            world.Human.transform.position = new Vector3(5, 0, -3);
            world.Follow.TickFrame(0.8f);
            world.Human.jumper.justJumped = true;
            world.Human.ground.isGrounded = false;
            world.Human.rb.linearVelocity = new Vector3(0, 4, 0);
            world.Human.transform.position = new Vector3(5.5f, 0.7f, -2);
            world.Follow.TickFrame(0.9f);
            world.Human.jumper.justJumped = false;
            world.Human.transform.position = new Vector3(6.5f, 1, -1);
            world.Follow.TickFrame(1.1f);
            world.Human.transform.position = Point(8);
            world.Human.ground.isGrounded = true;
            world.Follow.TickFrame(1.4f);
            world.Body.Character.transform.position = Point(0);
            for (var tick = 0; tick < 15; tick++)
            {
                world.Tick(1.5f + tick * 0.1f);
                var direction = world.Motion.LastMovementIntent;
                Expect(direction.x > 0.9f && Math.Abs(direction.z) < 0.01f,
                    "walkable follower chased a past zigzag or jump takeoff instead of the current human");
                world.Body.Character.transform.position += direction * 0.2f;
            }
            Expect(world.Jump.RequestTimes.Count == 0 && world.Motion.TraversalCalls == 0,
                "repeated playful human jumps became compulsory follower actions");
        }

        private static void ShortWallUsesWalkingDetour()
        {
            var world = RecordedJump(Point(3));
            world.Motion.Geometry.Clear = (from, to) =>
            {
                var steps = Math.Max(1, (int)Math.Ceiling(Vector3.Distance(from, to) / 0.025f));
                for (var sample = 0; sample <= steps; sample++)
                {
                    var point = from + (to - from) * ((float)sample / steps);
                    if (point.x >= 1 && point.x <= 2 && point.z >= -0.5f && point.z <= 0.5f)
                        return false;
                }
                return true;
            };
            world.Motion.Geometry.Walking = (from, to) => world.Motion.Geometry.Clear(from, to)
                ? CompanionWalkingConnection.Walkable : CompanionWalkingConnection.Obstructed;
            world.Body.Character.transform.position = Point(0);
            world.Tick(0.7f);
            Expect(world.Jump.RequestTimes.Count == 0 && Math.Abs(world.Motion.LastMovementIntent.z) > 0.1f,
                "follower copied a short-wall jump before considering the available walking detour");
            for (var tick = 1; tick < 70; tick++)
            {
                if (tick == 4) world.Human.transform.position = Point(6);
                world.Tick(0.7f + tick * 0.1f);
                var next = world.Body.Position + world.Motion.LastMovementIntent * 0.2f;
                Expect(world.Motion.Geometry.Clear(world.Body.Position, next),
                    "walking detour commanded movement through the short wall");
                world.Body.Character.transform.position = next;
            }
            Expect(world.Body.Position.x > 2f && world.Follow.StateLabel == "holding",
                "walking detour failed to pass the wall and settle near the human");
            Expect(world.Jump.RequestTimes.Count == 0 && world.Motion.TraversalCalls == 0,
                "following the detour later reinstated the discarded jump");
        }

        private static void AirborneHumanKeepsHorizontalChase()
        {
            var world = new World(Point(0), Point(-1));
            world.Follow.TickFrame(0);
            world.Human.jumper.justJumped = true;
            world.Human.ground.isGrounded = false;
            world.Human.rb.linearVelocity = new Vector3(0, 4, 0);
            world.Human.transform.position = new Vector3(1.4f, 0.7f, 0);
            world.Follow.TickFrame(0.1f);
            world.Human.jumper.justJumped = false;
            world.Human.transform.position = new Vector3(2.4f, 1, 0);
            world.Tick(0.4f);
            Expect(world.Motion.LastMovementIntent.x > 0.9f && world.Follow.StateLabel == "following",
                "human airborne motion froze following at the historical takeoff");
            Expect(world.Jump.RequestTimes.Count == 0, "projected ground chase copied the airborne human");
        }

        private static void GroundedDropEightyContinuesForward()
        {
            var takeoff = new Vector3(-215.21f, 33.35f, -508.62f);
            var landing = new Vector3(-214.83f, 32.68f, -508.93f);
            var world = new World(takeoff, takeoff);
            world.Motion.Geometry.Walking = (from, to) => CompanionWalkingConnection.Uncertain;
            world.Motion.Geometry.Support = candidate => candidate;
            world.Follow.TickFrame(0);
            world.Human.ground.isGrounded = false;
            world.Human.rb.linearVelocity = new Vector3(0, -4, 0);
            world.Human.transform.position = new Vector3(-215.02f, 33.02f, -508.78f);
            world.Follow.TickFrame(0.05f);
            world.Human.transform.position = landing;
            world.Human.ground.isGrounded = true;
            world.Follow.TickFrame(0.14f);
            world.Human.transform.position = new Vector3(-211.5f, 33.01f, -510.5f);
            world.Follow.TickFrame(0.3f);
            world.Tick(0.4f);
            world.Body.Character.transform.position = new Vector3(-214.94f, 32.54f, -508.88f);
            world.Tick(0.6f);
            world.Body.Character.transform.position = new Vector3(-214.44f, 32.84f, -509.25f);
            for (var tick = 0; tick < 30; tick++)
            {
                world.Tick(0.8f + tick * 0.1f);
                var direction = world.Motion.LastMovementIntent;
                if (direction.sqrMagnitude > 0.001f)
                {
                    var toHuman = (world.Human.transform.position - world.Body.Position).normalized;
                    Expect(Vector3.Dot(direction, toHuman) > 0,
                        "grounded descent reversed toward obsolete drop takeoff");
                    world.Body.Character.transform.position += direction * 0.2f;
                }
            }
            Expect(world.Jump.RequestTimes.Count == 0 && world.Motion.TraversalCalls == 0,
                "ordinary grounded descent entered compulsory airborne replay");
            Expect(world.Follow.StateLabel == "holding", "grounded drop failed to settle beside the human");
        }

        private static void VerticalJumpPipeline()
        {
            var world = RecordedJump(new Vector3(0, 1.6f, 0));
            var landing = FindTraversal(world);
            world.Body.Character.transform.position = Point(0);
            world.Tick(0.7f);
            Expect(world.Jump.RequestTimes.Count == 1 && ContainsTraversal(world, landing.Sequence),
                "same-column raised landing was mistaken for an already reached waypoint");
            world.Body.Character.transform.position = new Vector3(0, 0.9f, 0);
            world.Body.Character.ground.isGrounded = false;
            world.Tick(0.85f);
            world.Body.Character.transform.position = new Vector3(0, 1.55f, 0);
            world.Tick(1.0f);
            Expect(ContainsTraversal(world, landing.Sequence), "raised landing was removed before contact");
            world.Body.Character.transform.position = new Vector3(0, 1.6f, 0);
            world.Body.Character.ground.isGrounded = true;
            world.Tick(1.15f);
            world.Human.transform.position = new Vector3(4, 1.6f, 0);
            world.Tick(1.3f);
            Expect(!ContainsTraversal(world, landing.Sequence) && world.Jump.RequestTimes.Count == 1,
                "vertical jump completion failed to advance the trail");
        }

        private static void FailedLaunchPacing()
        {
            var world = RecordedJump(Point(3));
            world.Body.Character.transform.position = Point(0);
            for (var tick = 7; tick <= 67; tick++) world.Tick(tick * 0.1f);
            Expect(world.Jump.RequestTimes.Count >= 3 && world.Jump.RequestTimes.Count <= 7,
                "grounded launch either deadlocked or spammed native jump requests: " + world.Jump.RequestTimes.Count);
            for (var i = 1; i < world.Jump.RequestTimes.Count; i++)
                Expect(world.Jump.RequestTimes[i] - world.Jump.RequestTimes[i - 1] >= 0.8f,
                    "recorded launch retries bypassed airborne timeout and cooldown");
            Expect(FindTraversal(world).RequiresJump, "failed jump lost its traversal before taking off");
        }

        private static void CarryAndSuspensionStopMovement()
        {
            var world = new World(Point(5), Point(0));
            world.Tick(0);
            world.Human.hands.heldCharacter = world.Body.Character;
            world.Tick(0.2f);
            Expect(world.Follow.IsCarried && world.Follow.IsRequested &&
                world.Motion.LastMovementIntent.sqrMagnitude == 0, "carried companion continued moving");
            world.Human.hands.heldCharacter = null;
            world.Tick(0.4f);
            Expect(!world.Follow.IsCarried && world.Motion.LastMovementIntent.x > 0.9f,
                "carry release failed to resume retained follow intent");
            world.Body.Character.hands.heldCharacter = world.Human;
            world.Tick(0.6f, false, "carrying_player");
            Expect(world.Follow.IsCarryingHuman && world.Motion.LastMovementIntent.sqrMagnitude == 0,
                "companion carrying the human resumed movement without carry ownership");
            world.Body.Character.hands.heldCharacter = null;
            world.Tick(0.8f);
            world.Tick(1.0f, false, "interaction");
            Expect(world.Follow.StateLabel == "suspended" && world.Motion.LastMovementIntent.sqrMagnitude == 0,
                "exclusive action suspension left following movement active");
            world.Tick(1.2f, false, "interaction");
            Expect(world.Motion.LastMovementIntent.sqrMagnitude == 0, "suspended follow restarted on the next tick");
        }

        private static void RepositionRebasesTraversal()
        {
            var world = RecordedJump(Point(3));
            world.Body.Character.transform.position = Point(0);
            world.Tick(0.7f);
            var oldJumpCount = world.Jump.RequestTimes.Count;
            world.Body.Character.transform.position = Point(100);
            world.Human.transform.position = Point(105);
            world.Follow.RebaseAfterExternalReposition(world.Human, 0.8f, true, null);
            Expect(world.Motion.LastMovementIntent.sqrMagnitude == 0 && world.Trail.Count == 1,
                "teleport rebase retained active traversal motion or the old route");
            world.Tick(0.9f);
            Expect(world.Motion.LastMovementIntent.x > 0.9f && world.Jump.RequestTimes.Count == oldJumpCount,
                "teleport rebase chased a pre-teleport jump");
            world.Follow.SetMode(FollowMode.Stay, 1.0f, true, null);
            world.Body.Character.transform.position = Point(200);
            world.Human.transform.position = Point(205);
            world.Follow.RebaseAfterExternalReposition(world.Human, 1.1f, true, null);
            world.Tick(1.2f);
            Expect(!world.Follow.IsRequested && world.Motion.LastMovementIntent.sqrMagnitude == 0,
                "teleport rebase undid explicit stay");
        }

        private static void AuthorityLossStopsMovement()
        {
            var world = new World(Point(5), Point(0));
            world.Tick(0);
            world.Body.Networking.isServer = false;
            world.Tick(0.2f);
            Expect(world.Follow.StateLabel == "failed" && world.Motion.LastMovementIntent.sqrMagnitude == 0,
                "lost server authority retained physical following intent");
        }

        private static void ThinWallPreventsPassedBreadcrumbPruning()
        {
            var world = new World(Point(0), Point(0));
            world.Motion.Geometry.Clear = (from, to) =>
                (from.x < 0.25f && to.x < 0.25f) || (from.x > 0.25f && to.x > 0.25f);
            world.Motion.Geometry.Walking = (from, to) => world.Motion.Geometry.Clear(from, to)
                ? CompanionWalkingConnection.Walkable : CompanionWalkingConnection.Obstructed;
            world.Follow.TickFrame(0);
            world.Human.transform.position = new Vector3(0, 0, 1);
            world.Follow.TickFrame(0.1f);
            world.Human.transform.position = new Vector3(0, 0, 2);
            world.Follow.TickFrame(0.25f);
            world.Human.transform.position = new Vector3(0, 0, 4);
            world.Follow.TickFrame(0.4f);
            world.Follow.TickFixed(0.45f, true, null);
            var selected = world.Trail.Peek();
            world.Body.Character.transform.position = new Vector3(0.5f, 0, selected.Position.z + 1);
            world.Follow.TickFixed(0.6f, true, null);
            Expect(world.Trail.Peek().Sequence == selected.Sequence,
                "crossing a breadcrumb's arrival plane through a wall erased the required route");
        }

        private static World RecordedJump(Vector3 landing, bool walkingPossible = false)
        {
            var world = new World(Point(0), Point(-3));
            if (!walkingPossible)
            {
                world.Motion.Geometry.Walking = (from, to) => CompanionWalkingConnection.Uncertain;
                world.Motion.Geometry.Support = candidate => candidate;
            }
            world.Follow.TickFrame(0);
            world.Human.jumper.justJumped = true;
            world.Human.ground.isGrounded = false;
            world.Human.rb.linearVelocity = new Vector3(0, 4, 0);
            world.Human.transform.position = new Vector3(landing.x * 0.2f, 0.7f, landing.z * 0.2f);
            world.Follow.TickFrame(0.1f);
            world.Human.jumper.justJumped = false;
            world.Human.transform.position = new Vector3(landing.x * 0.6f, Math.Max(1.8f, landing.y + 0.2f), landing.z * 0.6f);
            world.Human.rb.linearVelocity = Vector3.zero;
            world.Follow.TickFrame(0.3f);
            world.Human.transform.position = landing;
            world.Human.ground.isGrounded = true;
            world.Follow.TickFrame(0.6f);
            return world;
        }

        private static BreadcrumbPoint FindTraversal(World world)
        {
            for (var i = 0; world.Trail.TryPeek(i, out var point); i++)
                if (point.RequiresJump || point.RequiresDrop) return point;
            throw new InvalidOperationException("recorded traversal was not present in the production breadcrumb trail");
        }

        private static bool ContainsTraversal(World world, int sequence)
        {
            for (var i = 0; world.Trail.TryPeek(i, out var point); i++)
                if (point.Sequence == sequence && (point.RequiresJump || point.RequiresDrop)) return true;
            return false;
        }

        private sealed class World
        {
            internal readonly PlayerCharacter Human = new PlayerCharacter();
            internal readonly CompanionBody Body = new CompanionBody();
            internal readonly CompanionLocomotion Motion = new CompanionLocomotion();
            internal readonly CompanionJumpActuator Jump = new CompanionJumpActuator();
            internal readonly CompanionFollowBehavior Follow;
            internal BreadcrumbTrail Trail => (BreadcrumbTrail)typeof(CompanionFollowBehavior)
                .GetField("_trail", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Follow);
            internal World(Vector3 human, Vector3 body)
            {
                Plugin.Logger.Lines.Clear();
                Mirror.NetworkServer.active = true;
                Human.transform.position = human;
                Human.playerNetworking.isLocalPlayer = true;
                WorldManager.localPlayerCharacter = Human;
                Body.Character.transform.position = body;
                Follow = new CompanionFollowBehavior(Motion, new CompanionAttention(), Jump);
                Follow.Bind(Body, Human, 0, true, null);
            }
            internal void Tick(float now, bool allowed = true, string blocker = null)
            { Follow.TickFrame(now); Follow.TickFixed(now, allowed, blocker); }
        }

        private static Vector3 Point(float x) => new Vector3(x, 0, 0);
        private static void Expect(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
        private static void Run(string name, Action check, ref int failures)
        {
            try { check(); Console.WriteLine("PASS " + name); }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine("FAIL " + name + ": " + exception.Message);
                foreach (var line in Plugin.Logger.Lines) Console.Error.WriteLine(line);
            }
        }
    }
}
