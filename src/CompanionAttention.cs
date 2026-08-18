using UnityEngine;

namespace Ramblers;

/// <summary>
/// Who the companion's single physical gaze belongs to. Higher values win, so a
/// deliberate inspection overrides walking-toward and both override the ambient
/// habit of watching the human.
/// </summary>
internal enum GazeChannel
{
    Follow = 0,
    Navigation = 1,
    Manipulation = 2,
    Inspection = 3
}

/// <summary>
/// Owns the companion's single physical gaze. Behaviours publish a target on
/// their own channel and the highest-priority claim wins, so no two behaviours
/// ever make competing writes to the replicated head pose.
/// </summary>
internal sealed class CompanionAttention
{
    private const int ChannelCount = 4;
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

    /// <summary>
    /// Whether the given channel currently owns the gaze and has settled within
    /// tolerance. Asking per channel stops one behaviour from mistaking another
    /// behaviour's alignment for its own.
    /// </summary>
    internal bool IsAimWithin(
        GazeChannel channel,
        float yawDegrees,
        float pitchDegrees)
    {
        return _activeChannel == (int)channel &&
               _facing.LastAimYawError <= yawDegrees &&
               _facing.LastAimPitchError <= pitchDegrees;
    }

    /// <summary>
    /// The direction the head is actually pointing, but only for the channel
    /// that owns the gaze. Callers that lose the gaze get <see cref="Vector3.zero"/>
    /// and are expected to fall back to their own target geometry.
    /// </summary>
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

        // A change of owner is a gap in that channel's aiming history, so the
        // first step after a handover must not be credited with the whole gap.
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

    /// <summary>
    /// Re-bases the aiming clock when a behaviour resumes after a pause without
    /// a change of gaze owner.
    /// </summary>
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
