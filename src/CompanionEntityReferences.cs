using System;
using System.Collections.Generic;

namespace Ramblers;

/// <summary>
/// Exact prop identities that were exposed to the model as game context for a
/// turn. The model may select an ID semantically; deterministic action code
/// resolves only the matching frozen object and never substitutes a neighbour.
/// </summary>
internal sealed class CompanionEntityReferenceSet
{
    private readonly Dictionary<string, CompanionInteractionTarget> _props =
        new Dictionary<string, CompanionInteractionTarget>(StringComparer.Ordinal);
    private readonly Dictionary<string, CompanionPeckTarget> _interactables =
        new Dictionary<string, CompanionPeckTarget>(StringComparer.Ordinal);

    internal int Count => _props.Count + _interactables.Count;

    internal bool Add(CompanionInteractionTarget target)
    {
        if (target == null || string.IsNullOrEmpty(target.StableId))
            return false;
        _props[target.StableId] = target;
        return true;
    }

    internal bool TryResolve(
        string stableId,
        out CompanionInteractionTarget target,
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

    internal bool Add(CompanionPeckTarget target)
    {
        if (target == null || string.IsNullOrEmpty(target.ReferenceId))
            return false;
        _interactables[target.ReferenceId] = target;
        return true;
    }

    internal bool TryResolveInteraction(
        string stableId,
        out CompanionPeckTarget target,
        out string error)
    {
        target = null;
        error = null;
        if (string.IsNullOrWhiteSpace(stableId) ||
            !_interactables.TryGetValue(stableId, out target))
        {
            target = null;
            error = "object_not_known";
            return false;
        }

        UnityEngine.Vector3 point;
        if (!target.TryGetCurrentPoint(out point, out error))
        {
            target = null;
            error = error ?? "object_not_available";
            return false;
        }
        return true;
    }
}
