using Mirror;
using UnityEngine;

namespace Ramblers;

/// <summary>
/// One concrete prop selected from the human's view at an utterance boundary.
/// The object identity is immutable: later action code may follow this prop's
/// own transform, but it may never reacquire a different object with a raycast
/// or nearest-item search.
/// </summary>
internal sealed class CompanionInteractionTarget
{
    internal const float MaximumHumanReferenceDistance = 40f;

    private const float SelfHitAdvance = 0.02f;
    private const int MaximumRaycastSteps = 8;

    private readonly NetworkIdentity _networkIdentity;
    private readonly uint _networkId;
    private readonly Vector3 _localHitPoint;

    private CompanionInteractionTarget(Prop prop, Vector3 hitPoint)
    {
        Prop = prop;
        ReferenceId = prop.GetInstanceID();
        _networkIdentity = prop.GetComponentInParent<NetworkIdentity>();
        _networkId = _networkIdentity == null ? 0u : _networkIdentity.netId;
        StableId = StableIdFor(prop);
        _localHitPoint = prop.transform.InverseTransformPoint(hitPoint);
    }

    internal Prop Prop { get; }
    internal int ReferenceId { get; }
    internal uint NetworkId => _networkId;
    internal string StableId { get; }

    internal static string StableIdFor(Prop prop)
    {
        if (prop == null)
            return "prop:unavailable";
        var identity = prop.GetComponentInParent<NetworkIdentity>();
        return identity != null && identity.netId != 0u
            ? "prop:net:" + identity.netId
            : "prop:local:" + prop.GetInstanceID();
    }

    /// <summary>
    /// Captures an exact context entity without requiring the human to aim at
    /// it. This is only called for props already selected into bounded world
    /// context; it performs no nearest-object fallback.
    /// </summary>
    internal static bool TryCaptureProp(
        Prop prop,
        out CompanionInteractionTarget target)
    {
        target = null;
        if (prop == null || prop.gameObject == null ||
            !prop.gameObject.activeInHierarchy || prop.isInInventory)
        {
            return false;
        }

        target = new CompanionInteractionTarget(prop, prop.transform.position);
        return true;
    }

    /// <summary>
    /// Freezes the exact prop already in the companion's hands. Drop has no
    /// target parameter at the game API boundary, so this snapshot supplies the
    /// same managed-object and network-identity guard used by pickup.
    /// </summary>
    internal static bool TryCaptureHeldProp(
        Prop prop,
        out CompanionInteractionTarget target)
    {
        target = null;
        if (prop == null || prop.gameObject == null ||
            !prop.gameObject.activeInHierarchy)
        {
            return false;
        }

        target = new CompanionInteractionTarget(
            prop,
            prop.transform.position);
        return true;
    }

    internal static bool TryResolve(
        PlayerCharacter human,
        CompanionBody body,
        out CompanionInteractionTarget target,
        out string error)
    {
        target = null;
        error = null;

        if (human == null || body == null || !body.IsAlive)
        {
            error = "human_reference_unavailable";
            return false;
        }

        var view = ResolveHumanViewTransform(human);
        if (view == null || view.forward.sqrMagnitude < 0.0001f)
        {
            error = "human_view_unavailable";
            return false;
        }

        var direction = view.forward.normalized;
        var rayOrigin = view.position;
        var remainingDistance = MaximumHumanReferenceDistance;
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
                error = "human_reference_not_visible";
                return false;
            }

            var hitTransform = hit.collider == null ? null : hit.collider.transform;
            if (IsUnderRoot(hitTransform, human.transform) ||
                IsUnderRoot(hitTransform, body.Transform))
            {
                var advance = Mathf.Max(SelfHitAdvance, hit.distance + SelfHitAdvance);
                rayOrigin += direction * advance;
                remainingDistance -= advance;
                continue;
            }

            var prop = hit.collider == null
                ? null
                : hit.collider.GetComponentInParent<Prop>();
            if (prop == null || prop.gameObject == null ||
                !prop.gameObject.activeInHierarchy || prop.isInInventory)
            {
                error = "human_reference_not_prop";
                return false;
            }

            target = new CompanionInteractionTarget(prop, hit.point);
            return true;
        }

        error = "human_reference_not_visible";
        return false;
    }

    /// <summary>
    /// Validates the original managed object and, when present, its network
    /// identity. A moving prop remains the same referent; a replacement object
    /// at the old point does not.
    /// </summary>
    internal bool IsStillTheSameProp(Prop candidate)
    {
        if (candidate == null || candidate != Prop ||
            candidate.GetInstanceID() != ReferenceId)
        {
            return false;
        }

        if (_networkIdentity == null)
            return _networkId == 0u;

        var candidateIdentity = candidate.GetComponentInParent<NetworkIdentity>();
        return candidateIdentity == _networkIdentity &&
               candidateIdentity != null &&
               candidateIdentity.netId == _networkId;
    }

    internal bool TryGetCurrentPoint(out Vector3 point)
    {
        point = Vector3.zero;
        if (!IsStillTheSameProp(Prop) || Prop.gameObject == null ||
            !Prop.gameObject.activeInHierarchy || Prop.isInInventory)
        {
            return false;
        }

        point = Prop.transform.TransformPoint(_localHitPoint);
        return true;
    }

    /// <summary>
    /// Resolves the same frozen prop for visual inspection. A human-held prop
    /// may be flagged as inventory by the game, so unlike pickup navigation the
    /// visual path permits that state while retaining exact identity.
    /// </summary>
    internal bool TryGetCurrentInspectionPoint(out Vector3 point)
    {
        point = Vector3.zero;
        if (!IsStillTheSameProp(Prop) || Prop.gameObject == null ||
            !Prop.gameObject.activeInHierarchy)
        {
            return false;
        }

        point = Prop.transform.TransformPoint(_localHitPoint);
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

        return human == null ? null : human.cameraTransform;
    }

    private static bool IsUnderRoot(Transform candidate, Transform root)
    {
        return candidate != null && root != null &&
               (candidate == root || candidate.IsChildOf(root));
    }
}
