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
    private readonly CompanionFollowBehavior _follow;
    private readonly CompanionPostureActuator _posture = new CompanionPostureActuator();
    private readonly CompanionJumpActuator _jump = new CompanionJumpActuator();

    internal CompanionActionCoordinator()
    {
        _follow = new CompanionFollowBehavior(_locomotion, _facing);
    }

    internal void Bind(CompanionBody body, PlayerCharacter human, float now)
    {
        _locomotion.ResolveGaitSpeeds(body.Character);
        _locomotion.Bind(body, now);
        _facing.Bind(body, now);
        _posture.Bind(body);
        _jump.Bind(body);
        _locomotion.SetPosture(_posture.Current);
        _follow.Bind(body, human, now);
    }

    internal void TickFrame(float now)
    {
        _follow.TickFrame(now);
    }

    internal void TickFixed(float now)
    {
        try
        {
            _follow.TickFixed(now, !_posture.BlocksMovement);
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
        return _follow.SetMode(mode, now, !_posture.BlocksMovement);
    }

    internal AgentToolResult SetPosture(CompanionPosture posture, float now)
    {
        var result = _posture.Set(posture);
        if (!result.Ok)
            return result;

        _locomotion.SetPosture(_posture.Current);
        if (_posture.BlocksMovement)
            _locomotion.Stop(now);
        _follow.SetMovementAllowed(!_posture.BlocksMovement, now);
        return result;
    }

    internal AgentToolResult RequestJump(float now)
    {
        return _jump.Request(now, _posture.Current);
    }

    internal void Release()
    {
        _follow.Release();
        _jump.Release();
        _posture.Release();
        _locomotion.Release();
        _facing.Release();
    }

    internal void StopQuietly()
    {
        _jump.Cancel("controller shutdown");
        _locomotion.StopQuietly();
    }
}
