using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Mirror;
using UnityEngine;

namespace Ramblers;

internal sealed class CompanionInteractableObservation
{
    internal CompanionInteractionReference Reference;
    internal Vector3 Point;
    internal float SeenAt;
    internal float CompanionDistance;
    internal float HumanDistance;
}

internal sealed class CompanionInteractableDiscovery
{
    internal const int MaximumNearbyInteractables = 6;
    internal const int MaximumRecentInteractables = 4;
    internal const int MaximumRememberedInteractables = 24;
    internal const float NearbyRadius = 12f;
    internal const float RecentLifetimeSeconds = 45f;
    internal const float RecentMaximumDistance = 30f;

    private const int MaximumSpawnedRootsInspected = 256;
    private const float SpawnedRootDiscoveryRadius = 36f;
    private const int MaximumSpawnedHierarchyNodes = 1024;
    private const int MaximumRegistryItemsInspected = 192;
    private const int MaximumRegistryHierarchyNodes = 512;
    private const int MaximumHierarchyNodesPerRegistryItem = 24;
    private const int MaximumRawCandidates = 48;

    internal const string CapabilityBoundary =
        "bounded spawned network roots, PropHome.allPropHomes, Prop.allProps, " +
        "and the exact current human interaction reference; never-observed " +
        "non-networked controls outside those registries are not enumerated";

    private sealed class Candidate
    {
        internal CastableTarget Target;
        internal int InstanceId;
        internal Vector3 Point;
        internal float CompanionDistance;
        internal float HumanDistance;
        internal bool DirectlyObserved;
    }

    private sealed class Remembered
    {
        internal CompanionInteractionReference Reference;
        internal float SeenAt;
    }

    private readonly Dictionary<string, Remembered> _remembered =
        new Dictionary<string, Remembered>(StringComparer.Ordinal);
    private bool _registryFailureLogged;

    internal void Clear()
    {
        _remembered.Clear();
        _registryFailureLogged = false;
    }

    internal void Capture(
        PlayerCharacter human,
        CompanionBody body,
        CompanionAffordanceCandidates affordanceCandidates,
        float now,
        CompanionEntityReferenceSet entityReferences,
        out CompanionInteractableObservation[] nearby,
        out CompanionInteractableObservation[] recent)
    {
        var nearbyResult = new List<CompanionInteractableObservation>();
        if (affordanceCandidates == null ||
            !affordanceCandidates.CanResolveContextReferences(body))
        {
            PruneRememberedByAge(now);
            nearby = nearbyResult.ToArray();
            recent = nearbyResult.ToArray();
            return;
        }

        var candidates = new Dictionary<int, Candidate>();
        if (human == null || body == null || !body.IsAlive)
        {
            nearby = nearbyResult.ToArray();
            recent = CaptureRecent(body, now, null, entityReferences);
            return;
        }

        var companionPosition = body.Position;
        var humanPosition = human.transform.position;
        var discoveryDegraded = false;
        try
        {
            ObserveCurrentHumanReference(
                affordanceCandidates,
                companionPosition,
                humanPosition,
                candidates);
        }
        catch (Exception exception)
        {
            discoveryDegraded = true;
            LogDiscoveryDegraded(exception);
        }

        Il2CppSystem.Type castableType = null;
        try
        {
            castableType = Il2CppType.Of<CastableTarget>();
        }
        catch (Exception exception)
        {
            discoveryDegraded = true;
            LogDiscoveryDegraded(exception);
        }
        try
        {
            CaptureSpawnedCandidates(
                castableType,
                companionPosition,
                humanPosition,
                candidates);
        }
        catch (Exception exception)
        {
            discoveryDegraded = true;
            LogDiscoveryDegraded(exception);
        }
        try
        {
            CapturePropHomeCandidates(
                castableType,
                companionPosition,
                humanPosition,
                candidates);
        }
        catch (Exception exception)
        {
            discoveryDegraded = true;
            LogDiscoveryDegraded(exception);
        }
        try
        {
            CapturePropCandidates(
                castableType,
                companionPosition,
                humanPosition,
                candidates);
        }
        catch (Exception exception)
        {
            discoveryDegraded = true;
            LogDiscoveryDegraded(exception);
        }
        if (!discoveryDegraded)
            _registryFailureLogged = false;

        var ordered = new List<Candidate>(candidates.Values);
        ordered.Sort(CompareCandidates);
        var nearbyIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < ordered.Count; index++)
        {
            var candidate = ordered[index];
            CompanionInteractionReference reference;
            string captureError;
            if (!CompanionInteractionReference.TryCapture(
                    candidate.Target,
                    out reference,
                    out captureError))
            {
                continue;
            }

            var isNearby = CompanionInteractionReferenceProtocol.IsNearby(
                candidate.CompanionDistance,
                candidate.HumanDistance,
                NearbyRadius);
            if (candidate.DirectlyObserved && !isNearby)
            {
                Remember(reference, now);
            }

            if (!isNearby ||
                nearbyResult.Count >= MaximumNearbyInteractables)
            {
                continue;
            }

            var observation = new CompanionInteractableObservation
            {
                Reference = reference,
                Point = candidate.Point,
                SeenAt = now,
                CompanionDistance = candidate.CompanionDistance,
                HumanDistance = candidate.HumanDistance
            };
            nearbyResult.Add(observation);
            nearbyIds.Add(reference.StableId);
            Remember(reference, now);
            entityReferences?.Add(reference);
        }

        nearby = nearbyResult.ToArray();
        recent = CaptureRecent(body, now, nearbyIds, entityReferences);
    }

    private void LogDiscoveryDegraded(Exception exception)
    {
        if (_registryFailureLogged)
            return;
        Plugin.Logger.LogWarning(
            "[AWARENESS] INTERACTABLE_DISCOVERY_DEGRADED " +
            $"error={exception.GetType().Name}, " +
            $"boundary={CapabilityBoundary}.");
        _registryFailureLogged = true;
    }

    private static void ObserveCurrentHumanReference(
        CompanionAffordanceCandidates affordanceCandidates,
        Vector3 companionPosition,
        Vector3 humanPosition,
        Dictionary<int, Candidate> candidates)
    {
        CastableTarget target;
        int frozenInstanceId;
        if (affordanceCandidates == null ||
            !affordanceCandidates.TryGetExactHumanWorldReference(
                out target,
                out frozenInstanceId))
        {
            return;
        }
        if (target == null || target.GetInstanceID() != frozenInstanceId)
            return;

        AddCandidate(
            target,
            companionPosition,
            humanPosition,
            true,
            candidates);
    }

    private static void CaptureSpawnedCandidates(
        Il2CppSystem.Type castableType,
        Vector3 companionPosition,
        Vector3 humanPosition,
        Dictionary<int, Candidate> candidates)
    {
        if (!NetworkServer.active)
            return;
        var spawned = NetworkServer.spawned;
        if (spawned == null)
            return;

        var rootsInspected = 0;
        var hierarchyNodesInspected = 0;
        foreach (var identity in spawned.Values)
        {
            if (rootsInspected++ >= MaximumSpawnedRootsInspected ||
                hierarchyNodesInspected >= MaximumSpawnedHierarchyNodes)
            {
                break;
            }
            if (identity == null || identity.gameObject == null ||
                !identity.gameObject.activeInHierarchy)
            {
                continue;
            }

            var rootPosition = identity.transform.position;
            if (!CompanionInteractionReferenceProtocol.IsNearby(
                    Vector3.Distance(companionPosition, rootPosition),
                    Vector3.Distance(humanPosition, rootPosition),
                    SpawnedRootDiscoveryRadius))
            {
                continue;
            }

            CaptureHierarchyCandidates(
                identity.transform,
                castableType,
                MaximumSpawnedHierarchyNodes,
                ref hierarchyNodesInspected,
                companionPosition,
                humanPosition,
                candidates);
        }
    }

    private static void CapturePropHomeCandidates(
        Il2CppSystem.Type castableType,
        Vector3 companionPosition,
        Vector3 humanPosition,
        Dictionary<int, Candidate> candidates)
    {
        var homes = PropHome.allPropHomes;
        if (homes == null)
            return;

        var hierarchyNodesInspected = 0;
        var count = Math.Min(homes.Count, MaximumRegistryItemsInspected);
        for (var index = 0;
             index < count &&
             hierarchyNodesInspected < MaximumRegistryHierarchyNodes;
             index++)
        {
            var home = homes[index];
            if (home == null || home.gameObject == null ||
                !home.gameObject.activeInHierarchy)
            {
                continue;
            }
            if (!IsRegistryRootNearby(
                    home.transform.position,
                    companionPosition,
                    humanPosition))
            {
                continue;
            }

            CaptureLocalHierarchyCandidates(
                home.transform,
                castableType,
                ref hierarchyNodesInspected,
                companionPosition,
                humanPosition,
                candidates);
        }
    }

    private static void CapturePropCandidates(
        Il2CppSystem.Type castableType,
        Vector3 companionPosition,
        Vector3 humanPosition,
        Dictionary<int, Candidate> candidates)
    {
        var props = Prop.allProps;
        if (props == null)
            return;

        var hierarchyNodesInspected = 0;
        var count = Math.Min(props.Count, MaximumRegistryItemsInspected);
        for (var index = 0;
             index < count &&
             hierarchyNodesInspected < MaximumRegistryHierarchyNodes;
             index++)
        {
            var prop = props[index];
            if (prop == null || prop.gameObject == null ||
                !prop.gameObject.activeInHierarchy)
            {
                continue;
            }
            if (!IsRegistryRootNearby(
                    prop.transform.position,
                    companionPosition,
                    humanPosition))
            {
                continue;
            }

            CaptureLocalHierarchyCandidates(
                prop.transform,
                castableType,
                ref hierarchyNodesInspected,
                companionPosition,
                humanPosition,
                candidates);
        }
    }

    private static void CaptureLocalHierarchyCandidates(
        Transform root,
        Il2CppSystem.Type castableType,
        ref int hierarchyNodesInspected,
        Vector3 companionPosition,
        Vector3 humanPosition,
        Dictionary<int, Candidate> candidates)
    {
        var before = hierarchyNodesInspected;
        var maximumForItem = Math.Min(
            MaximumRegistryHierarchyNodes,
            before + MaximumHierarchyNodesPerRegistryItem);
        CaptureParentCandidates(
            root,
            castableType,
            maximumForItem,
            ref hierarchyNodesInspected,
            companionPosition,
            humanPosition,
            candidates);
        CaptureHierarchyCandidates(
            root,
            castableType,
            maximumForItem,
            ref hierarchyNodesInspected,
            companionPosition,
            humanPosition,
            candidates);
    }

    private static void CaptureParentCandidates(
        Transform start,
        Il2CppSystem.Type castableType,
        int maximumNodes,
        ref int nodesInspected,
        Vector3 companionPosition,
        Vector3 humanPosition,
        Dictionary<int, Candidate> candidates)
    {
        if (start == null || castableType == null)
            return;
        var transform = start;
        while (transform != null && nodesInspected < maximumNodes)
        {
            nodesInspected++;
            var component = transform.GetComponent(castableType);
            var castableTarget = component == null
                ? null
                : component.TryCast<CastableTarget>();
            if (castableTarget != null)
            {
                AddCandidate(
                    castableTarget,
                    companionPosition,
                    humanPosition,
                    false,
                    candidates);
            }
            transform = transform.parent;
        }
    }

    private static void CaptureHierarchyCandidates(
        Transform root,
        Il2CppSystem.Type castableType,
        int maximumNodes,
        ref int nodesInspected,
        Vector3 companionPosition,
        Vector3 humanPosition,
        Dictionary<int, Candidate> candidates)
    {
        if (root == null || castableType == null)
            return;

        var pending = new Stack<Transform>();
        pending.Push(root);
        while (pending.Count > 0 && nodesInspected < maximumNodes)
        {
            var transform = pending.Pop();
            nodesInspected++;
            if (transform == null || transform.gameObject == null ||
                !transform.gameObject.activeInHierarchy)
            {
                continue;
            }

            var component = transform.GetComponent(castableType);
            var castableTarget = component == null
                ? null
                : component.TryCast<CastableTarget>();
            if (castableTarget != null)
            {
                AddCandidate(
                    castableTarget,
                    companionPosition,
                    humanPosition,
                    false,
                    candidates);
            }

            for (var childIndex = transform.childCount - 1;
                 childIndex >= 0 &&
                 nodesInspected + pending.Count < maximumNodes;
                 childIndex--)
            {
                pending.Push(transform.GetChild(childIndex));
            }
        }
    }

    private static void AddCandidate(
        CastableTarget target,
        Vector3 companionPosition,
        Vector3 humanPosition,
        bool directlyObserved,
        Dictionary<int, Candidate> candidates)
    {
        if (target == null ||
            !CompanionAffordanceTarget.IsExactWorldReference(
                target,
                target.GetInstanceID()))
        {
            return;
        }

        var instanceId = target.GetInstanceID();
        Candidate existing;
        if (candidates.TryGetValue(instanceId, out existing))
        {
            existing.DirectlyObserved |= directlyObserved;
            return;
        }
        var crosshair = target.GetCrosshairTransform();
        var point = crosshair == null
            ? target.transform.position
            : crosshair.position;
        var companionDistance = Vector3.Distance(companionPosition, point);
        var humanDistance = Vector3.Distance(humanPosition, point);
        if (!directlyObserved &&
            !CompanionInteractionReferenceProtocol.IsNearby(
                companionDistance,
                humanDistance,
                NearbyRadius))
        {
            return;
        }

        var candidate = new Candidate
        {
            Target = target,
            InstanceId = instanceId,
            Point = point,
            CompanionDistance = companionDistance,
            HumanDistance = humanDistance,
            DirectlyObserved = directlyObserved
        };
        if (candidates.Count >= MaximumRawCandidates)
        {
            var worstKey = 0;
            Candidate worst = null;
            foreach (var pair in candidates)
            {
                if (worst == null || CompareCandidates(pair.Value, worst) > 0)
                {
                    worstKey = pair.Key;
                    worst = pair.Value;
                }
            }
            if (worst == null || CompareCandidates(candidate, worst) >= 0)
                return;
            candidates.Remove(worstKey);
        }
        candidates[instanceId] = candidate;
    }

    private CompanionInteractableObservation[] CaptureRecent(
        CompanionBody body,
        float now,
        HashSet<string> nearbyIds,
        CompanionEntityReferenceSet entityReferences)
    {
        TrimRememberedToLimit();
        var result = new List<CompanionInteractableObservation>();
        var expired = new List<string>();
        foreach (var pair in _remembered)
        {
            var memory = pair.Value;
            Vector3 point;
            if (memory == null || memory.Reference == null ||
                !memory.Reference.TryGetCurrentPoint(out point))
            {
                expired.Add(pair.Key);
                continue;
            }

            var age = Mathf.Max(0f, now - memory.SeenAt);
            if (age > RecentLifetimeSeconds)
            {
                expired.Add(pair.Key);
                continue;
            }
            if (nearbyIds != null && nearbyIds.Contains(pair.Key))
                continue;

            var companionDistance = body == null
                ? float.MaxValue
                : Vector3.Distance(body.Position, point);
            if (!CompanionInteractionReferenceProtocol.IsRecent(
                    age,
                    companionDistance,
                    RecentLifetimeSeconds,
                    RecentMaximumDistance))
            {
                continue;
            }

            result.Add(new CompanionInteractableObservation
            {
                Reference = memory.Reference,
                Point = point,
                SeenAt = memory.SeenAt,
                CompanionDistance = companionDistance,
                HumanDistance = -1f
            });
        }

        for (var index = 0; index < expired.Count; index++)
            _remembered.Remove(expired[index]);
        result.Sort(CompareRecentObservations);
        if (result.Count > MaximumRecentInteractables)
        {
            result.RemoveRange(
                MaximumRecentInteractables,
                result.Count - MaximumRecentInteractables);
        }
        for (var index = 0; index < result.Count; index++)
            entityReferences?.Add(result[index].Reference);
        return result.ToArray();
    }

    private void Remember(
        CompanionInteractionReference reference,
        float seenAt)
    {
        if (reference == null || string.IsNullOrEmpty(reference.StableId))
            return;
        _remembered[reference.StableId] = new Remembered
        {
            Reference = reference,
            SeenAt = seenAt
        };
        TrimRememberedToLimit();
    }

    private void PruneRememberedByAge(float now)
    {
        TrimRememberedToLimit();
        var expired = new List<string>();
        foreach (var pair in _remembered)
        {
            var memory = pair.Value;
            if (memory == null || memory.Reference == null ||
                Mathf.Max(0f, now - memory.SeenAt) > RecentLifetimeSeconds)
            {
                expired.Add(pair.Key);
            }
        }
        for (var index = 0; index < expired.Count; index++)
            _remembered.Remove(expired[index]);
    }

    private void TrimRememberedToLimit()
    {
        while (_remembered.Count > MaximumRememberedInteractables)
        {
            string oldestKey = null;
            var oldestSeenAt = 0f;
            var hasOldest = false;
            foreach (var pair in _remembered)
            {
                var candidate = pair.Value;
                var candidateSeenAt = candidate == null
                    ? float.MinValue
                    : candidate.SeenAt;
                if (!hasOldest ||
                    CompanionInteractionReferenceProtocol
                        .CompareRememberedForEviction(
                            candidateSeenAt,
                            pair.Key,
                            oldestSeenAt,
                            oldestKey) < 0)
                {
                    oldestKey = pair.Key;
                    oldestSeenAt = candidateSeenAt;
                    hasOldest = true;
                }
            }
            if (oldestKey == null)
                break;
            _remembered.Remove(oldestKey);
        }
    }

    private static int CompareRecentObservations(
        CompanionInteractableObservation left,
        CompanionInteractableObservation right)
    {
        var timeOrder = right.SeenAt.CompareTo(left.SeenAt);
        if (timeOrder != 0)
            return timeOrder;
        return string.CompareOrdinal(
            left.Reference?.StableId,
            right.Reference?.StableId);
    }

    private static bool IsRegistryRootNearby(
        Vector3 rootPosition,
        Vector3 companionPosition,
        Vector3 humanPosition)
    {
        return CompanionInteractionReferenceProtocol.IsNearby(
            Vector3.Distance(companionPosition, rootPosition),
            Vector3.Distance(humanPosition, rootPosition),
            NearbyRadius);
    }

    private static int CompareCandidates(Candidate left, Candidate right)
    {
        if (left.DirectlyObserved != right.DirectlyObserved)
            return left.DirectlyObserved ? -1 : 1;
        var leftDistance = Mathf.Min(
            left.CompanionDistance,
            left.HumanDistance);
        var rightDistance = Mathf.Min(
            right.CompanionDistance,
            right.HumanDistance);
        var distanceOrder = leftDistance.CompareTo(rightDistance);
        return distanceOrder != 0
            ? distanceOrder
            : left.InstanceId.CompareTo(right.InstanceId);
    }
}
