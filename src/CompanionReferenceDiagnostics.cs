using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ramblers;

internal static class CompanionReferenceDiagnostics
{
    internal static void LogGaze(
        PlayerCharacter human,
        CompanionBody body,
        string source,
        GameObject hitObject,
        CastableTarget target,
        string error)
    {
        try
        {
            var view = CompanionAffordanceTarget.ResolveHumanViewTransform(human);
            var caster = human == null ? null : human.caster;
            if (source == "stock_castable" && target != null)
                hitObject = target.gameObject;
            Plugin.Logger.LogInfo(
                $"[REFERENCE] GAZE_CAPTURE source={source}, " +
                $"at={Time.realtimeSinceStartup:F3}, " +
                $"view={PathOf(view)}, " +
                $"viewOrigin={(view == null ? Vector3.zero : view.position)}, " +
                $"viewForward={(view == null ? Vector3.zero : view.forward)}, " +
                $"extendedMaxDistanceM={CompanionPropTarget.MaximumHumanReferenceDistance:F1}, " +
                $"selection=single_ray, " +
                $"layerMask={(caster == null ? 0 : caster.layerMask.value)}, " +
                $"stockReachM={(caster == null ? -1f : caster.raycastMaxDistance):F3}, " +
                $"stockUpReachM={(caster == null ? -1f : caster.raycastMaxDistanceUp):F3}, " +
                $"hitObject={PathOf(hitObject == null ? null : hitObject.transform)}, " +
                $"hitLayer={(hitObject == null ? -1 : hitObject.layer)}, " +
                $"castableInstanceId={(target == null ? 0 : target.GetInstanceID())}, " +
                $"error={error ?? "none"}.");
            if (target == null || view == null)
                return;

            var crosshair = target.GetCrosshairTransform();
            var point = crosshair == null ? target.transform.position : crosshair.position;
            Plugin.Logger.LogInfo(
                $"[REFERENCE] GAZE_TARGET castableInstanceId={target.GetInstanceID()}, " +
                $"path={PathOf(target.transform)}, point={point}, " +
                Geometry(view, point) +
                $", companionDistanceM={(body == null ? -1f : Vector3.Distance(body.HeadPosition, point)):F3}.");
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning(
                $"[REFERENCE] DIAGNOSTIC_FAILED stage=gaze, error={exception.GetType().Name}.");
        }
    }

    internal static void LogBinding(
        CastableTarget identity,
        CompanionAffordanceTarget target,
        string error)
    {
        try
        {
            Plugin.Logger.LogInfo(
                $"[REFERENCE] GAZE_BINDING identityCaptured={identity != null}, " +
                $"castableInstanceId={(identity == null ? 0 : identity.GetInstanceID())}, " +
                $"bound={target != null}, referenceId={target?.ReferenceId ?? "none"}, " +
                $"kind={target?.KindLabel ?? "none"}, error={error ?? "none"}.");
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning(
                $"[REFERENCE] DIAGNOSTIC_FAILED stage=binding, error={exception.GetType().Name}.");
        }
    }

    internal static void LogCandidates(
        PlayerCharacter human,
        CompanionAffordanceCandidates candidates,
        CompanionInteractableObservation[] nearby,
        CompanionInteractableObservation[] recent)
    {
        try
        {
            var view = CompanionAffordanceTarget.ResolveHumanViewTransform(human);
            if (view == null)
                return;
            var selectedInstanceId = 0;
            CastableTarget selected;
            if (candidates != null)
                candidates.TryGetExactHumanWorldReference(out selected, out selectedInstanceId);
            Plugin.Logger.LogInfo(
                $"[REFERENCE] CONTEXT_CANDIDATES nearby={nearby.Length}, recent={recent.Length}, " +
                $"nearbyRadiusM={CompanionInteractableDiscovery.NearbyRadius:F1}, " +
                "selectionUnchanged=True.");
            LogCandidates(view, nearby, "nearby", selectedInstanceId);
            LogCandidates(view, recent, "recent", selectedInstanceId);
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning(
                $"[REFERENCE] DIAGNOSTIC_FAILED stage=candidates, error={exception.GetType().Name}.");
        }
    }

    private static void LogCandidates(
        Transform view,
        CompanionInteractableObservation[] observations,
        string source,
        int selectedInstanceId)
    {
        foreach (var observation in observations)
        {
            var reference = observation.Reference;
            Plugin.Logger.LogInfo(
                $"[REFERENCE] CONTEXT_CANDIDATE source={source}, id={reference.StableId}, " +
                $"castableInstanceId={reference.CastableInstanceId}, name={reference.Name}, " +
                $"kind={reference.Kind}, gazeSelected={reference.CastableInstanceId == selectedInstanceId}, " +
                $"ageSeconds={Mathf.Max(0f, Time.realtimeSinceStartup - observation.SeenAt):F3}, " +
                $"point={observation.Point}, " + Geometry(view, observation.Point) + ".");
        }
    }

    private static string Geometry(Transform view, Vector3 point)
    {
        var offset = point - view.position;
        var forward = view.forward.normalized;
        var alongRay = Vector3.Dot(offset, forward);
        var offRay = (offset - forward * alongRay).magnitude;
        return $"referencePointDistanceM={offset.magnitude:F3}, " +
               $"referencePointAngleDegrees={Vector3.Angle(forward, offset):F3}, " +
               $"alongRayM={alongRay:F3}, offRayM={offRay:F3}";
    }

    private static string PathOf(Transform transform)
    {
        var names = new List<string>();
        for (var depth = 0; transform != null && depth < 5; depth++, transform = transform.parent)
            names.Add(transform.name.Replace('\r', ' ').Replace('\n', ' '));
        names.Reverse();
        return names.Count == 0 ? "none" : string.Join("/", names);
    }
}
