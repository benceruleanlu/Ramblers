using System.Text;
using UnityEngine;

namespace Ramblers;

internal enum MovementGait
{
    Stopped,
    Walk,
    Run
}

internal struct SteeringStatus
{
    internal bool Moving;
    internal float CommandedSpeed;
    internal float SteeringAngle;
    internal float Clearance;
    internal bool DirectPathBlocked;
    internal bool DirectGroundLimited;
    internal float GroundResponse;
    internal float SteepScalar;
}

internal sealed class CompanionLocomotion
{

    internal const float RunStartDistance = 6.75f;
    private const float FallbackWalkSpeed = 3f;
    private const float FallbackRunSpeed = 5.5f;

    private const float BrakingLookahead = 0.55f;
    private const float ObstacleProbeDistance = 1.5f;
    internal const float MinimumClearance = 0.7f;
    private const float MinimumGroundResponse = 0.08f;
    private const float WalkableSweepNormalY = 0.7f;
    private const float AvoidanceSideHold = 1.5f;
    private const float StuckObservationWindow = 2.5f;
    private const float StuckMovementThreshold = 0.15f;

    private static readonly float[] SteeringAngles =
    {
        0f,
        25f,
        -25f,
        50f,
        -50f,
        75f,
        -75f,
        95f,
        -95f,
        125f,
        -125f,
        155f,
        -155f,
        180f
    };

    private readonly LogLatch _stuckWarningLog = new LogLatch();

    private CompanionBody _body;
    private Vector3 _lastMovementIntent;
    private float _walkSpeed = FallbackWalkSpeed;
    private float _runSpeed = FallbackRunSpeed;
    private float _crouchWalkSpeed = FallbackWalkSpeed;
    private float _crouchRunSpeed = FallbackRunSpeed;
    private bool _gaitSpeedsFromTunings;
    private bool _crouchGaitSpeedsFromTunings;
    private CompanionPosture _posture = CompanionPosture.Standing;
    private MovementGait _gait = MovementGait.Stopped;
    private float _lastCommandedSpeed;
    private int _avoidanceSign;
    private float _avoidanceSignUntil;
    private float _lastSteeringAngle;
    private float _lastClearance;
    private bool _lastDirectPathBlocked;
    private bool _lastDirectGroundLimited;
    private float _lastGroundResponse;
    private float _lastSlopeResponse;
    private string _lastSteeringAuthority = "not_sampled";
    private float _lastSteepScalar;
    private string _lastDirectHit = "clear";
    private string _lastProbeSummary = "not_sampled";
    private Vector3 _progressAnchor;
    private float _progressWindowStartedAt;

    internal float WalkSpeed =>
        _posture == CompanionPosture.Crouching ? _crouchWalkSpeed : _walkSpeed;
    internal float RunSpeed =>
        _posture == CompanionPosture.Crouching ? _crouchRunSpeed : _runSpeed;
    internal bool GaitSpeedsFromTunings =>
        _posture == CompanionPosture.Crouching
            ? _crouchGaitSpeedsFromTunings
            : _gaitSpeedsFromTunings;
    internal CompanionPosture Posture => _posture;
    internal MovementGait Gait => _gait;
    internal float LastCommandedSpeed => _lastCommandedSpeed;
    internal Vector3 LastMovementIntent => _lastMovementIntent;
    internal float LastSteeringAngle => _lastSteeringAngle;
    internal float LastClearance => _lastClearance;
    internal bool LastDirectPathBlocked => _lastDirectPathBlocked;
    internal bool LastDirectGroundLimited => _lastDirectGroundLimited;
    internal float LastGroundResponse => _lastGroundResponse;
    internal float LastSlopeResponse => _lastSlopeResponse;
    internal string LastSteeringAuthority => _lastSteeringAuthority;
    internal float LastSteepScalar => _lastSteepScalar;
    internal string LastDirectHit => _lastDirectHit;
    internal string LastProbeSummary => _lastProbeSummary;

    internal string DescribeGait()
    {
        return _gait.ToString().ToLowerInvariant();
    }

    internal void ResolveGaitSpeeds(PlayerCharacter character)
    {
        var tunings = character.tunings;
        var hasTunedWalkSpeed = tunings != null && tunings.forwardSpeed > 0.01f;
        _walkSpeed = hasTunedWalkSpeed ? tunings.forwardSpeed : FallbackWalkSpeed;

        var hasTunedRunSpeed = tunings != null && tunings.forwardSprintSpeed > _walkSpeed;
        _runSpeed = hasTunedRunSpeed
            ? tunings.forwardSprintSpeed
            : Mathf.Max(_walkSpeed, FallbackRunSpeed);
        _gaitSpeedsFromTunings = hasTunedWalkSpeed && hasTunedRunSpeed;

        var hasTunedCrouchWalkSpeed = tunings != null && tunings.crouchForwardSpeed > 0.01f;
        _crouchWalkSpeed = hasTunedCrouchWalkSpeed
            ? tunings.crouchForwardSpeed
            : _walkSpeed;

        var hasTunedCrouchRunSpeed = tunings != null &&
                                     tunings.crouchForwardSprintSpeed > _crouchWalkSpeed;
        _crouchRunSpeed = hasTunedCrouchRunSpeed
            ? tunings.crouchForwardSprintSpeed
            : _crouchWalkSpeed;
        _crouchGaitSpeedsFromTunings =
            hasTunedCrouchWalkSpeed && hasTunedCrouchRunSpeed;
    }

    internal void Bind(CompanionBody body, float now)
    {
        _body = body;
        _lastMovementIntent = Vector3.zero;
        _avoidanceSign = 0;
        _lastSteeringAngle = 0f;
        _lastClearance = ObstacleProbeDistance;
        _lastDirectPathBlocked = false;
        _lastDirectGroundLimited = false;
        _lastGroundResponse = 1f;
        _lastSlopeResponse = 1f;
        _lastSteeringAuthority = "not_sampled";
        _lastSteepScalar = 1f;
        _lastDirectHit = "clear";
        _lastProbeSummary = "not_sampled";
        _posture = CompanionPosture.Standing;
        _gait = MovementGait.Stopped;
        _lastCommandedSpeed = 0f;
        ResetProgressObservation(now);
    }

    internal void Release()
    {
        _body = null;
        _lastMovementIntent = Vector3.zero;
        _avoidanceSign = 0;
        _walkSpeed = FallbackWalkSpeed;
        _runSpeed = FallbackRunSpeed;
        _crouchWalkSpeed = FallbackWalkSpeed;
        _crouchRunSpeed = FallbackRunSpeed;
        _gaitSpeedsFromTunings = false;
        _crouchGaitSpeedsFromTunings = false;
        _posture = CompanionPosture.Standing;
        _gait = MovementGait.Stopped;
        _lastCommandedSpeed = 0f;
    }

    internal bool TrySteerToward(
        Vector3 desiredDirection,
        float pathDistance,
        float now,
        out SteeringStatus status)
    {
        status = default(SteeringStatus);

        MovementGait requestedGait;
        var gaitSpeed = PreviewMovementSpeed(pathDistance, out requestedGait);

        var probeDistance = Mathf.Max(ObstacleProbeDistance, gaitSpeed * BrakingLookahead);

        Vector3 steeringDirection;
        float steeringAngle;
        float clearance;
        bool directBlocked;
        bool directGroundLimited;
        float groundResponse;
        float steepScalar;
        if (!TryChooseSteering(
                desiredDirection,
                now,
                probeDistance,
                out steeringDirection,
                out steeringAngle,
                out clearance,
                out directBlocked,
                out directGroundLimited,
                out groundResponse,
                out steepScalar))
        {
            _lastDirectPathBlocked = true;
            _lastDirectGroundLimited = directGroundLimited;
            _lastClearance = clearance;
            _lastGroundResponse = groundResponse;
            _lastSteepScalar = steepScalar;
            status.Clearance = clearance;
            status.DirectPathBlocked = true;
            status.DirectGroundLimited = directGroundLimited;
            status.GroundResponse = groundResponse;
            status.SteepScalar = steepScalar;
            return false;
        }

        _lastDirectPathBlocked = directBlocked;
        _lastDirectGroundLimited = directGroundLimited;
        _lastSteeringAngle = steeringAngle;
        _lastClearance = clearance;
        _lastGroundResponse = groundResponse;
        _lastSteepScalar = steepScalar;
        CommitMovementGait(requestedGait, pathDistance);

        var speed = Mathf.Min(gaitSpeed, clearance / BrakingLookahead);
        _lastCommandedSpeed = speed;
        SetMovementIntent(steeringDirection * speed);

        status.Moving = true;
        status.CommandedSpeed = speed;
        status.SteeringAngle = steeringAngle;
        status.Clearance = clearance;
        status.DirectPathBlocked = directBlocked;
        status.DirectGroundLimited = directGroundLimited;
        status.GroundResponse = groundResponse;
        status.SteepScalar = steepScalar;
        return true;
    }

    internal SteeringStatus CommitTraversalDirection(
        Vector3 desiredDirection,
        float pathDistance)
    {
        var status = default(SteeringStatus);
        desiredDirection.y = 0f;
        if (desiredDirection.sqrMagnitude < 0.0001f)
            return status;

        desiredDirection.Normalize();
        MovementGait requestedGait;
        var gaitSpeed = PreviewMovementSpeed(pathDistance, out requestedGait);
        var probeDistance = Mathf.Max(
            ObstacleProbeDistance,
            gaitSpeed * BrakingLookahead);
        string directHit;
        var clearance = MeasureClearance(
            desiredDirection,
            probeDistance,
            out directHit);
        var directBlocked = clearance < MinimumClearance;
        float steepScalar;
        var groundResponse = MeasureGroundResponse(desiredDirection, out steepScalar);
        var directGroundLimited = groundResponse < MinimumGroundResponse;

        _lastDirectPathBlocked = directBlocked || directGroundLimited;
        _lastDirectGroundLimited = directGroundLimited;
        _lastSteeringAngle = 0f;
        _lastClearance = clearance;
        _lastGroundResponse = groundResponse;
        _lastSlopeResponse = groundResponse;
        _lastSteeringAuthority = "committed_direction";
        _lastSteepScalar = steepScalar;
        _lastDirectHit = directHit;
        _lastProbeSummary = FormatProbe(0f, clearance, groundResponse, directHit);
        CommitMovementGait(requestedGait, pathDistance);
        _lastCommandedSpeed = gaitSpeed;
        SetMovementIntent(desiredDirection * gaitSpeed);

        status.Moving = true;
        status.CommandedSpeed = gaitSpeed;
        status.SteeringAngle = 0f;
        status.Clearance = clearance;
        status.DirectPathBlocked = directBlocked || directGroundLimited;
        status.DirectGroundLimited = directGroundLimited;
        status.GroundResponse = groundResponse;
        status.SteepScalar = steepScalar;
        return status;
    }

    internal void Stop(float now)
    {
        if (_lastMovementIntent.sqrMagnitude > 0f)
            SetMovementIntent(Vector3.zero);
        SetMovementGait(MovementGait.Stopped);
        _lastCommandedSpeed = 0f;
        ResetProgressObservation(now);
    }

    internal void StopQuietly()
    {
        try
        {
            SetMovementIntent(Vector3.zero);
        }
        catch
        {
            _lastMovementIntent = Vector3.zero;
        }

        SetMovementGait(MovementGait.Stopped);
    }

    internal void SetPosture(CompanionPosture posture)
    {
        _posture = posture;
    }

    private float PreviewMovementSpeed(
        float pathDistance,
        out MovementGait requestedGait)
    {
        requestedGait = _gait == MovementGait.Run || pathDistance >= RunStartDistance
            ? MovementGait.Run
            : MovementGait.Walk;
        return requestedGait == MovementGait.Run ? RunSpeed : WalkSpeed;
    }

    private void CommitMovementGait(MovementGait requestedGait, float pathDistance)
    {
        if (_gait == requestedGait)
            return;

        SetMovementGait(requestedGait);
        if (requestedGait == MovementGait.Run)
        {
            Plugin.Logger.LogInfo(
                "[FOLLOW] GAIT run " +
                $"trailDistance={pathDistance:F2}; latched until the next complete stop.");
        }
        else
        {
            Plugin.Logger.LogInfo(
                $"[FOLLOW] GAIT walk trailDistance={pathDistance:F2}.");
        }
    }

    private void SetMovementGait(MovementGait gait)
    {
        _gait = gait;
        if (_body == null || _body.Character?.sprinter == null)
            return;

        var sprinting = gait == MovementGait.Run;
        _body.Character.sprinter.isSprinting = sprinting;
        _body.Character.sprinter.sprintIsToggledOn = sprinting;
    }

    private void SetMovementIntent(Vector3 worldMovementIntent)
    {
        _body.Networking.NetworkcontrolsVelocity = worldMovementIntent;
        _lastMovementIntent = worldMovementIntent;
    }

    private bool TryChooseSteering(
        Vector3 desiredDirection,
        float now,
        float probeDistance,
        out Vector3 steeringDirection,
        out float steeringAngle,
        out float clearance,
        out bool directBlocked,
        out bool directGroundLimited,
        out float groundResponse,
        out float steepScalar)
    {
        steeringDirection = Vector3.zero;
        steeringAngle = 0f;
        clearance = 0f;

        string directHit;
        var directClearance = MeasureClearance(
            desiredDirection,
            probeDistance,
            out directHit);
        float directSteepScalar;
        var directSlopeResponse = MeasureGroundResponse(
            desiredDirection,
            out directSteepScalar);
        var directGroundResponse = directSlopeResponse;
        _lastSlopeResponse = directSlopeResponse;
        _lastSteeringAuthority = "stock_slope";
        directGroundLimited = directGroundResponse < MinimumGroundResponse;
        directBlocked = directClearance < MinimumClearance || directGroundLimited;
        groundResponse = directGroundResponse;
        steepScalar = directSteepScalar;
        _lastDirectHit = directHit;
        if (!directBlocked)
        {
            steeringDirection = desiredDirection;
            clearance = directClearance;
            _lastProbeSummary = FormatProbe(
                0f,
                directClearance,
                directGroundResponse,
                directHit);
            if (now >= _avoidanceSignUntil)
                _avoidanceSign = 0;
            return true;
        }

        var probeSummary = new StringBuilder(384);
        AppendProbe(
            probeSummary,
            0f,
            directClearance,
            directGroundResponse,
            directHit);
        var bestScore = float.NegativeInfinity;
        for (var index = 1; index < SteeringAngles.Length; index++)
        {
            var angle = SteeringAngles[index];
            var candidate = Quaternion.AngleAxis(angle, Vector3.up) * desiredDirection;
            string candidateHit;
            var candidateClearance = MeasureClearance(
                candidate,
                probeDistance,
                out candidateHit);
            float candidateSteepScalar;
            var candidateGroundResponse = MeasureGroundResponse(
                candidate,
                out candidateSteepScalar);
            AppendProbe(
                probeSummary,
                angle,
                candidateClearance,
                candidateGroundResponse,
                candidateHit);
            if (candidateClearance < MinimumClearance)
                continue;
            if (candidateGroundResponse < MinimumGroundResponse)
                continue;

            var candidateSign = Mathf.Abs(angle) >= 179f
                ? 0
                : angle > 0f ? 1 : -1;
            var turnPenalty = Mathf.Abs(angle) * 0.004f;
            var sideBonus = now < _avoidanceSignUntil && candidateSign == _avoidanceSign
                ? 0.35f
                : 0f;

            var score = Mathf.Min(candidateClearance, ObstacleProbeDistance)
                      - turnPenalty
                      + Mathf.Min(candidateGroundResponse, 1f) * 0.25f
                      + sideBonus;
            if (score <= bestScore)
                continue;

            bestScore = score;
            steeringDirection = candidate;
            steeringAngle = angle;
            clearance = candidateClearance;
            groundResponse = candidateGroundResponse;
            steepScalar = candidateSteepScalar;
        }

        if (bestScore == float.NegativeInfinity)
        {
            clearance = directClearance;
            _lastProbeSummary = probeSummary.ToString();
            return false;
        }

        _lastProbeSummary = probeSummary.ToString();
        if (Mathf.Abs(steeringAngle) < 179f)
        {
            _avoidanceSign = steeringAngle > 0f ? 1 : -1;
            _avoidanceSignUntil = now + AvoidanceSideHold;
        }
        return true;
    }

    private float MeasureGroundResponse(
        Vector3 direction,
        out float steepScalar)
    {
        steepScalar = 1f;
        var ground = _body?.Character?.ground;
        if (ground == null || !ground.isGrounded)
            return 1f;

        direction.y = 0f;
        if (direction.sqrMagnitude < 0.0001f)
            return 0f;

        direction.Normalize();
        var response = ground.GetSlopedMoveForce(direction, out steepScalar);
        return response.magnitude;
    }

    internal bool CanShortcutSegment(Vector3 destination)
    {
        if (_body == null || !_body.IsAlive)
            return false;

        var delta = destination - _body.Position;
        delta.y = 0f;
        var distance = delta.magnitude;
        if (distance < 0.05f)
            return true;

        var direction = delta / distance;
        string ignoredHit;
        return HasClearShortcutRay(direction, distance, 0.45f) &&
               HasClearShortcutRay(direction, distance, 1.1f) &&
               MeasureClearance(direction, distance, out ignoredHit) >=
                   distance - 0.02f;
    }

    private bool HasClearShortcutRay(
        Vector3 direction,
        float distance,
        float height)
    {
        var bodyCollider = _body.Character?.collision?.bodyCollider;
        var bodyRadius = bodyCollider == null ? 0.25f : bodyCollider.radius;
        var startOffset = Mathf.Min(distance, bodyRadius + 0.05f);
        var remaining = distance - startOffset;
        if (remaining <= 0.02f)
            return true;

        RaycastHit hit;
        return !Physics.Raycast(
            _body.Position + direction * startOffset + Vector3.up * height,
            direction,
            out hit,
            remaining,
            GetObstacleMask(_body.Character),
            QueryTriggerInteraction.Ignore);
    }

    private float MeasureClearance(
        Vector3 direction,
        float probeDistance,
        out string hitDescription)
    {
        var rigidbody = _body.Character.rb;
        if (rigidbody == null)
        {
            hitDescription = "no_rigidbody";
            return probeDistance;
        }

        RaycastHit hit;
        if (!rigidbody.SweepTest(
                direction,
                out hit,
                probeDistance,
                QueryTriggerInteraction.Ignore))
        {
            hitDescription = "clear";
            return probeDistance;
        }

        var hitTransform = hit.collider == null ? null : hit.collider.transform;
        if (_body != null && _body.Contains(hitTransform))
        {
            hitDescription = "ignored_self:" + DescribeHit(hit);
            return probeDistance;
        }

        if (hit.normal.y >= WalkableSweepNormalY)
        {
            hitDescription = "ignored_walkable:" + DescribeHit(hit);
            return probeDistance;
        }

        hitDescription = DescribeHit(hit);
        return hit.distance;
    }

    private string DescribeHit(RaycastHit hit)
    {
        var collider = hit.collider;
        if (collider == null)
            return "unknown_collider";

        var hitTransform = collider.transform;
        var hitName = hitTransform == null
            ? "unnamed"
            : SanitizeProbeText(hitTransform.name);
        var layer = collider.gameObject == null ? -1 : collider.gameObject.layer;
        var self = _body != null && _body.Contains(hitTransform);
        return $"{hitName}@layer{layer}:self={self}:normal={hit.normal}";
    }

    private static string SanitizeProbeText(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "unnamed";

        return value.Replace(',', '_').Replace(';', '_').Replace(' ', '_');
    }

    private static string FormatProbe(
        float angle,
        float clearance,
        float groundResponse,
        string hitDescription)
    {
        var builder = new StringBuilder(96);
        AppendProbe(builder, angle, clearance, groundResponse, hitDescription);
        return builder.ToString();
    }

    private static void AppendProbe(
        StringBuilder builder,
        float angle,
        float clearance,
        float groundResponse,
        string hitDescription)
    {
        if (builder.Length > 0)
            builder.Append(';');
        builder.Append(angle.ToString("+0;-0;0"));
        builder.Append(':');
        builder.Append(clearance.ToString("F2"));
        builder.Append("/g");
        builder.Append(groundResponse.ToString("F2"));
        builder.Append('/');
        builder.Append(hitDescription);
    }

    internal bool ObserveProgress(float now)
    {
        if (_lastCommandedSpeed <= 0.01f)
        {
            ResetProgressObservation(now);
            return false;
        }

        if (now - _progressWindowStartedAt < StuckObservationWindow)
            return false;

        var movement = Vector3.Distance(_progressAnchor, _body.Position);
        var stuck = movement < StuckMovementThreshold;
        if (stuck)
        {
            if (_stuckWarningLog.ShouldLog())
            {
                Plugin.Logger.LogWarning(
                    "[FOLLOW] POSSIBLY_STUCK " +
                    $"moved={movement:F2}m in {StuckObservationWindow:F1}s while commanded " +
                    $"speed={_lastCommandedSpeed:F2} m/s ({DescribeGait()}). " +
                    "Follow may attempt one bounded grounded traversal jump; " +
                    "teleport recovery remains disabled.");
            }
        }
        else
        {
            _stuckWarningLog.Reset();
        }

        _progressAnchor = _body.Position;
        _progressWindowStartedAt = now;
        return stuck;
    }

    internal void ResetProgressObservation(float now)
    {
        _progressAnchor = _body == null ? Vector3.zero : _body.Position;
        _progressWindowStartedAt = now;
        _stuckWarningLog.Reset();
    }

    internal static int GetObstacleMask(PlayerCharacter character)
    {
        if (character.ground != null && character.ground.layerMask.value != 0)
            return character.ground.layerMask.value;

        return Physics.DefaultRaycastLayers;
    }
}
