using UnityEngine;

namespace Ramblers;

internal enum CompanionApproachStepKind
{
    Advanced,
    RecoveryDeferred,
    RecoveryCommitted,
    Blocked
}

internal struct CompanionApproachStep
{
    internal CompanionApproachStepKind Kind;
    internal string Reason;
    internal string RecoveryError;
    internal int RecoveryAttempt;
}

internal sealed class CompanionApproachController
{
    private const float NavigationInterval = 0.1f;
    private const float RecoveryCommitSeconds = 0.45f;

    private readonly CompanionLocomotion _locomotion;
    private readonly CompanionJumpActuator _jump;
    private readonly string _actionName;

    private Vector3 _recoveryDirection;
    private float _recoveryUntil;
    private float _nextNavigationTick;
    private int _recoveryAttempts;

    internal CompanionApproachController(
        CompanionLocomotion locomotion,
        CompanionJumpActuator jump,
        string actionName)
    {
        _locomotion = locomotion;
        _jump = jump;
        _actionName = actionName;
    }

    internal void Begin(float now)
    {
        Reset();
        _nextNavigationTick = now;
        _locomotion.ResetProgressObservation(now);
    }

    internal bool TryBeginTick(float now)
    {
        if (now < _nextNavigationTick)
            return false;

        _nextNavigationTick = now + NavigationInterval;
        return true;
    }

    internal void Resume(float now)
    {

        CancelRecovery();
        _nextNavigationTick = now;
        ResetProgressObservation(now);
    }

    internal void ResetProgressObservation(float now)
    {
        _locomotion.ResetProgressObservation(now);
    }

    internal CompanionApproachStep Advance(
        float now,
        Vector3 direction,
        float distance)
    {
        if (now < _recoveryUntil)
        {
            _locomotion.CommitTraversalDirection(
                _recoveryDirection,
                distance);
        }
        else
        {
            SteeringStatus status;
            if (!_locomotion.TrySteerToward(
                    direction,
                    distance,
                    now,
                    out status))
            {
                _locomotion.Stop(now);
                return TryRecover(now, direction, distance, "blocked_path");
            }
        }

        if (_locomotion.ObserveProgress(now))
            return TryRecover(now, direction, distance, "stuck");

        return new CompanionApproachStep
        {
            Kind = CompanionApproachStepKind.Advanced
        };
    }

    internal void CancelRecovery()
    {
        _jump.CancelActionRecovery(_actionName);
        _recoveryUntil = 0f;
        _recoveryDirection = Vector3.zero;
    }

    internal void Reset()
    {
        _recoveryDirection = Vector3.zero;
        _recoveryUntil = 0f;
        _nextNavigationTick = 0f;
        _recoveryAttempts = 0;
    }

    private CompanionApproachStep TryRecover(
        float now,
        Vector3 direction,
        float distance,
        string reason)
    {
        string jumpError;
        if (!_jump.TryRequestActionRecovery(
                now,
                _locomotion.Posture,
                _actionName,
                reason,
                out jumpError))
        {
            if (CompanionJumpActuator.IsDeferredRecoveryError(jumpError))
            {
                _locomotion.ResetProgressObservation(now);
                return new CompanionApproachStep
                {
                    Kind = CompanionApproachStepKind.RecoveryDeferred,
                    Reason = reason,
                    RecoveryError = jumpError
                };
            }

            return new CompanionApproachStep
            {
                Kind = CompanionApproachStepKind.Blocked,
                Reason = reason,
                RecoveryError = jumpError
            };
        }

        _recoveryAttempts++;
        _recoveryUntil = now + RecoveryCommitSeconds;
        _recoveryDirection = direction;
        _locomotion.CommitTraversalDirection(direction, distance);
        _locomotion.ResetProgressObservation(now);
        return new CompanionApproachStep
        {
            Kind = CompanionApproachStepKind.RecoveryCommitted,
            Reason = reason,
            RecoveryAttempt = _recoveryAttempts
        };
    }
}
