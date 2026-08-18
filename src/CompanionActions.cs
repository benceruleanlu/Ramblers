using System;
using UnityEngine;

namespace Ramblers;

internal enum FollowMode
{
    Follow,
    Stay
}

internal enum CompanionPosture
{
    Standing,
    Crouching,
    Sitting
}

/// <summary>
/// Arbitrates independent companion capabilities. Navigation retains the
/// human's requested goal while a posture temporarily prevents movement.
/// </summary>
internal sealed class CompanionActionCoordinator
{
    private readonly CompanionLocomotion _locomotion = new CompanionLocomotion();
    private readonly CompanionFacing _facing = new CompanionFacing(CompanionFollowBehavior.NavigationInterval);
    private readonly CompanionAttention _attention;
    private readonly CompanionFollowBehavior _follow;
    private readonly CompanionPostureActuator _posture = new CompanionPostureActuator();
    private readonly CompanionJumpActuator _jump = new CompanionJumpActuator();
    private readonly CompanionInspectionBehavior _inspection;

    internal CompanionActionCoordinator()
    {
        _attention = new CompanionAttention(_facing);
        _follow = new CompanionFollowBehavior(_locomotion, _attention);
        _inspection = new CompanionInspectionBehavior(_attention);
    }

    internal void Bind(CompanionBody body, PlayerCharacter human, float now)
    {
        _locomotion.ResolveGaitSpeeds(body.Character);
        _locomotion.Bind(body, now);
        _attention.Bind(body, now);
        _posture.Bind(body);
        _jump.Bind(body);
        _inspection.Bind(body, human);
        _locomotion.SetPosture(_posture.Current);
        _follow.Bind(body, human, now);
    }

    internal void TickFrame(float now)
    {
        _follow.TickFrame(now);
    }

    internal void TickLateFrame(float now)
    {
        _inspection.TickFrame(now);
        _attention.Tick(now);
    }

    internal void TickFixed(float now)
    {
        try
        {
            _follow.TickFixed(now, MovementAllowed, MovementBlocker);
        }
        catch (Exception exception)
        {
            _follow.Fail($"navigation exception: {exception}");
        }

        try
        {
            _jump.TickFixed(now, _posture.Current);
        }
        catch (Exception exception)
        {
            _jump.Cancel("jump execution exception");
            Plugin.Logger.LogError($"[ACTION] JUMP failed: {exception}");
        }
    }

    internal AgentToolResult SetFollowMode(FollowMode mode, float now)
    {
        return _follow.SetMode(mode, now, MovementAllowed, MovementBlocker);
    }

    internal AgentToolResult SetPosture(CompanionPosture posture, float now)
    {
        var result = _posture.Set(posture);
        if (!result.Ok)
            return result;

        _locomotion.SetPosture(_posture.Current);
        if (_posture.BlocksMovement)
            _locomotion.Stop(now);
        _follow.SetMovementAllowed(MovementAllowed, now, MovementBlocker);
        return result;
    }

    internal AgentToolResult RequestJump(float now)
    {
        if (_inspection.IsActive)
            return AgentToolResult.Failure("inspection_in_progress");
        return _jump.Request(now, _posture.Current);
    }

    internal bool TryBeginInspection(float now, out AgentToolResult failure)
    {
        if (_jump.IsQueued)
        {
            failure = AgentToolResult.Failure("jump_in_progress");
            return false;
        }
        if (!_inspection.TryBegin(now, out failure))
            return false;

        _follow.SetMovementAllowed(MovementAllowed, now, MovementBlocker);
        return true;
    }

    internal bool TryTakeInspectionCompletion(out CompanionInspectionCompletion completion)
    {
        return _inspection.TryTakeCompletion(out completion);
    }

    internal void CancelInspection(float now)
    {
        _inspection.Cancel(now);
        _follow.SetMovementAllowed(MovementAllowed, now, MovementBlocker);
    }

    internal void ReleaseInspectionAttention(float now)
    {
        _inspection.ReleaseAttention(now);
        _follow.SetMovementAllowed(MovementAllowed, now, MovementBlocker);
    }

    internal void FailInspection(string error, float now)
    {
        _inspection.FailActive(error, now);
        _follow.SetMovementAllowed(MovementAllowed, now, MovementBlocker);
    }

    internal void Release()
    {
        _inspection.Release();
        _follow.Release();
        _jump.Release();
        _posture.Release();
        _locomotion.Release();
        _attention.Release();
    }

    internal void StopQuietly()
    {
        _inspection.Cancel(Time.realtimeSinceStartup);
        _jump.Cancel("controller shutdown");
        _locomotion.StopQuietly();
    }

    private bool MovementAllowed =>
        !_posture.BlocksMovement && !_inspection.BlocksMovement;

    private string MovementBlocker
    {
        get
        {
            if (_posture.BlocksMovement)
                return "posture";
            if (_inspection.BlocksMovement)
                return "inspection";
            return null;
        }
    }
}
