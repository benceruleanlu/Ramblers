using UnityEngine;

namespace Ramblers;

internal enum MovementGait
{
    Stopped,
    Walk,
    Run
}

/// <summary>
/// Result of one steering tick, reported to the behaviour that asked to move.
/// </summary>
internal struct SteeringStatus
{
    internal float SteeringAngle;
    internal float Clearance;
    internal bool DirectPathBlocked;
}

/// <summary>
/// Drives the companion's stock remote-player motor: gait selection, obstacle
/// sweeping, steering around blockages, and stuck observation. It is told a
/// direction to head and how long the remaining route is; it does not know what
/// is being approached or why, so every action can share it.
/// </summary>
internal sealed class CompanionLocomotion
{
    // PlayerNetworking.controlsVelocity is a world-space velocity in metres per
    // second: PlayerMover.FixedUpdate feeds it through PlayerGround.GetSlopedMoveForce
    // into the rigidbody for a remote body exactly as it does for a local one, whose
    // magnitude comes from PlayerMover.GetForwardSpeed(). Movement is therefore
    // commanded in game speed units, never as a normalized 0-1 intent.
    // Walking and running are discrete player gaits. The threshold is the midpoint
    // of the old blend interval, but there is no blended or jogging speed anymore.
    // A run remains latched until the companion comes to a complete stop; walking
    // may promote to running while moving, matching the stock player's controls.
    internal const float RunStartDistance = 6.75f;
    private const float FallbackWalkSpeed = 3f;
    private const float FallbackRunSpeed = 5.5f;
    private const float BrakingLookahead = 0.45f;
    private const float ObstacleProbeDistance = 1.5f;
    internal const float MinimumClearance = 0.7f;
    private const float AvoidanceSideHold = 0.6f;
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
        -95f
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

    internal string DescribeGait()
    {
        return _gait.ToString().ToLowerInvariant();
    }

    /// <summary>
    /// Reads the walk and sprint speeds from the live prefab before the body is
    /// spawned, so a failed spawn never leaves stale tunings behind.
    /// </summary>
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

    /// <summary>
    /// Steers toward <paramref name="desiredDirection"/>, choosing gait from the
    /// remaining route length. Returns false when no candidate heading had enough
    /// clearance; the caller decides what a blocked route means and must
    /// <see cref="Stop"/> if it wants the body to hold still.
    /// </summary>
    internal bool TrySteerToward(
        Vector3 desiredDirection,
        float pathDistance,
        float now,
        out SteeringStatus status)
    {
        status = default(SteeringStatus);

        var gaitSpeed = ResolveMovementSpeed(pathDistance);

        // Look far enough ahead to stop from the gait being requested. The sweep never
        // shortens below the walking probe, so obstacle detection is unchanged at walk.
        var probeDistance = Mathf.Max(ObstacleProbeDistance, gaitSpeed * BrakingLookahead);

        Vector3 steeringDirection;
        float steeringAngle;
        float clearance;
        bool directBlocked;
        if (!TryChooseSteering(
                desiredDirection,
                now,
                probeDistance,
                out steeringDirection,
                out steeringAngle,
                out clearance,
                out directBlocked))
        {
            _lastDirectPathBlocked = true;
            _lastClearance = clearance;
            status.Clearance = clearance;
            status.DirectPathBlocked = true;
            return false;
        }

        _lastDirectPathBlocked = directBlocked;
        _lastSteeringAngle = steeringAngle;
        _lastClearance = clearance;

        // Use the exact stock walk or run speed. Only immediate obstacle clearance
        // may cap it for collision safety; distance to the target never creates a
        // third, artificial "jog" speed.
        var speed = Mathf.Min(gaitSpeed, clearance / BrakingLookahead);
        _lastCommandedSpeed = speed;
        SetMovementIntent(steeringDirection * speed);

        status.SteeringAngle = steeringAngle;
        status.Clearance = clearance;
        status.DirectPathBlocked = directBlocked;
        return true;
    }

    /// <summary>
    /// Brings the body to a complete stop, which also unlatches a run.
    /// </summary>
    internal void Stop(float now)
    {
        if (_lastMovementIntent.sqrMagnitude > 0f)
            SetMovementIntent(Vector3.zero);
        SetMovementGait(MovementGait.Stopped);
        _lastCommandedSpeed = 0f;
        ResetProgressObservation(now);
    }

    /// <summary>
    /// Stop path for use when the body may already be gone; never throws.
    /// </summary>
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

    private float ResolveMovementSpeed(float pathDistance)
    {
        if (_gait != MovementGait.Run && pathDistance >= RunStartDistance)
        {
            SetMovementGait(MovementGait.Run);
            Plugin.Logger.LogInfo(
                "[FOLLOW] GAIT run " +
                $"trailDistance={pathDistance:F2}; latched until the next complete stop.");
        }
        else if (_gait == MovementGait.Stopped)
        {
            SetMovementGait(MovementGait.Walk);
            Plugin.Logger.LogInfo(
                $"[FOLLOW] GAIT walk trailDistance={pathDistance:F2}.");
        }

        return _gait == MovementGait.Run ? RunSpeed : WalkSpeed;
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
        out bool directBlocked)
    {
        steeringDirection = Vector3.zero;
        steeringAngle = 0f;
        clearance = 0f;

        var directClearance = MeasureClearance(desiredDirection, probeDistance);
        directBlocked = directClearance < MinimumClearance;
        if (!directBlocked)
        {
            steeringDirection = desiredDirection;
            clearance = directClearance;
            _avoidanceSign = 0;
            return true;
        }

        var bestScore = float.NegativeInfinity;
        for (var index = 1; index < SteeringAngles.Length; index++)
        {
            var angle = SteeringAngles[index];
            var candidate = Quaternion.AngleAxis(angle, Vector3.up) * desiredDirection;
            var candidateClearance = MeasureClearance(candidate, probeDistance);
            if (candidateClearance < MinimumClearance)
                continue;

            var candidateSign = angle > 0f ? 1 : -1;
            var turnPenalty = Mathf.Abs(angle) * 0.004f;
            var sideBonus = now < _avoidanceSignUntil && candidateSign == _avoidanceSign
                ? 0.35f
                : 0f;

            // Score on the walking probe window so a longer sweep at running speed
            // cannot outweigh the turn penalty and change which detour is chosen.
            var score = Mathf.Min(candidateClearance, ObstacleProbeDistance)
                      - turnPenalty
                      + sideBonus;
            if (score <= bestScore)
                continue;

            bestScore = score;
            steeringDirection = candidate;
            steeringAngle = angle;
            clearance = candidateClearance;
        }

        if (bestScore == float.NegativeInfinity)
        {
            clearance = directClearance;
            return false;
        }

        _avoidanceSign = steeringAngle > 0f ? 1 : -1;
        _avoidanceSignUntil = now + AvoidanceSideHold;
        return true;
    }

    private float MeasureClearance(Vector3 direction, float probeDistance)
    {
        if (_body.Character.rb == null)
            return probeDistance;

        RaycastHit hit;
        if (!_body.Character.rb.SweepTest(
            direction,
            out hit,
            probeDistance,
            QueryTriggerInteraction.Ignore))
        {
            return probeDistance;
        }

        return hit.distance;
    }

    /// <summary>
    /// Reports a body that is being commanded to move but is not making ground.
    /// Detection only; no recovery is attempted.
    /// </summary>
    internal void ObserveProgress(float now)
    {
        if (_lastCommandedSpeed <= 0.01f)
        {
            ResetProgressObservation(now);
            return;
        }

        if (now - _progressWindowStartedAt < StuckObservationWindow)
            return;

        var movement = BreadcrumbTrail.HorizontalDistance(_progressAnchor, _body.Position);
        if (movement < StuckMovementThreshold)
        {
            if (_stuckWarningLog.ShouldLog())
            {
                Plugin.Logger.LogWarning(
                    "[FOLLOW] POSSIBLY_STUCK " +
                    $"moved={movement:F2}m in {StuckObservationWindow:F1}s while commanded " +
                    $"speed={_lastCommandedSpeed:F2} m/s ({DescribeGait()}). " +
                    "Detection only; no recovery attempted.");
            }
        }
        else
        {
            _stuckWarningLog.Reset();
        }

        _progressAnchor = _body.Position;
        _progressWindowStartedAt = now;
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
