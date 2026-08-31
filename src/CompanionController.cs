using System;
using Dissonance.Integrations.MirrorIgnorance;
using LobbyNetworking;
using Mirror;
using UnityEngine;

namespace Ramblers;

internal sealed class CompanionController : MonoBehaviour
{
    private const float DetachedJobSettlementMaximumSeconds = 5f;

    private readonly CompanionActionCoordinator _actions = new CompanionActionCoordinator();
    private readonly CompanionAwareness _awareness = new CompanionAwareness();
    private readonly LogLatch _verificationLog = new LogLatch();
    private readonly LogLatch _awarenessLateLog = new LogLatch();

    private CompanionBody _body;
    private float _nextPoll;
    private float _verifyAt;
    private bool _hasSpawnedBot;
    private readonly CompanionJobLease _jobLease = new CompanionJobLease();
    private CompanionJobCompletion _completionAwaitingSettlement;

    private static CompanionController _activeController;
    private static long _nextJobToken;

    public CompanionController(IntPtr pointer) : base(pointer)
    {
    }

    private void Awake()
    {
        _activeController = this;
    }

    internal static AgentToolResult SetFollowMode(FollowMode mode)
    {
        CompanionController controller;
        AgentToolResult failure;
        if (!TryGetCommandTarget(out controller, out failure))
            return failure;
        return controller._actions.SetFollowMode(mode, Time.realtimeSinceStartup);
    }

    internal static AgentToolResult SetPosture(CompanionPosture posture)
    {
        CompanionController controller;
        AgentToolResult failure;
        if (!TryGetCommandTarget(out controller, out failure))
            return failure;
        return controller._actions.SetPosture(posture, Time.realtimeSinceStartup);
    }

    internal static AgentToolResult RequestJump()
    {
        CompanionController controller;
        AgentToolResult failure;
        if (!TryGetCommandTarget(out controller, out failure))
            return failure;
        return controller._actions.RequestJump(Time.realtimeSinceStartup);
    }

    internal static AgentToolResult CancelActiveWork()
    {
        CompanionController controller;
        AgentToolResult failure;
        if (!TryGetCommandTarget(out controller, out failure))
            return failure;
        var now = Time.realtimeSinceStartup;
        var result = controller._actions.CancelActiveWork(now);
        if (controller._jobLease.HasValue)
            controller._jobLease.MarkCancellationRequested(now);
        return result;
    }

    internal static bool TryBeginJob(
        string jobName,
        CompanionJobRequest request,
        out CompanionJobHandle handle,
        out AgentToolResult failure)
    {
        handle = null;
        CompanionController controller;
        if (!TryGetCommandTarget(out controller, out failure))
            return false;

        var now = Time.realtimeSinceStartup;
        controller.RetireSettledJob(now, "before_begin");
        if (controller._jobLease.HasValue)
        {
            failure = AgentToolResult.Failure(
                (controller._jobLease.JobName ?? "action") +
                "_in_progress");
            return false;
        }
        float timeoutSeconds;
        if (!controller._actions.TryBeginJob(
                jobName,
                request,
                now,
                out timeoutSeconds,
                out failure))
        {
            return false;
        }

        var token = ++_nextJobToken;
        if (!controller._jobLease.TryBegin(token, jobName))
        {
            controller._actions.CancelJob(jobName, now);
            failure = AgentToolResult.Failure("action_lease_unavailable");
            return false;
        }
        handle = new CompanionJobHandle
        {
            Token = token,
            TimeoutSeconds = timeoutSeconds
        };
        return true;
    }

    internal static bool TryCapturePropTarget(
        out CompanionPropTarget target,
        out string error)
    {
        target = null;
        error = null;

        var controller = _activeController;
        var body = controller == null ? null : controller._body;
        if (body == null || !body.IsAlive || !controller._hasSpawnedBot)
        {
            error = "bot_not_spawned";
            return false;
        }

        var human = WorldManager.localPlayerCharacter;
        if (human == null || human.gameObject == body.GameObject)
        {
            error = "human_player_unavailable";
            return false;
        }

        return CompanionPropTarget.TryResolve(
            human,
            body,
            out target,
            out error);
    }

    internal static bool TryCaptureCompanionHeldTarget(
        out CompanionPropTarget target,
        out string error)
    {
        target = null;
        error = null;

        var controller = _activeController;
        var body = controller == null ? null : controller._body;
        if (body == null || !body.IsAlive || !controller._hasSpawnedBot)
        {
            error = "bot_not_spawned";
            return false;
        }

        var hands = body.Character == null ? null : body.Character.hands;
        if (hands == null)
        {
            error = "hands_unavailable";
            return false;
        }

        if (hands.heldProp == null)
        {
            error = hands.heldCharacter == null
                ? "companion_held_item_unavailable"
                : "companion_held_item_not_prop";
            return false;
        }

        if (!CompanionPropTarget.TryCaptureHeldProp(
                hands.heldProp,
                out target))
        {
            error = "companion_held_item_unavailable";
            return false;
        }

        return true;
    }

    internal static bool TryCaptureInspectionCandidates(
        out CompanionInspectionCandidates candidates,
        out string error)
    {
        candidates = null;
        error = null;

        var controller = _activeController;
        var body = controller == null ? null : controller._body;
        if (body == null || !body.IsAlive || !controller._hasSpawnedBot)
        {
            error = "bot_not_spawned";
            return false;
        }

        var human = WorldManager.localPlayerCharacter;
        if (human == null || human.gameObject == body.GameObject)
        {
            error = "human_player_unavailable";
            return false;
        }

        return CompanionInspectionCandidates.TryCapture(
            human,
            body,
            out candidates,
            out error);
    }

    internal static bool TryCaptureAffordanceCandidates(
        out CompanionAffordanceCandidates candidates,
        out string error)
    {
        candidates = null;
        error = null;
        var controller = _activeController;
        var body = controller == null ? null : controller._body;
        if (body == null || !body.IsAlive || !controller._hasSpawnedBot)
        {
            error = "bot_not_spawned";
            return false;
        }

        var human = WorldManager.localPlayerCharacter;
        if (human == null || human.gameObject == body.GameObject)
        {
            error = "human_player_unavailable";
            return false;
        }

        try
        {
            return CompanionAffordanceCandidates.TryCapture(
                human,
                body,
                out candidates,
                out error);
        }
        catch (Exception exception)
        {
            error = "interaction_reference_capture_failed";
            Plugin.Logger.LogWarning(
                $"[INTERACT] REFERENCE_CAPTURE_FAILED error={exception.Message}");
            return false;
        }
    }

    internal static bool TryAdvanceHeldPropCandidates(
        CompanionAffordanceCandidates current,
        CompanionPropTarget exactProp,
        out CompanionAffordanceCandidates advanced,
        out string error)
    {
        advanced = null;
        error = null;
        var controller = _activeController;
        var body = controller == null ? null : controller._body;
        if (body == null || !body.IsAlive || !controller._hasSpawnedBot)
        {
            error = "bot_not_spawned";
            return false;
        }
        if (current == null || exactProp == null)
        {
            error = "turn_state_transition_invalid";
            return false;
        }

        return current.TryWithCompanionHeldProp(
            body,
            exactProp,
            out advanced,
            out error);
    }

    internal static bool TryCaptureHumanPlayerTarget(
        out CompanionPlayerTarget target,
        out string error)
    {
        target = null;
        error = null;
        var controller = _activeController;
        var body = controller == null ? null : controller._body;
        if (body == null || !body.IsAlive || !controller._hasSpawnedBot)
        {
            error = "bot_not_spawned";
            return false;
        }

        var human = WorldManager.localPlayerCharacter;
        if (human == null || human.gameObject == body.GameObject)
        {
            error = "human_player_unavailable";
            return false;
        }
        return CompanionPlayerTarget.TryCapture(
            human,
            body,
            out target,
            out error);
    }

    internal static bool TryTakeAwarenessTurnContext(
        CompanionAffordanceCandidates affordanceCandidates,
        out CompanionAwarenessTurnContext context,
        out string error)
    {
        context = null;
        error = null;
        var controller = _activeController;
        var body = controller == null ? null : controller._body;
        if (body == null || !body.IsAlive || !controller._hasSpawnedBot)
        {
            error = "bot_not_spawned";
            return false;
        }

        try
        {
            return controller._awareness.TryTakeTurnContext(
                Time.realtimeSinceStartup,
                affordanceCandidates,
                out context,
                out error);
        }
        catch (Exception exception)
        {
            error = "awareness_context_capture_failed";
            Plugin.Logger.LogWarning(
                $"[AWARENESS] TURN_CONTEXT_FAILED error={exception.Message}");
            return false;
        }
    }

    internal static void ConfirmAwarenessTurnContextDelivered(
        CompanionAwarenessTurnContext context)
    {
        var controller = _activeController;
        if (controller == null || context == null)
            return;
        controller._awareness.ConfirmTurnContextDelivered(context);
    }

    internal static bool TryTakeJobCompletion(
        long operationToken,
        out CompanionJobCompletion completion)
    {
        var controller = _activeController;
        if (controller == null || controller._body == null ||
            !controller._body.IsAlive || !controller._hasSpawnedBot)
        {
            completion = CompanionJobCompletion.Failed("bot_not_spawned");
            return true;
        }

        if (!controller._jobLease.Matches(operationToken))
        {

            completion = CompanionJobCompletion.Failed("cancelled");
            return true;
        }

        var now = Time.realtimeSinceStartup;
        var trackedJobName = controller._jobLease.JobName;
        if (controller._completionAwaitingSettlement != null)
        {
            if (!controller.TryConcludeTrackedJob(
                    now,
                    "completion_publication",
                    false))
            {
                completion = null;
                return false;
            }

            completion = controller._completionAwaitingSettlement;
            controller._completionAwaitingSettlement = null;
            controller.RevalidateCompletionForPublication(
                ref completion,
                operationToken,
                trackedJobName);
            return true;
        }

        if (!controller._actions.TryTakeJobCompletion(
                controller._jobLease.JobName,
                now,
                out completion))
        {
            if (!controller._actions.IsJobSettled(
                    controller._jobLease.JobName))
            {
                return false;
            }

            completion = CompanionJobCompletion.Failed("cancelled");
            Plugin.Logger.LogInfo(
                $"[ACTION] JOB_CANCEL_SETTLED token={operationToken}, " +
                $"job={controller._jobLease.JobName ?? "none"}.");
            controller._actions.ConcludeJob(controller._jobLease.JobName, now);
            controller.ClearActiveJobTracking();
            return true;
        }

        if (completion == null)
            return false;

        var retainUntilAssistantAudio = completion != null &&
                                        completion.Result != null &&
                                        completion.Result.Ok &&
                                        completion.RetainUntilAssistantAudio;
        if (!retainUntilAssistantAudio)
        {
            controller._completionAwaitingSettlement = completion;
            completion = null;
            if (!controller.TryConcludeTrackedJob(
                    now,
                    "completion_publication",
                    false))
            {
                return false;
            }
            completion = controller._completionAwaitingSettlement;
            controller._completionAwaitingSettlement = null;
        }
        controller.RevalidateCompletionForPublication(
            ref completion,
            operationToken,
            trackedJobName);
        return true;
    }

    private void RevalidateCompletionForPublication(
        ref CompanionJobCompletion completion,
        long operationToken,
        string jobName)
    {
        if (completion == null || completion.Result == null ||
            !completion.Result.Ok)
        {
            return;
        }

        var hands = _body == null || !_body.IsAlive ||
                    _body.Character == null
            ? null
            : _body.Character.hands;
        var exactPropHeld = hands != null &&
                            completion.ExactProp != null &&
                            completion.ExactProp.IsStillTheSameProp(
                                hands.heldProp);
        var exactPlayerHeld = hands != null &&
                              completion.ExactPlayer != null &&
                              completion.ExactPlayer.IsStillTheSamePlayer(
                                  hands.heldCharacter);
        var handsEmpty = hands != null && hands.heldProp == null &&
                         hands.heldCharacter == null;
        if (CompanionJobSettlementProtocol.IsCompletionTransitionCurrent(
                completion.HandsTransition,
                exactPropHeld,
                exactPlayerHeld,
                handsEmpty))
        {
            return;
        }

        var transition = completion.HandsTransition;
        Plugin.Logger.LogWarning(
            $"[ACTION] JOB_COMPLETION_INVALIDATED " +
            $"token={operationToken}, job={jobName ?? "none"}, " +
            $"transition={transition}, reason=native_state_changed.");
        completion = CompanionJobCompletion.Failed(
            "turn_state_transition_not_confirmed");
    }

    internal static void CancelJob(long operationToken)
    {
        var controller = _activeController;
        if (controller != null && controller._jobLease.Matches(operationToken))
        {
            var now = Time.realtimeSinceStartup;
            controller._completionAwaitingSettlement = null;
            controller._actions.CancelJob(
                controller._jobLease.JobName,
                now);
            controller._jobLease.MarkCancellationRequested(now);
        }
    }

    internal static bool DetachJob(long operationToken)
    {
        var controller = _activeController;
        if (controller == null || !controller._jobLease.Matches(operationToken))
        {
            return false;
        }

        var now = Time.realtimeSinceStartup;
        if (!controller._jobLease.CancellationRequested)
            controller._actions.CancelJob(controller._jobLease.JobName, now);
        controller._jobLease.MarkDetached(now);
        Plugin.Logger.LogInfo(
            $"[ACTION] JOB_SETTLEMENT_DETACHED token={operationToken}, " +
            $"job={controller._jobLease.JobName ?? "none"}.");
        return true;
    }

    internal static bool AbandonJobSettlement(
        long operationToken,
        string reason)
    {
        var controller = _activeController;
        if (controller == null || !controller._jobLease.Matches(operationToken))
        {
            return false;
        }

        var jobName = controller._jobLease.JobName;
        controller._actions.ConcludeJob(jobName, Time.realtimeSinceStartup);
        if (!controller._actions.IsJobSettled(jobName))
        {
            Plugin.Logger.LogError(
                $"[ACTION] JOB_SETTLEMENT_ABANDON_FAILED token={operationToken}, " +
                $"job={jobName ?? "none"}, reason={reason ?? "unknown"}.");
            return false;
        }
        controller.ClearActiveJobTracking();
        Plugin.Logger.LogWarning(
            $"[ACTION] JOB_SETTLEMENT_ABANDONED token={operationToken}, " +
            $"job={jobName ?? "none"}, reason={reason ?? "unknown"}, " +
            "disposition=ownership_returned_to_stock_state.");
        return true;
    }

    internal static bool ConcludeJob(long operationToken)
    {
        var controller = _activeController;
        if (controller == null || operationToken == 0 ||
            !controller._jobLease.Matches(operationToken))
        {
            return true;
        }
        return controller.TryConcludeTrackedJob(
            Time.realtimeSinceStartup,
            "bridge_conclusion",
            true);
    }

    internal static void SetConversationActive(bool active)
    {
        var controller = _activeController;
        if (controller == null || controller._body == null ||
            !controller._body.IsAlive || !controller._hasSpawnedBot)
        {
            return;
        }

        controller._actions.SetConversationActive(active);
    }

    internal static bool TryGetVoiceParticipants(
        out PlayerCharacter human,
        out PlayerCharacter bot)
    {
        var controller = _activeController;
        human = WorldManager.localPlayerCharacter;
        var body = controller == null ? null : controller._body;
        bot = body == null ? null : body.Character;
        return human != null && bot != null && body.GameObject != null;
    }

    private static bool TryGetCommandTarget(
        out CompanionController controller,
        out AgentToolResult failure)
    {
        controller = _activeController;
        if (controller == null)
        {
            failure = AgentToolResult.Failure("bot_controller_unavailable");
            return false;
        }

        var body = controller._body;
        if (body == null || !body.IsAlive || !controller._hasSpawnedBot)
        {
            failure = AgentToolResult.Failure("bot_not_spawned");
            return false;
        }

        if (!NetworkServer.active || !body.Networking.isServer || body.Networking.isLocalPlayer)
        {
            failure = AgentToolResult.Failure("bot_authority_unavailable");
            return false;
        }

        failure = null;
        return true;
    }

    private bool HasBody => _body != null && _body.IsAlive;

    private void Update()
    {
        if (HasBody)
        {
            var now = Time.realtimeSinceStartup;
            if (now >= _verifyAt && _verificationLog.ShouldLog())
                LogVerification();
            _actions.TickFrame(now);
            _awareness.Tick(now);
            return;
        }

        if (_hasSpawnedBot)
            ResetAfterBotDestroyed();

        if (Time.realtimeSinceStartup < _nextPoll)
            return;

        _nextPoll = Time.realtimeSinceStartup + 1f;
        if (!NetworkServer.active)
            return;

        var manager = NetworkManager.singleton;
        var localPlayer = WorldManager.localPlayerCharacter;
        if (manager == null || manager.playerPrefab == null || localPlayer == null)
            return;

        TrySpawn(manager, localPlayer);
    }

    private void FixedUpdate()
    {
        if (_body == null || !_body.IsAlive)
            return;

        try
        {
            _actions.TickFixed(Time.realtimeSinceStartup);
        }
        catch (Exception exception)
        {
            _actions.StopQuietly();
            Plugin.Logger.LogError($"[ACTION] Coordinator failed: {exception}");
        }
    }

    private void LateUpdate()
    {
        if (_body == null || !_body.IsAlive)
            return;

        var now = Time.realtimeSinceStartup;
        try
        {
            _actions.TickLateFrame(now);
            ReapDetachedJob(now);
        }
        catch (Exception exception)
        {
            _actions.FailActiveJobs(
                "action_execution_failed",
                Time.realtimeSinceStartup);
            Plugin.Logger.LogError($"[ACTION] Job update failed: {exception}");
            return;
        }

        try
        {
            CompanionAmbientObservationCandidate candidate;
            if (_actions.TryTakeAmbientObservation(now, out candidate))
                _awareness.TryRememberPassiveView(now, candidate);
            _awarenessLateLog.Reset();
        }
        catch (Exception exception)
        {
            if (_awarenessLateLog.ShouldLog())
            {
                Plugin.Logger.LogWarning(
                    $"[AWARENESS] PASSIVE_VIEW_UPDATE_FAILED error={exception.Message}");
            }
        }
    }

    private void TrySpawn(NetworkManager manager, PlayerCharacter localPlayer)
    {
        GameObject spawned = null;
        var networkSpawned = false;
        try
        {
            var position = localPlayer.transform.position
                         + localPlayer.transform.right * 2f
                         + Vector3.up * 0.25f;

            spawned = UnityEngine.Object.Instantiate(
                manager.playerPrefab,
                position,
                localPlayer.transform.rotation);
            spawned.name = CompanionIdentity.ObjectName;

            var playerCharacter = spawned.GetComponent<PlayerCharacter>();
            var playerNetworking = spawned.GetComponent<PlayerNetworking>();
            var networkIdentity = spawned.GetComponent<NetworkIdentity>();
            var networkTransform = spawned.GetComponent<HouseNetworkTransform>();
            var voiceIdentity = spawned.GetComponent<MirrorIgnorancePlayer>();

            if (playerCharacter == null || playerNetworking == null ||
                networkIdentity == null || networkTransform == null ||
                playerCharacter.mover == null)
            {
                throw new InvalidOperationException(
                    "The configured playerPrefab is missing a required player, mover, or network component.");
            }

            playerCharacter.mover.applyVelocityForRemotePlayers = true;
            CompanionIdentity.Apply(playerNetworking, voiceIdentity);
            NetworkServer.Spawn(spawned);
            networkSpawned = true;

            var now = Time.realtimeSinceStartup;
            _body = new CompanionBody(
                spawned,
                playerCharacter,
                playerNetworking,
                networkIdentity,
                networkTransform);

            ClearActiveJobTracking();
            _hasSpawnedBot = true;
            _actions.Bind(_body, localPlayer, now);
            try
            {
                _awareness.Bind(_body, localPlayer, _actions, now);
            }
            catch (Exception exception)
            {
                _awareness.Release();
                Plugin.Logger.LogWarning(
                    $"[AWARENESS] BIND_FAILED error={exception.Message}");
            }

            _verifyAt = now + 2f;
            Plugin.Logger.LogInfo(
                $"[RAMBLERS] Spawn requested: netId={networkIdentity.netId}, " +
                $"connectionToClient={(networkIdentity.connectionToClient == null ? "null" : "non-null")}, " +
                $"position={position}.");
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogError($"[RAMBLERS] Spawn failed: {exception}");
            if (spawned != null)
            {
                if (networkSpawned && NetworkServer.active)
                    NetworkServer.Destroy(spawned);
                else
                    UnityEngine.Object.Destroy(spawned);
            }
            _body = null;
            _hasSpawnedBot = false;
            _awareness.Release();
            _actions.Release();
        }
    }

    private void ResetAfterBotDestroyed()
    {
        _awareness.Release();
        _actions.Release();
        _body = null;
        _hasSpawnedBot = false;
        ClearActiveJobTracking();
        _verificationLog.Reset();
        _awarenessLateLog.Reset();
        Plugin.Logger.LogInfo("[RAMBLERS] Companion left the scene; controller state reset.");
    }

    private void OnDestroy()
    {
        if (_activeController == this)
            _activeController = null;
        ClearActiveJobTracking();

        try
        {
            _actions.StopQuietly();
            _awareness.Release();
            _actions.Release();
        }
        catch
        {

        }
    }

    private void RetireSettledJob(float now, string reason)
    {
        if (!_jobLease.HasValue ||
            !_actions.IsJobSettled(_jobLease.JobName))
        {
            return;
        }

        var token = _jobLease.Token;
        var jobName = _jobLease.JobName;
        CompanionJobCompletion ignored;
        _actions.TryTakeJobCompletion(jobName, now, out ignored);
        _actions.ConcludeJob(jobName, now);
        ClearActiveJobTracking();
        Plugin.Logger.LogInfo(
            $"[ACTION] JOB_TOKEN_RETIRED token={token}, " +
            $"job={jobName ?? "none"}, reason={reason}.");
    }

    private void ReapDetachedJob(float now)
    {
        if (!_jobLease.IsDetached || !_jobLease.HasValue)
            return;

        if (_actions.IsJobSettled(_jobLease.JobName))
        {
            RetireSettledJob(now, "detached_reconciled");
            return;
        }

        if (!_jobLease.DetachedSettlementTimedOut(
                now,
                DetachedJobSettlementMaximumSeconds))
        {
            return;
        }

        var token = _jobLease.Token;
        var jobName = _jobLease.JobName;
        _actions.ConcludeJob(jobName, now);
        if (!_actions.IsJobSettled(jobName))
        {
            Plugin.Logger.LogError(
                $"[ACTION] JOB_SETTLEMENT_ABANDON_FAILED token={token}, " +
                $"job={jobName ?? "none"}, reason=detached_timeout.");
            return;
        }
        ClearActiveJobTracking();
        Plugin.Logger.LogWarning(
            $"[ACTION] JOB_SETTLEMENT_ABANDONED token={token}, " +
            $"job={jobName ?? "none"}, reason=detached_timeout, " +
            "disposition=ownership_returned_to_stock_state.");
    }

    private void ClearActiveJobTracking()
    {
        _jobLease.Clear();
        _completionAwaitingSettlement = null;
    }

    private bool TryConcludeTrackedJob(
        float now,
        string reason,
        bool detachOnFailure)
    {
        if (!_jobLease.HasValue)
            return true;

        var token = _jobLease.Token;
        var jobName = _jobLease.JobName;
        _actions.ConcludeJob(jobName, now);
        var settled = _actions.IsJobSettled(jobName);
        if (!CompanionJobSettlementProtocol.CanReleaseLeaseAfterConclude(
                settled))
        {
            if (detachOnFailure)
                _jobLease.MarkDetached(now);
            Plugin.Logger.LogError(
                $"[ACTION] JOB_CONCLUSION_PENDING token={token}, " +
                $"job={jobName ?? "none"}, reason={reason}, " +
                $"detached={detachOnFailure}.");
            return false;
        }

        _jobLease.Clear();
        return true;
    }

    private void LogVerification()
    {
        if (!HasBody)
            return;

        var playerCharacter = _body.Character;
        var networking = _body.Networking;
        var identity = _body.Identity;
        var networkTransform = _body.NetworkTransform;
        var voiceIdentity = _body.GameObject.GetComponent<MirrorIgnorancePlayer>();
        var registeredPlayers = PlayerCharacter.allPlayerCharacters == null
            ? -1
            : PlayerCharacter.allPlayerCharacters.Count;

        Plugin.Logger.LogInfo(
            "[RAMBLERS] VERIFY " +
            $"version={Plugin.Version}, " +
            $"netId={identity?.netId ?? 0}, " +
            $"isServer={networking?.isServer}, " +
            $"isClient={networking?.isClient}, " +
            $"isLocalPlayer={networking?.isLocalPlayer}, " +
            $"serverOwnsTransform={networkTransform?.isOwned}, " +
            $"connectionToClient={(identity?.connectionToClient == null ? "null" : "non-null")}, " +
            $"registeredPlayerCharacters={registeredPlayers}, " +
            $"voicePlayerId={voiceIdentity?.PlayerId ?? "<none>"}, " +
            $"voiceTracking={voiceIdentity?.IsTracking}, " +
            $"remoteMotorEnabled={playerCharacter?.mover?.applyVelocityForRemotePlayers}, " +
            $"tuningForwardSpeed={playerCharacter?.tunings?.forwardSpeed}, " +
            $"tuningForwardSprintSpeed={playerCharacter?.tunings?.forwardSprintSpeed}, " +
            $"tuningCrouchSpeed={playerCharacter?.tunings?.crouchForwardSpeed}, " +
            $"tuningCrouchSprintSpeed={playerCharacter?.tunings?.crouchForwardSprintSpeed}, " +
            $"tuningJumpForce={playerCharacter?.tunings?.jumpForce}, " +
            $"trueCrouchness={networking?.trueCrouchness}, " +
            $"isSitting={networking?.isSitting}, " +
            $"isGrounded={playerCharacter?.ground?.isGrounded}, " +
            $"isOnJumpableGround={playerCharacter?.ground?.isOnJumpableGround}, " +
            $"movementResting={networkTransform?.IsRestingForPlayerMovement}, " +
            $"playerCharacterPresent={playerCharacter != null}.");
    }
}
