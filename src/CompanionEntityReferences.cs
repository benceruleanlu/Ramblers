using System;
using System.Collections.Generic;

namespace Ramblers;

internal sealed class CompanionEntityReferenceSet
{
    private readonly Dictionary<string, CompanionPropTarget> _props =
        new Dictionary<string, CompanionPropTarget>(StringComparer.Ordinal);
    private readonly Dictionary<string, CompanionInteractionReference>
        _interactions =
            new Dictionary<string, CompanionInteractionReference>(
                StringComparer.Ordinal);

    internal int Count => _props.Count + _interactions.Count;

    internal bool Add(CompanionPropTarget target)
    {
        if (target == null || string.IsNullOrEmpty(target.StableId))
            return false;
        _props[target.StableId] = target;
        return true;
    }

    internal bool TryResolve(
        string stableId,
        out CompanionPropTarget target,
        out string error)
    {
        target = null;
        error = null;
        if (string.IsNullOrWhiteSpace(stableId) ||
            !_props.TryGetValue(stableId, out target))
        {
            target = null;
            error = "item_not_known";
            return false;
        }

        UnityEngine.Vector3 point;
        if (!target.TryGetCurrentPoint(out point))
        {
            target = null;
            error = "item_not_available";
            return false;
        }

        return true;
    }

    internal bool Add(CompanionInteractionReference reference)
    {
        if (reference == null || string.IsNullOrEmpty(reference.StableId))
            return false;
        _interactions[reference.StableId] = reference;
        return true;
    }

    internal bool TryResolveInteraction(
        string stableId,
        CompanionAffordanceCandidates candidates,
        out CompanionAffordanceTarget target,
        out string error)
    {
        target = null;
        error = null;
        CompanionInteractionReference reference;
        if (string.IsNullOrWhiteSpace(stableId) ||
            !_interactions.TryGetValue(stableId, out reference))
        {
            error = "object_not_known";
            return false;
        }

        if (!reference.TryResolve(candidates, out target, out error))
        {
            target = null;
            error = error ?? "object_not_available";
            return false;
        }
        return true;
    }

}
