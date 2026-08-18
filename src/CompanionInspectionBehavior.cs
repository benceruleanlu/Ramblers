using UnityEngine;

namespace Ramblers;

internal sealed class CompanionInspectionCompletion
{
    internal AgentToolResult Result;
    internal CompanionVisionObservation Observation;
}

/// <summary>
/// A bounded shared-attention action: acknowledge the human, latch the point
/// under their gaze, visibly turn toward it, then capture one bot-eye frame.
/// </summary>
internal sealed class CompanionInspectionBehavior
{
    private const float MinimumHumanGlanceSeconds = 0.30f;
    private const float MaximumHumanGlanceSeconds = 0.65f;
    private const float HumanAimToleranceDegrees = 12f;
    private const float MinimumReferenceLookSeconds = 0.30f;
    private const float MaximumReferenceLookSeconds = 1.25f;
    private const float ReferenceSettleSeconds = 0.10f;
    private const float ReferenceHoldSeconds = 3.00f;
    private const float ReferenceAimToleranceDegrees = 4f;
    private const float MaximumReferenceDistance = 40f;
    private const float SelfHitAdvance = 0.02f;
    private const int MaximumRaycastSteps = 8;

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
    private CompanionInspectionCompletion _completion;

    internal CompanionInspectionBehavior(CompanionAttention attention)
    {
        _attention = attention;
    }

    internal bool IsActive => _state != InspectionState.Idle;

    internal bool BlocksMovement => IsActive;

    internal void Bind(CompanionBody body, PlayerCharacter human)
    {
        _body = body;
        _humanAtSpawn = human;
        _state = InspectionState.Idle;
        _completion = null;
        _referenceAlignedAt = -1f;
        _referencePoint = Vector3.zero;
        _referenceRayHit = false;
    }

    internal bool TryBegin(float now, out AgentToolResult failure)
    {
        failure = null;
        if (_body == null || !_body.IsAlive)
        {
            failure = AgentToolResult.Failure("bot_not_spawned");
            return false;
        }

        if (IsActive || _completion != null)
        {
            failure = AgentToolResult.Failure("inspection_in_progress");
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
        _attention.BeginInspection(CompanionBody.HeadPositionOf(human), now);
        Plugin.Logger.LogInfo("[VISION] INSPECTION_STARTED phase=look_at_human.");
        return true;
    }

    internal void TickFrame(float now)
    {
        if (_state == InspectionState.Idle)
            return;

        if (_state == InspectionState.HoldingReference)
        {
            _attention.SetInspectionTarget(_referencePoint);
            if (now - _stateStartedAt >= ReferenceHoldSeconds)
                EndAttention(now);
            return;
        }

        if (_body == null || !_body.IsAlive)
        {
            CompleteFailure("bot_not_spawned", now);
            return;
        }

        var human = GetHumanPlayer();
        if (human == null)
        {
            CompleteFailure("human_player_unavailable", now);
            return;
        }

        if (_state == InspectionState.AcknowledgingHuman)
        {
            _attention.SetInspectionTarget(CompanionBody.HeadPositionOf(human));
            var glanceSeconds = now - _stateStartedAt;
            if (glanceSeconds < MinimumHumanGlanceSeconds)
                return;
            if (!_attention.IsAimWithin(
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
                CompleteFailure("human_gaze_unavailable", now);
                return;
            }

            _state = InspectionState.AligningReference;
            _stateStartedAt = now;
            _referenceAlignedAt = -1f;
            _attention.SetInspectionTarget(_referencePoint);
            Plugin.Logger.LogInfo(
                $"[VISION] REFERENCE_LATCHED source=" +
                $"{(_referenceRayHit ? "raycast_hit" : "gaze_fallback")}, " +
                $"target={_referencePoint}, glanceSeconds={glanceSeconds:F2}, " +
                $"glanceTimedOut={glanceSeconds >= MaximumHumanGlanceSeconds}.");
            return;
        }

        _attention.SetInspectionTarget(_referencePoint);
        var lookSeconds = now - _stateStartedAt;
        if (lookSeconds < MinimumReferenceLookSeconds)
            return;

        if (_attention.IsAimWithin(
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

    internal bool TryTakeCompletion(out CompanionInspectionCompletion completion)
    {
        completion = _completion;
        if (completion == null)
            return false;

        _completion = null;
        return true;
    }

    internal void Cancel(float now)
    {
        _completion = null;
        EndAttention(now);
    }

    internal void ReleaseAttention(float now)
    {
        if (_state == InspectionState.HoldingReference)
            EndAttention(now);
    }

    internal void FailActive(string error, float now)
    {
        if (_completion != null)
        {
            EndAttention(now);
            return;
        }
        if (IsActive)
            CompleteFailure(error ?? "action_execution_failed", now);
    }

    internal void Release()
    {
        _body = null;
        _humanAtSpawn = null;
        _state = InspectionState.Idle;
        _completion = null;
        _referenceAlignedAt = -1f;
        _referencePoint = Vector3.zero;
        _referenceRayHit = false;
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
                _attention.LastAimDirection,
                _referenceRayHit,
                alignmentTimedOut,
                out observation,
                out error))
        {
            CompleteFailure(error ?? "image_capture_failed", now);
            return;
        }

        _completion = new CompanionInspectionCompletion
        {
            Result = AgentToolResult.Success(
                AgentToolCatalog.InspectReference,
                "captured",
                "reference_observed"),
            Observation = observation
        };
        _state = InspectionState.HoldingReference;
        _stateStartedAt = now;
        Plugin.Logger.LogInfo(
            $"[VISION] CAPTURED width={observation.Width}, height={observation.Height}, " +
            $"jpegBytes={observation.JpegBytes.Length}, lookSeconds={lookSeconds:F2}, " +
            $"aimYawError={_attention.LastAimYawError:F1}, " +
            $"aimPitchError={_attention.LastAimPitchError:F1}, " +
            $"alignmentTimedOut={alignmentTimedOut}.");
    }

    private void CompleteFailure(string error, float now)
    {
        _completion = new CompanionInspectionCompletion
        {
            Result = AgentToolResult.Failure(error),
            Observation = null
        };
        Plugin.Logger.LogWarning($"[VISION] INSPECTION_FAILED error={error}.");
        EndAttention(now);
    }

    private void EndAttention(float now)
    {
        if (_state != InspectionState.Idle)
            _attention.EndInspection(now);
        _state = InspectionState.Idle;
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
                !IsUnderRoot(hitTransform, body.Transform))
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

    private static bool IsUnderRoot(Transform candidate, Transform root)
    {
        return candidate != null && root != null &&
               (candidate == root || candidate.IsChildOf(root));
    }

    private static bool IsHumanBodyCollider(
        Collider candidate,
        PlayerCharacter human)
    {
        return candidate != null && human != null && human.collision != null &&
               candidate == human.collision.bodyCollider;
    }
}
