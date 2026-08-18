using UnityEngine;

namespace Ramblers;

/// <summary>
/// Owns the companion's single physical gaze. Long-running behaviours publish
/// a normal target while short-lived actions may temporarily take exclusive
/// attention without competing writes to the replicated head pose.
/// </summary>
internal sealed class CompanionAttention
{
    private readonly CompanionFacing _facing;

    private bool _hasFollowTarget;
    private Vector3 _followTarget;
    private bool _inspectionActive;
    private bool _hasInspectionTarget;
    private Vector3 _inspectionTarget;

    internal CompanionAttention(CompanionFacing facing)
    {
        _facing = facing;
    }

    internal Vector2 HeadState => _facing.HeadState;
    internal float LastBodyYaw => _facing.LastBodyYaw;
    internal float LastTargetYaw => _facing.LastTargetYaw;
    internal float LastAimYawError => _facing.LastAimYawError;
    internal float LastAimPitchError => _facing.LastAimPitchError;
    internal Vector3 LastAimDirection => _facing.LastAimDirection;

    internal bool IsAimWithin(float yawDegrees, float pitchDegrees)
    {
        return _facing.LastAimYawError <= yawDegrees &&
               _facing.LastAimPitchError <= pitchDegrees;
    }

    internal void Bind(CompanionBody body, float now)
    {
        _hasFollowTarget = false;
        _followTarget = Vector3.zero;
        _inspectionActive = false;
        _hasInspectionTarget = false;
        _inspectionTarget = Vector3.zero;
        _facing.Bind(body, now);
    }

    internal void Tick(float now)
    {
        if (_inspectionActive && _hasInspectionTarget)
        {
            _facing.Face(_inspectionTarget, now);
            return;
        }

        if (_hasFollowTarget)
            _facing.Face(_followTarget, now);
    }

    internal void SetFollowTarget(Vector3 target)
    {
        _followTarget = target;
        _hasFollowTarget = true;
    }

    internal void ClearFollowTarget()
    {
        _hasFollowTarget = false;
    }

    internal void BeginInspection(Vector3 target, float now)
    {
        _inspectionActive = true;
        _inspectionTarget = target;
        _hasInspectionTarget = true;
        _facing.ResumeAt(now);
    }

    internal void SetInspectionTarget(Vector3 target)
    {
        _inspectionTarget = target;
        _hasInspectionTarget = true;
    }

    internal void EndInspection(float now)
    {
        _inspectionActive = false;
        _hasInspectionTarget = false;
        _inspectionTarget = Vector3.zero;
        _facing.ResumeAt(now);
    }

    internal void ResumeAt(float now)
    {
        _facing.ResumeAt(now);
    }

    internal void Release()
    {
        _hasFollowTarget = false;
        _inspectionActive = false;
        _hasInspectionTarget = false;
        _facing.Release();
    }
}
