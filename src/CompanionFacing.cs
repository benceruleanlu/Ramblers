using UnityEngine;

namespace Ramblers;

internal sealed class CompanionFacing
{
    internal const float BodyTurnSpeed = 180f;

    private const float LookRate = 300f;
    private const float LookApproachGain = 8f;

    private const float FallbackSideLookLimit = 85f;
    private const float FallbackVerticalLookLimit = 55f;

    private readonly float _expectedUpdateInterval;

    private CompanionBody _body;
    private Vector2 _headState;
    private bool _bodyTurnAllowed = true;
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

    internal void ResumeAt(float now)
    {
        _lastUpdateAt = now;
    }

    internal void SetBodyTurnAllowed(bool allowed)
    {
        _bodyTurnAllowed = allowed;
    }

    internal void Release()
    {
        _body = null;
        _headState = Vector2.zero;
        _bodyTurnAllowed = true;
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

        var desiredPitch = -Mathf.Atan2(toTarget.y, horizontalDistance) * Mathf.Rad2Deg;
        var maxLookStep = LookRate * elapsed;
        var aimYaw = bodyYaw + _headState.x;
        var headYaw = Mathf.Clamp(
            _headState.x + LookStep(Mathf.DeltaAngle(aimYaw, targetYaw), elapsed, maxLookStep),
            -sideLookLimit,
            sideLookLimit);
        var headPitch = Mathf.Clamp(
            _headState.y + LookStep(desiredPitch - _headState.y, elapsed, maxLookStep),
            -upperLookLimit,
            lowerLookLimit);
        ApplyLowerCornerLimit(ref headYaw, ref headPitch, sideLookLimit, lowerLookLimit);

        var bodyStep = _bodyTurnAllowed
            ? Mathf.Clamp(headYaw, -BodyTurnSpeed * elapsed, BodyTurnSpeed * elapsed)
            : 0f;
        headYaw -= bodyStep;
        if (bodyStep != 0f)
        {
            networkTransform.targetRotation =
                Quaternion.AngleAxis(bodyStep, Vector3.up) * currentRotation;
        }

        _headState = new Vector2(headYaw, headPitch);
        _lastBodyYaw = bodyYaw + bodyStep;
        _lastTargetYaw = targetYaw;
        _lastAimYawError = Mathf.Abs(Mathf.DeltaAngle(
            _lastBodyYaw + headYaw,
            targetYaw));
        _lastAimPitchError = Mathf.Abs(desiredPitch - headPitch);
        _lastAimDirection = Quaternion.Euler(
            headPitch,
            _lastBodyYaw + headYaw,
            0f) * Vector3.forward;

        _body.Character.head.headState = _headState;
        _body.Networking.NetworkheadState = _headState;
    }

    private static float LookStep(float error, float elapsed, float maxStep)
    {
        var step = Mathf.Clamp(error * LookApproachGain * elapsed, -maxStep, maxStep);
        return Mathf.Abs(step) > Mathf.Abs(error) ? error : step;
    }

    private static void ApplyLowerCornerLimit(
        ref float headYaw,
        ref float headPitch,
        float sideLookLimit,
        float lowerLookLimit)
    {
        if (headPitch <= 0f || sideLookLimit <= 0.01f || lowerLookLimit <= 0.01f)
            return;

        var scale = lowerLookLimit / sideLookLimit;
        var limited = Vector2.ClampMagnitude(
            new Vector2(headYaw * scale, headPitch),
            lowerLookLimit);
        headYaw = limited.x / scale;
        headPitch = limited.y;
    }
}
