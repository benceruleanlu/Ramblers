using UnityEngine;

namespace Ramblers;

internal readonly struct CompanionTraversalCompleted
{
    internal CompanionTraversalCompleted(
        Vector3 takeoff,
        Vector3 landing,
        bool requiresJump,
        float duration,
        float peakRise)
    {
        Takeoff = takeoff;
        Landing = landing;
        RequiresJump = requiresJump;
        RequiresDrop = !requiresJump;
        Duration = duration;
        PeakRise = peakRise;
        var direction = landing - takeoff;
        direction.y = 0f;
        HorizontalDistance = direction.magnitude;
        TravelDirection = direction.sqrMagnitude > 0.0001f
            ? direction.normalized
            : Vector3.zero;
    }

    internal Vector3 Takeoff { get; }
    internal Vector3 Landing { get; }
    internal Vector3 TravelDirection { get; }
    internal bool RequiresJump { get; }
    internal bool RequiresDrop { get; }
    internal float Duration { get; }
    internal float PeakRise { get; }
    internal float HorizontalDistance { get; }
}

internal sealed class CompanionTraversalRecorder
{
    private const float SupportInterruptionTolerance = 0.12f;
    private const float GroundedJumpExpiry = 0.20f;
    private const float MeaningfulVerticalTravel = 0.18f;
    private const float AscendingVelocityThreshold = 0.50f;

    private bool _hasGroundedPosition;
    private Vector3 _lastGroundedPosition;
    private float _lastGroundedAt;
    private bool _hasSample;
    private float _lastSampleAt;
    private bool _previousJustJumped;
    private bool _active;
    private bool _observedAirborne;
    private bool _nativeJump;
    private Vector3 _takeoff;
    private float _takeoffAt;
    private float _peakY;
    private float _maximumAscendingVelocity;

    internal bool TraversalInProgress => _active;
    internal Vector3 TakeoffPosition => _active
        ? _takeoff
        : _lastGroundedPosition;

    internal void Reset()
    {
        _hasGroundedPosition = false;
        _lastGroundedPosition = Vector3.zero;
        _lastGroundedAt = 0f;
        _hasSample = false;
        _lastSampleAt = 0f;
        _previousJustJumped = false;
        _active = false;
        _observedAirborne = false;
        _nativeJump = false;
        _takeoff = Vector3.zero;
        _takeoffAt = 0f;
        _peakY = 0f;
        _maximumAscendingVelocity = 0f;
    }

    internal bool Sample(
        Vector3 position,
        bool grounded,
        bool justJumped,
        float verticalVelocity,
        float now,
        out CompanionTraversalCompleted traversal)
    {
        traversal = default(CompanionTraversalCompleted);
        if (_hasSample && now < _lastSampleAt)
            Reset();

        var jumpStarted = justJumped && !_previousJustJumped;
        _previousJustJumped = justJumped;
        _lastSampleAt = now;
        _hasSample = true;

        if (!_hasGroundedPosition)
        {
            if (!grounded)
                return false;
            RememberGround(position, now);
        }

        if (!_active)
        {
            if (grounded && !jumpStarted)
            {
                RememberGround(position, now);
                return false;
            }

            _active = true;
            _observedAirborne = false;
            _nativeJump = jumpStarted;
            _takeoff = _lastGroundedPosition;
            _takeoffAt = _lastGroundedAt;
            _peakY = _takeoff.y;
            _maximumAscendingVelocity = 0f;
        }

        if (!grounded)
        {
            _observedAirborne = true;
            _nativeJump |= jumpStarted;
        }

        _peakY = Mathf.Max(_peakY, position.y);
        _maximumAscendingVelocity = Mathf.Max(
            _maximumAscendingVelocity,
            verticalVelocity);

        if (!grounded)
            return false;

        var duration = Mathf.Max(0f, now - _takeoffAt);
        if (!_observedAirborne)
        {
            if (duration >= GroundedJumpExpiry)
            {
                _active = false;
                RememberGround(position, now);
            }
            return false;
        }

        var peakRise = _peakY - _takeoff.y;
        var confirmedAirborne = duration >= SupportInterruptionTolerance;
        var inferredJump = confirmedAirborne &&
                           peakRise >= MeaningfulVerticalTravel &&
                           _maximumAscendingVelocity >= AscendingVelocityThreshold;
        var requiresJump = _nativeJump || inferredJump;
        var requiresDrop = !requiresJump &&
                           confirmedAirborne &&
                           _takeoff.y - position.y >= MeaningfulVerticalTravel;
        if (requiresJump || requiresDrop)
        {
            traversal = new CompanionTraversalCompleted(
                _takeoff,
                position,
                requiresJump,
                duration,
                peakRise);
        }

        _active = false;
        RememberGround(position, now);
        if (jumpStarted)
        {
            _active = true;
            _observedAirborne = false;
            _nativeJump = true;
            _takeoff = position;
            _takeoffAt = now;
            _peakY = position.y;
            _maximumAscendingVelocity = Mathf.Max(0f, verticalVelocity);
        }
        return requiresJump || requiresDrop;
    }

    private void RememberGround(Vector3 position, float now)
    {
        _hasGroundedPosition = true;
        _lastGroundedPosition = position;
        _lastGroundedAt = now;
    }
}
