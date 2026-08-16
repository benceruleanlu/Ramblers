using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Dissonance.Integrations.MirrorIgnorance;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using LobbyNetworking;
using Mirror;
using UnityEngine;

namespace BigWalkBotProbe;

[BepInPlugin(Guid, Name, Version)]
public sealed class Plugin : BasePlugin
{
    public const string Guid = "local.bigwalk.botprobe";
    public const string Name = "Big Walk Bot Feasibility Probe";
    public const string Version = "0.3.2";

    internal static ManualLogSource Logger = null;

    public override void Load()
    {
        Logger = Log;
        ClassInjector.RegisterTypeInIl2Cpp<ProbeRunner>();

        var harmony = new Harmony(Guid);
        harmony.PatchAll(typeof(PlayerNetworkingStartPatch));
        harmony.PatchAll(typeof(HouseNetworkTransformIsOwnedPatch));
        harmony.PatchAll(typeof(HouseNetworkTransformIsRestingPatch));

        AddComponent<ProbeRunner>();
        Logger.LogInfo(
            $"[BOT-PROBE] Loaded version {Version}. Waiting for a host session and local player.");
    }
}

internal static class ProbeIdentity
{
    public const string ObjectName = "__NitrogenHostBotProbe";
    public const string NetworkIdentifier = "local-bot:nitrogen";

    public static bool IsBot(PlayerNetworking networking)
    {
        var gameObject = networking == null ? null : networking.gameObject;
        return networking != null &&
               (networking.Networkidentifier == NetworkIdentifier ||
                (gameObject != null && gameObject.name == ObjectName));
    }
}

[HarmonyPatch(typeof(PlayerNetworking), "Start")]
internal static class PlayerNetworkingStartPatch
{
    private static bool Prefix(PlayerNetworking __instance)
    {
        if (!ProbeIdentity.IsBot(__instance))
            return true;

        // The stock server-side Start path assumes connectionToClient and its
        // authenticationData are non-null. A server-owned bot intentionally has
        // neither, so identity is initialized before NetworkServer.Spawn instead.
        Plugin.Logger.LogInfo(
            "[BOT-PROBE] Bypassed connection-dependent PlayerNetworking.Start for bot.");
        return false;
    }
}

[HarmonyPatch(typeof(HouseNetworkTransform), "get_isOwned")]
internal static class HouseNetworkTransformIsOwnedPatch
{
    private static void Postfix(HouseNetworkTransform __instance, ref bool __result)
    {
        if (__result || __instance == null)
            return;

        var networking = __instance.GetComponent<PlayerNetworking>();
        if (ProbeIdentity.IsBot(networking))
            __result = true;
    }
}

[HarmonyPatch(typeof(HouseNetworkTransform), "get_IsRestingForPlayerMovement")]
internal static class HouseNetworkTransformIsRestingPatch
{
    private static void Postfix(HouseNetworkTransform __instance, ref bool __result)
    {
        if (!__result || __instance == null)
            return;

        // A connectionless player never receives the normal client interpolation
        // goal, so the stock getter reports it as permanently resting and
        // PlayerMover zeros the otherwise valid controlsVelocity. The bot is
        // server-owned and driven directly by the host, so that remote-only gate
        // does not apply.
        var networking = __instance.GetComponent<PlayerNetworking>();
        if (ProbeIdentity.IsBot(networking))
            __result = false;
    }
}

internal sealed class ProbeRunner : MonoBehaviour
{
    // This first locomotion experiment is deliberately a short local traverse,
    // not a navigation claim. The staging room only has about two metres of
    // clearance along the host's initial facing direction.
    private const float AutomaticGoalDistance = 1.5f;
    private const float WalkStartDelay = 4f;
    private const float ArrivalTolerance = 0.65f;
    private const float SlowdownDistance = 1.75f;
    private const float MinimumMovementIntent = 0.35f;
    private const float ProgressEpsilon = 0.12f;
    private const float StuckTimeout = 2f;
    private const float DetourDuration = 1.25f;
    private const float DetourAngle = 55f;
    private const float WalkTimeout = 30f;
    private const float StatusLogInterval = 1f;
    private const int MaximumRecoveryAttempts = 4;

    private enum WalkState
    {
        Waiting,
        Walking,
        Detouring,
        Arrived,
        Failed
    }

    private GameObject _bot;
    private PlayerCharacter _botCharacter;
    private PlayerNetworking _botNetworking;
    private PlayerCharacter _humanAtSpawn;
    private float _nextPoll;
    private float _verifyAt;
    private float _walkAt;
    private bool _verificationLogged;
    private bool _hasSpawnedBot;

    private WalkState _walkState = WalkState.Waiting;
    private Vector3 _walkGoal;
    private Vector3 _lastMovementIntent;
    private float _walkStartedAt;
    private float _bestDistance;
    private float _lastProgressAt;
    private float _detourUntil;
    private float _nextStatusLog;
    private int _recoveryAttempts;
    private int _detourSign = 1;

    public ProbeRunner(IntPtr pointer) : base(pointer)
    {
    }

    private void Update()
    {
        if (_bot != null)
        {
            if (!_verificationLogged && Time.realtimeSinceStartup >= _verifyAt)
                LogVerification();
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
        if (_bot == null || _botCharacter == null || _botNetworking == null)
            return;

        try
        {
            TickAutonomousWalk();
        }
        catch (Exception exception)
        {
            FailWalk($"motor exception: {exception}");
        }
    }

    private void TrySpawn(NetworkManager manager, PlayerCharacter localPlayer)
    {
        try
        {
            var position = localPlayer.transform.position
                         + localPlayer.transform.right * 2f
                         + Vector3.up * 0.25f;

            _bot = UnityEngine.Object.Instantiate(
                manager.playerPrefab,
                position,
                localPlayer.transform.rotation);
            _bot.name = ProbeIdentity.ObjectName;

            var playerCharacter = _bot.GetComponent<PlayerCharacter>();
            var playerNetworking = _bot.GetComponent<PlayerNetworking>();
            var networkIdentity = _bot.GetComponent<NetworkIdentity>();
            var networkTransform = _bot.GetComponent<HouseNetworkTransform>();
            var voiceIdentity = _bot.GetComponent<MirrorIgnorancePlayer>();

            if (playerCharacter == null || playerNetworking == null ||
                networkIdentity == null || networkTransform == null ||
                playerCharacter.mover == null)
            {
                throw new InvalidOperationException(
                    "The configured playerPrefab is missing a required player, mover, or network component.");
            }

            playerCharacter.mover.applyVelocityForRemotePlayers = true;
            SetSyntheticIdentity(playerNetworking, voiceIdentity);
            NetworkServer.Spawn(_bot);

            _botCharacter = playerCharacter;
            _botNetworking = playerNetworking;
            _humanAtSpawn = localPlayer;
            _hasSpawnedBot = true;
            _walkState = WalkState.Waiting;
            _lastMovementIntent = Vector3.zero;
            _recoveryAttempts = 0;
            _detourSign = 1;

            _verifyAt = Time.realtimeSinceStartup + 2f;
            _walkAt = Time.realtimeSinceStartup + WalkStartDelay;
            Plugin.Logger.LogInfo(
                $"[BOT-PROBE] Spawn requested: netId={networkIdentity.netId}, " +
                $"connectionToClient={(networkIdentity.connectionToClient == null ? "null" : "non-null")}, " +
                $"position={position}.");
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogError($"[BOT-PROBE] Spawn failed: {exception}");
            if (_bot != null)
                UnityEngine.Object.Destroy(_bot);
            _bot = null;
        }
    }

    private void TickAutonomousWalk()
    {
        if (_walkState == WalkState.Arrived || _walkState == WalkState.Failed)
            return;

        var now = Time.realtimeSinceStartup;
        if (_walkState == WalkState.Waiting)
        {
            if (now < _walkAt)
                return;

            BeginAutonomousWalk(now);
            if (_walkState != WalkState.Walking)
                return;
        }

        if (!NetworkServer.active || !_botNetworking.isServer || _botNetworking.isLocalPlayer)
        {
            FailWalk(
                $"authority invariant failed: serverActive={NetworkServer.active}, " +
                $"isServer={_botNetworking.isServer}, isLocalPlayer={_botNetworking.isLocalPlayer}");
            return;
        }

        var position = _bot.transform.position;
        var toGoal = _walkGoal - position;
        toGoal.y = 0f;
        var distance = toGoal.magnitude;

        if (distance <= ArrivalTolerance)
        {
            CompleteWalk(now, distance);
            return;
        }

        if (now - _walkStartedAt >= WalkTimeout)
        {
            FailWalk($"timed out after {WalkTimeout:F1}s at distance={distance:F2}");
            return;
        }

        if (distance <= _bestDistance - ProgressEpsilon)
        {
            _bestDistance = distance;
            _lastProgressAt = now;
        }

        Vector3 direction;
        if (_walkState == WalkState.Detouring)
        {
            if (now >= _detourUntil)
            {
                _walkState = WalkState.Walking;
                _bestDistance = distance;
                _lastProgressAt = now;
                Plugin.Logger.LogInfo(
                    $"[BOT-WALK] Detour {_recoveryAttempts} complete; resuming direct steering.");
            }
        }
        else if (now - _lastProgressAt >= StuckTimeout)
        {
            if (_recoveryAttempts >= MaximumRecoveryAttempts)
            {
                FailWalk(
                    $"stuck after {MaximumRecoveryAttempts} non-teleporting detour attempts; " +
                    $"distance={distance:F2}");
                return;
            }

            BeginDetour(now, distance);
        }

        direction = toGoal / distance;
        if (_walkState == WalkState.Detouring)
        {
            direction = Quaternion.AngleAxis(
                _detourSign * DetourAngle,
                Vector3.up) * direction;
        }

        var intentMagnitude = _walkState == WalkState.Detouring
            ? 1f
            : Mathf.Clamp(distance / SlowdownDistance, MinimumMovementIntent, 1f);
        SetMovementIntent(direction * intentMagnitude);

        if (now >= _nextStatusLog)
        {
            LogWalkStatus(now, distance);
            _nextStatusLog = now + StatusLogInterval;
        }
    }

    private void BeginAutonomousWalk(float now)
    {
        var human = WorldManager.localPlayerCharacter;
        if (human == null)
            human = _humanAtSpawn;

        if (human == null || human.gameObject == _bot)
        {
            FailWalk("local human player was unavailable when selecting the automatic goal");
            return;
        }

        var forward = human.transform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.01f)
            forward = Vector3.forward;
        else
            forward.Normalize();

        var start = _bot.transform.position;
        _walkGoal = start + forward * AutomaticGoalDistance;
        _walkGoal.y = start.y;

        var startDelta = _walkGoal - start;
        startDelta.y = 0f;

        _walkState = WalkState.Walking;
        _walkStartedAt = now;
        _bestDistance = startDelta.magnitude;
        _lastProgressAt = now;
        _nextStatusLog = now;

        Plugin.Logger.LogInfo(
            "[BOT-WALK] START " +
            $"start={start}, goal={_walkGoal}, distance={_bestDistance:F2}, " +
            $"goalRule=botStart+hostForward*{AutomaticGoalDistance:F1}m, " +
            $"applyVelocityForRemotePlayers={_botCharacter.mover.applyVelocityForRemotePlayers}, " +
            $"isServer={_botNetworking.isServer}, isLocalPlayer={_botNetworking.isLocalPlayer}.");
    }

    private void BeginDetour(float now, float distance)
    {
        _recoveryAttempts++;
        _detourSign = (_recoveryAttempts % 2 == 1) ? 1 : -1;
        _detourUntil = now + DetourDuration;
        _walkState = WalkState.Detouring;
        _bestDistance = distance;
        _lastProgressAt = now;

        Plugin.Logger.LogWarning(
            "[BOT-WALK] STUCK " +
            $"distance={distance:F2}, recovery={_recoveryAttempts}/{MaximumRecoveryAttempts}, " +
            $"detourAngle={_detourSign * DetourAngle:F0}, detourSeconds={DetourDuration:F2}.");
    }

    private void CompleteWalk(float now, float distance)
    {
        _walkState = WalkState.Arrived;
        SetMovementIntent(Vector3.zero);

        var human = WorldManager.localPlayerCharacter;
        var hostStillLocal = human != null &&
                             human.gameObject != _bot &&
                             human.playerNetworking != null &&
                             human.playerNetworking.isLocalPlayer;

        Plugin.Logger.LogInfo(
            "[BOT-WALK] ARRIVED " +
            $"position={_bot.transform.position}, goal={_walkGoal}, distance={distance:F2}, " +
            $"elapsed={now - _walkStartedAt:F2}s, recoveries={_recoveryAttempts}, " +
            $"botIsLocalPlayer={_botNetworking.isLocalPlayer}, hostStillLocal={hostStillLocal}.");
    }

    private void FailWalk(string reason)
    {
        if (_walkState == WalkState.Failed)
            return;

        _walkState = WalkState.Failed;
        try
        {
            SetMovementIntent(Vector3.zero);
        }
        catch
        {
            _lastMovementIntent = Vector3.zero;
        }

        Plugin.Logger.LogError($"[BOT-WALK] FAILED {reason}");
    }

    private void SetMovementIntent(Vector3 worldMovementIntent)
    {
        _botNetworking.NetworkcontrolsVelocity = worldMovementIntent;
        _lastMovementIntent = worldMovementIntent;
    }

    private void LogWalkStatus(float now, float distance)
    {
        var rigidbodyVelocity = _botCharacter.rb == null
            ? Vector3.zero
            : _botCharacter.rb.linearVelocity;
        var human = WorldManager.localPlayerCharacter;
        var hostStillLocal = human != null &&
                             human.gameObject != _bot &&
                             human.playerNetworking != null &&
                             human.playerNetworking.isLocalPlayer;

        Plugin.Logger.LogInfo(
            "[BOT-WALK] STATUS " +
            $"state={_walkState}, elapsed={now - _walkStartedAt:F2}, " +
            $"position={_bot.transform.position}, distance={distance:F2}, " +
            $"intent={_lastMovementIntent}, rigidbodyVelocity={rigidbodyVelocity}, " +
            $"networkIntent={_botNetworking.controlsVelocity}, " +
            $"botIsLocalPlayer={_botNetworking.isLocalPlayer}, hostStillLocal={hostStillLocal}.");
    }

    private void ResetAfterBotDestroyed()
    {
        _botCharacter = null;
        _botNetworking = null;
        _humanAtSpawn = null;
        _hasSpawnedBot = false;
        _verificationLogged = false;
        _walkState = WalkState.Waiting;
        _lastMovementIntent = Vector3.zero;
        _recoveryAttempts = 0;
        Plugin.Logger.LogInfo("[BOT-PROBE] Bot left the scene; probe state reset.");
    }

    private void OnDestroy()
    {
        if (_botNetworking == null || !NetworkServer.active)
            return;

        try
        {
            SetMovementIntent(Vector3.zero);
        }
        catch
        {
            // The network object may already be gone during scene shutdown.
        }
    }

    private static void SetSyntheticIdentity(
        PlayerNetworking networking,
        MirrorIgnorancePlayer voiceIdentity)
    {
        var username = "Nitrogen";
        var identifier = ProbeIdentity.NetworkIdentifier;
        var moderationName = "Nitrogen";
        var epicUserId = string.Empty;
        ulong platformUserId = 0;
        var isHost = false;

        networking.Networkusername = username;
        networking.Networkidentifier = identifier;
        networking.NetworkmoderationName = moderationName;
        networking.NetworkuserPlatformId = platformUserId;
        networking.NetworkepicUserId = epicUserId;
        networking.NetworkisHost = isHost;

        if (voiceIdentity != null)
        {
            var voicePlayerId = "NitrogenHostBot";
            voiceIdentity.Network_playerId = voicePlayerId;
        }
    }

    private void LogVerification()
    {
        _verificationLogged = true;

        if (_bot == null)
            return;

        var playerCharacter = _bot.GetComponent<PlayerCharacter>();
        var networking = _bot.GetComponent<PlayerNetworking>();
        var identity = _bot.GetComponent<NetworkIdentity>();
        var networkTransform = _bot.GetComponent<HouseNetworkTransform>();
        var voiceIdentity = _bot.GetComponent<MirrorIgnorancePlayer>();

        var registeredPlayers = PlayerCharacter.allPlayerCharacters == null
            ? -1
            : PlayerCharacter.allPlayerCharacters.Count;

        Plugin.Logger.LogInfo(
            "[BOT-PROBE] VERIFY " +
            $"probeVersion={Plugin.Version}, " +
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
            $"movementResting={networkTransform?.IsRestingForPlayerMovement}, " +
            $"playerCharacterPresent={playerCharacter != null}.");
    }
}
