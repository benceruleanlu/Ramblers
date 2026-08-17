using System;
using Dissonance.Integrations.MirrorIgnorance;
using LobbyNetworking;
using Mirror;
using UnityEngine;

namespace Ramblers;

/// <summary>
/// Owns the companion's lifecycle — spawning a connectionless copy of the real
/// player prefab, holding the authority invariants, and tearing down cleanly —
/// and runs the breadcrumb-follow behaviour on top of the shared locomotion and
/// facing components.
/// </summary>
internal sealed class CompanionController : MonoBehaviour
{
    private const float NavigationInterval = 0.1f;
    private const float TrailSampleInterval = 0.1f;
    private const float BreadcrumbSpacing = 0.65f;
    private const float BreadcrumbArrivalTolerance = 0.8f;
    private const float FollowDistance = 2.25f;
    private const float ResumeDistance = 2.5f;
    private const float TrailResetDistance = 8f;
    private const float StatusLogInterval = 1f;
    private const int MaximumBreadcrumbs = 256;

    private enum FollowState
    {
        Idle,
        Waiting,
        Following,
        Holding,
        Blocked,
        Failed
    }

    private readonly BreadcrumbTrail _trail = new BreadcrumbTrail(MaximumBreadcrumbs);
    private readonly CompanionLocomotion _locomotion = new CompanionLocomotion();
    private readonly CompanionFacing _facing = new CompanionFacing(NavigationInterval);
    private readonly LogLatch _verificationLog = new LogLatch();

    private CompanionBody _body;
    private PlayerCharacter _humanAtSpawn;
    private float _nextPoll;
    private float _verifyAt;
    private float _followAt;
    private float _nextNavigationTick;
    private float _nextTrailSample;
    private bool _hasSpawnedBot;
    private bool _followRequested;

    private FollowState _followState = FollowState.Idle;
    private float _lastTrailDistance;
    private Vector3 _currentTarget;
    private float _followStartedAt;
    private float _nextStatusLog;

    private static CompanionController _activeController;

    public CompanionController(IntPtr pointer) : base(pointer)
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
        var body = controller == null ? null : controller._body;
        bot = body == null ? null : body.Character;
        return human != null && bot != null && body.GameObject != null;
    }

    private bool HasBody => _body != null && _body.GameObject != null;

    private void Update()
    {
        if (HasBody)
        {
            if (Time.realtimeSinceStartup >= _verifyAt && _verificationLog.ShouldLog())
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
        if (_body == null || !_body.IsAlive)
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
        GameObject spawned = null;
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
            _locomotion.ResolveGaitSpeeds(playerCharacter);
            CompanionIdentity.Apply(playerNetworking, voiceIdentity);
            NetworkServer.Spawn(spawned);

            var now = Time.realtimeSinceStartup;
            _body = new CompanionBody(
                spawned,
                playerCharacter,
                playerNetworking,
                networkIdentity,
                networkTransform);
            _humanAtSpawn = localPlayer;
            _hasSpawnedBot = true;
            _followRequested = false;
            _followState = FollowState.Idle;
            _lastTrailDistance = 0f;
            _locomotion.Bind(_body, now);
            _facing.Bind(_body, now);
            _trail.Clear();
            _trail.Add(localPlayer.transform.position);

            _verifyAt = now + 2f;
            _followAt = float.PositiveInfinity;
            _nextNavigationTick = _followAt;
            _nextTrailSample = now + TrailSampleInterval;
            Plugin.Logger.LogInfo(
                $"[RAMBLERS] Spawn requested: netId={networkIdentity.netId}, " +
                $"connectionToClient={(networkIdentity.connectionToClient == null ? "null" : "non-null")}, " +
                $"position={position}.");
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogError($"[RAMBLERS] Spawn failed: {exception}");
            if (spawned != null)
                UnityEngine.Object.Destroy(spawned);
            _body = null;
        }
    }

    private void TickFollowPlayer()
    {
        if (!_followRequested)
        {
            if (_followState != FollowState.Idle ||
                _locomotion.LastMovementIntent.sqrMagnitude > 0f)
            {
                StopForState(FollowState.Idle, Time.realtimeSinceStartup);
            }
            return;
        }

        if (_followState == FollowState.Failed)
            return;

        var now = Time.realtimeSinceStartup;
        if (now < _followAt)
            return;

        if (!NetworkServer.active ||
            !_body.Networking.isServer ||
            _body.Networking.isLocalPlayer)
        {
            FailFollow(
                $"authority invariant failed: serverActive={NetworkServer.active}, " +
                $"isServer={_body.Networking.isServer}, " +
                $"isLocalPlayer={_body.Networking.isLocalPlayer}");
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
        _facing.ResumeAt(now);
        _locomotion.ResetProgressObservation(now);

        var bodyCollider = _body.Character.collision == null
            ? null
            : _body.Character.collision.bodyCollider;
        var obstacleMask = CompanionLocomotion.GetObstacleMask(_body.Character);
        Plugin.Logger.LogInfo(
            "[FOLLOW] START " +
            $"bot={_body.Position}, human={human.transform.position}, " +
            $"followDistance={FollowDistance:F2}, breadcrumbSpacing={BreadcrumbSpacing:F2}, " +
            $"walkSpeed={_locomotion.WalkSpeed:F2}, runSpeed={_locomotion.RunSpeed:F2}, " +
            $"gaitSpeedsFromTunings={_locomotion.GaitSpeedsFromTunings}, " +
            $"runStartDistance={CompanionLocomotion.RunStartDistance:F2}, runLatchesUntilStop=true, " +
            $"bodyTurnSpeed={CompanionFacing.BodyTurnSpeed:F0}, lookLimitsFromTunings=true, " +
            $"navigationHz={1f / NavigationInterval:F0}, obstacleMask={obstacleMask}, " +
            $"bodyRadius={(bodyCollider == null ? -1f : bodyCollider.radius):F2}, " +
            $"bodyHeight={(bodyCollider == null ? -1f : bodyCollider.height):F2}.");
    }

    private string SetFollowMode(string mode)
    {
        if (_body == null || !_body.IsAlive || !_hasSpawnedBot)
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
            _trail.Clear();
            _trail.Add(human.transform.position);
            _locomotion.ResetProgressObservation(now);
            Plugin.Logger.LogInfo("[AGENT] TOOL set_follow_mode mode=follow accepted.");
            return "{\"ok\":true,\"mode\":\"follow\",\"status\":\"started\"}";
        }

        if (string.Equals(mode, "stay", StringComparison.OrdinalIgnoreCase))
        {
            _followRequested = false;
            _followAt = float.PositiveInfinity;
            StopForState(FollowState.Idle, now);
            Plugin.Logger.LogInfo("[AGENT] TOOL set_follow_mode mode=stay accepted.");
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
            Plugin.Logger.LogWarning("[FOLLOW] BLOCKED local human player is unavailable.");
            return;
        }

        var botPosition = _body.Position;
        _facing.Face(CompanionBody.HeadPositionOf(human), now);
        var humanDistance = BreadcrumbTrail.HorizontalDistance(
            botPosition,
            human.transform.position);
        if (humanDistance <= FollowDistance ||
            (_followState == FollowState.Holding && humanDistance < ResumeDistance))
        {
            StopForState(FollowState.Holding, now);
            LogFollowStatusIfDue(now, humanDistance, 0f);
            return;
        }

        _trail.RemoveReached(botPosition, BreadcrumbArrivalTolerance);
        if (_trail.Count == 0)
            _trail.Add(human.transform.position);

        _currentTarget = _trail.Peek();
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
        var trailDistance = _trail.MeasureDistance(botPosition, human.transform.position);
        _lastTrailDistance = trailDistance;

        var previousState = _followState;
        var previousAngle = _locomotion.LastSteeringAngle;

        SteeringStatus status;
        if (!_locomotion.TrySteerToward(desiredDirection, trailDistance, now, out status))
        {
            var stateChanged = _followState != FollowState.Blocked;
            StopForState(FollowState.Blocked, now);
            if (stateChanged)
            {
                Plugin.Logger.LogWarning(
                    "[FOLLOW] BLOCKED " +
                    $"target={_currentTarget}, targetDistance={targetDistance:F2}, " +
                    $"humanDistance={humanDistance:F2}; no steering candidate had " +
                    $"{CompanionLocomotion.MinimumClearance:F2}m clearance. " +
                    "No recovery or teleport attempted.");
            }
            LogFollowStatusIfDue(now, humanDistance, targetDistance);
            return;
        }

        _followState = FollowState.Following;

        if (status.DirectPathBlocked &&
            (previousState != FollowState.Following ||
             Mathf.Abs(previousAngle - status.SteeringAngle) >= 1f))
        {
            Plugin.Logger.LogInfo(
                "[FOLLOW] AVOID " +
                $"steeringAngle={status.SteeringAngle:F0}, clearance={status.Clearance:F2}, " +
                $"target={_currentTarget}, targetDistance={targetDistance:F2}.");
        }
        else if (!status.DirectPathBlocked && previousState == FollowState.Blocked)
        {
            Plugin.Logger.LogInfo("[FOLLOW] Path clear; resuming breadcrumb follow.");
        }

        _locomotion.ObserveProgress(now);
        LogFollowStatusIfDue(now, humanDistance, targetDistance);
    }

    private void RecordHumanTrail()
    {
        var human = GetHumanPlayer();
        if (human == null)
            return;

        var position = human.transform.position;
        Vector3 lastAdded;
        if (!_trail.TryGetLastAdded(out lastAdded))
        {
            _trail.Add(position);
            return;
        }

        var distance = Vector3.Distance(position, lastAdded);
        if (distance >= TrailResetDistance)
        {
            _trail.Clear();
            _trail.Add(position);
            Plugin.Logger.LogWarning(
                "[FOLLOW] TRAIL_RESET " +
                $"human moved {distance:F2}m between samples; refusing to invent a traversable segment.");
            return;
        }

        if (distance >= BreadcrumbSpacing)
            _trail.Add(position);
    }

    private PlayerCharacter GetHumanPlayer()
    {
        var human = WorldManager.localPlayerCharacter;
        if (human == null)
            human = _humanAtSpawn;

        if (human == null || (_body != null && human.gameObject == _body.GameObject))
            return null;

        return human;
    }

    private void StopForState(FollowState state, float now)
    {
        _locomotion.Stop(now);
        _followState = state;
    }

    private void FailFollow(string reason)
    {
        if (_followState == FollowState.Failed)
            return;

        _followState = FollowState.Failed;
        _locomotion.StopQuietly();
        Plugin.Logger.LogError($"[FOLLOW] FAILED {reason}");
    }

    private void LogFollowStatusIfDue(
        float now,
        float humanDistance,
        float targetDistance)
    {
        if (now < _nextStatusLog)
            return;

        _nextStatusLog = now + StatusLogInterval;
        var rigidbodyVelocity = _body.Character.rb == null
            ? Vector3.zero
            : _body.Character.rb.linearVelocity;
        var human = WorldManager.localPlayerCharacter;
        var hostStillLocal = human != null &&
                             human.gameObject != _body.GameObject &&
                             human.playerNetworking != null &&
                             human.playerNetworking.isLocalPlayer;

        Plugin.Logger.LogInfo(
            "[FOLLOW] STATUS " +
            $"state={_followState}, elapsed={now - _followStartedAt:F2}, " +
            $"position={_body.Position}, humanDistance={humanDistance:F2}, " +
            $"target={_currentTarget}, targetDistance={targetDistance:F2}, " +
            $"trailDistance={_lastTrailDistance:F2}, " +
            $"commandedSpeed={_locomotion.LastCommandedSpeed:F2}, " +
            $"gait={_locomotion.DescribeGait()}, bodyYaw={_facing.LastBodyYaw:F1}, " +
            $"targetYaw={_facing.LastTargetYaw:F1}, headState={_facing.HeadState}, " +
            $"breadcrumbs={_trail.Count}, directBlocked={_locomotion.LastDirectPathBlocked}, " +
            $"steeringAngle={_locomotion.LastSteeringAngle:F0}, " +
            $"clearance={_locomotion.LastClearance:F2}, " +
            $"intent={_locomotion.LastMovementIntent}, rigidbodyVelocity={rigidbodyVelocity}, " +
            $"networkIntent={_body.Networking.controlsVelocity}, " +
            $"botIsLocalPlayer={_body.Networking.isLocalPlayer}, hostStillLocal={hostStillLocal}.");
    }

    private void ResetAfterBotDestroyed()
    {
        _body = null;
        _humanAtSpawn = null;
        _hasSpawnedBot = false;
        _verificationLog.Reset();
        _followRequested = false;
        _followState = FollowState.Idle;
        _lastTrailDistance = 0f;
        _locomotion.Release();
        _facing.Release();
        _trail.Clear();
        Plugin.Logger.LogInfo("[RAMBLERS] Companion left the scene; controller state reset.");
    }

    private void OnDestroy()
    {
        if (_activeController == this)
            _activeController = null;

        if (_body == null || !NetworkServer.active)
            return;

        try
        {
            _locomotion.StopQuietly();
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
            $"tuningSideLookLimit={playerCharacter?.tunings?.sideLookLimit}, " +
            $"tuningUpperLookLimit={playerCharacter?.tunings?.upperLookLimit}, " +
            $"tuningLowerLookLimit={playerCharacter?.tunings?.lowerLookLimit}, " +
            $"movementResting={networkTransform?.IsRestingForPlayerMovement}, " +
            $"playerCharacterPresent={playerCharacter != null}.");
    }
}
