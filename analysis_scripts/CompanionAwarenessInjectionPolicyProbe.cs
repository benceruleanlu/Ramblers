using System;
using System.Collections.Generic;
using Ramblers;

internal static class CompanionAwarenessInjectionPolicyProbe
{
    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static int Main()
    {
        Require(
            CompanionAwarenessInjectionPolicy.SelectTrigger(
                true, false, false, false, 0f) ==
            CompanionAwarenessInjectionTrigger.SalientEvent,
            "a salient event did not trigger a packet");
        Require(
            CompanionAwarenessInjectionPolicy.SelectTrigger(
                false, true, false, false, 0f) ==
            CompanionAwarenessInjectionTrigger.EntityEnteredAwareness,
            "a new entity did not trigger a packet");
        Require(
            CompanionAwarenessInjectionPolicy.SelectTrigger(
                false, false, true, false, 29.9f) ==
            CompanionAwarenessInjectionTrigger.None,
            "a heartbeat fired before its interval");
        Require(
            CompanionAwarenessInjectionPolicy.SelectTrigger(
                false, false, true, false, 30f) ==
            CompanionAwarenessInjectionTrigger.Heartbeat,
            "a changed state did not ride the heartbeat");
        Require(
            CompanionAwarenessInjectionPolicy.SelectTrigger(
                false, false, false, true, 30f) ==
            CompanionAwarenessInjectionTrigger.Heartbeat,
            "undelivered events did not ride the heartbeat");
        Require(
            CompanionAwarenessInjectionPolicy.SelectTrigger(
                false, false, false, false, 600f) ==
            CompanionAwarenessInjectionTrigger.None,
            "an empty delta produced a heartbeat");
        Require(
            CompanionAwarenessInjectionPolicy.SelectTrigger(
                true, true, true, true, 0f) ==
            CompanionAwarenessInjectionTrigger.SalientEvent,
            "a salient event did not take precedence");

        string reason;
        Require(
            !CompanionAwarenessInjectionPolicy.IsRateCapped(
                10f, -1f, 0, out reason),
            "the first packet was rate capped");
        Require(
            CompanionAwarenessInjectionPolicy.IsRateCapped(
                13.9f, 10f, 0, out reason) &&
            reason == "minimum_interval",
            "a packet inside the minimum interval was admitted");
        Require(
            !CompanionAwarenessInjectionPolicy.IsRateCapped(
                14f, 10f, 0, out reason),
            "the minimum interval did not release on its boundary");
        Require(
            CompanionAwarenessInjectionPolicy.IsRateCapped(
                100f, 10f, 8, out reason) &&
            reason == "per_minute_cap",
            "the per-minute cap did not hold at its bound");
        Require(
            !CompanionAwarenessInjectionPolicy.IsRateCapped(
                100f, 10f, 7, out reason),
            "the per-minute cap fired below its bound");
        Require(
            CompanionAwarenessInjectionPolicy.IsRateCapped(
                12f, 10f, 8, out reason) &&
            reason == "minimum_interval",
            "the minimum interval did not take precedence over the cap");

        var window = new Queue<float>();
        for (var index = 0; index < 8; index++)
            window.Enqueue(index * 5f);
        Require(
            CompanionAwarenessInjectionPolicy.CountInWindow(window, 36f) == 8,
            "in-window packets were dropped");
        Require(
            CompanionAwarenessInjectionPolicy.CountInWindow(window, 60f) == 7,
            "a sixty-second-old packet stayed in the window");
        Require(
            CompanionAwarenessInjectionPolicy.CountInWindow(window, 200f) == 0,
            "stale packets stayed in the window");
        Require(
            CompanionAwarenessInjectionPolicy.CountInWindow(null, 0f) == 0,
            "a missing window was not empty");

        Require(
            CompanionAwarenessInjectionPolicy.ShouldResendEntity(false, 0f, 0f),
            "an unsent entity was suppressed");
        Require(
            !CompanionAwarenessInjectionPolicy.ShouldResendEntity(
                true, 10f, 54.9f),
            "a recently sent entity was resent");
        Require(
            CompanionAwarenessInjectionPolicy.ShouldResendEntity(
                true, 10f, 55f),
            "an expired entity was not resent");

        Require(
            CompanionAwarenessInjectionPolicy.HumanDistanceBucket(-1f) == 0 &&
            CompanionAwarenessInjectionPolicy.HumanDistanceBucket(2.9f) == 0 &&
            CompanionAwarenessInjectionPolicy.HumanDistanceBucket(3f) == 1 &&
            CompanionAwarenessInjectionPolicy.HumanDistanceBucket(12.5f) == 4,
            "human distance buckets are not three metres wide");

        Require(
            CompanionAwarenessInjectionPolicy.NextScanAfterSalientEvent(
                20f, -1f, 25f) == 20f,
            "the first salient event did not scan immediately");
        Require(
            CompanionAwarenessInjectionPolicy.NextScanAfterSalientEvent(
                20f, 18f, 25f) == 22f,
            "a salient event scan ignored the minimum interval");
        Require(
            CompanionAwarenessInjectionPolicy.NextScanAfterSalientEvent(
                20f, 10f, 25f) == 20f,
            "a salient event scan was delayed past now");
        Require(
            CompanionAwarenessInjectionPolicy.NextScanAfterSalientEvent(
                20f, 18f, 21f) == 21f,
            "a salient event pushed a sooner scheduled scan later");

        Require(
            CompanionAwarenessInjectionPolicy.TriggerLabel(
                CompanionAwarenessInjectionTrigger.SalientEvent) ==
            "salient_event" &&
            CompanionAwarenessInjectionPolicy.TriggerLabel(
                CompanionAwarenessInjectionTrigger.EntityEnteredAwareness) ==
            "entity_entered_awareness" &&
            CompanionAwarenessInjectionPolicy.TriggerLabel(
                CompanionAwarenessInjectionTrigger.Heartbeat) ==
            "heartbeat" &&
            CompanionAwarenessInjectionPolicy.TriggerLabel(
                CompanionAwarenessInjectionTrigger.None) == "none",
            "trigger labels do not match the runtime log vocabulary");

        Console.WriteLine("Companion awareness injection policy probe passed.");
        return 0;
    }
}
