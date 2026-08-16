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
    public const string Name = "Big Walk Bot Feasibility Probe";
    public const string Version = "0.4.0";

    internal static ManualLogSource Logger = null;
    internal static ConfigEntry<bool> AutomatedLeaderWalk = null;

    public override void Load()
    {
        Logger = Log;
        AutomatedLeaderWalk = Config.Bind(
            "Testing",
            "AutomatedLeaderWalk",
            false,
            "Move the host through one short physics-driven test path and place a temporary obstacle. " +
            "Diagnostic only; keep false for normal play.");
        ClassInjector.RegisterTypeInIl2Cpp<ProbeRunner>();

        var harmony = new Harmony(Guid);
        harmony.PatchAll(typeof(PlayerNetworkingStartPatch));
        harmony.PatchAll(typeof(HouseNetworkTransformIsOwnedPatch));
        harmony.PatchAll(typeof(HouseNetworkTransformIsRestingPatch));
        harmony.PatchAll(typeof(PlayerMoverFixedUpdateTestPatch));

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

[HarmonyPatch(typeof(PlayerMover), "FixedUpdate")]
internal static class PlayerMoverFixedUpdateTestPatch
{
    private static void Postfix(PlayerMover __instance)
    {
        ProbeRunner.ApplyAutomatedLeaderVelocity(__instance);
    }
}

internal sealed class ProbeRunner : MonoBehaviour
{
    private const float FollowStartDelay = 4f;
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
    private const float TestLeaderSpeed = 0.75f;
    private const float TestLeaderMaximumDistance = 2.5f;
    private const float TestObstacleLifetime = 12f;
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

    private FollowState _followState = FollowState.Waiting;
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
    private Vector3 _testLeaderDirection;
    private float _testLeaderEndsAt;
    private float _testObstacleDestroyAt;
    private bool _testLeaderActive;
    private GameObject _testObstacle;

    private static ProbeRunner _activeTestRunner;

    public ProbeRunner(IntPtr pointer) : base(pointer)
    {
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
            TickAutomatedLeaderTest(Time.realtimeSinceStartup);
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
            _followState = FollowState.Waiting;
            _lastMovementIntent = Vector3.zero;
            _avoidanceSign = 0;
            _lastSteeringAngle = 0f;
            _lastClearance = ObstacleProbeDistance;
            _lastDirectPathBlocked = false;
            _stuckWarningIssued = false;
            ClearBreadcrumbs();
            AddBreadcrumb(localPlayer.transform.position);

            _verifyAt = Time.realtimeSinceStartup + 2f;
            _followAt = Time.realtimeSinceStartup + FollowStartDelay;
            _nextNavigationTick = _followAt;
            _nextTrailSample = Time.realtimeSinceStartup + TrailSampleInterval;
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

    private void TickFollowPlayer()
    {
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

        if (Plugin.AutomatedLeaderWalk.Value)
            BeginAutomatedLeaderTest(now, human);
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

    private void BeginAutomatedLeaderTest(float now, PlayerCharacter human)
    {
        var awayFromBot = human.transform.position - _bot.transform.position;
        awayFromBot.y = 0f;
        if (awayFromBot.sqrMagnitude < 0.01f)
            awayFromBot = human.transform.forward;
        awayFromBot.y = 0f;
        awayFromBot.Normalize();

        var bestDirection = Vector3.zero;
        var bestClearance = 0f;
        var bestScore = float.NegativeInfinity;
        for (var index = 0; index < SteeringAngles.Length; index++)
        {
            var angle = SteeringAngles[index];
            var candidate = Quaternion.AngleAxis(angle, Vector3.up) * awayFromBot;
            var candidateClearance = MeasureCharacterClearance(
                human,
                candidate,
                TestLeaderMaximumDistance + 0.5f);
            var score = candidateClearance - Mathf.Abs(angle) * 0.003f;
            if (score <= bestScore)
                continue;

            bestScore = score;
            bestDirection = candidate;
            bestClearance = candidateClearance;
        }

        var travelDistance = Mathf.Min(
            TestLeaderMaximumDistance,
            Mathf.Max(0f, bestClearance - 0.35f));
        if (travelDistance < 0.75f || human.rb == null)
        {
            Plugin.Logger.LogWarning(
                "[BOT-TEST] Automated leader walk skipped: no safe 0.75m path or rigidbody.");
            return;
        }

        _testLeaderDirection = bestDirection;
        _testLeaderEndsAt = now + travelDistance / TestLeaderSpeed;
        _testLeaderActive = true;
        _activeTestRunner = this;
        CreateTestObstacle(now, human);
        Plugin.Logger.LogInfo(
            "[BOT-TEST] LEADER_START " +
            $"direction={bestDirection}, plannedDistance={travelDistance:F2}, " +
            $"speed={TestLeaderSpeed:F2}, obstacle={_testObstacle != null}.");
    }

    internal static void ApplyAutomatedLeaderVelocity(PlayerMover mover)
    {
        var runner = _activeTestRunner;
        if (runner == null || !runner._testLeaderActive ||
            Plugin.AutomatedLeaderWalk == null || !Plugin.AutomatedLeaderWalk.Value)
        {
            return;
        }

        var human = runner.GetHumanPlayer();
        if (human == null || human.mover != mover || human.rb == null)
            return;

        var velocity = human.rb.linearVelocity;
        velocity.x = runner._testLeaderDirection.x * TestLeaderSpeed;
        velocity.z = runner._testLeaderDirection.z * TestLeaderSpeed;
        human.rb.linearVelocity = velocity;
    }

    private void TickAutomatedLeaderTest(float now)
    {
        if (_testLeaderActive)
        {
            var human = GetHumanPlayer();
            if (human == null || human.rb == null || now >= _testLeaderEndsAt)
            {
                if (human != null && human.rb != null)
                {
                    var velocity = human.rb.linearVelocity;
                    velocity.x = 0f;
                    velocity.z = 0f;
                    human.rb.linearVelocity = velocity;
                }

                _testLeaderActive = false;
                if (_activeTestRunner == this)
                    _activeTestRunner = null;
                Plugin.Logger.LogInfo(
                    $"[BOT-TEST] LEADER_END position={human?.transform.position}.");
            }
            else
            {
                var velocity = human.rb.linearVelocity;
                velocity.x = _testLeaderDirection.x * TestLeaderSpeed;
                velocity.z = _testLeaderDirection.z * TestLeaderSpeed;
                human.rb.linearVelocity = velocity;
            }
        }

        if (_testObstacle != null && now >= _testObstacleDestroyAt)
        {
            UnityEngine.Object.Destroy(_testObstacle);
            _testObstacle = null;
            Plugin.Logger.LogInfo("[BOT-TEST] Temporary obstacle removed.");
        }
    }

    private void CreateTestObstacle(float now, PlayerCharacter human)
    {
        _testObstacle = GameObject.CreatePrimitive(PrimitiveType.Cube);
        _testObstacle.name = "__NitrogenFollowTestObstacle";
        var midpoint = (_bot.transform.position + human.transform.position) * 0.5f;
        midpoint.y = Mathf.Min(_bot.transform.position.y, human.transform.position.y) + 0.75f;
        _testObstacle.transform.position = midpoint;
        _testObstacle.transform.localScale = new Vector3(0.7f, 1.5f, 0.7f);

        if (human.ground != null && human.ground.groundCollider != null)
        {
            _testObstacle.layer = human.ground.groundCollider.gameObject.layer;
        }
        else
        {
            var mask = GetObstacleMask(human);
            for (var layer = 0; layer < 32; layer++)
            {
                if ((mask & (1 << layer)) == 0)
                    continue;
                _testObstacle.layer = layer;
                break;
            }
        }

        Physics.SyncTransforms();

        _testObstacleDestroyAt = now + TestObstacleLifetime;
        Plugin.Logger.LogInfo(
            "[BOT-TEST] OBSTACLE " +
            $"position={midpoint}, scale={_testObstacle.transform.localScale}, " +
            $"layer={_testObstacle.layer}, lifetime={TestObstacleLifetime:F1}s.");
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
        _followState = FollowState.Waiting;
        _lastMovementIntent = Vector3.zero;
        _avoidanceSign = 0;
        _testLeaderActive = false;
        if (_activeTestRunner == this)
            _activeTestRunner = null;
        if (_testObstacle != null)
            UnityEngine.Object.Destroy(_testObstacle);
        _testObstacle = null;
        ClearBreadcrumbs();
        Plugin.Logger.LogInfo("[BOT-PROBE] Bot left the scene; probe state reset.");
    }

    private void OnDestroy()
    {
        if (_activeTestRunner == this)
            _activeTestRunner = null;

        if (_testObstacle != null)
        {
            UnityEngine.Object.Destroy(_testObstacle);
            _testObstacle = null;
        }

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
