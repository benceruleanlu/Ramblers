using UnityEngine;

namespace Ramblers;

internal enum CompanionInspectionSource
{
    HumanGaze,
    HumanHeldItem
}

/// <summary>
/// Both visual referents available at one utterance boundary. The model may
/// choose which meaning the human expressed, but it cannot ask Unity to select
/// a new object or cast a newer gaze ray after that choice.
/// </summary>
internal sealed class CompanionInspectionCandidates
{
    private const float MaximumReferenceDistance = 40f;
    private const float SelfHitAdvance = 0.02f;
    private const int MaximumRaycastSteps = 8;

    private readonly Vector3 _gazePoint;
    private readonly CompanionInteractionTarget _heldItem;

    private CompanionInspectionCandidates(
        bool gazeAvailable,
        Vector3 gazePoint,
        bool gazeRayHit,
        string gazeCaptureError,
        CompanionInteractionTarget heldItem,
        string heldItemCaptureError)
    {
        GazeAvailable = gazeAvailable;
        _gazePoint = gazePoint;
        GazeRayHit = gazeRayHit;
        GazeCaptureError = gazeCaptureError;
        _heldItem = heldItem;
        HeldItemCaptureError = heldItemCaptureError;
    }

    internal bool GazeAvailable { get; }
    internal bool GazeRayHit { get; }
    internal string GazeCaptureError { get; }
    internal bool HeldItemAvailable => _heldItem != null;
    internal string HeldItemCaptureError { get; }
    internal int HeldItemReferenceId => _heldItem == null ? 0 : _heldItem.ReferenceId;
    internal uint HeldItemNetworkId => _heldItem == null ? 0u : _heldItem.NetworkId;

    internal static bool TryCapture(
        PlayerCharacter human,
        CompanionBody body,
        out CompanionInspectionCandidates candidates,
        out string error)
    {
        candidates = null;
        error = null;
        if (human == null || body == null || !body.IsAlive)
        {
            error = "inspection_reference_unavailable";
            return false;
        }

        CompanionInteractionTarget heldItem = null;
        string heldItemError;
        if (human.hands == null)
        {
            heldItemError = "human_hands_unavailable";
        }
        else if (human.hands.heldProp == null)
        {
            heldItemError = human.hands.heldCharacter == null
                ? "human_held_item_unavailable"
                : "human_held_item_not_prop";
        }
        else if (!CompanionInteractionTarget.TryCaptureHeldProp(
                     human.hands.heldProp,
                     out heldItem))
        {
            heldItemError = "human_held_item_unavailable";
        }
        else
        {
            heldItemError = null;
        }

        Vector3 gazePoint;
        bool gazeRayHit;
        string gazeError;
        var gazeAvailable = TryCaptureGaze(
            human,
            body,
            out gazePoint,
            out gazeRayHit,
            out gazeError);
        candidates = new CompanionInspectionCandidates(
            gazeAvailable,
            gazePoint,
            gazeRayHit,
            gazeError,
            heldItem,
            heldItemError);
        return true;
    }

    internal bool TrySelect(
        CompanionInspectionSource source,
        out CompanionInspectionReferent referent,
        out string error)
    {
        referent = null;
        error = null;
        if (source == CompanionInspectionSource.HumanHeldItem)
        {
            if (_heldItem == null)
            {
                error = HeldItemCaptureError ?? "human_held_item_unavailable";
                return false;
            }

            referent = CompanionInspectionReferent.FromHeldItem(_heldItem);
            return true;
        }

        if (!GazeAvailable)
        {
            error = GazeCaptureError ?? "human_gaze_unavailable";
            return false;
        }

        referent = CompanionInspectionReferent.FromGaze(
            _gazePoint,
            GazeRayHit);
        return true;
    }

    private static bool TryCaptureGaze(
        PlayerCharacter human,
        CompanionBody body,
        out Vector3 gazePoint,
        out bool rayHit,
        out string error)
    {
        gazePoint = Vector3.zero;
        rayHit = false;
        error = null;
        var viewTransform = ResolveHumanViewTransform(human);
        if (viewTransform == null || viewTransform.forward.sqrMagnitude < 0.0001f)
        {
            error = "human_gaze_unavailable";
            return false;
        }

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
            if (!IsUnderRoot(hitTransform, human.transform) &&
                !body.Contains(hitTransform))
            {
                gazePoint = hit.point;
                rayHit = true;
                return true;
            }

            var advance = Mathf.Max(SelfHitAdvance, hit.distance + SelfHitAdvance);
            rayOrigin += direction * advance;
            remainingDistance -= advance;
        }

        gazePoint = origin + direction * MaximumReferenceDistance;
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
}

/// <summary>
/// One model-selected visual referent. A gaze point remains frozen; a held prop
/// may move, but only that exact managed object and network identity are
/// followed.
/// </summary>
internal sealed class CompanionInspectionReferent
{
    private readonly Vector3 _frozenPoint;
    private readonly CompanionInteractionTarget _movingTarget;

    private CompanionInspectionReferent(
        CompanionInspectionSource source,
        Vector3 frozenPoint,
        bool gazeRayHit,
        CompanionInteractionTarget movingTarget)
    {
        Source = source;
        _frozenPoint = frozenPoint;
        GazeRayHit = gazeRayHit;
        _movingTarget = movingTarget;
    }

    internal CompanionInspectionSource Source { get; }
    internal bool GazeRayHit { get; }
    internal string SourceLabel =>
        Source == CompanionInspectionSource.HumanHeldItem
            ? "human_held_item"
            : GazeRayHit
                ? "human_gaze_raycast_hit"
                : "human_gaze_fallback";
    internal string UnavailableError =>
        Source == CompanionInspectionSource.HumanHeldItem
            ? "human_held_item_unavailable"
            : "human_gaze_unavailable";

    internal static CompanionInspectionReferent FromGaze(
        Vector3 point,
        bool rayHit)
    {
        return new CompanionInspectionReferent(
            CompanionInspectionSource.HumanGaze,
            point,
            rayHit,
            null);
    }

    internal static CompanionInspectionReferent FromHeldItem(
        CompanionInteractionTarget target)
    {
        return new CompanionInspectionReferent(
            CompanionInspectionSource.HumanHeldItem,
            Vector3.zero,
            false,
            target);
    }

    internal bool TryGetCurrentPoint(out Vector3 point)
    {
        if (_movingTarget != null)
            return _movingTarget.TryGetCurrentInspectionPoint(out point);

        point = _frozenPoint;
        return true;
    }
}
