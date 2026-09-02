using System.Collections.Generic;

namespace Ramblers;

internal enum CompanionAwarenessInjectionTrigger
{
    None,
    SalientEvent,
    EntityEnteredAwareness,
    Heartbeat
}

internal static class CompanionAwarenessInjectionPolicy
{
    internal const float MinimumIntervalSeconds = 4f;
    internal const int MaximumPacketsPerMinute = 8;
    internal const float RateWindowSeconds = 60f;
    internal const float HeartbeatIntervalSeconds = 30f;
    internal const float ScanIntervalSeconds = 5f;
    internal const float EntityResendSeconds = 45f;
    internal const float HumanDistanceBucketMetres = 3f;

    internal static CompanionAwarenessInjectionTrigger SelectTrigger(
        bool salientEventPending,
        bool entityEntered,
        bool stateChanged,
        bool undeliveredEvents,
        float secondsSinceLastPacket)
    {
        if (salientEventPending)
            return CompanionAwarenessInjectionTrigger.SalientEvent;
        if (entityEntered)
            return CompanionAwarenessInjectionTrigger.EntityEnteredAwareness;
        if (secondsSinceLastPacket >= HeartbeatIntervalSeconds &&
            (stateChanged || undeliveredEvents))
        {
            return CompanionAwarenessInjectionTrigger.Heartbeat;
        }
        return CompanionAwarenessInjectionTrigger.None;
    }

    internal static bool IsRateCapped(
        float now,
        float lastPacketAt,
        int packetsInWindow,
        out string reason)
    {
        reason = null;
        if (lastPacketAt >= 0f && now - lastPacketAt < MinimumIntervalSeconds)
        {
            reason = "minimum_interval";
            return true;
        }
        if (packetsInWindow >= MaximumPacketsPerMinute)
        {
            reason = "per_minute_cap";
            return true;
        }
        return false;
    }

    internal static int CountInWindow(Queue<float> sentAt, float now)
    {
        if (sentAt == null)
            return 0;
        while (sentAt.Count > 0 && now - sentAt.Peek() >= RateWindowSeconds)
            sentAt.Dequeue();
        return sentAt.Count;
    }

    internal static bool ShouldResendEntity(
        bool sentBefore,
        float lastSentAt,
        float now)
    {
        return !sentBefore || now - lastSentAt >= EntityResendSeconds;
    }

    internal static int HumanDistanceBucket(float distanceMetres)
    {
        if (distanceMetres <= 0f)
            return 0;
        return (int)(distanceMetres / HumanDistanceBucketMetres);
    }

    internal static float NextScanAfterSalientEvent(
        float now,
        float lastPacketAt,
        float scheduledScanAt)
    {
        var earliest = lastPacketAt < 0f
            ? now
            : lastPacketAt + MinimumIntervalSeconds;
        if (earliest < now)
            earliest = now;
        return earliest < scheduledScanAt ? earliest : scheduledScanAt;
    }

    internal static string TriggerLabel(
        CompanionAwarenessInjectionTrigger trigger)
    {
        switch (trigger)
        {
            case CompanionAwarenessInjectionTrigger.SalientEvent:
                return "salient_event";
            case CompanionAwarenessInjectionTrigger.EntityEnteredAwareness:
                return "entity_entered_awareness";
            case CompanionAwarenessInjectionTrigger.Heartbeat:
                return "heartbeat";
            default:
                return "none";
        }
    }
}
