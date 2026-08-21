using System.Text;
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
    internal bool Moving;
    internal float CommandedSpeed;
    internal float SteeringAngle;
    internal float Clearance;
    internal bool DirectPathBlocked;
    internal bool DirectGroundLimited;
    internal float GroundResponse;
    internal float SteepScalar;
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
    // Movement intent is refreshed on the 10 Hz navigation cadence. Include one
    // full cadence interval beyond the 0.45s commit window so the support and
    // obstacle proof still covers motion until the first tick after expiry.
    private const float BrakingLookahead = 0.55f;
    private const float ObstacleProbeDistance = 1.5f;
    internal const float MinimumClearance = 0.7f;
    private const float MinimumGroundResponse = 0.08f;
    private const float WalkableSweepNormalY = 0.7f;
    private const float GroundSupportProbeStep = 0.2f;
    private const float MaximumGroundGrade = 1f;
    private const float GroundHeightTolerance = 0.2f;
    private const int MaximumUnsupportedProbeRun = 1;
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
    internal float LastSteepScalar => _lastSteepScalar;
    internal string LastDirectHit => _lastDirectHit;
    internal string LastProbeSummary => _lastProbeSummary;

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
        _lastDirectGroundLimited = false;
        _lastGroundResponse = 1f;
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

        MovementGait requestedGait;
        var gaitSpeed = PreviewMovementSpeed(pathDistance, out requestedGait);

        // Look far enough ahead to stop from the gait being requested. The sweep never
        // shortens below the walking probe, so obstacle detection is unchanged at walk.
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

        // Use the exact stock walk or run speed. Only immediate obstacle clearance
        // may cap it for collision safety; distance to the target never creates a
        // third, artificial "jog" speed.
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

    /// <summary>
    /// Keeps forward intent through a route-proven jump or ledge transition.
    /// Ordinary steering remains clearance-gated; this narrow path is entered
    /// only after follow code has committed to replaying a human traversal
    /// breadcrumb, where braking at the edge or obstacle would defeat it.
    /// </summary>
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
        var directGroundResponse = MeasureGroundResponse(
            desiredDirection,
            out directSteepScalar);
        if (!HasGroundSupportAhead(desiredDirection, probeDistance))
            directGroundResponse = 0f;
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
            if (candidateClearance >= MinimumClearance &&
                candidateGroundResponse >= MinimumGroundResponse &&
                !HasGroundSupportAhead(candidate, probeDistance))
            {
                candidateGroundResponse = 0f;
            }
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

            // Score on the walking probe window so a longer sweep at running speed
            // cannot outweigh the turn penalty and change which detour is chosen.
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

    /// <summary>
    /// Asks the stock ground solver how much of a candidate heading it would
    /// actually pass to the rigidbody. Clearance alone cannot identify a steep
    /// grassy face whose slope limiter reduces an otherwise clear command to
    /// zero, which is the runtime failure this check is intended to expose.
    /// </summary>
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

    /// <summary>
    /// Verifies that an ordinary grounded move has floor beneath the body's
    /// forward corridor. A single unsupported sample is tolerated so the
    /// companion crosses tiny mesh seams, but a real gap cannot look like an
    /// unobstructed path merely because the head ray and current slope are clear.
    /// Recorded jump/drop traversal uses CommitTraversalDirection and remains
    /// the only path allowed to cross unsupported ground intentionally.
    /// </summary>
    internal bool HasGroundSupportAhead(Vector3 direction, float distance)
    {
        if (_body == null || !_body.IsAlive)
        {
            return false;
        }
        if (_body.Character?.ground == null ||
            !_body.Character.ground.isGrounded)
        {
            // Ordinary mid-air steering preserves the previous movement path.
            // Only a grounded body can prove or disprove forward floor support.
            return true;
        }

        direction.y = 0f;
        if (direction.sqrMagnitude < 0.0001f)
            return true;
        direction.Normalize();

        var probeDistance = Mathf.Max(distance, GroundSupportProbeStep);
        var unsupportedRun = 0;
        var lastAlong = 0f;
        var lastSupportedAlong = 0f;
        var lastSupportedHeight = _body.Position.y;
        for (var along = GroundSupportProbeStep;
             along <= probeDistance + 0.01f;
             along += GroundSupportProbeStep)
        {
            lastAlong = along;
            var center = _body.Position + direction * along;
            float groundHeight;
            var continuityFrom = unsupportedRun > 0
                ? along - GroundSupportProbeStep
                : lastSupportedAlong;
            var supported = TryMeasureGroundHeight(
                center,
                along,
                lastSupportedHeight,
                continuityFrom,
                out groundHeight);
            if (supported)
            {
                unsupportedRun = 0;
                lastSupportedAlong = along;
                lastSupportedHeight = groundHeight;
                continue;
            }

            unsupportedRun++;
            if (unsupportedRun > MaximumUnsupportedProbeRun)
                return false;
        }

        // Always prove the destination of a short segment. A missing sample is
        // seam tolerance only when later support brackets it; an unsupported
        // trailing edge is a ledge, not a seam.
        if (probeDistance - lastAlong > 0.05f)
        {
            float groundHeight;
            var continuityFrom = unsupportedRun > 0
                ? probeDistance - GroundSupportProbeStep
                : lastSupportedAlong;
            var supported = TryMeasureGroundHeight(
                    _body.Position + direction * probeDistance,
                    probeDistance,
                    lastSupportedHeight,
                    continuityFrom,
                    out groundHeight);
            if (supported)
            {
                unsupportedRun = 0;
            }
            else
            {
                unsupportedRun++;
            }
        }

        return unsupportedRun == 0;
    }

    /// <summary>
    /// Conservative proof used before deleting a breadcrumb route prefix.
    /// The destination must be reachable by the body without a wall or an
    /// unsupported corridor; human-recorded traversal markers remain intact.
    /// </summary>
    internal bool CanTraverseGroundedSegment(Vector3 destination)
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
                   distance - 0.02f &&
               HasGroundSupportAhead(direction, distance);
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

    private bool TryMeasureGroundHeight(
        Vector3 point,
        float along,
        float previousHeight,
        float previousAlong,
        out float height)
    {
        height = 0f;
        var allowedChange =
            (along - previousAlong) * MaximumGroundGrade +
            GroundHeightTolerance;
        var originHeight = previousHeight + allowedChange + 0.05f;
        var castDepth = allowedChange * 2f + 0.1f;
        var layerMask = GetObstacleMask(_body.Character);
        var bodyCollider = _body.Character?.collision?.bodyCollider;
        if (bodyCollider?.gameObject != null)
            layerMask &= ~(1 << bodyCollider.gameObject.layer);
        if (_body.GameObject != null)
            layerMask &= ~(1 << _body.GameObject.layer);

        RaycastHit hit;
        if (!Physics.Raycast(
            new Vector3(point.x, originHeight, point.z),
            Vector3.down,
            out hit,
            castDepth,
            layerMask,
            QueryTriggerInteraction.Ignore))
        {
            return false;
        }

        if (hit.normal.y < WalkableSweepNormalY)
            return false;
        height = hit.point.y;
        return Mathf.Abs(height - previousHeight) <= allowedChange;
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

        // Big Walk meshes can expose tiny seams between otherwise continuous
        // floor pieces. The closest capsule sweep contact at those seams points
        // upward and is safe to cross; treating it as a wall makes follow pace
        // in place. Keep this on SweepTest, which is available in this IL2CPP
        // build, rather than SweepTestAll, which is stripped at runtime.
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

    /// <summary>
    /// Reports whether a body commanded to move failed to make spatial progress
    /// over the observation window. Vertical motion counts, so a deliberate
    /// jump or fall cannot be mislabeled as a horizontal stall.
    /// </summary>
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
