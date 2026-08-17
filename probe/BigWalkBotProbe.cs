using System;
using BepInEx;
using BepInEx.Configuration;
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
    public const string Name = "Ramblers";
    public const string Version = "0.7.2";

    internal static ManualLogSource Logger = null;
    internal static ConfigEntry<bool> EnableRealtimeAgent = null;
    internal static ConfigEntry<string> OpenAIRealtimeModel = null;

    public override void Load()
    {
        Logger = Log;
        EnableRealtimeAgent = Config.Bind(
            "OpenAI",
            "Enabled",
            true,
            "Connect to the OpenAI Realtime API when OPENAI_API_KEY is present. " +
            "Listening follows Big Walk's voice controls and direct-voice audibility.");
        OpenAIRealtimeModel = Config.Bind(
            "OpenAI",
            "Model",
            "gpt-realtime-2.1",
            "Realtime model ID. Keep the documented default unless deliberately testing another model.");
        ClassInjector.RegisterTypeInIl2Cpp<BotController>();
        ClassInjector.RegisterTypeInIl2Cpp<RealtimeAgentBridge>();

        var harmony = new Harmony(Guid);
        harmony.PatchAll(typeof(PlayerNetworkingStartPatch));
        harmony.PatchAll(typeof(HouseNetworkTransformIsOwnedPatch));
        harmony.PatchAll(typeof(HouseNetworkTransformIsRestingPatch));

        AddComponent<BotController>();
        AddComponent<RealtimeAgentBridge>();
        Logger.LogInfo(
            $"[RAMBLERS] Loaded version {Version}. Waiting for a host session and local player.");
    }
}

internal static class CompanionIdentity
{
    public const string ObjectName = "__RamblersHostCompanion";
    public const string NetworkIdentifier = "ramblers:companion:rambler";

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
        if (!CompanionIdentity.IsBot(__instance))
            return true;

        // The stock server-side Start path assumes connectionToClient and its
        // authenticationData are non-null. A server-owned bot intentionally has
        // neither, so identity is initialized before NetworkServer.Spawn instead.
        Plugin.Logger.LogInfo(
            "[RAMBLERS] Bypassed connection-dependent PlayerNetworking.Start for companion.");
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
        if (CompanionIdentity.IsBot(networking))
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
        if (CompanionIdentity.IsBot(networking))
            __result = false;
    }
}

internal sealed class BotController : MonoBehaviour
{
    private const float NavigationInterval = 0.1f;
    private const float TrailSampleInterval = 0.1f;
    private const float BreadcrumbSpacing = 0.65f;
    private const float BreadcrumbArrivalTolerance = 0.8f;
    private const float FollowDistance = 2.25f;
    private const float ResumeDistance = 2.5f;
    private const float TrailResetDistance = 8f;
    private const float SlowdownDistance = 2.25f;
    private const float MinimumMovementIntent = 0.35f;
    private const float ObstacleProbeDistance = 1.5f;
    private const float MinimumClearance = 0.7f;
    private const float AvoidanceSideHold = 0.6f;
    private const float StuckObservationWindow = 2.5f;
    private const float StuckMovementThreshold = 0.15f;
    private const float StatusLogInterval = 1f;
    private const int MaximumBreadcrumbs = 256;

    private static readonly float[] SteeringAngles =
    {
        0f,
        25f,
        -25f,
        50f,
        -50f,
        75f,
        -75f,
        95f,
        -95f
    };

    private enum FollowState
    {
        Idle,
        Waiting,
        Following,
        Holding,
        Blocked,
        Failed
    }

    private GameObject _bot;
    private PlayerCharacter _botCharacter;
    private PlayerNetworking _botNetworking;
    private PlayerCharacter _humanAtSpawn;
    private float _nextPoll;
    private float _verifyAt;
    private float _followAt;
    private float _nextNavigationTick;
    private float _nextTrailSample;
    private bool _verificationLogged;
    private bool _hasSpawnedBot;
    private bool _followRequested;

    private FollowState _followState = FollowState.Idle;
    private readonly Vector3[] _breadcrumbs = new Vector3[MaximumBreadcrumbs];
    private int _breadcrumbHead;
    private int _breadcrumbCount;
    private Vector3 _lastHumanTrailPosition;
    private bool _hasHumanTrailPosition;
    private Vector3 _lastMovementIntent;
    private Vector3 _currentTarget;
    private float _followStartedAt;
    private float _nextStatusLog;
    private int _avoidanceSign;
    private float _avoidanceSignUntil;
    private float _lastSteeringAngle;
    private float _lastClearance;
    private bool _lastDirectPathBlocked;
    private Vector3 _progressAnchor;
    private float _progressWindowStartedAt;
    private bool _stuckWarningIssued;
    private static BotController _activeController;

    public BotController(IntPtr pointer) : base(pointer)
    {
    }

    private void Awake()
    {
        _activeController = this;
    }

    internal static string ExecuteAgentTool(string toolName, string mode)
    {
        var controller = _activeController;
        if (controller == null)
            return "{\"ok\":false,\"error\":\"bot_controller_unavailable\"}";

        if (!string.Equals(toolName, "set_follow_mode", StringComparison.Ordinal))
            return "{\"ok\":false,\"error\":\"unknown_tool\"}";

        return controller.SetFollowMode(mode);
    }

    internal static bool TryGetVoiceParticipants(
        out PlayerCharacter human,
        out PlayerCharacter bot)
    {
        var controller = _activeController;
        human = WorldManager.localPlayerCharacter;
        bot = controller == null ? null : controller._botCharacter;
        return human != null && bot != null && controller._bot != null;
    }

    private void Update()
    {
        if (_bot != null)
        {
            if (!_verificationLogged && Time.realtimeSinceStartup >= _verifyAt)
                LogVerification();

            if (Time.realtimeSinceStartup >= _nextTrailSample)
            {
                _nextTrailSample = Time.realtimeSinceStartup + TrailSampleInterval;
                RecordHumanTrail();
            }
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
            TickFollowPlayer();
        }
        catch (Exception exception)
        {
            FailFollow($"navigation exception: {exception}");
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
            _bot.name = CompanionIdentity.ObjectName;

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
            _followRequested = false;
            _followState = FollowState.Idle;
            _lastMovementIntent = Vector3.zero;
            _avoidanceSign = 0;
            _lastSteeringAngle = 0f;
            _lastClearance = ObstacleProbeDistance;
            _lastDirectPathBlocked = false;
            _stuckWarningIssued = false;
            ClearBreadcrumbs();
            AddBreadcrumb(localPlayer.transform.position);

            _verifyAt = Time.realtimeSinceStartup + 2f;
            _followAt = float.PositiveInfinity;
            _nextNavigationTick = _followAt;
            _nextTrailSample = Time.realtimeSinceStartup + TrailSampleInterval;
            Plugin.Logger.LogInfo(
                $"[RAMBLERS] Spawn requested: netId={networkIdentity.netId}, " +
                $"connectionToClient={(networkIdentity.connectionToClient == null ? "null" : "non-null")}, " +
                $"position={position}.");
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogError($"[RAMBLERS] Spawn failed: {exception}");
            if (_bot != null)
                UnityEngine.Object.Destroy(_bot);
            _bot = null;
        }
    }

    private void TickFollowPlayer()
    {
        if (!_followRequested)
        {
            if (_followState != FollowState.Idle || _lastMovementIntent.sqrMagnitude > 0f)
                StopForState(FollowState.Idle, Time.realtimeSinceStartup);
            return;
        }

        if (_followState == FollowState.Failed)
            return;

        var now = Time.realtimeSinceStartup;
        if (now < _followAt)
            return;

        if (!NetworkServer.active || !_botNetworking.isServer || _botNetworking.isLocalPlayer)
        {
            FailFollow(
                $"authority invariant failed: serverActive={NetworkServer.active}, " +
                $"isServer={_botNetworking.isServer}, isLocalPlayer={_botNetworking.isLocalPlayer}");
            return;
        }

        if (_followState == FollowState.Waiting)
            BeginFollowing(now);

        if (now < _nextNavigationTick)
            return;

        _nextNavigationTick = now + NavigationInterval;
        NavigateTowardHuman(now);
    }

    private void BeginFollowing(float now)
    {
        var human = GetHumanPlayer();
        if (human == null)
        {
            FailFollow("local human player was unavailable when follow began");
            return;
        }

        _followState = FollowState.Following;
        _followStartedAt = now;
        _nextStatusLog = now;
        ResetProgressObservation(now);

        var bodyCollider = _botCharacter.collision == null
            ? null
            : _botCharacter.collision.bodyCollider;
        var obstacleMask = GetObstacleMask();
        Plugin.Logger.LogInfo(
            "[BOT-FOLLOW] START " +
            $"bot={_bot.transform.position}, human={human.transform.position}, " +
            $"followDistance={FollowDistance:F2}, breadcrumbSpacing={BreadcrumbSpacing:F2}, " +
            $"navigationHz={1f / NavigationInterval:F0}, obstacleMask={obstacleMask}, " +
            $"bodyRadius={(bodyCollider == null ? -1f : bodyCollider.radius):F2}, " +
            $"bodyHeight={(bodyCollider == null ? -1f : bodyCollider.height):F2}.");

    }

    private string SetFollowMode(string mode)
    {
        if (_bot == null || _botNetworking == null || !_hasSpawnedBot)
            return "{\"ok\":false,\"error\":\"bot_not_spawned\"}";

        var now = Time.realtimeSinceStartup;
        if (string.Equals(mode, "follow", StringComparison.OrdinalIgnoreCase))
        {
            var human = GetHumanPlayer();
            if (human == null)
                return "{\"ok\":false,\"error\":\"human_player_unavailable\"}";

            _followRequested = true;
            _followState = FollowState.Waiting;
            _followAt = now;
            _nextNavigationTick = now;
            ClearBreadcrumbs();
            AddBreadcrumb(human.transform.position);
            ResetProgressObservation(now);
            Plugin.Logger.LogInfo("[BOT-AGENT] TOOL set_follow_mode mode=follow accepted.");
            return "{\"ok\":true,\"mode\":\"follow\",\"status\":\"started\"}";
        }

        if (string.Equals(mode, "stay", StringComparison.OrdinalIgnoreCase))
        {
            _followRequested = false;
            _followAt = float.PositiveInfinity;
            StopForState(FollowState.Idle, now);
            Plugin.Logger.LogInfo("[BOT-AGENT] TOOL set_follow_mode mode=stay accepted.");
            return "{\"ok\":true,\"mode\":\"stay\",\"status\":\"stopped\"}";
        }

        return "{\"ok\":false,\"error\":\"invalid_mode\"}";
    }

    private void NavigateTowardHuman(float now)
    {
        var human = GetHumanPlayer();
        if (human == null)
        {
            StopForState(FollowState.Blocked, now);
            Plugin.Logger.LogWarning("[BOT-FOLLOW] BLOCKED local human player is unavailable.");
            return;
        }

        var botPosition = _bot.transform.position;
        var humanDistance = HorizontalDistance(botPosition, human.transform.position);
        if (humanDistance <= FollowDistance ||
            (_followState == FollowState.Holding && humanDistance < ResumeDistance))
        {
            StopForState(FollowState.Holding, now);
            LogFollowStatusIfDue(now, humanDistance, 0f);
            return;
        }

        RemoveReachedBreadcrumbs(botPosition);
        if (_breadcrumbCount == 0)
            AddBreadcrumb(human.transform.position);

        _currentTarget = PeekBreadcrumb();
        var toTarget = _currentTarget - botPosition;
        toTarget.y = 0f;
        var targetDistance = toTarget.magnitude;
        if (targetDistance < 0.001f)
        {
            StopForState(FollowState.Holding, now);
            return;
        }

        var desiredDirection = toTarget / targetDistance;
        Vector3 steeringDirection;
        float steeringAngle;
        float clearance;
        bool directBlocked;
        if (!TryChooseSteering(
                desiredDirection,
                now,
                out steeringDirection,
                out steeringAngle,
                out clearance,
                out directBlocked))
        {
            var stateChanged = _followState != FollowState.Blocked;
            StopForState(FollowState.Blocked, now);
            _lastDirectPathBlocked = true;
            _lastClearance = clearance;
            if (stateChanged)
            {
                Plugin.Logger.LogWarning(
                    "[BOT-FOLLOW] BLOCKED " +
                    $"target={_currentTarget}, targetDistance={targetDistance:F2}, " +
                    $"humanDistance={humanDistance:F2}; no steering candidate had " +
                    $"{MinimumClearance:F2}m clearance. No recovery or teleport attempted.");
            }
            LogFollowStatusIfDue(now, humanDistance, targetDistance);
            return;
        }

        var previousState = _followState;
        var previousAngle = _lastSteeringAngle;
        _followState = FollowState.Following;
        _lastDirectPathBlocked = directBlocked;
        _lastSteeringAngle = steeringAngle;
        _lastClearance = clearance;

        var intentMagnitude = Mathf.Clamp(
            Mathf.Min(targetDistance, humanDistance - FollowDistance) / SlowdownDistance,
            MinimumMovementIntent,
            1f);
        var clearanceScale = Mathf.Clamp01((clearance - 0.15f) / MinimumClearance);
        intentMagnitude *= Mathf.Max(MinimumMovementIntent, clearanceScale);
        SetMovementIntent(steeringDirection * intentMagnitude);

        if (directBlocked &&
            (previousState != FollowState.Following ||
             Mathf.Abs(previousAngle - steeringAngle) >= 1f))
        {
            Plugin.Logger.LogInfo(
                "[BOT-FOLLOW] AVOID " +
                $"steeringAngle={steeringAngle:F0}, clearance={clearance:F2}, " +
                $"target={_currentTarget}, targetDistance={targetDistance:F2}.");
        }
        else if (!directBlocked && previousState == FollowState.Blocked)
        {
            Plugin.Logger.LogInfo("[BOT-FOLLOW] Path clear; resuming breadcrumb follow.");
        }

        ObservePossibleStuck(now);
        LogFollowStatusIfDue(now, humanDistance, targetDistance);
    }

    private bool TryChooseSteering(
        Vector3 desiredDirection,
        float now,
        out Vector3 steeringDirection,
        out float steeringAngle,
        out float clearance,
        out bool directBlocked)
    {
        steeringDirection = Vector3.zero;
        steeringAngle = 0f;
        clearance = 0f;

        var directClearance = MeasureClearance(desiredDirection);
        directBlocked = directClearance < MinimumClearance;
        if (!directBlocked)
        {
            steeringDirection = desiredDirection;
            clearance = directClearance;
            _avoidanceSign = 0;
            return true;
        }

        var bestScore = float.NegativeInfinity;
        for (var index = 1; index < SteeringAngles.Length; index++)
        {
            var angle = SteeringAngles[index];
            var candidate = Quaternion.AngleAxis(angle, Vector3.up) * desiredDirection;
            var candidateClearance = MeasureClearance(candidate);
            if (candidateClearance < MinimumClearance)
                continue;

            var candidateSign = angle > 0f ? 1 : -1;
            var turnPenalty = Mathf.Abs(angle) * 0.004f;
            var sideBonus = now < _avoidanceSignUntil && candidateSign == _avoidanceSign
                ? 0.35f
                : 0f;
            var score = candidateClearance - turnPenalty + sideBonus;
            if (score <= bestScore)
                continue;

            bestScore = score;
            steeringDirection = candidate;
            steeringAngle = angle;
            clearance = candidateClearance;
        }

        if (bestScore == float.NegativeInfinity)
        {
            clearance = directClearance;
            return false;
        }

        _avoidanceSign = steeringAngle > 0f ? 1 : -1;
        _avoidanceSignUntil = now + AvoidanceSideHold;
        return true;
    }

    private float MeasureClearance(Vector3 direction)
    {
        return MeasureCharacterClearance(
            _botCharacter,
            direction,
            ObstacleProbeDistance);
    }

    private float MeasureCharacterClearance(
        PlayerCharacter character,
        Vector3 direction,
        float probeDistance)
    {
        if (character.rb == null)
            return probeDistance;

        RaycastHit hit;
        if (!character.rb.SweepTest(
            direction,
            out hit,
            probeDistance,
            QueryTriggerInteraction.Ignore))
        {
            return probeDistance;
        }

        return hit.distance;
    }

    private int GetObstacleMask()
    {
        return GetObstacleMask(_botCharacter);
    }

    private static int GetObstacleMask(PlayerCharacter character)
    {
        if (character.ground != null && character.ground.layerMask.value != 0)
            return character.ground.layerMask.value;

        return Physics.DefaultRaycastLayers;
    }

    private void RecordHumanTrail()
    {
        var human = GetHumanPlayer();
        if (human == null)
            return;

        var position = human.transform.position;
        if (!_hasHumanTrailPosition)
        {
            AddBreadcrumb(position);
            return;
        }

        var distance = Vector3.Distance(position, _lastHumanTrailPosition);
        if (distance >= TrailResetDistance)
        {
            ClearBreadcrumbs();
            AddBreadcrumb(position);
            Plugin.Logger.LogWarning(
                "[BOT-FOLLOW] TRAIL_RESET " +
                $"human moved {distance:F2}m between samples; refusing to invent a traversable segment.");
            return;
        }

        if (distance >= BreadcrumbSpacing)
            AddBreadcrumb(position);
    }

    private void AddBreadcrumb(Vector3 position)
    {
        if (_breadcrumbCount == MaximumBreadcrumbs)
        {
            _breadcrumbHead = (_breadcrumbHead + 1) % MaximumBreadcrumbs;
            _breadcrumbCount--;
        }

        var tail = (_breadcrumbHead + _breadcrumbCount) % MaximumBreadcrumbs;
        _breadcrumbs[tail] = position;
        _breadcrumbCount++;
        _lastHumanTrailPosition = position;
        _hasHumanTrailPosition = true;
    }

    private Vector3 PeekBreadcrumb()
    {
        return _breadcrumbs[_breadcrumbHead];
    }

    private void RemoveReachedBreadcrumbs(Vector3 botPosition)
    {
        while (_breadcrumbCount > 0 &&
               HorizontalDistance(botPosition, PeekBreadcrumb()) <= BreadcrumbArrivalTolerance)
        {
            _breadcrumbHead = (_breadcrumbHead + 1) % MaximumBreadcrumbs;
            _breadcrumbCount--;
        }
    }

    private void ClearBreadcrumbs()
    {
        _breadcrumbHead = 0;
        _breadcrumbCount = 0;
        _hasHumanTrailPosition = false;
    }

    private PlayerCharacter GetHumanPlayer()
    {
        var human = WorldManager.localPlayerCharacter;
        if (human == null)
            human = _humanAtSpawn;

        if (human == null || human.gameObject == _bot)
            return null;

        return human;
    }

    private static float HorizontalDistance(Vector3 from, Vector3 to)
    {
        var delta = to - from;
        delta.y = 0f;
        return delta.magnitude;
    }

    private void StopForState(FollowState state, float now)
    {
        if (_lastMovementIntent.sqrMagnitude > 0f)
            SetMovementIntent(Vector3.zero);
        _followState = state;
        ResetProgressObservation(now);
    }

    private void ObservePossibleStuck(float now)
    {
        if (_lastMovementIntent.magnitude < MinimumMovementIntent)
        {
            ResetProgressObservation(now);
            return;
        }

        if (now - _progressWindowStartedAt < StuckObservationWindow)
            return;

        var movement = HorizontalDistance(_progressAnchor, _bot.transform.position);
        if (movement < StuckMovementThreshold)
        {
            if (!_stuckWarningIssued)
            {
                _stuckWarningIssued = true;
                Plugin.Logger.LogWarning(
                    "[BOT-FOLLOW] POSSIBLY_STUCK " +
                    $"moved={movement:F2}m in {StuckObservationWindow:F1}s while intent=" +
                    $"{_lastMovementIntent.magnitude:F2}. Detection only; no recovery attempted.");
            }
        }
        else
        {
            _stuckWarningIssued = false;
        }

        _progressAnchor = _bot.transform.position;
        _progressWindowStartedAt = now;
    }

    private void ResetProgressObservation(float now)
    {
        _progressAnchor = _bot == null ? Vector3.zero : _bot.transform.position;
        _progressWindowStartedAt = now;
        _stuckWarningIssued = false;
    }

    private void FailFollow(string reason)
    {
        if (_followState == FollowState.Failed)
            return;

        _followState = FollowState.Failed;
        try
        {
            SetMovementIntent(Vector3.zero);
        }
        catch
        {
            _lastMovementIntent = Vector3.zero;
        }

        Plugin.Logger.LogError($"[BOT-FOLLOW] FAILED {reason}");
    }

    private void SetMovementIntent(Vector3 worldMovementIntent)
    {
        _botNetworking.NetworkcontrolsVelocity = worldMovementIntent;
        _lastMovementIntent = worldMovementIntent;
    }

    private void LogFollowStatusIfDue(
        float now,
        float humanDistance,
        float targetDistance)
    {
        if (now < _nextStatusLog)
            return;

        _nextStatusLog = now + StatusLogInterval;
        var rigidbodyVelocity = _botCharacter.rb == null
            ? Vector3.zero
            : _botCharacter.rb.linearVelocity;
        var human = WorldManager.localPlayerCharacter;
        var hostStillLocal = human != null &&
                             human.gameObject != _bot &&
                             human.playerNetworking != null &&
                             human.playerNetworking.isLocalPlayer;

        Plugin.Logger.LogInfo(
            "[BOT-FOLLOW] STATUS " +
            $"state={_followState}, elapsed={now - _followStartedAt:F2}, " +
            $"position={_bot.transform.position}, humanDistance={humanDistance:F2}, " +
            $"target={_currentTarget}, targetDistance={targetDistance:F2}, " +
            $"breadcrumbs={_breadcrumbCount}, directBlocked={_lastDirectPathBlocked}, " +
            $"steeringAngle={_lastSteeringAngle:F0}, clearance={_lastClearance:F2}, " +
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
        _followRequested = false;
        _followState = FollowState.Idle;
        _lastMovementIntent = Vector3.zero;
        _avoidanceSign = 0;
        ClearBreadcrumbs();
        Plugin.Logger.LogInfo("[RAMBLERS] Companion left the scene; controller state reset.");
    }

    private void OnDestroy()
    {
        if (_activeController == this)
            _activeController = null;

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
        var username = "Rambler";
        var identifier = CompanionIdentity.NetworkIdentifier;
        var moderationName = "Rambler";
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
            var voicePlayerId = "RamblerHostBot";
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
            "[RAMBLERS] VERIFY " +
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
