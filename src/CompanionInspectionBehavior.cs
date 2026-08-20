using UnityEngine;

namespace Ramblers;

/// <summary>
/// A bounded shared-attention job: acknowledge the human, visibly turn toward
/// the turn-bound referent the model selected, then capture one bot-eye frame.
/// </summary>
internal sealed class CompanionInspectionBehavior : ICompanionJob
{
    private const float MinimumHumanGlanceSeconds = 0.30f;
    private const float MaximumHumanGlanceSeconds = 0.65f;
    private const float HumanAimToleranceDegrees = 12f;
    private const float MinimumReferenceLookSeconds = 0.30f;

    // A reference directly behind the companion needs a full half-turn, and the
    // aim can only advance as fast as the body absorbs it once head yaw
    // saturates — 180 degrees per second, so about 1.2s worst case. The old
    // 1.25s budget was set when facing snapped onto its target and would now
    // time out on rear references rather than aiming at them.
    private const float MaximumReferenceLookSeconds = 2.00f;
    private const float ReferenceSettleSeconds = 0.10f;
    private const float ReferenceHoldSeconds = 3.00f;
    private const float ReferenceAimToleranceDegrees = 4f;

    // The gaze work is bounded by the constants above at roughly two seconds,
    // so this only has to cover a stalled frame loop.
    private const float InspectionTimeoutSeconds = 5f;

    private enum InspectionState
    {
        Idle,
        AcknowledgingHuman,
        AligningReference,
        HoldingReference
    }

    private readonly CompanionAttention _attention;

    private CompanionBody _body;
    private PlayerCharacter _humanAtSpawn;
    private InspectionState _state;
    private float _stateStartedAt;
    private float _referenceAlignedAt;
    private Vector3 _referencePoint;
    private bool _referenceRayHit;
    private CompanionInspectionReferent _inspectionReferent;
    private CompanionJobCompletion _completion;

    internal CompanionInspectionBehavior(CompanionAttention attention)
    {
        _attention = attention;
    }

    public string Name => AgentToolCatalog.InspectReference;

    public string ActiveName => Name;

    public bool Handles(string actionName)
    {
        return string.Equals(
            actionName,
            Name,
            System.StringComparison.Ordinal);
    }

    public JobResources RequiredFor(CompanionJobRequest request)
    {
        return JobResources.Locomotion | JobResources.Gaze;
    }

    /// <summary>
    /// Locomotion is released the moment the frame is captured. The settle hold
    /// that follows is a gaze commitment only, so a follow request arriving with
    /// the model's reply is not answered with a suspension the human cannot see.
    /// </summary>
    public JobResources Held
    {
        get
        {
            switch (_state)
            {
                case InspectionState.Idle:
                    return JobResources.None;
                case InspectionState.HoldingReference:
                    return JobResources.Gaze;
                default:
                    return JobResources.Locomotion | JobResources.Gaze;
            }
        }
    }

    public bool IsActive => _state != InspectionState.Idle;

    public float TimeoutSeconds => InspectionTimeoutSeconds;

    public void Bind(CompanionBody body, PlayerCharacter human)
    {
        _body = body;
        _humanAtSpawn = human;
        _state = InspectionState.Idle;
        _completion = null;
        _referenceAlignedAt = -1f;
        _referencePoint = Vector3.zero;
        _referenceRayHit = false;
        _inspectionReferent = null;
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

        if (IsActive || _completion != null)
        {
            failure = AgentToolResult.Failure("inspect_reference_in_progress");
            return false;
        }

        var human = GetHumanPlayer();
        if (human == null)
        {
            failure = AgentToolResult.Failure("human_player_unavailable");
            return false;
        }

        var referent = request == null ? null : request.InspectionReferent;
        Vector3 referencePoint;
        if (referent == null || !referent.TryGetCurrentPoint(out referencePoint))
        {
            failure = AgentToolResult.Failure(
                referent == null
                    ? "inspection_reference_unavailable"
                    : referent.UnavailableError);
            return false;
        }

        _inspectionReferent = referent;
        _referencePoint = referencePoint;
        _referenceRayHit = referent.GazeRayHit;
        _state = InspectionState.AcknowledgingHuman;
        _stateStartedAt = now;
        _referenceAlignedAt = -1f;
        _attention.SetTarget(
            GazeChannel.Inspection,
            CompanionBody.HeadPositionOf(human));
        Plugin.Logger.LogInfo(
            $"[VISION] INSPECTION_STARTED phase=look_at_human, " +
            $"referenceSource={_inspectionReferent.SourceLabel}.");
        return true;
    }

    public void Tick(float now)
    {
        if (_state == InspectionState.Idle)
            return;

        if (_state == InspectionState.HoldingReference)
        {
            Vector3 heldPoint;
            if (_inspectionReferent != null &&
                _inspectionReferent.TryGetCurrentPoint(out heldPoint))
            {
                _referencePoint = heldPoint;
            }
            _attention.SetTarget(GazeChannel.Inspection, _referencePoint);
            if (now - _stateStartedAt >= ReferenceHoldSeconds)
                EndAttention();
            return;
        }

        if (_body == null || !_body.IsAlive)
        {
            CompleteFailure("bot_not_spawned");
            return;
        }

        var human = GetHumanPlayer();
        if (human == null)
        {
            CompleteFailure("human_player_unavailable");
            return;
        }

        if (_state == InspectionState.AcknowledgingHuman)
        {
            _attention.SetTarget(
                GazeChannel.Inspection,
                CompanionBody.HeadPositionOf(human));
            var glanceSeconds = now - _stateStartedAt;
            if (glanceSeconds < MinimumHumanGlanceSeconds)
                return;
            if (!_attention.IsAimWithin(
                    GazeChannel.Inspection,
                    HumanAimToleranceDegrees,
                    HumanAimToleranceDegrees) &&
                glanceSeconds < MaximumHumanGlanceSeconds)
            {
                return;
            }

            string referenceError;
            if (!TryRefreshReferencePoint(out referenceError))
            {
                CompleteFailure(referenceError);
                return;
            }

            _state = InspectionState.AligningReference;
            _stateStartedAt = now;
            _referenceAlignedAt = -1f;
            _attention.SetTarget(GazeChannel.Inspection, _referencePoint);
            Plugin.Logger.LogInfo(
                $"[VISION] REFERENCE_LATCHED source={_inspectionReferent.SourceLabel}, " +
                $"target={_referencePoint}, glanceSeconds={glanceSeconds:F2}, " +
                $"glanceTimedOut={glanceSeconds >= MaximumHumanGlanceSeconds}.");
            return;
        }

        string refreshError;
        if (!TryRefreshReferencePoint(out refreshError))
        {
            CompleteFailure(refreshError);
            return;
        }
        _attention.SetTarget(GazeChannel.Inspection, _referencePoint);
        var lookSeconds = now - _stateStartedAt;
        if (lookSeconds < MinimumReferenceLookSeconds)
            return;

        if (_attention.IsAimWithin(
                GazeChannel.Inspection,
                ReferenceAimToleranceDegrees,
                ReferenceAimToleranceDegrees))
        {
            if (_referenceAlignedAt < 0f)
                _referenceAlignedAt = now;
            if (now - _referenceAlignedAt >= ReferenceSettleSeconds)
            {
                CaptureReference(human, now, lookSeconds, false);
                return;
            }
        }
        else
        {
            _referenceAlignedAt = -1f;
        }

        if (lookSeconds >= MaximumReferenceLookSeconds)
            CaptureReference(human, now, lookSeconds, true);
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
        if (_state == InspectionState.HoldingReference)
            EndAttention();
    }

    public void Cancel(float now)
    {
        _completion = null;
        EndAttention();
    }

    public void Fail(string error, float now)
    {
        if (_completion != null)
        {
            EndAttention();
            return;
        }
        if (IsActive)
            CompleteFailure(error ?? "action_execution_failed");
    }

    public void Release()
    {
        _body = null;
        _humanAtSpawn = null;
        _state = InspectionState.Idle;
        _completion = null;
        _referenceAlignedAt = -1f;
        _referencePoint = Vector3.zero;
        _referenceRayHit = false;
        _inspectionReferent = null;
        _attention.ClearTarget(GazeChannel.Inspection);
    }

    private void CaptureReference(
        PlayerCharacter human,
        float now,
        float lookSeconds,
        bool alignmentTimedOut)
    {
        string refreshError;
        if (!TryRefreshReferencePoint(out refreshError))
        {
            CompleteFailure(refreshError);
            return;
        }

        CompanionVisionObservation observation;
        string error;
        if (!CompanionVisionCapture.TryCapture(
                _body,
                human,
                _referencePoint,
                _attention.AimDirectionFor(GazeChannel.Inspection),
                _referenceRayHit,
                alignmentTimedOut,
                out observation,
                out error))
        {
            CompleteFailure(error ?? "image_capture_failed");
            return;
        }

        _completion = new CompanionJobCompletion
        {
            Result = AgentToolResult.Success(
                AgentToolCatalog.InspectReference,
                "captured",
                "reference_observed"),
            Continuation = new[]
            {
                AgentContinuationItem.FromImage(
                    DescribeObservation(
                        observation,
                        _inspectionReferent.SourceLabel),
                    observation.ImageBytes,
                    observation.MediaType)
            },
            // Inspection intentionally keeps looking at the referent while the
            // model begins describing the captured image.
            RetainUntilAssistantAudio = true
        };
        _state = InspectionState.HoldingReference;
        _stateStartedAt = now;
        Plugin.Logger.LogInfo(
            $"[VISION] CAPTURED width={observation.Width}, height={observation.Height}, " +
            $"imageBytes={observation.ImageBytes.Length}, " +
            $"base64Bytes={((observation.ImageBytes.Length + 2) / 3) * 4}, " +
            $"mediaType={observation.MediaType}, lookSeconds={lookSeconds:F2}, " +
            $"referenceSource={_inspectionReferent.SourceLabel}, " +
            $"encodingQuality={observation.EncodingQuality}, " +
            $"fieldOfViewMatched={observation.FieldOfViewMatched}, " +
            $"sourceFieldOfView={observation.SourceFieldOfView:F2}, " +
            $"captureFieldOfView={observation.CaptureFieldOfView:F2}, " +
            $"aimYawError={_attention.LastAimYawError:F1}, " +
            $"aimPitchError={_attention.LastAimPitchError:F1}, " +
            $"alignmentTimedOut={alignmentTimedOut}.");
    }

    private static string DescribeObservation(
        CompanionVisionObservation observation,
        string referenceSource)
    {
        return
            "Bot-eye observation captured for inspect_reference. " +
            $"reference_source={referenceSource}; " +
            $"human_gaze_raycast_hit={observation.ReferenceRayHit}; " +
            $"alignment_timed_out={observation.AlignmentTimedOut}.";
    }

    private void CompleteFailure(string error)
    {
        _completion = CompanionJobCompletion.Failed(error);
        Plugin.Logger.LogWarning($"[VISION] INSPECTION_FAILED error={error}.");
        EndAttention();
    }

    private void EndAttention()
    {
        _state = InspectionState.Idle;
        _inspectionReferent = null;
        _attention.ClearTarget(GazeChannel.Inspection);
    }

    private bool TryRefreshReferencePoint(out string error)
    {
        error = null;
        Vector3 point;
        if (_inspectionReferent == null ||
            !_inspectionReferent.TryGetCurrentPoint(out point))
        {
            error = _inspectionReferent == null
                ? "inspection_reference_unavailable"
                : _inspectionReferent.UnavailableError;
            return false;
        }

        _referencePoint = point;
        return true;
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
}
