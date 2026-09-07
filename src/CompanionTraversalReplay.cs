using System;
using UnityEngine;

namespace Ramblers;

internal sealed class CompanionTraversalReplay
{
    private enum ReplayPhase
    {
        None,
        Approach,
        AwaitAirborne,
        Airborne,
        Completed
    }

    private const float TakeoffHorizontalTolerance = 0.55f;
    private const float TakeoffVerticalTolerance = 1.1f;
    private const float LaunchTimeout = 0.8f;
    private const float RetryCooldown = 0.35f;
    private const float UnsupportedConfirmationTime = 0.08f;
    private const float VerticalDepartureConfirmation = 0.06f;
    private const float LandingHorizontalTolerance = 0.2f;
    private const float GroundedLandingHorizontalTolerance = 0.25f;
    private const float GroundedLandingVerticalTolerance = 0.25f;
    private const float LandingPassTolerance = 0.75f;
    private const float LandingPassLateralTolerance = 0.3f;
    private const float MinimumArrivalProgress = 0.1f;

    private BreadcrumbPoint _point;
    private ReplayPhase _phase;
    private Vector3 _launchDirection;
    private float _launchY;
    private float _launchedAt;
    private float _nextLaunchAt;
    private bool _observedUnsupported;
    private float _unsupportedSince;
    private bool _hasTick;
    private float _lastTickAt;

    internal bool Active => _phase == ReplayPhase.AwaitAirborne ||
                            _phase == ReplayPhase.Airborne;
    internal int TargetSequence => _point.Sequence;
    internal Vector3 ApproachPosition => _point.HasTakeoff &&
                                         _phase != ReplayPhase.Completed
        ? _point.TakeoffPosition
        : _point.Position;
    internal int JumpCommittedSequence { get; private set; }
    internal int DropCommittedSequence { get; private set; }

    internal void CancelActive()
    {
        if (!Active)
            return;
        _phase = ReplayPhase.Approach;
        _launchDirection = Vector3.zero;
        _launchY = 0f;
        _launchedAt = 0f;
        _nextLaunchAt = _lastTickAt + RetryCooldown;
        _observedUnsupported = false;
        _unsupportedSince = 0f;
    }

    internal void Reset()
    {
        _point = default(BreadcrumbPoint);
        _phase = ReplayPhase.None;
        _launchDirection = Vector3.zero;
        _launchY = 0f;
        _launchedAt = 0f;
        _nextLaunchAt = 0f;
        _observedUnsupported = false;
        _unsupportedSince = 0f;
        _hasTick = false;
        _lastTickAt = 0f;
        JumpCommittedSequence = 0;
        DropCommittedSequence = 0;
    }

    internal bool Tick(
        BreadcrumbPoint point,
        Vector3 bodyPosition,
        bool grounded,
        float now,
        Func<bool> requestJump,
        out Vector3 direction)
    {
        direction = Vector3.zero;
        if (_hasTick && now < _lastTickAt)
            Reset();
        _hasTick = true;
        _lastTickAt = now;

        if (Active && grounded && HasReachedLanding(bodyPosition))
        {
            Complete();
            return false;
        }

        if (_phase == ReplayPhase.Airborne)
        {
            if (!grounded)
            {
                direction = ResolveAirborneDirection(bodyPosition);
                return true;
            }

            Complete();
            return false;
        }

        if (_phase == ReplayPhase.AwaitAirborne)
        {
            if (!grounded)
            {
                if (!_observedUnsupported)
                {
                    _observedUnsupported = true;
                    _unsupportedSince = now;
                }
                if (Mathf.Abs(bodyPosition.y - _launchY) >= VerticalDepartureConfirmation ||
                    now - _unsupportedSince >= UnsupportedConfirmationTime)
                {
                    _phase = ReplayPhase.Airborne;
                }
                direction = _phase == ReplayPhase.Airborne
                    ? ResolveAirborneDirection(bodyPosition)
                    : ResolveLaunchDirection(bodyPosition);
                return true;
            }

            _observedUnsupported = false;
            if (now - _launchedAt >= LaunchTimeout)
            {
                _phase = ReplayPhase.Approach;
                _nextLaunchAt = now + RetryCooldown;
                return false;
            }

            direction = ResolveLaunchDirection(bodyPosition);
            return true;
        }

        if (_point.Sequence != point.Sequence || _phase == ReplayPhase.None)
        {
            _point = point;
            _phase = point.HasTakeoff && (point.RequiresJump || point.RequiresDrop)
                ? ReplayPhase.Approach
                : ReplayPhase.None;
            if (_phase == ReplayPhase.Approach &&
                (!point.RequiresJump || JumpCommittedSequence == point.Sequence) &&
                (!point.RequiresDrop || DropCommittedSequence == point.Sequence))
            {
                _phase = ReplayPhase.Completed;
            }
            _nextLaunchAt = now;
        }

        if (_phase == ReplayPhase.Approach && grounded && HasReachedLanding(bodyPosition))
        {
            Complete();
            return false;
        }

        if (_phase != ReplayPhase.Approach || !grounded || now < _nextLaunchAt)
            return false;

        var toTakeoff = _point.TakeoffPosition - bodyPosition;
        var verticalDistance = Mathf.Abs(toTakeoff.y);
        toTakeoff.y = 0f;
        if (toTakeoff.magnitude > TakeoffHorizontalTolerance ||
            verticalDistance > TakeoffVerticalTolerance)
        {
            return false;
        }

        if (_point.RequiresJump && (requestJump == null || !requestJump()))
        {
            _nextLaunchAt = now + RetryCooldown;
            return false;
        }

        _launchDirection = ResolveLaunchDirection(bodyPosition);

        _launchY = bodyPosition.y;
        _launchedAt = now;
        _observedUnsupported = false;
        _phase = ReplayPhase.AwaitAirborne;
        direction = _launchDirection;
        return true;
    }

    private void Complete()
    {
        _phase = ReplayPhase.Completed;
        if (_point.RequiresJump)
            JumpCommittedSequence = _point.Sequence;
        if (_point.RequiresDrop)
            DropCommittedSequence = _point.Sequence;
    }

    private bool HasReachedLanding(Vector3 bodyPosition)
    {
        var fromTakeoff = bodyPosition - _point.TakeoffPosition;
        var recordedTravel = _point.Position - _point.TakeoffPosition;
        var recordedDistance = recordedTravel.magnitude;
        if (recordedDistance < MinimumArrivalProgress)
            return false;

        var progress = Vector3.Dot(fromTakeoff, recordedTravel) / recordedDistance;
        if (progress < Math.Min(MinimumArrivalProgress, recordedDistance * 0.5f))
            return false;

        var fromLanding = bodyPosition - _point.Position;
        if (Mathf.Abs(fromLanding.y) > GroundedLandingVerticalTolerance)
            return false;

        fromLanding.y = 0f;
        if (fromLanding.sqrMagnitude <=
            GroundedLandingHorizontalTolerance * GroundedLandingHorizontalTolerance)
        {
            return true;
        }

        recordedTravel.y = 0f;
        if (recordedTravel.sqrMagnitude < MinimumArrivalProgress * MinimumArrivalProgress)
            return false;

        var travelDirection = recordedTravel.normalized;
        var beyondLanding = Vector3.Dot(fromLanding, travelDirection);
        if (beyondLanding < 0f || beyondLanding > LandingPassTolerance)
            return false;

        var lateralOffset = fromLanding - travelDirection * beyondLanding;
        return lateralOffset.sqrMagnitude <=
               LandingPassLateralTolerance * LandingPassLateralTolerance;
    }

    private Vector3 ResolveLaunchDirection(Vector3 bodyPosition)
    {
        var direction = _point.Position - bodyPosition;
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.0001f)
        {
            direction = _point.TravelDirection;
            direction.y = 0f;
        }
        return direction.sqrMagnitude >= 0.0001f ? direction.normalized : Vector3.zero;
    }

    private Vector3 ResolveAirborneDirection(Vector3 bodyPosition)
    {
        var toLanding = _point.Position - bodyPosition;
        toLanding.y = 0f;
        return toLanding.sqrMagnitude <= LandingHorizontalTolerance * LandingHorizontalTolerance
            ? Vector3.zero
            : toLanding.normalized;
    }
}
