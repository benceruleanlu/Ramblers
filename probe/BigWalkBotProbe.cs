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
    public const string Version = "0.7.5";

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

    // PlayerNetworking.controlsVelocity is a world-space velocity in metres per
    // second: PlayerMover.FixedUpdate feeds it through PlayerGround.GetSlopedMoveForce
    // into the rigidbody for a remote body exactly as it does for a local one, whose
    // magnitude comes from PlayerMover.GetForwardSpeed(). Movement is therefore
    // commanded in game speed units, never as a normalized 0-1 intent.
    // Walking and running are discrete player gaits. The threshold is the midpoint
    // of the old blend interval, but there is no blended or jogging speed anymore.
    // A run remains latched until the companion comes to a complete stop; walking
    // may promote to running while moving, matching the stock player's controls.
    private const float RunStartDistance = 6.75f;
    private const float FallbackWalkSpeed = 3f;
    private const float FallbackRunSpeed = 5.5f;
    private const float BodyTurnSpeed = 180f;
    private const float FallbackSideLookLimit = 85f;
    private const float FallbackVerticalLookLimit = 55f;
    private const float BrakingLookahead = 0.45f;
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

    private enum MovementGait
    {
        Stopped,
        Walk,
        Run
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
    private float _walkSpeed = FallbackWalkSpeed;
    private float _runSpeed = FallbackRunSpeed;
    private bool _gaitSpeedsFromTunings;
    private MovementGait _movementGait = MovementGait.Stopped;
    private float _lastCommandedSpeed;
    private float _lastTrailDistance;
    private Vector2 _headState;
    private float _lastFacingUpdateAt;
    private float _lastBodyYaw;
    private float _lastTargetYaw;
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
            ResolveGaitSpeeds(playerCharacter);
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
            _movementGait = MovementGait.Stopped;
            _lastCommandedSpeed = 0f;
            _lastTrailDistance = 0f;
            _headState = Vector2.zero;
            _lastFacingUpdateAt = Time.realtimeSinceStartup;
            _lastBodyYaw = _bot.transform.eulerAngles.y;
            _lastTargetYaw = _lastBodyYaw;
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
        _lastFacingUpdateAt = now;
        ResetProgressObservation(now);

        var bodyCollider = _botCharacter.collision == null
            ? null
            : _botCharacter.collision.bodyCollider;
        var obstacleMask = GetObstacleMask();
        Plugin.Logger.LogInfo(
            "[BOT-FOLLOW] START " +
            $"bot={_bot.transform.position}, human={human.transform.position}, " +
            $"followDistance={FollowDistance:F2}, breadcrumbSpacing={BreadcrumbSpacing:F2}, " +
            $"walkSpeed={_walkSpeed:F2}, runSpeed={_runSpeed:F2}, " +
            $"gaitSpeedsFromTunings={_gaitSpeedsFromTunings}, " +
            $"runStartDistance={RunStartDistance:F2}, runLatchesUntilStop=true, " +
            $"bodyTurnSpeed={BodyTurnSpeed:F0}, lookLimitsFromTunings=true, " +
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
        FaceHuman(human, now);
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

        // The gait follows the remaining breadcrumb trail rather than the straight-line
        // gap, so a human who is close through a wall but far along the walked route
        // still makes the companion run.
        var trailDistance = MeasureTrailDistance(botPosition, human.transform.position);
        var gaitSpeed = ResolveMovementSpeed(trailDistance);
        _lastTrailDistance = trailDistance;

        // Look far enough ahead to stop from the gait being requested. The sweep never
        // shortens below the walking probe, so obstacle detection is unchanged at walk.
        var probeDistance = Mathf.Max(ObstacleProbeDistance, gaitSpeed * BrakingLookahead);

        Vector3 steeringDirection;
        float steeringAngle;
        float clearance;
        bool directBlocked;
        if (!TryChooseSteering(
                desiredDirection,
                now,
                probeDistance,
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

        // Use the exact stock walk or run speed. Only immediate obstacle clearance
        // may cap it for collision safety; distance to the player never creates a
        // third, artificial "jog" speed.
        var speed = Mathf.Min(gaitSpeed, clearance / BrakingLookahead);
        _lastCommandedSpeed = speed;
        SetMovementIntent(steeringDirection * speed);

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
        float probeDistance,
        out Vector3 steeringDirection,
        out float steeringAngle,
        out float clearance,
        out bool directBlocked)
    {
        steeringDirection = Vector3.zero;
        steeringAngle = 0f;
        clearance = 0f;

        var directClearance = MeasureClearance(desiredDirection, probeDistance);
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
            var candidateClearance = MeasureClearance(candidate, probeDistance);
            if (candidateClearance < MinimumClearance)
                continue;

            var candidateSign = angle > 0f ? 1 : -1;
            var turnPenalty = Mathf.Abs(angle) * 0.004f;
            var sideBonus = now < _avoidanceSignUntil && candidateSign == _avoidanceSign
                ? 0.35f
                : 0f;

            // Score on the walking probe window so a longer sweep at running speed
            // cannot outweigh the turn penalty and change which detour is chosen.
            var score = Mathf.Min(candidateClearance, ObstacleProbeDistance)
                      - turnPenalty
                      + sideBonus;
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

    private float MeasureClearance(Vector3 direction, float probeDistance)
    {
        return MeasureCharacterClearance(
            _botCharacter,
            direction,
            probeDistance);
    }

    private void ResolveGaitSpeeds(PlayerCharacter character)
    {
        var tunings = character.tunings;
        var hasTunedWalkSpeed = tunings != null && tunings.forwardSpeed > 0.01f;
        _walkSpeed = hasTunedWalkSpeed ? tunings.forwardSpeed : FallbackWalkSpeed;

        var hasTunedRunSpeed = tunings != null && tunings.forwardSprintSpeed > _walkSpeed;
        _runSpeed = hasTunedRunSpeed
            ? tunings.forwardSprintSpeed
            : Mathf.Max(_walkSpeed, FallbackRunSpeed);
        _gaitSpeedsFromTunings = hasTunedWalkSpeed && hasTunedRunSpeed;
    }

    private float ResolveMovementSpeed(float trailDistance)
    {
        if (_movementGait != MovementGait.Run && trailDistance >= RunStartDistance)
        {
            SetMovementGait(MovementGait.Run);
            Plugin.Logger.LogInfo(
                "[BOT-FOLLOW] GAIT run " +
                $"trailDistance={trailDistance:F2}; latched until the next complete stop.");
        }
        else if (_movementGait == MovementGait.Stopped)
        {
            SetMovementGait(MovementGait.Walk);
            Plugin.Logger.LogInfo(
                $"[BOT-FOLLOW] GAIT walk trailDistance={trailDistance:F2}.");
        }

        return _movementGait == MovementGait.Run ? _runSpeed : _walkSpeed;
    }

    private void SetMovementGait(MovementGait gait)
    {
        _movementGait = gait;
        if (_botCharacter?.sprinter == null)
            return;

        var sprinting = gait == MovementGait.Run;
        _botCharacter.sprinter.isSprinting = sprinting;
        _botCharacter.sprinter.sprintIsToggledOn = sprinting;
    }

    private void FaceHuman(PlayerCharacter human, float now)
    {
        if (human == null ||
            _botCharacter?.head == null ||
            _botCharacter.houseNetworkTransform == null ||
            _botNetworking == null)
            return;

        var botHeadPosition = _botCharacter.cameraTransform == null
            ? _bot.transform.position + Vector3.up * 1.5f
            : _botCharacter.cameraTransform.position;
        var humanHeadPosition = human.cameraTransform == null
            ? human.transform.position + Vector3.up * 1.5f
            : human.cameraTransform.position;
        var toHuman = humanHeadPosition - botHeadPosition;
        var horizontalDirection = new Vector3(toHuman.x, 0f, toHuman.z);
        var horizontalDistance = horizontalDirection.magnitude;
        if (horizontalDistance < 0.001f && Mathf.Abs(toHuman.y) < 0.001f)
            return;

        var networkTransform = _botCharacter.houseNetworkTransform;
        var currentRotation = networkTransform.targetRotation;
        var currentForward = currentRotation * Vector3.forward;
        currentForward.y = 0f;
        if (currentForward.sqrMagnitude < 0.0001f)
        {
            currentForward = _bot.transform.forward;
            currentForward.y = 0f;
        }

        var bodyYaw = Mathf.Atan2(currentForward.x, currentForward.z) * Mathf.Rad2Deg;
        var targetYaw = horizontalDistance < 0.001f
            ? bodyYaw
            : Mathf.Atan2(horizontalDirection.x, horizontalDirection.z) * Mathf.Rad2Deg;
        var elapsed = _lastFacingUpdateAt <= 0f
            ? NavigationInterval
            : Mathf.Clamp(now - _lastFacingUpdateAt, 0f, NavigationInterval * 2f);
        _lastFacingUpdateAt = now;

        // Stock PlayerMover.UpdatePerFrameRotation absorbs horizontal look into
        // PlayerCharacter.kernal at 180 degrees per second. That method is local-only,
        // so a connectionless non-local companion performs the same body step here.
        var yawError = Mathf.DeltaAngle(bodyYaw, targetYaw);
        var bodyStep = Mathf.Clamp(
            yawError,
            -BodyTurnSpeed * elapsed,
            BodyTurnSpeed * elapsed);
        var nextRotation = Quaternion.AngleAxis(bodyStep, Vector3.up) * currentRotation;
        networkTransform.targetRotation = nextRotation;

        var tunings = _botCharacter.tunings;
        var sideLookLimit = tunings != null && tunings.sideLookLimit > 0.01f
            ? tunings.sideLookLimit
            : FallbackSideLookLimit;
        var upperLookLimit = tunings != null && tunings.upperLookLimit > 0.01f
            ? tunings.upperLookLimit
            : FallbackVerticalLookLimit;
        var lowerLookLimit = tunings != null && tunings.lowerLookLimit > 0.01f
            ? tunings.lowerLookLimit
            : FallbackVerticalLookLimit;

        // PlayerHead's replicated Vector2 is (yaw relative to the body, pitch).
        // The residual yaw decays to zero as the body catches the target. Unity's
        // positive X rotation looks downward, hence the negative pitch.
        var remainingYaw = Mathf.DeltaAngle(bodyYaw + bodyStep, targetYaw);
        var desiredPitch = -Mathf.Atan2(toHuman.y, horizontalDistance) * Mathf.Rad2Deg;
        _headState = new Vector2(
            Mathf.Clamp(remainingYaw, -sideLookLimit, sideLookLimit),
            Mathf.Clamp(desiredPitch, -upperLookLimit, lowerLookLimit));

        _lastBodyYaw = bodyYaw + bodyStep;
        _lastTargetYaw = targetYaw;

        // The body rotation is sampled by the already-owned HouseNetworkTransform;
        // residual head pose uses the stock SyncVar/animator path.
        _botCharacter.head.headState = _headState;
        _botNetworking.NetworkheadState = _headState;
    }

    private float MeasureTrailDistance(Vector3 botPosition, Vector3 humanPosition)
    {
        if (_breadcrumbCount == 0)
            return HorizontalDistance(botPosition, humanPosition);

        var previous = PeekBreadcrumb();
        var total = HorizontalDistance(botPosition, previous);
        for (var offset = 1; offset < _breadcrumbCount; offset++)
        {
            var current = _breadcrumbs[(_breadcrumbHead + offset) % MaximumBreadcrumbs];
            total += HorizontalDistance(previous, current);
            previous = current;
        }

        return total + HorizontalDistance(previous, humanPosition);
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
        SetMovementGait(MovementGait.Stopped);
        _lastCommandedSpeed = 0f;
        _followState = state;
        ResetProgressObservation(now);
    }

    private void ObservePossibleStuck(float now)
    {
        if (_lastCommandedSpeed <= 0.01f)
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
                    $"moved={movement:F2}m in {StuckObservationWindow:F1}s while commanded " +
                    $"speed={_lastCommandedSpeed:F2} m/s ({DescribeGait()}). " +
                    "Detection only; no recovery attempted.");
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
        SetMovementGait(MovementGait.Stopped);

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
            $"trailDistance={_lastTrailDistance:F2}, " +
            $"commandedSpeed={_lastCommandedSpeed:F2}, " +
            $"gait={DescribeGait()}, bodyYaw={_lastBodyYaw:F1}, " +
            $"targetYaw={_lastTargetYaw:F1}, headState={_headState}, " +
            $"breadcrumbs={_breadcrumbCount}, directBlocked={_lastDirectPathBlocked}, " +
            $"steeringAngle={_lastSteeringAngle:F0}, clearance={_lastClearance:F2}, " +
            $"intent={_lastMovementIntent}, rigidbodyVelocity={rigidbodyVelocity}, " +
            $"networkIntent={_botNetworking.controlsVelocity}, " +
            $"botIsLocalPlayer={_botNetworking.isLocalPlayer}, hostStillLocal={hostStillLocal}.");
    }

    private string DescribeGait()
    {
        return _movementGait.ToString().ToLowerInvariant();
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
        _walkSpeed = FallbackWalkSpeed;
        _runSpeed = FallbackRunSpeed;
        _gaitSpeedsFromTunings = false;
        _movementGait = MovementGait.Stopped;
        _lastCommandedSpeed = 0f;
        _lastTrailDistance = 0f;
        _headState = Vector2.zero;
        _lastFacingUpdateAt = 0f;
        _lastBodyYaw = 0f;
        _lastTargetYaw = 0f;
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
            SetMovementGait(MovementGait.Stopped);
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
            $"tuningForwardSpeed={playerCharacter?.tunings?.forwardSpeed}, " +
            $"tuningForwardSprintSpeed={playerCharacter?.tunings?.forwardSprintSpeed}, " +
            $"tuningSideLookLimit={playerCharacter?.tunings?.sideLookLimit}, " +
            $"tuningUpperLookLimit={playerCharacter?.tunings?.upperLookLimit}, " +
            $"tuningLowerLookLimit={playerCharacter?.tunings?.lowerLookLimit}, " +
            $"movementResting={networkTransform?.IsRestingForPlayerMovement}, " +
            $"playerCharacterPresent={playerCharacter != null}.");
    }
}
