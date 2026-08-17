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

    private static CompanionController _activeController;

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
        _verificationLog.Reset();
        Plugin.Logger.LogInfo("[RAMBLERS] Companion left the scene; controller state reset.");
    }

    private void OnDestroy()
    {
        if (_activeController == this)
            _activeController = null;

        try
        {
            _actions.StopQuietly();
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
