using System;
using Dissonance.Integrations.MirrorIgnorance;
using LobbyNetworking;
using Mirror;
using UnityEngine;

namespace Ramblers;

/// <summary>
/// Owns the companion lifecycle: spawning a connectionless copy of the real
/// player prefab, preserving authority invariants, binding deterministic
/// actions, and tearing the body down cleanly.
/// </summary>
internal sealed class CompanionController : MonoBehaviour
{
    private readonly CompanionActionCoordinator _actions = new CompanionActionCoordinator();
    private readonly LogLatch _verificationLog = new LogLatch();

    private CompanionBody _body;
    private float _nextPoll;
    private float _verifyAt;
    private bool _hasSpawnedBot;
    private long _activeJobToken;
    private string _activeJobName;

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
        controller._activeJobToken = 0;
        controller._activeJobName = null;
        return controller._actions.CancelActiveWork(Time.realtimeSinceStartup);
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

        float timeoutSeconds;
        if (!controller._actions.TryBeginJob(
                jobName,
                request,
                Time.realtimeSinceStartup,
                out timeoutSeconds,
                out failure))
        {
            return false;
        }

        controller._activeJobToken = ++_nextJobToken;
        controller._activeJobName = jobName;
        handle = new CompanionJobHandle
        {
            Token = controller._activeJobToken,
            TimeoutSeconds = timeoutSeconds
        };
        return true;
    }

    /// <summary>
    /// Captures a physical referent on Unity's main thread. The caller binds the
    /// returned immutable object to one response turn before any model tool is
    /// allowed to dispatch it.
    /// </summary>
    internal static bool TryCaptureInteractionTarget(
        out CompanionInteractionTarget target,
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

        return CompanionInteractionTarget.TryResolve(
            human,
            body,
            out target,
            out error);
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

        if (operationToken == 0 || controller._activeJobToken != operationToken)
        {
            // The job this caller was waiting on was cancelled or replaced.
            completion = CompanionJobCompletion.Failed("cancelled");
            return true;
        }

        if (!controller._actions.TryTakeJobCompletion(
                controller._activeJobName,
                Time.realtimeSinceStartup,
                out completion))
        {
            return false;
        }

        // A job that succeeded may still hold something past completion — an
        // inspection keeps its gaze — so it keeps its token until the model has
        // responded. A failed job has nothing left to own.
        if (completion == null || completion.Result == null || !completion.Result.Ok)
        {
            controller._activeJobToken = 0;
            controller._activeJobName = null;
        }
        return true;
    }

    internal static void CancelJob(long operationToken)
    {
        var controller = _activeController;
        if (controller != null && operationToken != 0 &&
            controller._activeJobToken == operationToken)
        {
            controller._actions.CancelJob(
                controller._activeJobName,
                Time.realtimeSinceStartup);
            controller._activeJobToken = 0;
            controller._activeJobName = null;
        }
    }

    internal static void ConcludeJob(long operationToken)
    {
        var controller = _activeController;
        if (controller != null && operationToken != 0 &&
            controller._activeJobToken == operationToken)
        {
            controller._actions.ConcludeJob(
                controller._activeJobName,
                Time.realtimeSinceStartup);
            controller._activeJobToken = 0;
            controller._activeJobName = null;
        }
    }

    /// <summary>
    /// Tells the companion whether a conversation is in progress. Unlike the
    /// action entry points this is ambient state rather than a command, so it is
    /// silently ignored when there is no body to apply it to.
    /// </summary>
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

        try
        {
            _actions.TickLateFrame(Time.realtimeSinceStartup);
        }
        catch (Exception exception)
        {
            _actions.FailActiveJobs(
                "action_execution_failed",
                Time.realtimeSinceStartup);
            Plugin.Logger.LogError($"[ACTION] Job update failed: {exception}");
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
            // A controller can survive a body replacement. Invalidating the
            // active token prevents an old deferred call from targeting it.
            _activeJobToken = 0;
            _activeJobName = null;
            _hasSpawnedBot = true;
            _actions.Bind(_body, localPlayer, now);

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
            _actions.Release();
        }
    }

    private void ResetAfterBotDestroyed()
    {
        _actions.Release();
        _body = null;
        _hasSpawnedBot = false;
        _activeJobToken = 0;
        _activeJobName = null;
        _verificationLog.Reset();
        Plugin.Logger.LogInfo("[RAMBLERS] Companion left the scene; controller state reset.");
    }

    private void OnDestroy()
    {
        if (_activeController == this)
            _activeController = null;
        _activeJobToken = 0;
        _activeJobName = null;

        try
        {
            _actions.StopQuietly();
            _actions.Release();
        }
        catch
        {
            // The network object may already be gone during scene shutdown.
        }
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
