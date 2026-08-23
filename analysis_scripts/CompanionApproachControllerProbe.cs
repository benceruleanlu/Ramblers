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
    }
}

namespace Ramblers
{
    internal enum CompanionPosture
    {
        Standing
    }

    internal struct SteeringStatus
    {
    }

    internal sealed class CompanionLocomotion
    {
        internal bool SteerResult = true;
        internal bool ObserveResult;
        internal int SteerCalls;
        internal int CommitCalls;
        internal int ObserveCalls;
        internal int StopCalls;
        internal int ResetProgressCalls;
        internal UnityEngine.Vector3 LastSteerDirection;
        internal UnityEngine.Vector3 LastCommitDirection;
        internal float LastDistance;
        internal float LastResetAt;

        internal CompanionPosture Posture => CompanionPosture.Standing;

        internal bool TrySteerToward(
            UnityEngine.Vector3 direction,
            float distance,
            float now,
            out SteeringStatus status)
        {
            SteerCalls++;
            LastSteerDirection = direction;
            LastDistance = distance;
            status = default(SteeringStatus);
            return SteerResult;
        }

        internal void CommitTraversalDirection(
            UnityEngine.Vector3 direction,
            float distance)
        {
            CommitCalls++;
            LastCommitDirection = direction;
            LastDistance = distance;
        }

        internal bool ObserveProgress(float now)
        {
            ObserveCalls++;
            return ObserveResult;
        }

        internal void Stop(float now)
        {
            StopCalls++;
        }

        internal void ResetProgressObservation(float now)
        {
            ResetProgressCalls++;
            LastResetAt = now;
        }
    }

    internal sealed class CompanionJumpActuator
    {
        internal bool RecoveryResult = true;
        internal string RecoveryError;
        internal int RecoveryCalls;
        internal int CancelCalls;
        internal string LastActionName;
        internal string LastReason;

        internal bool TryRequestActionRecovery(
            float now,
            CompanionPosture posture,
            string actionName,
            string reason,
            out string error)
        {
            RecoveryCalls++;
            LastActionName = actionName;
            LastReason = reason;
            error = RecoveryError;
            return RecoveryResult;
        }

        internal static bool IsDeferredRecoveryError(string error)
        {
            return string.Equals(error, "jump_in_progress", StringComparison.Ordinal) ||
                   string.Equals(error, "jump_cooldown", StringComparison.Ordinal) ||
                   string.Equals(error, "not_on_jumpable_ground", StringComparison.Ordinal);
        }

        internal void CancelActionRecovery(string actionName)
        {
            CancelCalls++;
            LastActionName = actionName;
        }
    }

    internal static class CompanionApproachControllerProbe
    {
        private static int Main()
        {
            NavigationCadenceAndResumeAreDeterministic();
            ClearRouteAdvancesWithoutRecovery();
            BlockedRouteCommitsRecoveryDirection();
            DeferredRecoveryRemainsRetryable();
            UnavailableRecoveryReportsBlocked();
            StuckObservationUsesTheSameRecoveryPath();
            CancellationClearsOnlyTheActiveCommit();
            Console.WriteLine("Companion approach controller probe passed.");
            return 0;
        }

        private static void NavigationCadenceAndResumeAreDeterministic()
        {
            CompanionLocomotion locomotion;
            CompanionJumpActuator jump;
            var controller = Create(out locomotion, out jump);

            controller.Begin(10f);
            Expect(locomotion.ResetProgressCalls == 1, "begin did not reset progress");
            Expect(!controller.TryBeginTick(9.99f), "tick ran before begin time");
            Expect(controller.TryBeginTick(10f), "begin-time tick was suppressed");
            Expect(!controller.TryBeginTick(10.05f), "cadence admitted an early tick");
            Expect(controller.TryBeginTick(10.11f), "cadence suppressed a due tick");

            controller.Resume(10.12f);
            Expect(controller.TryBeginTick(10.12f), "resume did not admit an immediate tick");
            Expect(locomotion.ResetProgressCalls == 2, "resume did not reset progress");
            Expect(jump.CancelCalls == 1, "resume did not cancel stale recovery ownership");
        }

        private static void ClearRouteAdvancesWithoutRecovery()
        {
            CompanionLocomotion locomotion;
            CompanionJumpActuator jump;
            var controller = Create(out locomotion, out jump);
            controller.Begin(0f);

            var direction = new UnityEngine.Vector3(1f, 0f, 0f);
            var step = controller.Advance(0f, direction, 5f);

            Expect(step.Kind == CompanionApproachStepKind.Advanced, "clear route did not advance");
            Expect(locomotion.SteerCalls == 1, "clear route did not steer exactly once");
            Expect(locomotion.ObserveCalls == 1, "clear route did not observe progress");
            Expect(locomotion.StopCalls == 0, "clear route stopped locomotion");
            Expect(jump.RecoveryCalls == 0, "clear route requested recovery");
        }

        private static void BlockedRouteCommitsRecoveryDirection()
        {
            CompanionLocomotion locomotion;
            CompanionJumpActuator jump;
            var controller = Create(out locomotion, out jump);
            controller.Begin(1f);
            locomotion.SteerResult = false;
            var recoveryDirection = new UnityEngine.Vector3(1f, 0f, 0f);

            var recovery = controller.Advance(1f, recoveryDirection, 7f);

            Expect(recovery.Kind == CompanionApproachStepKind.RecoveryCommitted, "blocked route did not recover");
            Expect(recovery.Reason == "blocked_path", "blocked recovery lost its reason");
            Expect(recovery.RecoveryAttempt == 1, "first recovery attempt was not one");
            Expect(locomotion.StopCalls == 1, "blocked route was not stopped before recovery");
            Expect(locomotion.CommitCalls == 1, "recovery direction was not committed");
            Expect(jump.LastActionName == "probe_action", "recovery lost action ownership");

            var changedDirection = new UnityEngine.Vector3(0f, 0f, 1f);
            var committed = controller.Advance(1.2f, changedDirection, 6f);
            Expect(committed.Kind == CompanionApproachStepKind.Advanced, "commit window did not advance");
            Expect(locomotion.SteerCalls == 1, "commit window incorrectly re-steered");
            Expect(locomotion.CommitCalls == 2, "commit window did not replay traversal");
            ExpectVector(locomotion.LastCommitDirection, recoveryDirection, "commit direction drifted");

            locomotion.SteerResult = true;
            controller.Advance(1.46f, changedDirection, 5f);
            Expect(locomotion.SteerCalls == 2, "expired commit did not return to steering");
            ExpectVector(locomotion.LastSteerDirection, changedDirection, "steering used the stale commit direction");
        }

        private static void DeferredRecoveryRemainsRetryable()
        {
            CompanionLocomotion locomotion;
            CompanionJumpActuator jump;
            var controller = Create(out locomotion, out jump);
            controller.Begin(2f);
            locomotion.SteerResult = false;
            jump.RecoveryResult = false;
            jump.RecoveryError = "jump_cooldown";

            var step = controller.Advance(
                2f,
                new UnityEngine.Vector3(1f, 0f, 0f),
                4f);

            Expect(step.Kind == CompanionApproachStepKind.RecoveryDeferred, "cooldown was not deferred");
            Expect(step.RecoveryError == "jump_cooldown", "deferred error was not returned");
            Expect(locomotion.ResetProgressCalls == 2, "deferred recovery did not reset observation");
            Expect(locomotion.CommitCalls == 0, "deferred recovery committed movement");
        }

        private static void UnavailableRecoveryReportsBlocked()
        {
            CompanionLocomotion locomotion;
            CompanionJumpActuator jump;
            var controller = Create(out locomotion, out jump);
            controller.Begin(3f);
            locomotion.SteerResult = false;
            jump.RecoveryResult = false;
            jump.RecoveryError = "jump_unavailable";

            var step = controller.Advance(
                3f,
                new UnityEngine.Vector3(1f, 0f, 0f),
                3f);

            Expect(step.Kind == CompanionApproachStepKind.Blocked, "unavailable recovery did not block");
            Expect(step.Reason == "blocked_path", "blocked step lost its route reason");
            Expect(step.RecoveryError == "jump_unavailable", "blocked step lost its recovery error");
            Expect(locomotion.ResetProgressCalls == 1, "terminal block reset progress unexpectedly");
        }

        private static void StuckObservationUsesTheSameRecoveryPath()
        {
            CompanionLocomotion locomotion;
            CompanionJumpActuator jump;
            var controller = Create(out locomotion, out jump);
            controller.Begin(4f);
            locomotion.ObserveResult = true;

            var step = controller.Advance(
                4f,
                new UnityEngine.Vector3(0f, 0f, 1f),
                8f);

            Expect(step.Kind == CompanionApproachStepKind.RecoveryCommitted, "stuck route did not recover");
            Expect(step.Reason == "stuck", "stuck recovery lost its reason");
            Expect(jump.LastReason == "stuck", "jump request lost the stuck reason");
            Expect(locomotion.StopCalls == 0, "stuck recovery added a blocked-path stop");
        }

        private static void CancellationClearsOnlyTheActiveCommit()
        {
            CompanionLocomotion locomotion;
            CompanionJumpActuator jump;
            var controller = Create(out locomotion, out jump);
            controller.Begin(5f);
            locomotion.SteerResult = false;
            controller.Advance(
                5f,
                new UnityEngine.Vector3(1f, 0f, 0f),
                5f);

            controller.CancelRecovery();
            locomotion.SteerResult = true;
            var step = controller.Advance(
                5.1f,
                new UnityEngine.Vector3(0f, 0f, 1f),
                4f);

            Expect(jump.CancelCalls == 1, "cancel did not release action recovery");
            Expect(jump.LastActionName == "probe_action", "cancel released the wrong action");
            Expect(step.Kind == CompanionApproachStepKind.Advanced, "cancelled controller did not resume steering");
            Expect(locomotion.SteerCalls == 2, "cancel retained the recovery commit window");

            locomotion.ObserveResult = true;
            var laterRecovery = controller.Advance(
                5.2f,
                new UnityEngine.Vector3(0f, 0f, 1f),
                3f);
            Expect(laterRecovery.RecoveryAttempt == 2, "cancel reset recovery telemetry");

            controller.Begin(6f);
            locomotion.ObserveResult = true;
            var newLifecycleRecovery = controller.Advance(
                6f,
                new UnityEngine.Vector3(0f, 0f, 1f),
                2f);
            Expect(newLifecycleRecovery.RecoveryAttempt == 1, "begin retained a prior lifecycle attempt count");
        }

        private static CompanionApproachController Create(
            out CompanionLocomotion locomotion,
            out CompanionJumpActuator jump)
        {
            locomotion = new CompanionLocomotion();
            jump = new CompanionJumpActuator();
            return new CompanionApproachController(
                locomotion,
                jump,
                "probe_action");
        }

        private static void ExpectVector(
            UnityEngine.Vector3 actual,
            UnityEngine.Vector3 expected,
            string message)
        {
            Expect(
                actual.x == expected.x &&
                actual.y == expected.y &&
                actual.z == expected.z,
                message);
        }

        private static void Expect(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
    }
}
