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
    private readonly LogLatch _unsolicitedFailureLog = new LogLatch();

    private CompanionBody _body;
    private float _nextPoll;
    private float _verifyAt;
    private bool _hasSpawnedBot;
    private readonly System.Collections.Generic.List<CompanionJobLease> _jobLeases =
        new System.Collections.Generic.List<CompanionJobLease>();
    private readonly System.Collections.Generic.Dictionary<long, CompanionJobCompletion>
        _completionsAwaitingSettlement =
            new System.Collections.Generic.Dictionary<long, CompanionJobCompletion>();

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
        for (var index = 0; index < controller._jobLeases.Count; index++)
            controller._jobLeases[index].MarkCancellationRequested(now);
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
        controller.RetireSettledJobs(now, "before_begin");
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
        var lease = new CompanionJobLease();
        if (!lease.TryBegin(token, jobName))
        {
            controller._actions.CancelJob(jobName, now);
            failure = AgentToolResult.Failure("action_lease_unavailable");
            return false;
        }
        controller._jobLeases.Add(lease);
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

    internal static bool TryTakeUnsolicitedAwarenessContext(
        float now,
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
        if (!controller._awareness.IsUnsolicitedScanDue(now))
        {
            error = "scan_not_due";
            return false;
        }

        CompanionAffordanceCandidates candidates = null;
        var human = WorldManager.localPlayerCharacter;
        if (human != null && human.gameObject != body.GameObject)
        {
            try
            {
                string candidateError;
                CompanionAffordanceCandidates.TryCaptureAmbient(
                    human,
                    body,
                    out candidates,
                    out candidateError);
            }
            catch (Exception exception)
            {
                candidates = null;
                if (controller._unsolicitedFailureLog.ShouldLog())
                {
                    Plugin.Logger.LogWarning(
                        $"[AWARENESS] AMBIENT_REFERENCE_CAPTURE_FAILED error={exception.Message}");
                }
            }
        }

        try
        {
            var taken = controller._awareness.TryTakeUnsolicitedContext(
                now,
                candidates,
                out context,
                out error);
            controller._unsolicitedFailureLog.Reset();
            return taken;
        }
        catch (Exception exception)
        {
            error = "awareness_context_capture_failed";
            if (controller._unsolicitedFailureLog.ShouldLog())
            {
                Plugin.Logger.LogWarning(
                    $"[AWARENESS] EVENT_CONTEXT_FAILED error={exception.Message}");
            }
            return false;
        }
    }

    internal static void ConfirmUnsolicitedAwarenessContextDelivered(
        CompanionAwarenessTurnContext context)
    {
        var controller = _activeController;
        if (controller == null || context == null)
            return;
        controller._awareness.ConfirmUnsolicitedContextDelivered(context);
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

        var lease = controller.FindLease(operationToken);
        if (lease == null)
        {
            completion = CompanionJobCompletion.Failed("cancelled");
            return true;
        }

        var now = Time.realtimeSinceStartup;
        var trackedJobName = lease.JobName;
        CompanionJobCompletion stashed;
        if (controller._completionsAwaitingSettlement.TryGetValue(
                operationToken,
                out stashed))
        {
            if (!controller.TryConcludeLease(
                    lease,
                    now,
                    "completion_publication",
                    false))
            {
                completion = null;
                return false;
            }

            completion = stashed;
            controller._completionsAwaitingSettlement.Remove(operationToken);
            controller.RevalidateCompletionForPublication(
                ref completion,
                operationToken,
                trackedJobName);
            return true;
        }

        if (!controller._actions.TryTakeJobCompletion(
                trackedJobName,
                now,
                out completion))
        {
            if (!controller._actions.IsJobSettled(trackedJobName))
            {
                return false;
            }

            completion = CompanionJobCompletion.Failed("cancelled");
            Plugin.Logger.LogInfo(
                $"[ACTION] JOB_CANCEL_SETTLED token={operationToken}, " +
                $"job={trackedJobName ?? "none"}.");
            controller._actions.ConcludeJob(trackedJobName, now);
            controller.RemoveLease(lease);
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
            controller._completionsAwaitingSettlement[operationToken] = completion;
            completion = null;
            if (!controller.TryConcludeLease(
                    lease,
                    now,
                    "completion_publication",
                    false))
            {
                return false;
            }
            completion = controller._completionsAwaitingSettlement[operationToken];
            controller._completionsAwaitingSettlement.Remove(operationToken);
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
        var lease = controller?.FindLease(operationToken);
        if (lease != null)
        {
            var now = Time.realtimeSinceStartup;
            controller._completionsAwaitingSettlement.Remove(operationToken);
            controller._actions.CancelJob(lease.JobName, now);
            lease.MarkCancellationRequested(now);
        }
    }

    internal static bool DetachJob(long operationToken)
    {
        var controller = _activeController;
        var lease = controller?.FindLease(operationToken);
        if (lease == null)
        {
            return false;
        }

        var now = Time.realtimeSinceStartup;
        if (!lease.CancellationRequested)
            controller._actions.CancelJob(lease.JobName, now);
        lease.MarkDetached(now);
        Plugin.Logger.LogInfo(
            $"[ACTION] JOB_SETTLEMENT_DETACHED token={operationToken}, " +
            $"job={lease.JobName ?? "none"}.");
        return true;
    }

    internal static bool AbandonJobSettlement(
        long operationToken,
        string reason)
    {
        var controller = _activeController;
        var lease = controller?.FindLease(operationToken);
        if (lease == null)
        {
            return false;
        }

        var jobName = lease.JobName;
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
        var lease = controller?.FindLease(operationToken);
        if (lease == null)
        {
            return true;
        }
        return controller.TryConcludeLease(
            lease,
            Time.realtimeSinceStartup,
            "bridge_conclusion",
            true);
    }

    private CompanionJobLease FindLease(long token)
    {
        if (token == 0)
            return null;
        for (var index = 0; index < _jobLeases.Count; index++)
        {
            if (_jobLeases[index].Matches(token))
                return _jobLeases[index];
        }

        return null;
    }

    private void RemoveLease(CompanionJobLease lease)
    {
        _jobLeases.Remove(lease);
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
            TryApplyCraneShortcut(now);
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

    private void TryApplyCraneShortcut(float now)
    {
        if (!Plugin.EnableCraneShortcut.Value)
            return;

        try
        {
            if (!Input.GetKeyDown(KeyCode.F8))
                return;

            if (!NetworkServer.active || !_body.Networking.isServer ||
                _body.Networking.isLocalPlayer)
            {
                Plugin.Logger.LogWarning(
                    "[QA] CRANE_REPRO_SKIPPED reason=companion_authority_unavailable.");
                return;
            }

            var human = WorldManager.localPlayerCharacter;
            if (_jobLeases.Count != 0 || _actions.ActiveJobName != null || _actions.JumpQueued)
            {
                Plugin.Logger.LogWarning(
                    "[QA] CRANE_REPRO_SKIPPED reason=companion_action_active.");
                return;
            }

            if (CompanionFollowBehavior.IsHumanCarryingBody(_body, human) ||
                CompanionFollowBehavior.IsBodyCarryingHuman(_body, human))
            {
                Plugin.Logger.LogWarning(
                    "[QA] CRANE_REPRO_SKIPPED reason=carry_relationship_active.");
                return;
            }

            string failure;
            if (!DevelopmentCraneShortcut.TryApply(_body, human, out failure))
            {
                Plugin.Logger.LogWarning($"[QA] CRANE_REPRO_SKIPPED reason={failure}.");
                return;
            }

            _actions.RebaseAfterExternalReposition(human, now);
            Plugin.Logger.LogInfo(
                "[QA] CRANE_REPRO_APPLIED hotkey=F8 " +
                $"human={human.transform.position}, companion={_body.Position}.");
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogError($"[QA] CRANE_REPRO_FAILED error={exception.Message}");
        }
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
            ReapDetachedJobs(now);
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

    private void RetireSettledJobs(float now, string reason)
    {
        for (var index = _jobLeases.Count - 1; index >= 0; index--)
        {
            var lease = _jobLeases[index];
            if (!lease.HasValue ||
                _completionsAwaitingSettlement.ContainsKey(lease.Token) ||
                !_actions.IsJobSettled(lease.JobName))
            {
                continue;
            }

            RetireLeaseAt(index, now, reason);
        }
    }

    private void RetireLeaseAt(int index, float now, string reason)
    {
        var lease = _jobLeases[index];
        var token = lease.Token;
        var jobName = lease.JobName;
        CompanionJobCompletion ignored;
        _actions.TryTakeJobCompletion(jobName, now, out ignored);
        _actions.ConcludeJob(jobName, now);
        _jobLeases.RemoveAt(index);
        _completionsAwaitingSettlement.Remove(token);
        Plugin.Logger.LogInfo(
            $"[ACTION] JOB_TOKEN_RETIRED token={token}, " +
            $"job={jobName ?? "none"}, reason={reason}.");
    }

    private void ReapDetachedJobs(float now)
    {
        for (var index = _jobLeases.Count - 1; index >= 0; index--)
        {
            var lease = _jobLeases[index];
            if (!lease.HasValue || !lease.IsDetached)
                continue;

            if (_actions.IsJobSettled(lease.JobName))
            {
                RetireLeaseAt(index, now, "detached_reconciled");
                continue;
            }

            if (!lease.DetachedSettlementTimedOut(
                    now,
                    DetachedJobSettlementMaximumSeconds))
            {
                continue;
            }

            var token = lease.Token;
            var jobName = lease.JobName;
            _actions.ConcludeJob(jobName, now);
            if (!_actions.IsJobSettled(jobName))
            {
                Plugin.Logger.LogError(
                    $"[ACTION] JOB_SETTLEMENT_ABANDON_FAILED token={token}, " +
                    $"job={jobName ?? "none"}, reason=detached_timeout.");
                continue;
            }
            _jobLeases.RemoveAt(index);
            _completionsAwaitingSettlement.Remove(token);
            Plugin.Logger.LogWarning(
                $"[ACTION] JOB_SETTLEMENT_ABANDONED token={token}, " +
                $"job={jobName ?? "none"}, reason=detached_timeout, " +
                "disposition=ownership_returned_to_stock_state.");
        }
    }

    private void ClearActiveJobTracking()
    {
        _jobLeases.Clear();
        _completionsAwaitingSettlement.Clear();
    }

    private bool TryConcludeLease(
        CompanionJobLease lease,
        float now,
        string reason,
        bool detachOnFailure)
    {
        if (lease == null || !lease.HasValue)
            return true;

        var token = lease.Token;
        var jobName = lease.JobName;
        _actions.ConcludeJob(jobName, now);
        var settled = _actions.IsJobSettled(jobName);
        if (!CompanionJobSettlementProtocol.CanReleaseLeaseAfterConclude(
                settled))
        {
            if (detachOnFailure)
                lease.MarkDetached(now);
            Plugin.Logger.LogError(
                $"[ACTION] JOB_CONCLUSION_PENDING token={token}, " +
                $"job={jobName ?? "none"}, reason={reason}, " +
                $"detached={detachOnFailure}.");
            return false;
        }

        RemoveLease(lease);
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
