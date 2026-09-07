using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace Ramblers;

internal enum CompanionWalkingConnection
{
    Walkable,
    Uncertain,
    Obstructed
}

internal sealed class CompanionNavigationGeometry
{
    private const int HitCapacity = 32;
    private const float WalkableNormalY = 0.7f;
    private const float CastSkin = 0.06f;
    private const float SupportRise = 0.9f;
    private const float SupportDrop = 1.6f;
    private const int RayContinuationLimit = 4;
    private const float WalkingSampleSpacing = 0.35f;
    private const float WalkingHorizon = 16f;
    private const int WalkingQueryBudget = 256;
    private const float WalkingHeightTolerance = 0.2f;
    private const float HeightComparisonEpsilon = 0.001f;

    private CompanionBody _body;
    private Il2CppStructArray<RaycastHit> _hits;
    private float _nextUncertaintyLogAt;
    private int _uncertainSupportCount;
    private int _saturatedCastCount;
    private int _queryLimit = int.MaxValue;

    internal int NativeQueryCount { get; private set; }
    internal string LastWalkingConnectionReason { get; private set; } = "not_sampled";

    internal void Bind(CompanionBody body)
    {
        _body = body;
        _hits = new Il2CppStructArray<RaycastHit>(HitCapacity);
        _nextUncertaintyLogAt = 0f;
        _uncertainSupportCount = 0;
        _saturatedCastCount = 0;
        NativeQueryCount = 0;
        _queryLimit = int.MaxValue;
        LastWalkingConnectionReason = "not_sampled";
    }

    internal void Release()
    {
        _body = null;
        _hits = null;
        _uncertainSupportCount = 0;
        _saturatedCastCount = 0;
        NativeQueryCount = 0;
        _queryLimit = int.MaxValue;
        LastWalkingConnectionReason = "released";
    }

    internal bool TryGroundPoint(Vector3 candidate, out Vector3 groundedPosition)
    {
        Vector3 supportNormal;
        return TryGroundPoint(candidate, out groundedPosition, out supportNormal);
    }

    private bool TryGroundPoint(
        Vector3 candidate,
        out Vector3 groundedPosition,
        out Vector3 supportNormal)
    {
        groundedPosition = candidate;
        supportNormal = Vector3.up;
        if (_body == null || !_body.IsAlive)
            return false;

        float footOffset;
        float radius;
        float height;
        GetDimensions(out footOffset, out radius, out height);
        var footprint = Mathf.Max(0.12f, radius * 0.75f);
        var bestDifference = float.PositiveInfinity;
        var found = false;
        for (var sample = 0; sample < 5; sample++)
        {
            var offset = Vector3.zero;
            if (sample == 1) offset.x = footprint;
            if (sample == 2) offset.x = -footprint;
            if (sample == 3) offset.z = footprint;
            if (sample == 4) offset.z = -footprint;
            var rayOrigin = candidate + offset +
                            Vector3.up * (SupportRise - footOffset);
            var travelled = 0f;
            for (var layer = 0; layer < RayContinuationLimit; layer++)
            {
                var remaining = SupportRise + SupportDrop - travelled;
                RaycastHit support;
                if (remaining <= 0f || !TryRayHit(
                        rayOrigin + Vector3.down * travelled,
                        Vector3.down,
                        remaining,
                        out support))
                    break;

                if (support.normal.y >= WalkableNormalY)
                {
                    var supportedHeight = support.point.y + footOffset;
                    var heightDifference = Mathf.Abs(supportedHeight - candidate.y);
                    var difference = heightDifference + (sample == 0 ? 0f : 0.025f);
                    if (difference < bestDifference)
                    {
                        bestDifference = difference;
                        groundedPosition.y = supportedHeight;
                        supportNormal = support.normal;
                        found = true;
                    }
                    if (heightDifference <= 0.035f)
                        return true;
                    if (supportedHeight <= candidate.y)
                        break;
                }

                travelled += support.distance + CastSkin;
            }
        }

        if (!found)
        {
            _uncertainSupportCount++;
            ReportUncertainty();
        }
        return found;
    }

    internal bool CanWalkSegment(Vector3 from, Vector3 to)
    {
        return ProbeWalkingConnection(from, to) == CompanionWalkingConnection.Walkable;
    }

    internal CompanionWalkingConnection ProbeWalkingConnection(Vector3 from, Vector3 to)
    {
        if (_body == null || !_body.IsAlive)
            return WalkingResult(CompanionWalkingConnection.Uncertain, "body_unavailable");

        var delta = to - from;
        var distance = delta.magnitude;
        if (distance > WalkingHorizon)
            return WalkingResult(CompanionWalkingConnection.Uncertain, $"beyond_horizon:{distance:F2}");

        var priorLimit = _queryLimit;
        _queryLimit = System.Math.Min(priorLimit,
            NativeQueryCount > int.MaxValue - WalkingQueryBudget
                ? int.MaxValue : NativeQueryCount + WalkingQueryBudget);
        try
        {
            Vector3 previous;
            Vector3 previousNormal;
            var startSupported = TryGroundPoint(from, out previous, out previousNormal);
            if (NativeQueryCount >= _queryLimit)
                return WalkingResult(CompanionWalkingConnection.Uncertain, "query_budget:start");
            if (!startSupported)
                return WalkingResult(CompanionWalkingConnection.Uncertain, "starting_support_missing");
            var startHeightDifference = Mathf.Abs(previous.y - from.y);
            if (startHeightDifference > WalkingHeightTolerance + HeightComparisonEpsilon)
                return WalkingResult(CompanionWalkingConnection.Uncertain, $"start_height:{startHeightDifference:F3}");

            var samples = System.Math.Max(1, (int)System.Math.Ceiling(distance / WalkingSampleSpacing));
            for (var sample = 1; sample <= samples; sample++)
            {
                Vector3 supported;
                Vector3 normal;
                var candidate = from + delta * ((float)sample / samples);
                candidate.y = previous.y -
                    (previousNormal.x * (candidate.x - previous.x) +
                     previousNormal.z * (candidate.z - previous.z)) /
                    Mathf.Max(0.1f, previousNormal.y);
                var sampledSupport = TryGroundPoint(candidate, out supported, out normal);
                if (NativeQueryCount >= _queryLimit)
                    return WalkingResult(CompanionWalkingConnection.Uncertain, $"query_budget:sample={sample}");
                if (!sampledSupport)
                    return WalkingResult(CompanionWalkingConnection.Uncertain, $"sample_support_missing:sample={sample}");

                var walkingDelta = supported - previous;
                var vertical = walkingDelta.y;
                walkingDelta.y = 0f;
                var predictedClimb = (
                    -(previousNormal.x * walkingDelta.x + previousNormal.z * walkingDelta.z) /
                    Mathf.Max(0.1f, previousNormal.y) +
                    -(normal.x * walkingDelta.x + normal.z * walkingDelta.z) /
                    Mathf.Max(0.1f, normal.y)) * 0.5f;
                var discontinuity = Mathf.Abs(vertical - predictedClimb);
                if (discontinuity > WalkingHeightTolerance + HeightComparisonEpsilon)
                    return WalkingResult(CompanionWalkingConnection.Uncertain,
                        $"abrupt_step:sample={sample}:height={vertical:F3}:error={discontinuity:F3}");
                if (!IsSegmentClear(previous, supported))
                    return WalkingResult(CompanionWalkingConnection.Obstructed, $"collision:sample={sample}");
                if (NativeQueryCount >= _queryLimit)
                    return WalkingResult(CompanionWalkingConnection.Uncertain, $"query_budget:sample={sample}");

                previous = supported;
                previousNormal = normal;
            }

            var endHeightDifference = Mathf.Abs(previous.y - to.y);
            return endHeightDifference <= WalkingHeightTolerance + HeightComparisonEpsilon
                ? WalkingResult(CompanionWalkingConnection.Walkable, "connected")
                : WalkingResult(CompanionWalkingConnection.Uncertain, $"end_height:{endHeightDifference:F3}");
        }
        finally
        {
            _queryLimit = priorLimit;
        }
    }

    private CompanionWalkingConnection WalkingResult(CompanionWalkingConnection result, string reason)
    {
        LastWalkingConnectionReason = reason;
        return result;
    }

    internal bool IsSegmentClear(Vector3 from, Vector3 to)
    {
        var delta = to - from;
        var distance = delta.magnitude;
        if (distance < 0.05f)
            return true;

        string description;
        return MeasureClearance(from, delta / distance, distance, false, out description) >=
               distance - 0.06f;
    }

    internal float MeasureClearance(
        Vector3 origin,
        Vector3 direction,
        float distance,
        out string description)
    {
        return MeasureClearance(origin, direction, distance, true, out description);
    }

    private float MeasureClearance(
        Vector3 origin,
        Vector3 direction,
        float distance,
        bool describe,
        out string description)
    {
        description = "clear";
        if (distance <= 0f)
            return 0f;
        if (_body == null || !_body.IsAlive || direction.sqrMagnitude < 0.0001f)
        {
            description = "geometry_unavailable";
            return distance;
        }

        direction.Normalize();
        var bodyCollider = _body.Character.collision?.bodyCollider;
        var closest = distance;
        var ignoredSupport = 0;
        var count = 0;
        if (bodyCollider != null && _hits != null && NativeQueryCount < _queryLimit)
        {
            NativeQueryCount++;
            count = PlayerGround.ColliderCastNonAlloc(
                bodyCollider,
                origin - _body.Position + Vector3.up * CastSkin,
                direction,
                _hits,
                distance,
                CompanionLocomotion.GetObstacleMask(_body.Character),
                QueryTriggerInteraction.Ignore);
            if (count >= HitCapacity)
            {
                _saturatedCastCount++;
                ReportUncertainty();
            }
            count = Mathf.Min(count, HitCapacity);
            for (var index = 0; index < count; index++)
            {
                var hit = _hits[index];
                if (IsSelf(hit))
                    continue;
                if (hit.normal.y >= WalkableNormalY)
                {
                    ignoredSupport++;
                    continue;
                }
                if (hit.distance >= closest || hit.collider == null)
                    continue;

                closest = Mathf.Max(0f, hit.distance);
                description = describe ? DescribeHit(hit) : "blocked";
            }
        }

        float footOffset;
        float radius;
        float height;
        GetDimensions(out footOffset, out radius, out height);
        for (var sample = 0; sample < 2; sample++)
        {
            var rayHeight = sample == 0
                ? Mathf.Max(radius + 0.1f, height * 0.3f)
                : height * 0.7f;
            var rayOrigin = origin + Vector3.up * (rayHeight - footOffset);
            var travelled = 0f;
            for (var continuation = 0; continuation < RayContinuationLimit; continuation++)
            {
                RaycastHit hit;
                if (!TryRayHit(rayOrigin, direction, distance - travelled, out hit))
                    break;
                if (hit.normal.y < WalkableNormalY)
                {
                    var contactDistance = Mathf.Max(0f, travelled + hit.distance - radius);
                    if (contactDistance < closest)
                    {
                        closest = contactDistance;
                        description = describe ? "ray:" + DescribeHit(hit) : "blocked";
                    }
                    break;
                }

                var advance = hit.distance + CastSkin;
                travelled += advance;
                if (travelled >= distance)
                    break;
                rayOrigin += direction * advance;
            }
        }

        if (describe && closest >= distance && ignoredSupport > 0)
            description = $"clear_after_support:hits={count}:support={ignoredSupport}";
        if (describe && count >= HitCapacity)
            description += ":saturated";
        return closest;
    }

    private bool TryRayHit(
        Vector3 origin,
        Vector3 direction,
        float distance,
        out RaycastHit result)
    {
        result = default(RaycastHit);
        var travelled = 0f;
        for (var attempt = 0; attempt < RayContinuationLimit && travelled < distance; attempt++)
        {
            RaycastHit hit;
            if (NativeQueryCount >= _queryLimit)
                return false;
            NativeQueryCount++;
            if (!Physics.Raycast(
                    origin + direction * travelled,
                    direction,
                    out hit,
                    distance - travelled,
                    CompanionLocomotion.GetObstacleMask(_body.Character),
                    QueryTriggerInteraction.Ignore))
                return false;
            if (!IsSelf(hit))
            {
                hit.distance += travelled;
                result = hit;
                return true;
            }
            travelled += hit.distance + CastSkin;
        }
        return false;
    }

    private bool IsSelf(RaycastHit hit)
    {
        return hit.collider != null && _body.Contains(hit.collider.transform);
    }

    private void GetDimensions(out float footOffset, out float radius, out float height)
    {
        var collider = _body.Character.collision?.bodyCollider;
        if (collider == null)
        {
            footOffset = 0f;
            radius = 0.25f;
            height = 1.5f;
            return;
        }

        var bounds = collider.bounds;
        footOffset = _body.Position.y - bounds.min.y;
        radius = Mathf.Max(0.1f, Mathf.Max(bounds.extents.x, bounds.extents.z));
        height = Mathf.Max(radius * 2f, bounds.size.y);
    }

    private static string DescribeHit(RaycastHit hit)
    {
        var collider = hit.collider;
        if (collider == null)
            return "unknown_collider";
        var hitTransform = collider.transform;
        var name = hitTransform == null ? "unnamed" : hitTransform.name;
        name = name.Replace(',', '_').Replace(';', '_').Replace(' ', '_');
        return $"{name}@layer{collider.gameObject.layer}:normal={hit.normal}";
    }

    private void ReportUncertainty()
    {
        var now = Time.time;
        if (now < _nextUncertaintyLogAt)
            return;
        _nextUncertaintyLogAt = now + 5f;
        Plugin.Logger.LogInfo(
            "[FOLLOW] GEOMETRY_UNCERTAIN " +
            $"supportSamples={_uncertainSupportCount}, saturatedCasts={_saturatedCastCount}; " +
            "uncertain terrain remains traversable under native physics.");
        _uncertainSupportCount = 0;
        _saturatedCastCount = 0;
    }
}
