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

    // A deliberate look is mouse motion, not teleportation. Aiming straight at
    // a new target would put the whole angular error into headState.x on the
    // first frame, a residual only a fast flick reaches, because the stock
    // 180 deg/s drain is eating the input the whole time a real hand is moving.
    // The gain gives the ease-out a hand has on approach; the cap is its peak.
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

    /// <summary>
    /// Re-bases the update clock when facing resumes after a gap, so the first
    /// step is not credited with all the time since the last one.
    /// </summary>
    internal void ResumeAt(float now)
    {
        _lastUpdateAt = now;
    }

    /// <summary>
    /// Whether head yaw may be absorbed into the body. Stock
    /// PlayerMover.UpdatePerFrameRotation skips its drain block entirely while
    /// PlayerSitter.isSittingCorrected is true, so a seated player is the one
    /// case that can hold a sustained head yaw instead of turning to face
    /// what they are looking at.
    /// </summary>
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

        // PlayerHead's replicated Vector2 is (yaw relative to the body, pitch).
        // Unity's positive X rotation looks downward, hence the negative pitch.
        // The aim is wherever the head is currently pointing; look input moves
        // it toward the target at a bounded rate, exactly as a hand on a mouse
        // does, and PlayerHead.SetHeadStateLocal accumulates that delta.
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

        // Stock PlayerMover.UpdatePerFrameRotation subtracts up to 180 degrees
        // per second from headState.x and adds it to PlayerCharacter.kernal, so
        // the residual is a lag buffer rather than a pose: it always decays to
        // zero once the aim settles. That method is local-only, so a
        // connectionless non-local companion performs the same step here.
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

        // The body rotation is sampled by the already-owned HouseNetworkTransform;
        // residual head pose uses the stock SyncVar/animator path.
        _body.Character.head.headState = _headState;
        _body.Networking.NetworkheadState = _headState;
    }

    /// <summary>
    /// One frame of simulated look input: proportional to the error so the aim
    /// eases onto the target, capped so it never exceeds a plausible hand.
    /// </summary>
    private static float LookStep(float error, float elapsed, float maxStep)
    {
        var step = Mathf.Clamp(error * LookApproachGain * elapsed, -maxStep, maxStep);
        return Mathf.Abs(step) > Mathf.Abs(error) ? error : step;
    }

    /// <summary>
    /// PlayerHead.SetHeadStateLocal shrinks the side-look allowance as the head
    /// pitches down, so looking at the ground and far to the side at once is not
    /// a pose a player can hold. Reproduced on the same ellipse the stock method
    /// uses, gated on the same downward-pitch test.
    /// </summary>
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
