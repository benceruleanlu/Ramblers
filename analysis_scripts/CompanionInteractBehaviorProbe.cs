using System;
using System.Collections.Generic;

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

        internal float magnitude => (float)Math.Sqrt(x * x + y * y + z * z);

        public static Vector3 operator -(Vector3 left, Vector3 right)
        {
            return new Vector3(
                left.x - right.x,
                left.y - right.y,
                left.z - right.z);
        }

        public static Vector3 operator /(Vector3 value, float divisor)
        {
            return new Vector3(
                value.x / divisor,
                value.y / divisor,
                value.z / divisor);
        }

        internal static float Distance(Vector3 left, Vector3 right)
        {
            return (left - right).magnitude;
        }

        public override string ToString()
        {
            return $"({x}, {y}, {z})";
        }
    }

    internal static class Time
    {
        internal static float realtimeSinceStartup;
    }
}

namespace Ramblers
{
    internal interface ICompanionJob
    {
    }

    [Flags]
    internal enum JobResources
    {
        None = 0,
        Locomotion = 1,
        Gaze = 2,
        Hands = 4
    }

    internal enum GazeChannel
    {
        Inspection
    }

    internal enum CompanionPosture
    {
        Standing
    }

    internal enum CompanionInteractionIntent
    {
        Use
    }

    internal enum CompanionAffordanceReadinessState
    {
        Ready,
        NeedsApproach,
        Unavailable
    }

    internal enum CompanionAffordanceKind
    {
        WorldSwitch,
        PropHome
    }

    internal enum CompanionTurnHandsTransition
    {
        None,
        HandsEmpty
    }

    internal struct SteeringStatus
    {
    }

    internal sealed class PlayerCharacter
    {
    }

    internal sealed class CompanionBody
    {
        internal bool IsAlive = true;
        internal UnityEngine.Vector3 Position;
    }

    internal sealed class CompanionAffordanceActivation
    {
        internal readonly string Id;
        internal bool AuthorityCrossed { get; set; }

        internal CompanionAffordanceActivation(string id)
        {
            Id = id;
        }
    }

    internal sealed class CompanionAffordanceReadiness
    {
        internal CompanionAffordanceReadinessState State;
        internal UnityEngine.Vector3 Point;
        internal CompanionAffordanceActivation Activation;
        internal string Error;
    }

    internal sealed class CompanionAffordanceTarget
    {
        internal bool IsWorldTarget = true;
        internal string KindLabel = "world_switch";
        internal string SourceLabel = "human_gaze";
        internal string ReferenceId = "interaction:17";
        internal uint NetworkId = 23;
        internal CompanionAffordanceKind Kind = CompanionAffordanceKind.WorldSwitch;
        internal CompanionAffordanceReadiness Readiness;
        internal bool CurrentPointAvailable = true;
        internal string CurrentPointError = null;
        internal bool ActivationAvailable = true;
        internal bool ActivationCrossesAuthority = true;
        internal string ActivationError = null;
        internal bool ProgressAvailable = true;
        internal bool Observed;
        internal string Observation = "changed";
        internal string ProgressError = null;
        internal int ReadinessCalls;
        internal int ActivateCalls;
        internal int ProgressCalls;

        internal CompanionAffordanceReadiness GetReadiness(
            CompanionBody body,
            CompanionInteractionIntent intent)
        {
            ReadinessCalls++;
            return Readiness;
        }

        internal bool TryGetCurrentPoint(
            out UnityEngine.Vector3 point,
            out string error)
        {
            point = Readiness == null
                ? UnityEngine.Vector3.zero
                : Readiness.Point;
            error = CurrentPointError;
            return CurrentPointAvailable;
        }

        internal string DescribeActivation(CompanionAffordanceActivation activation)
        {
            return "activation=" + (activation == null ? "none" : activation.Id);
        }

        internal bool TryActivate(
            CompanionBody body,
            CompanionAffordanceActivation activation,
            float now,
            out string error)
        {
            ActivateCalls++;
            if (ActivationCrossesAuthority)
                activation.AuthorityCrossed = true;
            error = ActivationError;
            return ActivationAvailable;
        }

        internal bool TryProgressActivation(
            CompanionBody body,
            CompanionAffordanceActivation activation,
            float now,
            out bool observed,
            out string observation,
            out string error)
        {
            ProgressCalls++;
            observed = Observed;
            observation = Observation;
            error = ProgressError;
            return ProgressAvailable;
        }

        internal string SuccessState(CompanionAffordanceActivation activation)
        {
            return "activated";
        }
    }

    internal sealed class CompanionJobRequest
    {
        internal CompanionAffordanceTarget AffordanceTarget;
        internal CompanionInteractionIntent InteractionIntent;
        internal string CallId;
        internal long TurnId;
    }

    internal sealed class AgentToolResult
    {
        internal bool Succeeded;
        internal string Action;
        internal string Status;
        internal string State;
        internal string Error;

        internal static AgentToolResult Success(
            string action,
            string status,
            string state)
        {
            return new AgentToolResult
            {
                Succeeded = true,
                Action = action,
                Status = status,
                State = state
            };
        }

        internal static AgentToolResult Failure(string error)
        {
            return new AgentToolResult
            {
                Error = error
            };
        }
    }

    internal sealed class CompanionJobCompletion
    {
        internal AgentToolResult Result;
        internal CompanionTurnHandsTransition HandsTransition;

        internal static CompanionJobCompletion Failed(string error)
        {
            return new CompanionJobCompletion
            {
                Result = AgentToolResult.Failure(error)
            };
        }
    }

    internal static class AgentToolCatalog
    {
        internal const string InteractWithObject = "interact_with_object";
    }

    internal sealed class CompanionAttention
    {
        internal bool AimWithin = true;
        internal int SetCalls;
        internal int ClearCalls;
        internal UnityEngine.Vector3 LastTarget;

        internal void SetTarget(GazeChannel channel, UnityEngine.Vector3 point)
        {
            SetCalls++;
            LastTarget = point;
        }

        internal void ClearTarget(GazeChannel channel)
        {
            ClearCalls++;
        }

        internal bool IsAimWithin(
            GazeChannel channel,
            float yawTolerance,
            float pitchTolerance)
        {
            return AimWithin;
        }
    }

    internal sealed class CompanionLocomotion
    {
        internal bool SteerResult = true;
        internal bool ObserveResult = false;
        internal int SteerCalls;
        internal int CommitCalls;
        internal int ObserveCalls;
        internal int StopCalls;
        internal int ResetProgressCalls;

        internal CompanionPosture Posture => CompanionPosture.Standing;

        internal bool TrySteerToward(
            UnityEngine.Vector3 direction,
            float distance,
            float now,
            out SteeringStatus status)
        {
            SteerCalls++;
            status = default(SteeringStatus);
            return SteerResult;
        }

        internal void CommitTraversalDirection(
            UnityEngine.Vector3 direction,
            float distance)
        {
            CommitCalls++;
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
        }
    }

    internal sealed class CompanionJumpActuator
    {
        internal bool RecoveryResult = true;
        internal string RecoveryError = null;
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

    internal sealed class ProbeLogger
    {
        internal readonly List<string> Entries = new List<string>();

        internal void LogInfo(string message)
        {
            Entries.Add(message);
        }

        internal void LogWarning(string message)
        {
            Entries.Add(message);
        }

        internal void Clear()
        {
            Entries.Clear();
        }

        internal bool Contains(string text)
        {
            foreach (var entry in Entries)
            {
                if (entry.IndexOf(text, StringComparison.Ordinal) >= 0)
                    return true;
            }
            return false;
        }
    }

    internal static class Plugin
    {
        internal static readonly ProbeLogger Logger = new ProbeLogger();
    }

    internal static class CompanionInteractBehaviorProbe
    {
        private static int Main()
        {
            ApproachAlignmentActivationAndConfirmationCompose();
            BlockedApproachUsesSharedRecoveryAndCancelsCleanly();
            UnavailableApproachTerminatesBeforeAuthority();
            FinalReadinessCheckPrecedesAuthority();
            FailedActivationAfterAuthorityReconciles();
            PostAuthorityCancellationReconcilesWithoutCompletion();
            Console.WriteLine("Companion interaction lifecycle probe passed.");
            return 0;
        }

        private static void ApproachAlignmentActivationAndConfirmationCompose()
        {
            ProbeLoggerReset();
            CompanionLocomotion locomotion;
            CompanionAttention attention;
            CompanionJumpActuator jump;
            var behavior = CreateBehavior(out locomotion, out attention, out jump);
            var target = CreateTarget(
                CompanionAffordanceReadinessState.NeedsApproach,
                new UnityEngine.Vector3(8f, 0f, 0f));
            var request = CreateRequest(target, "call-approach", 41L);

            var required = behavior.RequiredFor(request);
            Expect(
                (required & JobResources.Locomotion) != 0,
                "needs-approach admission omitted locomotion");

            AgentToolResult failure;
            Expect(behavior.TryBegin(0f, request, out failure), "approach admission failed");
            Expect(failure == null, "successful admission returned a failure");
            Expect(
                (behavior.Held & JobResources.Locomotion) != 0,
                "active approach did not retain locomotion");
            Expect(locomotion.ResetProgressCalls == 1, "world admission did not reset progress");

            behavior.Tick(0f);
            Expect(locomotion.SteerCalls == 1, "approach did not steer");
            Expect(target.ActivateCalls == 0, "approach crossed authority early");

            SetReadiness(
                target,
                CompanionAffordanceReadinessState.Ready,
                new UnityEngine.Vector3(2f, 0f, 0f));
            behavior.Tick(0.11f);
            Expect(locomotion.StopCalls == 1, "ready transition did not stop approach movement");
            Expect(
                (behavior.Held & JobResources.Locomotion) != 0,
                "ready transition discarded the latched locomotion resource");

            behavior.Tick(0.41f);
            Expect(target.ActivateCalls == 0, "alignment settle was skipped");
            behavior.Tick(0.52f);
            Expect(target.ActivateCalls == 1, "aligned interaction did not activate");
            Expect(target.ProgressCalls == 1, "activation did not begin confirmation");
            Expect(behavior.IsActive, "unconfirmed authority crossing ended early");

            target.Observed = true;
            behavior.Tick(0.53f);
            Expect(!behavior.IsActive, "confirmed interaction retained an active job");

            CompanionJobCompletion completion;
            Expect(behavior.TryTakeCompletion(out completion), "confirmation did not publish completion");
            Expect(completion.Result.Succeeded, "confirmed interaction reported failure");
            Expect(completion.Result.State == "activated", "success state was not preserved");
            Expect(
                completion.HandsTransition == CompanionTurnHandsTransition.None,
                "world switch incorrectly published a hands transition");
            Expect(Plugin.Logger.Contains("[INTERACT] APPROACH_REACHED"), "approach receipt was lost");
            Expect(Plugin.Logger.Contains("[INTERACT] AUTHORITY_REQUEST"), "authority receipt was lost");
            Expect(Plugin.Logger.Contains("[INTERACT] CONFIRMED"), "confirmation receipt was lost");
        }

        private static void BlockedApproachUsesSharedRecoveryAndCancelsCleanly()
        {
            ProbeLoggerReset();
            CompanionLocomotion locomotion;
            CompanionAttention attention;
            CompanionJumpActuator jump;
            var behavior = CreateBehavior(out locomotion, out attention, out jump);
            locomotion.SteerResult = false;
            var target = CreateTarget(
                CompanionAffordanceReadinessState.NeedsApproach,
                new UnityEngine.Vector3(6f, 0f, 0f));

            AgentToolResult failure;
            Expect(
                behavior.TryBegin(1f, CreateRequest(target, "call-recovery", 42L), out failure),
                "recovery scenario admission failed");
            behavior.Tick(1f);

            Expect(jump.RecoveryCalls == 1, "blocked approach did not request recovery");
            Expect(jump.LastReason == "blocked_path", "blocked recovery lost its reason");
            Expect(locomotion.CommitCalls == 1, "blocked recovery did not commit traversal");
            Expect(Plugin.Logger.Contains("[INTERACT] APPROACH_RECOVERY"), "recovery telemetry was lost");
            Expect(Plugin.Logger.Contains("attempt=1"), "recovery telemetry lost its attempt");

            behavior.Cancel(1.01f);
            Expect(!behavior.IsActive, "pre-authority cancellation retained the job");
            Expect(jump.CancelCalls == 1, "cancellation did not release approach recovery");
            CompanionJobCompletion completion;
            Expect(!behavior.TryTakeCompletion(out completion), "pre-authority cancellation published completion");
        }

        private static void UnavailableApproachTerminatesBeforeAuthority()
        {
            ProbeLoggerReset();
            CompanionLocomotion locomotion;
            CompanionAttention attention;
            CompanionJumpActuator jump;
            var behavior = CreateBehavior(out locomotion, out attention, out jump);
            var target = CreateTarget(
                CompanionAffordanceReadinessState.NeedsApproach,
                new UnityEngine.Vector3(5f, 0f, 0f));

            AgentToolResult failure;
            Expect(
                behavior.TryBegin(2f, CreateRequest(target, "call-unavailable", 43L), out failure),
                "unavailable scenario admission failed");
            SetReadiness(
                target,
                CompanionAffordanceReadinessState.Unavailable,
                new UnityEngine.Vector3(5f, 0f, 0f),
                "switch_blocked");
            behavior.Tick(2f);

            CompanionJobCompletion completion;
            Expect(behavior.TryTakeCompletion(out completion), "unavailable approach did not complete");
            Expect(completion.Result.Error == "switch_blocked", "unavailable error was not preserved");
            Expect(target.ActivateCalls == 0, "unavailable approach crossed authority");
            Expect(!behavior.IsActive, "unavailable approach retained an active job");
        }

        private static void FinalReadinessCheckPrecedesAuthority()
        {
            ProbeLoggerReset();
            CompanionLocomotion locomotion;
            CompanionAttention attention;
            CompanionJumpActuator jump;
            var behavior = CreateBehavior(out locomotion, out attention, out jump);
            var target = CreateTarget(
                CompanionAffordanceReadinessState.Ready,
                new UnityEngine.Vector3(2f, 0f, 0f));

            AgentToolResult failure;
            Expect(
                behavior.TryBegin(3f, CreateRequest(target, "call-revalidate", 44L), out failure),
                "ready scenario admission failed");
            behavior.Tick(3.3f);
            SetReadiness(
                target,
                CompanionAffordanceReadinessState.Unavailable,
                new UnityEngine.Vector3(2f, 0f, 0f),
                "activation_no_longer_ready");
            behavior.Tick(3.41f);

            CompanionJobCompletion completion;
            Expect(behavior.TryTakeCompletion(out completion), "failed final readiness did not complete");
            Expect(
                completion.Result.Error == "activation_no_longer_ready",
                "final readiness error was not preserved");
            Expect(target.ActivateCalls == 0, "authority ran after failed final readiness");
        }

        private static void PostAuthorityCancellationReconcilesWithoutCompletion()
        {
            ProbeLoggerReset();
            CompanionLocomotion locomotion;
            CompanionAttention attention;
            CompanionJumpActuator jump;
            var behavior = CreateBehavior(out locomotion, out attention, out jump);
            var target = CreateTarget(
                CompanionAffordanceReadinessState.Ready,
                new UnityEngine.Vector3(1f, 0f, 0f));
            target.IsWorldTarget = false;

            var request = CreateRequest(target, "call-cancel", 45L);
            Expect(
                (behavior.RequiredFor(request) & JobResources.Locomotion) == 0,
                "ready in-place interaction reserved locomotion");

            AgentToolResult failure;
            Expect(behavior.TryBegin(4f, request, out failure), "cancel scenario admission failed");
            behavior.Tick(4.3f);
            behavior.Tick(4.41f);
            Expect(target.ActivateCalls == 1, "cancel scenario did not cross authority");

            behavior.Cancel(4.42f);
            Expect(behavior.IsActive, "post-authority cancel skipped reconciliation");
            Expect(Plugin.Logger.Contains("[INTERACT] CANCEL_PENDING"), "pending cancel telemetry was lost");

            target.Observed = true;
            behavior.Tick(4.43f);
            Expect(!behavior.IsActive, "observed cancellation retained the job");
            CompanionJobCompletion completion;
            Expect(!behavior.TryTakeCompletion(out completion), "reconciled cancellation published normal completion");
            Expect(Plugin.Logger.Contains("[INTERACT] CANCEL_RECONCILED"), "cancel reconciliation telemetry was lost");
            Expect(locomotion.ResetProgressCalls == 0, "in-place interaction reset locomotion progress");
        }

        private static void FailedActivationAfterAuthorityReconciles()
        {
            ProbeLoggerReset();
            CompanionLocomotion locomotion;
            CompanionAttention attention;
            CompanionJumpActuator jump;
            var behavior = CreateBehavior(out locomotion, out attention, out jump);
            var target = CreateTarget(
                CompanionAffordanceReadinessState.Ready,
                new UnityEngine.Vector3(1f, 0f, 0f));
            target.IsWorldTarget = false;
            target.ActivationAvailable = false;
            target.ActivationCrossesAuthority = true;
            target.ActivationError = "native_wrapper_failed";

            AgentToolResult failure;
            Expect(
                behavior.TryBegin(
                    5f,
                    CreateRequest(target, "call-failed-dispatch", 46L),
                    out failure),
                "failed-dispatch scenario admission failed");
            behavior.Tick(5.3f);
            behavior.Tick(5.41f);

            Expect(target.ActivateCalls == 1, "failed-dispatch scenario did not attempt authority");
            Expect(target.ProgressCalls == 1, "failed-dispatch scenario skipped reconciliation");
            Expect(behavior.IsActive, "post-authority activation failure ended before reconciliation");
            CompanionJobCompletion completion;
            Expect(
                !behavior.TryTakeCompletion(out completion),
                "post-authority activation failure published a pre-authority failure");
            Expect(
                Plugin.Logger.Contains("[INTERACT] RECONCILIATION_PENDING"),
                "post-authority activation failure lost reconciliation telemetry");

            target.Observed = true;
            behavior.Tick(5.42f);
            Expect(!behavior.IsActive, "reconciled failed-dispatch scenario retained the job");
            Expect(
                behavior.TryTakeCompletion(out completion),
                "reconciled failed-dispatch scenario did not publish completion");
            Expect(completion.Result.Succeeded, "observed native outcome remained a failure");
        }

        private static CompanionInteractBehavior CreateBehavior(
            out CompanionLocomotion locomotion,
            out CompanionAttention attention,
            out CompanionJumpActuator jump)
        {
            locomotion = new CompanionLocomotion();
            attention = new CompanionAttention();
            jump = new CompanionJumpActuator();
            var behavior = new CompanionInteractBehavior(
                locomotion,
                attention,
                jump);
            behavior.Bind(
                new CompanionBody
                {
                    IsAlive = true,
                    Position = UnityEngine.Vector3.zero
                },
                new PlayerCharacter());
            return behavior;
        }

        private static CompanionAffordanceTarget CreateTarget(
            CompanionAffordanceReadinessState state,
            UnityEngine.Vector3 point)
        {
            var target = new CompanionAffordanceTarget();
            SetReadiness(target, state, point);
            return target;
        }

        private static void SetReadiness(
            CompanionAffordanceTarget target,
            CompanionAffordanceReadinessState state,
            UnityEngine.Vector3 point,
            string error = null)
        {
            target.Readiness = new CompanionAffordanceReadiness
            {
                State = state,
                Point = point,
                Activation = state == CompanionAffordanceReadinessState.Ready
                    ? new CompanionAffordanceActivation("activation-1")
                    : null,
                Error = error
            };
        }

        private static CompanionJobRequest CreateRequest(
            CompanionAffordanceTarget target,
            string callId,
            long turnId)
        {
            return new CompanionJobRequest
            {
                AffordanceTarget = target,
                InteractionIntent = CompanionInteractionIntent.Use,
                CallId = callId,
                TurnId = turnId
            };
        }

        private static void ProbeLoggerReset()
        {
            Plugin.Logger.Clear();
            UnityEngine.Time.realtimeSinceStartup = 100f;
        }

        private static void Expect(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
    }
}
