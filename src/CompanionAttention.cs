using UnityEngine;

namespace Ramblers;

internal enum GazeChannel
{
    Follow = 0,
    Ambient = 1,
    Navigation = 2,
    Manipulation = 3,
    Inspection = 4
}

internal sealed class CompanionAttention
{
    private const int ChannelCount = 5;
    private const int NoChannel = -1;

    private readonly CompanionFacing _facing;
    private readonly bool[] _claimed = new bool[ChannelCount];
    private readonly Vector3[] _targets = new Vector3[ChannelCount];

    private int _activeChannel = NoChannel;

    internal CompanionAttention(CompanionFacing facing)
    {
        _facing = facing;
    }

    internal Vector2 HeadState => _facing.HeadState;
    internal float LastBodyYaw => _facing.LastBodyYaw;
    internal float LastTargetYaw => _facing.LastTargetYaw;
    internal float LastAimYawError => _facing.LastAimYawError;
    internal float LastAimPitchError => _facing.LastAimPitchError;

    internal bool IsAimWithin(
        GazeChannel channel,
        float yawDegrees,
        float pitchDegrees)
    {
        return _activeChannel == (int)channel &&
               _facing.LastAimYawError <= yawDegrees &&
               _facing.LastAimPitchError <= pitchDegrees;
    }

    internal bool IsOverridden(GazeChannel channel)
    {
        return _activeChannel > (int)channel;
    }

    internal void SetBodyTurnAllowed(bool allowed)
    {
        _facing.SetBodyTurnAllowed(allowed);
    }

    internal Vector3 AimDirectionFor(GazeChannel channel)
    {
        return _activeChannel == (int)channel
            ? _facing.LastAimDirection
            : Vector3.zero;
    }

    internal void Bind(CompanionBody body, float now)
    {
        ClearAll();
        _facing.Bind(body, now);
    }

    internal void Tick(float now)
    {
        var channel = ResolveActiveChannel();
        if (channel == NoChannel)
        {
            _activeChannel = NoChannel;
            return;
        }

        if (channel != _activeChannel)
        {
            _facing.ResumeAt(now);
            _activeChannel = channel;
        }

        _facing.Face(_targets[channel], now);
    }

    internal void SetTarget(GazeChannel channel, Vector3 target)
    {
        var index = (int)channel;
        _claimed[index] = true;
        _targets[index] = target;
    }

    internal void ClearTarget(GazeChannel channel)
    {
        _claimed[(int)channel] = false;
    }

    internal void ResumeAt(float now)
    {
        _facing.ResumeAt(now);
    }

    internal void Release()
    {
        ClearAll();
        _facing.Release();
    }

    private int ResolveActiveChannel()
    {
        for (var index = ChannelCount - 1; index >= 0; index--)
        {
            if (_claimed[index])
                return index;
        }

        return NoChannel;
    }

    private void ClearAll()
    {
        for (var index = 0; index < ChannelCount; index++)
        {
            _claimed[index] = false;
            _targets[index] = Vector3.zero;
        }

        _activeChannel = NoChannel;
    }
}
