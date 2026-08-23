using System;
using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace Ramblers;

/// <summary>
/// One model-visible context ID bound to the exact CastableTarget. Its raw
/// structural kind and identity are frozen without actor prerequisites; tool
/// routing rematerializes a current typed affordance through the original turn
/// actor context. It never searches for another component.
/// </summary>
internal sealed class CompanionInteractionReference
{
    private const int MaximumNameHierarchyDepth = 5;

    private readonly CastableTarget _castableTarget;
    private readonly int _castableInstanceId;
    private readonly NetworkIdentity _networkIdentity;
    private readonly uint _networkId;
    private readonly CompanionInteractionStructuralKind _structuralKind;

    private CompanionInteractionReference(
        CastableTarget castableTarget,
        CompanionInteractionStructuralKind structuralKind,
        string naturalName)
    {
        _castableTarget = castableTarget;
        _castableInstanceId = castableTarget.GetInstanceID();
        _networkIdentity = castableTarget.GetComponentInParent<NetworkIdentity>();
        _networkId = _networkIdentity == null ? 0u : _networkIdentity.netId;
        _structuralKind = structuralKind;
        StableId = CompanionInteractionReferenceProtocol.BuildStableId(
            _networkId,
            _castableInstanceId);
        Name = naturalName;
    }

    internal string StableId { get; }
    internal string Name { get; }
    internal string Kind =>
        CompanionInteractionReferenceProtocol.KindLabel(_structuralKind);
    internal uint NetworkId => _networkId;
    internal int CastableInstanceId => _castableInstanceId;

    internal static bool TryCapture(
        CastableTarget castableTarget,
        out CompanionInteractionReference reference,
        out string error)
    {
        reference = null;
        error = null;
        try
        {
            if (castableTarget == null ||
                !CompanionAffordanceTarget.IsExactWorldReference(
                    castableTarget,
                    castableTarget.GetInstanceID()))
            {
                error = "interaction_target_unavailable";
                return false;
            }

            CompanionInteractionStructuralKind structuralKind;
            if (!TryCaptureStructuralKind(
                    castableTarget,
                    out structuralKind,
                    out error))
            {
                return false;
            }

            reference = new CompanionInteractionReference(
                castableTarget,
                structuralKind,
                NaturalNameFor(castableTarget, structuralKind));
            return true;
        }
        catch (Exception)
        {
            reference = null;
            error = "interaction_reference_capture_failed";
            return false;
        }
    }

    internal bool TryResolve(
        CompanionAffordanceCandidates candidates,
        out CompanionAffordanceTarget target,
        out string error)
    {
        target = null;
        error = null;
        try
        {
            if (!IsExactCastableIdentity())
            {
                error = "interaction_target_changed";
                return false;
            }
            if (candidates == null)
            {
                error = "interaction_reference_unavailable";
                return false;
            }
            return candidates.TrySelectExactWorldReference(
                CompanionAffordanceSource.ContextEntity,
                _castableTarget,
                _castableInstanceId,
                out target,
                out error);
        }
        catch (Exception)
        {
            target = null;
            error = "interaction_target_unavailable";
            return false;
        }
    }

    internal bool TryGetCurrentPoint(out Vector3 point)
    {
        point = Vector3.zero;
        try
        {
            if (!IsExactCastableIdentity())
                return false;
            var crosshair = _castableTarget.GetCrosshairTransform();
            point = crosshair == null
                ? _castableTarget.transform.position
                : crosshair.position;
            return true;
        }
        catch (Exception)
        {
            point = Vector3.zero;
            return false;
        }
    }

    private bool IsExactCastableIdentity()
    {
        if (!CompanionAffordanceTarget.IsExactWorldReference(
                _castableTarget,
                _castableInstanceId))
        {
            return false;
        }

        var currentIdentity =
            _castableTarget.GetComponentInParent<NetworkIdentity>();
        if (_networkIdentity == null)
            return currentIdentity == null && _networkId == 0u;
        return currentIdentity == _networkIdentity &&
               currentIdentity != null &&
               currentIdentity.netId == _networkId;
    }

    private static bool TryCaptureStructuralKind(
        CastableTarget castableTarget,
        out CompanionInteractionStructuralKind kind,
        out string error)
    {
        kind = CompanionInteractionStructuralKind.None;
        error = null;
        var outcomes = castableTarget == null ? null : castableTarget.outcomes;
        var outcomeCount = outcomes == null ? 0 : outcomes.Length;
        for (var index = 0; index < outcomeCount; index++)
        {
            var outcome = outcomes[index];
            var candidate = CompanionInteractionStructuralKind.None;
            // Match Big Walk's primary-action precedence without consulting
            // actor reach, hands, keys, pockets, or other dynamic admission.
            if (outcome?.playerPose != null)
            {
                candidate = CompanionInteractionStructuralKind.PlayerPose;
            }
            else if (outcome?.propHome != null)
            {
                candidate = CompanionInteractionStructuralKind.PropHome;
            }
            else if (outcome?.peckSwitch != null)
            {
                candidate = CompanionInteractionStructuralKind.WorldSwitch;
            }
            kind = CompanionInteractionReferenceProtocol.MergeStructuralKind(
                kind,
                candidate);
        }

        if (kind != CompanionInteractionStructuralKind.None)
            return true;
        error = "interaction_kind_unsupported";
        return false;
    }

    private static string NaturalNameFor(
        CastableTarget castableTarget,
        CompanionInteractionStructuralKind structuralKind)
    {
        var hierarchyNames = new List<string>(MaximumNameHierarchyDepth);
        var transform = castableTarget == null
            ? null
            : castableTarget.transform;
        for (var depth = 0;
             transform != null && depth < MaximumNameHierarchyDepth;
             depth++)
        {
            hierarchyNames.Add(
                transform.gameObject == null
                    ? null
                    : transform.gameObject.name);
            transform = transform.parent;
        }
        return CompanionInteractionReferenceProtocol.NaturalNameFromHierarchy(
            hierarchyNames,
            CompanionInteractionReferenceProtocol.DefaultNaturalName(
                structuralKind));
    }
}
