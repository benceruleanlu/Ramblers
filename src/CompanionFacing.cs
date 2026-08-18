using UnityEngine;

namespace Ramblers;

/// <summary>
/// Aims the companion at a world point: the whole body turns at Big Walk's stock
/// rate and whatever yaw the body has not yet absorbed is expressed as head pose.
/// The point is supplied by the caller, so an action can aim at the speaker, at
/// its destination, or at an object without this class knowing the difference.
/// </summary>
internal sealed class CompanionFacing
{
    internal const float BodyTurnSpeed = 180f;
    private const float FallbackSideLookLimit = 85f;
    private const float FallbackVerticalLookLimit = 55f;

    private readonly float _expectedUpdateInterval;

    private CompanionBody _body;
    private Vector2 _headState;
    private float _lastUpdateAt;
    private float _lastBodyYaw;
    private float _lastTargetYaw;
    private float _lastAimYawError = float.PositiveInfinity;
    private float _lastAimPitchError = float.PositiveInfinity;
    private Vector3 _lastAimDirection = Vector3.forward;

    internal CompanionFacing(float expectedUpdateInterval)
    {
        _expectedUpdateInterval = expectedUpdateInterval;
    }

    internal Vector2 HeadState => _headState;
    internal float LastBodyYaw => _lastBodyYaw;
    internal float LastTargetYaw => _lastTargetYaw;
    internal float LastAimYawError => _lastAimYawError;
    internal float LastAimPitchError => _lastAimPitchError;
    internal Vector3 LastAimDirection => _lastAimDirection;

    internal void Bind(CompanionBody body, float now)
    {
        _body = body;
        _headState = Vector2.zero;
        _lastUpdateAt = now;
        _lastBodyYaw = body.Transform.eulerAngles.y;
        _lastTargetYaw = _lastBodyYaw;
        _lastAimYawError = float.PositiveInfinity;
        _lastAimPitchError = float.PositiveInfinity;
        _lastAimDirection = body.Transform.forward;
    }

    /// <summary>
    /// Re-bases the update clock when facing resumes after a gap, so the first
    /// step is not credited with all the time since the last one.
    /// </summary>
    internal void ResumeAt(float now)
    {
        _lastUpdateAt = now;
    }

    internal void Release()
    {
        _body = null;
        _headState = Vector2.zero;
        _lastUpdateAt = 0f;
        _lastBodyYaw = 0f;
        _lastTargetYaw = 0f;
        _lastAimYawError = float.PositiveInfinity;
        _lastAimPitchError = float.PositiveInfinity;
        _lastAimDirection = Vector3.forward;
    }

    internal void Face(Vector3 targetPoint, float now)
    {
        if (_body == null ||
            _body.Character?.head == null ||
            _body.Character.houseNetworkTransform == null ||
            _body.Networking == null)
            return;

        var toTarget = targetPoint - _body.HeadPosition;
        var horizontalDirection = new Vector3(toTarget.x, 0f, toTarget.z);
        var horizontalDistance = horizontalDirection.magnitude;
        if (horizontalDistance < 0.001f && Mathf.Abs(toTarget.y) < 0.001f)
        {
            _lastAimYawError = 0f;
            _lastAimPitchError = 0f;
            return;
        }

        var networkTransform = _body.Character.houseNetworkTransform;
        var currentRotation = networkTransform.targetRotation;
        var currentForward = currentRotation * Vector3.forward;
        currentForward.y = 0f;
        if (currentForward.sqrMagnitude < 0.0001f)
        {
            currentForward = _body.Transform.forward;
            currentForward.y = 0f;
        }

        var bodyYaw = Mathf.Atan2(currentForward.x, currentForward.z) * Mathf.Rad2Deg;
        var targetYaw = horizontalDistance < 0.001f
            ? bodyYaw
            : Mathf.Atan2(horizontalDirection.x, horizontalDirection.z) * Mathf.Rad2Deg;
        var elapsed = _lastUpdateAt <= 0f
            ? _expectedUpdateInterval
            : Mathf.Clamp(now - _lastUpdateAt, 0f, _expectedUpdateInterval * 2f);
        _lastUpdateAt = now;

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

        var tunings = _body.Character.tunings;
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
        var desiredPitch = -Mathf.Atan2(toTarget.y, horizontalDistance) * Mathf.Rad2Deg;
        var clampedHeadYaw = Mathf.Clamp(remainingYaw, -sideLookLimit, sideLookLimit);
        var clampedHeadPitch = Mathf.Clamp(
            desiredPitch,
            -upperLookLimit,
            lowerLookLimit);
        _headState = new Vector2(clampedHeadYaw, clampedHeadPitch);

        _lastBodyYaw = bodyYaw + bodyStep;
        _lastTargetYaw = targetYaw;
        _lastAimYawError = Mathf.Abs(Mathf.DeltaAngle(
            _lastBodyYaw + clampedHeadYaw,
            targetYaw));
        _lastAimPitchError = Mathf.Abs(desiredPitch - clampedHeadPitch);
        _lastAimDirection = Quaternion.Euler(
            clampedHeadPitch,
            _lastBodyYaw + clampedHeadYaw,
            0f) * Vector3.forward;

        // The body rotation is sampled by the already-owned HouseNetworkTransform;
        // residual head pose uses the stock SyncVar/animator path.
        _body.Character.head.headState = _headState;
        _body.Networking.NetworkheadState = _headState;
    }
}
