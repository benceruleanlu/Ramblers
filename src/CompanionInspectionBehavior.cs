using UnityEngine;

namespace Ramblers;

/// <summary>
/// A bounded shared-attention job: acknowledge the human, latch the point under
/// their gaze, visibly turn toward it, then capture one bot-eye frame.
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
    private const float MaximumReferenceDistance = 40f;
    private const float SelfHitAdvance = 0.02f;
    private const int MaximumRaycastSteps = 8;

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

        _state = InspectionState.AcknowledgingHuman;
        _stateStartedAt = now;
        _referenceAlignedAt = -1f;
        _attention.SetTarget(
            GazeChannel.Inspection,
            CompanionBody.HeadPositionOf(human));
        Plugin.Logger.LogInfo("[VISION] INSPECTION_STARTED phase=look_at_human.");
        return true;
    }

    public void Tick(float now)
    {
        if (_state == InspectionState.Idle)
            return;

        if (_state == InspectionState.HoldingReference)
        {
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

            if (!TryResolveReference(
                    human,
                    _body,
                    out _referencePoint,
                    out _referenceRayHit))
            {
                CompleteFailure("human_gaze_unavailable");
                return;
            }

            _state = InspectionState.AligningReference;
            _stateStartedAt = now;
            _referenceAlignedAt = -1f;
            _attention.SetTarget(GazeChannel.Inspection, _referencePoint);
            Plugin.Logger.LogInfo(
                $"[VISION] REFERENCE_LATCHED source=" +
                $"{(_referenceRayHit ? "raycast_hit" : "gaze_fallback")}, " +
                $"target={_referencePoint}, glanceSeconds={glanceSeconds:F2}, " +
                $"glanceTimedOut={glanceSeconds >= MaximumHumanGlanceSeconds}.");
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
        _attention.ClearTarget(GazeChannel.Inspection);
    }

    private void CaptureReference(
        PlayerCharacter human,
        float now,
        float lookSeconds,
        bool alignmentTimedOut)
    {
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
                    DescribeObservation(observation),
                    observation.ImageBytes,
                    observation.MediaType)
            }
        };
        _state = InspectionState.HoldingReference;
        _stateStartedAt = now;
        Plugin.Logger.LogInfo(
            $"[VISION] CAPTURED width={observation.Width}, height={observation.Height}, " +
            $"imageBytes={observation.ImageBytes.Length}, " +
            $"mediaType={observation.MediaType}, lookSeconds={lookSeconds:F2}, " +
            $"aimYawError={_attention.LastAimYawError:F1}, " +
            $"aimPitchError={_attention.LastAimPitchError:F1}, " +
            $"alignmentTimedOut={alignmentTimedOut}.");
    }

    private static string DescribeObservation(CompanionVisionObservation observation)
    {
        return
            "Bot-eye observation captured for inspect_reference. " +
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
        _attention.ClearTarget(GazeChannel.Inspection);
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

    private static bool TryResolveReference(
        PlayerCharacter human,
        CompanionBody body,
        out Vector3 referencePoint,
        out bool rayHit)
    {
        referencePoint = Vector3.zero;
        rayHit = false;
        var viewTransform = ResolveHumanViewTransform(human);
        if (viewTransform == null || viewTransform.forward.sqrMagnitude < 0.0001f)
            return false;

        var origin = viewTransform.position;
        var direction = viewTransform.forward.normalized;
        var rayOrigin = origin;
        var remainingDistance = MaximumReferenceDistance;
        var layerMask = Physics.DefaultRaycastLayers;
        if (human.caster != null && human.caster.layerMask.value != 0)
            layerMask = human.caster.layerMask.value;
        for (var step = 0;
             step < MaximumRaycastSteps && remainingDistance > 0f;
             step++)
        {
            RaycastHit hit;
            if (!Physics.Raycast(
                    rayOrigin,
                    direction,
                    out hit,
                    remainingDistance,
                    layerMask,
                    QueryTriggerInteraction.Ignore))
            {
                break;
            }

            var hitTransform = hit.collider == null ? null : hit.collider.transform;
            if (!IsHumanBodyCollider(hit.collider, human) &&
                !body.Contains(hitTransform))
            {
                referencePoint = hit.point;
                rayHit = true;
                return true;
            }

            var advance = Mathf.Max(SelfHitAdvance, hit.distance + SelfHitAdvance);
            rayOrigin += direction * advance;
            remainingDistance -= advance;
        }

        referencePoint = origin + direction * MaximumReferenceDistance;
        return true;
    }

    private static Transform ResolveHumanViewTransform(PlayerCharacter human)
    {
        if (human != null && human.cameraMinder != null)
        {
            var references = human.cameraMinder.playerCameraReferences;
            if (references != null && references.playerCamera != null)
                return references.playerCamera.transform;
        }

        if (Camera.main != null)
            return Camera.main.transform;
        return human == null ? null : human.cameraTransform;
    }

    private static bool IsHumanBodyCollider(
        Collider candidate,
        PlayerCharacter human)
    {
        return candidate != null && human != null && human.collision != null &&
               candidate == human.collision.bodyCollider;
    }
}
