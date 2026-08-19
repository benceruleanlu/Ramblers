using System;
using Mirror;
using UnityEngine;

namespace Ramblers;

/// <summary>
/// Long-running breadcrumb-follow goal. It owns navigation state but delegates
/// body motion and facing to reusable actuators.
/// </summary>
internal sealed class CompanionFollowBehavior
{
    internal const float NavigationInterval = 0.1f;

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
        Suspended,
        Blocked,
        Failed
    }

    private readonly BreadcrumbTrail _trail = new BreadcrumbTrail(MaximumBreadcrumbs);
    private readonly CompanionLocomotion _locomotion;
    private readonly CompanionAttention _attention;

    private CompanionBody _body;
    private PlayerCharacter _humanAtSpawn;
    private bool _followRequested;
    private FollowState _state = FollowState.Idle;
    private float _followAt;
    private float _nextNavigationTick;
    private float _nextTrailSample;
    private float _lastTrailDistance;
    private Vector3 _currentTarget;
    private float _followStartedAt;
    private float _nextStatusLog;
    private string _suspensionReason;

    internal CompanionFollowBehavior(
        CompanionLocomotion locomotion,
        CompanionAttention attention)
    {
        _locomotion = locomotion;
        _attention = attention;
    }

    /// <summary>Whether a follow intent is outstanding, suspended or not.</summary>
    internal bool IsRequested => _followRequested;

    internal void Bind(
        CompanionBody body,
        PlayerCharacter human,
        float now,
        bool movementAllowed,
        string movementBlocker)
    {
        _body = body;
        _humanAtSpawn = human;
        _followRequested = false;
        _state = FollowState.Idle;
        _lastTrailDistance = 0f;
        _suspensionReason = null;
        _followAt = float.PositiveInfinity;
        _nextNavigationTick = _followAt;
        _nextTrailSample = now + TrailSampleInterval;
        _trail.Clear();
        if (human == null)
            return;

        StartFollowIntent(human, now, movementAllowed, movementBlocker);
        Plugin.Logger.LogInfo(
            $"[FOLLOW] DEFAULT mode=follow status={(movementAllowed ? "started" : "suspended")}.");
    }

    internal void TickFrame(float now)
    {
        if (_body == null || !_body.IsAlive || now < _nextTrailSample)
            return;

        _nextTrailSample = now + TrailSampleInterval;
        RecordHumanTrail();
    }

    internal void TickFixed(float now, bool movementAllowed, string movementBlocker)
    {
        if (_body == null || !_body.IsAlive)
            return;

        if (!_followRequested)
        {
            _attention.ClearTarget(GazeChannel.Follow);
            if (_state != FollowState.Idle ||
                _locomotion.LastMovementIntent.sqrMagnitude > 0f)
            {
                StopForState(FollowState.Idle, now);
            }
            return;
        }

        if (!movementAllowed)
        {
            SetMovementAllowed(false, now, movementBlocker);
            return;
        }

        if (_state == FollowState.Suspended)
            SetMovementAllowed(true, now, null);
        if (_state == FollowState.Failed)
            return;
        if (now < _followAt)
            return;

        if (!NetworkServer.active ||
            !_body.Networking.isServer ||
            _body.Networking.isLocalPlayer)
        {
            Fail(
                $"authority invariant failed: serverActive={NetworkServer.active}, " +
                $"isServer={_body.Networking.isServer}, " +
                $"isLocalPlayer={_body.Networking.isLocalPlayer}");
            return;
        }

        if (_state == FollowState.Waiting)
            BeginFollowing(now);

        if (now < _nextNavigationTick)
            return;

        _nextNavigationTick = now + NavigationInterval;
        NavigateTowardHuman(now);
    }

    internal AgentToolResult SetMode(
        FollowMode mode,
        float now,
        bool movementAllowed,
        string movementBlocker)
    {
        if (_body == null || !_body.IsAlive)
            return AgentToolResult.Failure("bot_not_spawned");

        if (mode == FollowMode.Follow)
        {
            var human = GetHumanPlayer();
            if (human == null)
                return AgentToolResult.Failure("human_player_unavailable");

            StartFollowIntent(human, now, movementAllowed, movementBlocker);
            var status = movementAllowed ? "started" : "suspended";
            Plugin.Logger.LogInfo(
                $"[AGENT] TOOL {AgentToolCatalog.SetFollowMode} mode=follow status={status}.");
            return AgentToolResult.Success(
                AgentToolCatalog.SetFollowMode,
                status,
                "follow");
        }

        Stop(now);
        Plugin.Logger.LogInfo(
            $"[AGENT] TOOL {AgentToolCatalog.SetFollowMode} mode=stay status=stopped.");
        return AgentToolResult.Success(
            AgentToolCatalog.SetFollowMode,
            "stopped",
            "stay");
    }

    private void StartFollowIntent(
        PlayerCharacter human,
        float now,
        bool movementAllowed,
        string movementBlocker)
    {
        _followRequested = true;
        _state = movementAllowed ? FollowState.Waiting : FollowState.Suspended;
        _suspensionReason = movementAllowed ? null : movementBlocker;
        _followAt = now;
        _nextNavigationTick = now;
        _trail.Clear();
        _trail.Add(human.transform.position);
        _attention.SetTarget(
            GazeChannel.Follow,
            CompanionBody.HeadPositionOf(human));
        _locomotion.ResetProgressObservation(now);
    }

    /// <summary>
    /// Drops the follow intent without reporting a <c>set_follow_mode</c> result,
    /// so a general cancel can stop navigation without pretending the model
    /// asked for stay.
    /// </summary>
    internal void Stop(float now)
    {
        _followRequested = false;
        _suspensionReason = null;
        _followAt = float.PositiveInfinity;
        _attention.ClearTarget(GazeChannel.Follow);
        StopForState(FollowState.Idle, now);
    }

    internal void SetMovementAllowed(bool allowed, float now, string movementBlocker)
    {
        if (!_followRequested)
            return;

        if (!allowed)
        {
            var nextReason = movementBlocker ?? "companion_action";
            var reasonChanged = !string.Equals(
                _suspensionReason,
                nextReason,
                StringComparison.Ordinal);
            if (_state != FollowState.Suspended ||
                _locomotion.LastMovementIntent.sqrMagnitude > 0f)
            {
                StopForState(FollowState.Suspended, now);
                _suspensionReason = nextReason;
                Plugin.Logger.LogInfo(
                    $"[FOLLOW] SUSPENDED by {_suspensionReason}.");
            }
            else if (reasonChanged)
            {
                _suspensionReason = nextReason;
                Plugin.Logger.LogInfo(
                    $"[FOLLOW] SUSPENSION_BLOCKER changed to {_suspensionReason}.");
            }
            return;
        }

        if (_state != FollowState.Suspended)
            return;

        _state = FollowState.Waiting;
        _followAt = now;
        _nextNavigationTick = now;
        _attention.ResumeAt(now);
        _locomotion.ResetProgressObservation(now);
        Plugin.Logger.LogInfo(
            $"[FOLLOW] RESUMED after {_suspensionReason ?? "companion_action"} cleared.");
        _suspensionReason = null;
    }

    private void BeginFollowing(float now)
    {
        var human = GetHumanPlayer();
        if (human == null)
        {
            Fail("local human player was unavailable when follow began");
            return;
        }

        _state = FollowState.Following;
        _followStartedAt = now;
        _nextStatusLog = now;
        _attention.ResumeAt(now);
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
            $"posture={CompanionPostureActuator.Describe(_locomotion.Posture)}, " +
            $"runStartDistance={CompanionLocomotion.RunStartDistance:F2}, runLatchesUntilStop=true, " +
            $"bodyTurnSpeed={CompanionFacing.BodyTurnSpeed:F0}, lookLimitsFromTunings=true, " +
            $"navigationHz={1f / NavigationInterval:F0}, obstacleMask={obstacleMask}, " +
            $"bodyRadius={(bodyCollider == null ? -1f : bodyCollider.radius):F2}, " +
            $"bodyHeight={(bodyCollider == null ? -1f : bodyCollider.height):F2}.");
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
        _attention.SetTarget(
            GazeChannel.Follow,
            CompanionBody.HeadPositionOf(human));
        var humanDistance = BreadcrumbTrail.HorizontalDistance(
            botPosition,
            human.transform.position);
        if (humanDistance <= FollowDistance ||
            (_state == FollowState.Holding && humanDistance < ResumeDistance))
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
        var trailDistance = _trail.MeasureDistance(botPosition, human.transform.position);
        _lastTrailDistance = trailDistance;

        var previousState = _state;
        var previousAngle = _locomotion.LastSteeringAngle;
        SteeringStatus status;
        if (!_locomotion.TrySteerToward(desiredDirection, trailDistance, now, out status))
        {
            var stateChanged = _state != FollowState.Blocked;
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

        _state = FollowState.Following;
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
        _state = state;
    }

    internal void Fail(string reason)
    {
        if (_state == FollowState.Failed)
            return;

        _state = FollowState.Failed;
        _attention.ClearTarget(GazeChannel.Follow);
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
            $"state={_state}, elapsed={now - _followStartedAt:F2}, " +
            $"position={_body.Position}, humanDistance={humanDistance:F2}, " +
            $"target={_currentTarget}, targetDistance={targetDistance:F2}, " +
            $"trailDistance={_lastTrailDistance:F2}, " +
            $"commandedSpeed={_locomotion.LastCommandedSpeed:F2}, " +
            $"gait={_locomotion.DescribeGait()}, posture={CompanionPostureActuator.Describe(_locomotion.Posture)}, " +
            $"bodyYaw={_attention.LastBodyYaw:F1}, targetYaw={_attention.LastTargetYaw:F1}, " +
            $"headState={_attention.HeadState}, breadcrumbs={_trail.Count}, " +
            $"directBlocked={_locomotion.LastDirectPathBlocked}, " +
            $"steeringAngle={_locomotion.LastSteeringAngle:F0}, " +
            $"clearance={_locomotion.LastClearance:F2}, " +
            $"intent={_locomotion.LastMovementIntent}, rigidbodyVelocity={rigidbodyVelocity}, " +
            $"networkIntent={_body.Networking.controlsVelocity}, " +
            $"botIsLocalPlayer={_body.Networking.isLocalPlayer}, hostStillLocal={hostStillLocal}.");
    }

    internal void Release()
    {
        _body = null;
        _humanAtSpawn = null;
        _followRequested = false;
        _suspensionReason = null;
        _state = FollowState.Idle;
        _lastTrailDistance = 0f;
        _trail.Clear();
        _attention.ClearTarget(GazeChannel.Follow);
    }
}
