using Mirror;
using UnityEngine;

namespace Ramblers;

/// <summary>
/// Kicks only the prop frozen at the originating utterance boundary. Big Walk
/// implements a kick as an authoritative pickup followed by a charged low
/// held-prop launch, so this job preserves that visible sequence and uses the
/// game's own normalized charge curve rather than applying a Rigidbody force.
/// </summary>
internal sealed class CompanionKickBehavior : ICompanionJob
{
    private const float MinimumTargetLookSeconds = 0.20f;
    private const float MaximumTargetLookSeconds = 1.50f;
    private const float TargetAimToleranceDegrees = 8f;
    private const float KickReachDistance = 2.75f;
    private const float HoldConfirmationSeconds = 1.00f;
    private const float ReleaseConfirmationSeconds = 1.50f;
    private const float StableEmptySeconds = 0.10f;
    private const float ReconciliationEmptySeconds = 0.25f;
    private const float ReconciliationRetrySeconds = 0.50f;
    private const float MinimumMotionDistance = 0.12f;
    private const float MinimumMotionSpeed = 0.40f;
    private const float LightWindUp = 0.35f;
    private const float NormalWindUp = 0.65f;
    private const float HardWindUp = 1.00f;
    private const float KickTimeoutSecondsValue = 12f;

    private enum KickState
    {
        Idle,
        AligningTarget,
        AwaitingHold,
        Charging,
        AwaitingRelease,
        ReconcilingFailure,
        Cancelling,
        Faulted
    }

    private readonly CompanionAttention _attention;

    private CompanionBody _body;
    private PlayerCharacter _humanAtSpawn;
    private CompanionInteractionTarget _target;
    private CompanionKickStrength _strength;
    private CompanionKickDirection _direction;
    private KickState _state;
    private float _stateStartedAt;
    private float _chargeStartedAt;
    private float _chargeDuration;
    private float _windUp;
    private float _emptySince = -1f;
    private float _recoveryDropIssuedAt = -1f;
    private Vector3 _launchPosition;
    private CompanionJobCompletion _completion;

    internal CompanionKickBehavior(CompanionAttention attention)
    {
        _attention = attention;
    }

    public string Name => AgentToolCatalog.KickItem;

    public string ActiveName => Name;

    public bool Handles(string actionName)
    {
        return string.Equals(
            actionName,
            AgentToolCatalog.KickItem,
            System.StringComparison.Ordinal);
    }

    public JobResources RequiredFor(CompanionJobRequest request)
    {
        return JobResources.Locomotion | JobResources.Gaze | JobResources.Hands;
    }

    public JobResources Held => _state == KickState.Idle
        ? JobResources.None
        : _state == KickState.ReconcilingFailure ||
          _state == KickState.Cancelling ||
          _state == KickState.Faulted
            ? JobResources.Hands
            : JobResources.Locomotion | JobResources.Gaze | JobResources.Hands;

    public bool IsActive => _state != KickState.Idle;

    public float TimeoutSeconds => KickTimeoutSecondsValue;

    public void Bind(CompanionBody body, PlayerCharacter human)
    {
        _body = body;
        _humanAtSpawn = human;
        ResetState();
    }

    public bool TryBegin(
        float now,
        CompanionJobRequest request,
        out AgentToolResult failure)
    {
        failure = null;
        if (_body == null || !_body.IsAlive)
        {
            failure = AgentToolResult.Failure("bot_not_spawned");
            return false;
        }

        if (_state != KickState.Idle || _completion != null)
        {
            failure = AgentToolResult.Failure("kick_item_in_progress");
            return false;
        }

        _target = request == null ? null : request.InteractionTarget;
        _strength = request == null
            ? CompanionKickStrength.Normal
            : request.KickStrength;
        _direction = request == null
            ? CompanionKickDirection.AwayFromCompanion
            : request.KickDirection;
        if (_target == null)
        {
            failure = AgentToolResult.Failure("human_reference_not_captured");
            return false;
        }

        Vector3 targetPoint;
        string validationError;
        if (!TryValidateAdmission(out targetPoint, out validationError))
        {
            ClearActionParameters();
            failure = AgentToolResult.Failure(validationError);
            return false;
        }

        _state = KickState.AligningTarget;
        _stateStartedAt = now;
        ResetSettlementTracking();
        _attention.SetTarget(GazeChannel.Manipulation, targetPoint);
        Plugin.Logger.LogInfo(
            $"[ACTION] KICK_STARTED referenceId={_target.ReferenceId}, " +
            $"netId={_target.NetworkId}, strength={StrengthForLog}, " +
            $"direction={DirectionForLog}, " +
            $"turnId={(request == null ? 0 : request.TurnId)}.");
        return true;
    }

    public void Tick(float now)
    {
        switch (_state)
        {
            case KickState.Idle:
            case KickState.Faulted:
                return;
            case KickState.AligningTarget:
                TickAlignment(now);
                return;
            case KickState.AwaitingHold:
                TickAwaitingHold(now);
                return;
            case KickState.Charging:
                TickCharging(now);
                return;
            case KickState.AwaitingRelease:
                TickAwaitingRelease(now);
                return;
            case KickState.ReconcilingFailure:
            case KickState.Cancelling:
                TickReconciliation(now);
                return;
        }
    }

    public bool TryTakeCompletion(out CompanionJobCompletion completion)
    {
        completion = _completion;
        if (completion == null)
            return false;
        _completion = null;
        return true;
    }

    public void Conclude(float now)
    {
    }

    public void Cancel(float now)
    {
        _completion = null;
        if (_state == KickState.Idle)
            return;

        if (_state == KickState.AligningTarget)
        {
            Plugin.Logger.LogInfo(
                $"[ACTION] KICK_CANCELLED phase=before_authority, " +
                $"referenceId={ReferenceIdForLog}.");
            EndAction();
            return;
        }

        if (_state == KickState.Faulted)
        {
            Plugin.Logger.LogWarning(
                "[ACTION] KICK_CANCEL_BLOCKED reason=target_identity_fault.");
            return;
        }

        var cancelledPhase = _state == KickState.Charging
            ? "charging"
            : _state == KickState.AwaitingRelease
                ? "release"
                : "pickup";
        _state = KickState.Cancelling;
        _stateStartedAt = now;
        ResetSettlementTracking();
        _attention.ClearTarget(GazeChannel.Manipulation);
        Plugin.Logger.LogInfo(
            $"[ACTION] KICK_CANCEL_RECONCILE_STARTED phase={cancelledPhase}, " +
            $"referenceId={ReferenceIdForLog}.");
        TickReconciliation(now);
    }

    public void Fail(string error, float now)
    {
        if (_state == KickState.Idle || _state == KickState.Faulted)
            return;

        if (_state == KickState.AligningTarget)
        {
            CompleteFailure(error ?? "action_execution_failed");
            return;
        }

        BeginPostAuthorityFailure(
            error ?? "action_execution_failed",
            now);
    }

    public void Release()
    {
        _body = null;
        _humanAtSpawn = null;
        ResetState();
        _attention.ClearTarget(GazeChannel.Manipulation);
    }

    private void TickAlignment(float now)
    {
        Vector3 targetPoint;
        string validationError;
        if (!TryValidateBeforeAuthority(out targetPoint, out validationError))
        {
            CompleteFailure(validationError);
            return;
        }

        _attention.SetTarget(GazeChannel.Manipulation, targetPoint);
        var lookSeconds = now - _stateStartedAt;
        if (lookSeconds < MinimumTargetLookSeconds)
            return;

        if (_attention.IsAimWithin(
                GazeChannel.Manipulation,
                TargetAimToleranceDegrees,
                TargetAimToleranceDegrees))
        {
            BeginAuthoritativePickup(now);
            return;
        }

        if (lookSeconds >= MaximumTargetLookSeconds)
        {
            Plugin.Logger.LogWarning(
                $"[ACTION] KICK_ALIGNMENT_TIMEOUT referenceId={ReferenceIdForLog}, " +
                $"lookSeconds={lookSeconds:F2}; continuing with exact target.");
            BeginAuthoritativePickup(now);
        }
    }

    private void BeginAuthoritativePickup(float now)
    {
        Vector3 currentPoint;
        string validationError;
        if (!TryValidateBeforeAuthority(out currentPoint, out validationError))
        {
            CompleteFailure(validationError);
            return;
        }

        if (!TryResolveCharge(out _windUp, out _chargeDuration, out validationError))
        {
            CompleteFailure(validationError);
            return;
        }

        _state = KickState.AwaitingHold;
        _stateStartedAt = now;
        try
        {
            _body.Networking.ServerPickUpPropAutomatic(_target.Prop);
        }
        catch (System.Exception exception)
        {
            Plugin.Logger.LogError(
                $"[ACTION] KICK_PICKUP_FAILED exception={exception}");
            BeginPostAuthorityFailure("kick_execution_failed", now);
            return;
        }

        _attention.SetTarget(GazeChannel.Manipulation, currentPoint);
        Plugin.Logger.LogInfo(
            $"[ACTION] KICK_PICKUP_REQUESTED referenceId={ReferenceIdForLog}, " +
            $"netId={_target.NetworkId}.");
        TickAwaitingHold(now);
    }

    private void TickAwaitingHold(float now)
    {
        if (_body == null || !_body.IsAlive)
        {
            BeginPostAuthorityFailure("bot_not_spawned", now);
            return;
        }

        var hands = GetHands();
        if (hands == null)
        {
            EnterIdentityFault("hands_unavailable", true);
            return;
        }

        if (hands.heldCharacter != null)
        {
            EnterIdentityFault("kick_target_mismatch", true);
            return;
        }

        var heldProp = hands.heldProp;
        if (heldProp != null)
        {
            if (!_target.IsStillTheSameProp(heldProp))
            {
                EnterIdentityFault("kick_target_mismatch", true);
                return;
            }

            BeginCharge(now);
            return;
        }

        Vector3 targetPoint;
        if (_target.TryGetCurrentPoint(out targetPoint))
            _attention.SetTarget(GazeChannel.Manipulation, targetPoint);

        if (now - _stateStartedAt >= HoldConfirmationSeconds)
            BeginPostAuthorityFailure("kick_pickup_not_confirmed", now);
    }

    private void BeginCharge(float now)
    {
        string validationError;
        if (!TryValidateHeldTarget(out validationError))
        {
            BeginPostAuthorityFailure(validationError, now);
            return;
        }

        _state = KickState.Charging;
        _stateStartedAt = now;
        _chargeStartedAt = now;
        Vector3 lookPoint;
        if (TryGetChargeLookPoint(out lookPoint))
            _attention.SetTarget(GazeChannel.Manipulation, lookPoint);
        Plugin.Logger.LogInfo(
            $"[ACTION] KICK_CHARGE_STARTED referenceId={ReferenceIdForLog}, " +
            $"strength={StrengthForLog}, direction={DirectionForLog}, " +
            $"windUp={_windUp:0.00}, duration={_chargeDuration:0.00}.");
    }

    private void TickCharging(float now)
    {
        string validationError;
        if (!TryValidateHeldTarget(out validationError))
        {
            BeginPostAuthorityFailure(validationError, now);
            return;
        }

        Vector3 lookPoint;
        if (TryGetChargeLookPoint(out lookPoint))
            _attention.SetTarget(GazeChannel.Manipulation, lookPoint);

        if (now - _chargeStartedAt < _chargeDuration)
            return;

        DispatchStockKick(now);
    }

    private void DispatchStockKick(float now)
    {
        var hands = GetHands();
        if (hands == null || _target == null ||
            !_target.IsStillTheSameProp(hands.heldProp))
        {
            EnterIdentityFault("kick_target_mismatch", true);
            return;
        }

        string validationError;
        if (!TryValidateHeldTarget(out validationError))
        {
            BeginPostAuthorityFailure(validationError, now);
            return;
        }

        Quaternion launchRotation;
        if (!TryResolveLaunch(
                out _launchPosition,
                out launchRotation,
                out validationError))
        {
            BeginPostAuthorityFailure(validationError, now);
            return;
        }

        ResetReleaseTracking();
        _state = KickState.AwaitingRelease;
        _stateStartedAt = now;
        try
        {
            var dropInformation = PlayerHeldInformation.ThrowInfo(
                _windUp,
                _launchPosition,
                launchRotation);
            var currentInformation = _body.Networking.playerHeldInformation;
            dropInformation.actionNumber = currentInformation == null
                ? 1
                : currentInformation.actionNumber + 1;

            // This is the server-side body of Big Walk's stock pickup/drop
            // command. A drop record with launch data makes OnSetHeld call the
            // stock low-held launch path, which supplies kick force, animation,
            // audio, and replicated prop state.
            _body.Networking.UserCode_CmdPickUp__PlayerHeldInformation(
                dropInformation);
        }
        catch (System.Exception exception)
        {
            Plugin.Logger.LogError(
                $"[ACTION] KICK_LAUNCH_FAILED exception={exception}");
            BeginPostAuthorityFailure("kick_execution_failed", now);
            return;
        }

        Plugin.Logger.LogInfo(
            $"[ACTION] KICK_LAUNCH_REQUESTED referenceId={ReferenceIdForLog}, " +
            $"netId={_target.NetworkId}, strength={StrengthForLog}, " +
            $"direction={DirectionForLog}, windUp={_windUp:0.00}, " +
            $"chargedFor={now - _chargeStartedAt:0.00}.");
        TickAwaitingRelease(now);
    }

    private void TickAwaitingRelease(float now)
    {
        if (_body == null || !_body.IsAlive)
        {
            BeginPostAuthorityFailure("bot_not_spawned", now);
            return;
        }

        var hands = GetHands();
        if (hands == null)
        {
            EnterIdentityFault("hands_unavailable", true);
            return;
        }

        if (hands.heldCharacter != null)
        {
            EnterIdentityFault("kick_target_mismatch", true);
            return;
        }

        var heldProp = hands.heldProp;
        if (heldProp != null)
        {
            _emptySince = -1f;
            if (!_target.IsStillTheSameProp(heldProp))
            {
                EnterIdentityFault("kick_target_mismatch", true);
                return;
            }

            if (now - _stateStartedAt >= ReleaseConfirmationSeconds)
                BeginPostAuthorityFailure("kick_release_not_confirmed", now);
            return;
        }

        if (_emptySince < 0f)
            _emptySince = now;

        if (_target == null || !_target.IsStillTheSameProp(_target.Prop) ||
            _target.Prop == null || _target.Prop.rb == null)
        {
            CompleteFailure("kick_target_lost_after_launch");
            return;
        }

        _attention.SetTarget(
            GazeChannel.Manipulation,
            _target.Prop.transform.position);
        var displacement = Vector3.Distance(
            _launchPosition,
            _target.Prop.transform.position);
        var speed = _target.Prop.rb.linearVelocity.magnitude;
        if ((displacement >= MinimumMotionDistance ||
             speed >= MinimumMotionSpeed) &&
            now - _emptySince >= StableEmptySeconds)
        {
            _completion = new CompanionJobCompletion
            {
                Result = AgentToolResult.Success(
                    AgentToolCatalog.KickItem,
                    "kicked",
                    "item_moving")
            };
            Plugin.Logger.LogInfo(
                $"[ACTION] KICK_CONFIRMED referenceId={ReferenceIdForLog}, " +
                $"strength={StrengthForLog}, direction={DirectionForLog}, " +
                $"displacement={displacement:0.000}, speed={speed:0.000}.");
            EndAction();
            return;
        }

        if (now - _stateStartedAt >= ReleaseConfirmationSeconds)
            CompleteFailure("kick_motion_not_confirmed");
    }

    private void BeginPostAuthorityFailure(string error, float now)
    {
        if (_completion == null)
            _completion = CompanionJobCompletion.Failed(error);
        _state = KickState.ReconcilingFailure;
        _stateStartedAt = now;
        ResetSettlementTracking();
        _attention.ClearTarget(GazeChannel.Manipulation);
        Plugin.Logger.LogWarning(
            $"[ACTION] KICK_RECONCILIATION_STARTED error={error}, " +
            $"referenceId={ReferenceIdForLog}.");
        TickReconciliation(now);
    }

    private void TickReconciliation(float now)
    {
        if (_body == null || !_body.IsAlive || _target == null)
        {
            EndAction();
            return;
        }

        var hands = GetHands();
        if (hands == null)
        {
            EnterIdentityFault("hands_unavailable", _completion == null);
            return;
        }

        if (hands.heldCharacter != null)
        {
            EnterIdentityFault("kick_target_mismatch", _completion == null);
            return;
        }

        var heldProp = hands.heldProp;
        if (heldProp != null)
        {
            _emptySince = -1f;
            if (!_target.IsStillTheSameProp(heldProp))
            {
                EnterIdentityFault(
                    "kick_target_mismatch",
                    _completion == null);
                return;
            }

            if (_recoveryDropIssuedAt < 0f ||
                now - _recoveryDropIssuedAt >= ReconciliationRetrySeconds)
            {
                TryIssueExactRecoveryDrop(now);
            }
            return;
        }

        if (_emptySince < 0f)
            _emptySince = now;
        if (now - _emptySince < ReconciliationEmptySeconds)
            return;

        Plugin.Logger.LogInfo(
            $"[ACTION] KICK_RECONCILED disposition=not_held, " +
            $"referenceId={ReferenceIdForLog}.");
        EndAction();
    }

    private void TryIssueExactRecoveryDrop(float now)
    {
        var hands = GetHands();
        if (hands == null || _target == null ||
            !_target.IsStillTheSameProp(hands.heldProp))
        {
            return;
        }

        _recoveryDropIssuedAt = now;
        if (!HasAuthority)
        {
            EnterIdentityFault(
                "bot_authority_unavailable",
                _completion == null);
            return;
        }

        try
        {
            // Cancellation is a plain drop, never another kick. The exact held
            // identity check above is the boundary for this parameterless API.
            _body.Networking.ServerDropPropAutomatic(false);
            Plugin.Logger.LogInfo(
                $"[ACTION] KICK_RECOVERY_DROP_REQUESTED " +
                $"referenceId={ReferenceIdForLog}.");
        }
        catch (System.Exception exception)
        {
            Plugin.Logger.LogError(
                $"[ACTION] KICK_RECOVERY_DROP_FAILED exception={exception}");
        }
    }

    private bool TryValidateAdmission(out Vector3 point, out string error)
    {
        return TryValidateBeforeAuthority(out point, out error, false);
    }

    private bool TryValidateBeforeAuthority(
        out Vector3 point,
        out string error,
        bool validateCurrentPose = true)
    {
        point = Vector3.zero;
        error = null;
        if (_body == null || !_body.IsAlive)
        {
            error = "bot_not_spawned";
            return false;
        }

        if (_target == null || !_target.IsStillTheSameProp(_target.Prop))
        {
            error = "item_reference_lost";
            return false;
        }

        if (!_target.TryGetCurrentPoint(out point))
        {
            error = "item_unavailable";
            return false;
        }

        if (Vector3.Distance(_body.Position, point) > KickReachDistance)
        {
            error = "item_out_of_reach";
            return false;
        }

        var hands = GetHands();
        if (hands == null)
        {
            error = "hands_unavailable";
            return false;
        }

        if (hands.heldProp != null || hands.heldCharacter != null)
        {
            error = "hands_occupied";
            return false;
        }

        if (_target.Prop.rb == null || !hands.IsSafeToPickUp(_target.Prop))
        {
            error = "item_not_kickable";
            return false;
        }

        if (!TryValidateKickPose(validateCurrentPose, out error))
            return false;

        float ignoredWindUp;
        float ignoredDuration;
        if (!TryResolveCharge(
                out ignoredWindUp,
                out ignoredDuration,
                out error))
        {
            return false;
        }

        Vector3 ignoredPosition;
        Quaternion ignoredRotation;
        if (!TryResolveLaunch(
                out ignoredPosition,
                out ignoredRotation,
                out error))
        {
            return false;
        }

        if (!HasAuthority)
        {
            error = "bot_authority_unavailable";
            return false;
        }

        return true;
    }

    private bool TryValidateHeldTarget(out string error)
    {
        error = null;
        if (_body == null || !_body.IsAlive)
        {
            error = "bot_not_spawned";
            return false;
        }

        var hands = GetHands();
        if (hands == null)
        {
            error = "hands_unavailable";
            return false;
        }

        if (hands.heldCharacter != null || _target == null ||
            !_target.IsStillTheSameProp(hands.heldProp))
        {
            error = "kick_target_mismatch";
            return false;
        }

        if (_target.Prop == null || _target.Prop.rb == null)
        {
            error = "item_not_kickable";
            return false;
        }

        if (_body.Character.gestures != null &&
            _body.Character.gestures.isHoldingRaised)
        {
            // Raised releases use Big Walk's throw settings. Failing closed
            // here prevents a requested kick from silently becoming a throw.
            error = "kick_pose_unavailable";
            return false;
        }

        if (!TryValidateKickPose(true, out error))
            return false;

        Vector3 ignoredPosition;
        Quaternion ignoredRotation;
        if (!TryResolveLaunch(
                out ignoredPosition,
                out ignoredRotation,
                out error))
        {
            return false;
        }

        if (!HasAuthority)
        {
            error = "bot_authority_unavailable";
            return false;
        }

        return true;
    }

    private bool TryValidateKickPose(
        bool validateCurrentPose,
        out string error)
    {
        error = null;
        var pose = _body.Character.poser == null
            ? null
            : _body.Character.poser.currentPose;
        if (validateCurrentPose && pose != null && !pose.allowKicking)
        {
            error = "kick_pose_unavailable";
            return false;
        }

        if (PlayerArms.LegIsBusyKicking(_body.Character))
        {
            error = "kick_in_progress";
            return false;
        }

        return true;
    }

    private bool TryResolveCharge(
        out float windUp,
        out float duration,
        out string error)
    {
        windUp = 0f;
        duration = 0f;
        error = null;
        switch (_strength)
        {
            case CompanionKickStrength.Light:
                windUp = LightWindUp;
                break;
            case CompanionKickStrength.Normal:
                windUp = NormalWindUp;
                break;
            case CompanionKickStrength.Hard:
                windUp = HardWindUp;
                break;
            default:
                error = "invalid_kick_strength";
                return false;
        }

        var tunings = _body == null || _body.Character == null
            ? null
            : _body.Character.tunings;
        if (tunings == null || tunings.maxWindUpDuration <= 0f ||
            float.IsNaN(tunings.maxWindUpDuration) ||
            float.IsInfinity(tunings.maxWindUpDuration))
        {
            error = "kick_charge_unavailable";
            return false;
        }

        // Native PlayerArms.GetWindUp returns the held duration divided by
        // maxWindUpDuration, clamped to 1. Waiting the inverse duration here
        // gives the bot the same charge fraction that ThrowInfo receives.
        duration = tunings.maxWindUpDuration * windUp;
        return true;
    }

    private bool TryResolveLaunch(
        out Vector3 launchPosition,
        out Quaternion launchRotation,
        out string error)
    {
        launchPosition = Vector3.zero;
        launchRotation = Quaternion.identity;
        error = null;
        if (_body == null || !_body.IsAlive || _target == null ||
            _target.Prop == null)
        {
            error = "kick_direction_unavailable";
            return false;
        }

        launchPosition = _target.Prop.transform.position;
        Vector3 horizontalDirection;
        switch (_direction)
        {
            case CompanionKickDirection.AwayFromCompanion:
                horizontalDirection = launchPosition - _body.Position;
                horizontalDirection.y = 0f;
                if (horizontalDirection.sqrMagnitude < 0.0001f)
                {
                    horizontalDirection = _body.Transform.forward;
                    horizontalDirection.y = 0f;
                }
                break;
            case CompanionKickDirection.TowardHuman:
                var human = GetHumanPlayer();
                if (human == null)
                {
                    error = "human_player_unavailable";
                    return false;
                }
                horizontalDirection = human.transform.position - launchPosition;
                horizontalDirection.y = 0f;
                break;
            default:
                error = "invalid_kick_direction";
                return false;
        }

        if (horizontalDirection.sqrMagnitude < 0.0001f)
        {
            error = "kick_direction_unavailable";
            return false;
        }

        launchRotation = Quaternion.LookRotation(
            horizontalDirection.normalized,
            Vector3.up);
        return true;
    }

    private bool TryGetChargeLookPoint(out Vector3 point)
    {
        point = Vector3.zero;
        if (_direction == CompanionKickDirection.TowardHuman)
        {
            var human = GetHumanPlayer();
            if (human == null)
                return false;
            point = human.transform.position;
            return true;
        }

        return _target != null && _target.TryGetCurrentPoint(out point);
    }

    private PlayerCharacter GetHumanPlayer()
    {
        var human = WorldManager.localPlayerCharacter;
        if (human == null)
            human = _humanAtSpawn;
        if (human == null || (_body != null && human.gameObject == _body.GameObject))
            return null;
        return human;
    }

    private bool HasAuthority =>
        _body != null && _body.IsAlive && _body.Networking != null &&
        NetworkServer.active && _body.Networking.isServer &&
        !_body.Networking.isLocalPlayer;

    private PlayerHands GetHands()
    {
        return _body == null || _body.Character == null
            ? null
            : _body.Character.hands;
    }

    private void CompleteFailure(string error)
    {
        _completion = CompanionJobCompletion.Failed(error);
        Plugin.Logger.LogWarning(
            $"[ACTION] KICK_FAILED error={error}, " +
            $"referenceId={ReferenceIdForLog}.");
        EndAction();
    }

    private void EnterIdentityFault(string error, bool reportFailure)
    {
        if (reportFailure && _completion == null)
            _completion = CompanionJobCompletion.Failed(error);
        _state = KickState.Faulted;
        ResetSettlementTracking();
        _attention.ClearTarget(GazeChannel.Manipulation);
        Plugin.Logger.LogError(
            $"[ACTION] KICK_IDENTITY_FAULT error={error}, " +
            $"referenceId={ReferenceIdForLog}. Hands remain blocked; " +
            "no command will target a different prop.");
    }

    private int ReferenceIdForLog => _target == null ? 0 : _target.ReferenceId;

    private string StrengthForLog =>
        _strength == CompanionKickStrength.Light
            ? "light"
            : _strength == CompanionKickStrength.Hard
                ? "hard"
                : "normal";

    private string DirectionForLog =>
        _direction == CompanionKickDirection.TowardHuman
            ? "toward_human"
            : "away_from_companion";

    private void EndAction()
    {
        _state = KickState.Idle;
        ClearActionParameters();
        ResetSettlementTracking();
        _attention.ClearTarget(GazeChannel.Manipulation);
    }

    private void ClearActionParameters()
    {
        _target = null;
        _strength = CompanionKickStrength.Normal;
        _direction = CompanionKickDirection.AwayFromCompanion;
        _chargeStartedAt = 0f;
        _chargeDuration = 0f;
        _windUp = 0f;
    }

    private void ResetReleaseTracking()
    {
        _emptySince = -1f;
        _recoveryDropIssuedAt = -1f;
    }

    private void ResetSettlementTracking()
    {
        ResetReleaseTracking();
        _launchPosition = Vector3.zero;
    }

    private void ResetState()
    {
        _state = KickState.Idle;
        _stateStartedAt = 0f;
        _completion = null;
        ClearActionParameters();
        ResetSettlementTracking();
    }
}
