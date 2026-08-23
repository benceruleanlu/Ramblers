using System;
using Ramblers;

internal static class CompanionAffordanceProtocolProbe
{
    private static int Main()
    {
        Assert(CompanionAffordanceProtocol.NextActionNumber(-1) == 0,
            "first action number");
        Assert(CompanionAffordanceProtocol.NextActionNumber(41) == 42,
            "monotonic action number");
        Assert(CompanionAffordanceProtocol.NextActionNumber(int.MaxValue) == int.MinValue,
            "stock unchecked counter wrap");
        Assert(CompanionAffordanceProtocol.NextDistinctActionNumber(8, 4) == 9,
            "ordinary player counter increment");
        Assert(CompanionAffordanceProtocol.NextDistinctActionNumber(8, 9) == 10,
            "receipt counter avoids the tracked context");
        Assert(CompanionAffordanceProtocol.NextDistinctActionNumber(
                int.MaxValue,
                int.MinValue) == int.MinValue + 1,
            "receipt counter stays distinct across wrap");

        Assert(!CompanionAffordanceProtocol.MatchesSwitchReceipt(
                false, true, 1, -1, 1, 0, 0, -1),
            "missing context cannot confirm");
        Assert(!CompanionAffordanceProtocol.MatchesSwitchReceipt(
                true, true, 1, 8, 1, 9, 1, 8),
            "same state with stale action cannot confirm");
        Assert(CompanionAffordanceProtocol.MatchesSwitchReceipt(
                true, true, 1, 9, 1, 9, 1, 8),
            "same-state action confirms by exact receipt");
        Assert(!CompanionAffordanceProtocol.MatchesSwitchReceipt(
                true, true, 2, 9, 1, 9, 1, 8),
            "wrong state cannot confirm");
        Assert(!CompanionAffordanceProtocol.MatchesSwitchReceipt(
                true, true, 1, 9, 1, 9, 1, 9),
            "unchanged state and action are not fresh evidence");
        Assert(CompanionAffordanceProtocol.MatchesSwitchReceipt(
                true, true, 2, 9, 2, 9, 1, 9),
            "changed state is fresh evidence even when counters coincide");
        Assert(!CompanionAffordanceProtocol.MatchesSwitchReceipt(
                true, false, 1, 9, 1, 9, 1, 8),
            "another actor's matching counter cannot confirm");

        Assert(!CompanionAffordanceProtocol.IsReleaseDue(
                false, true, 1f),
            "release waits for down dispatch");
        Assert(!CompanionAffordanceProtocol.IsReleaseDue(
                true, false, 1f),
            "one-shot action has no synthetic release");
        Assert(!CompanionAffordanceProtocol.IsReleaseDue(
                true,
                true,
                CompanionAffordanceProtocol.SwitchReleaseDelaySeconds - 0.001f),
            "release respects pulse duration");
        Assert(CompanionAffordanceProtocol.IsReleaseDue(
                true,
                true,
                CompanionAffordanceProtocol.SwitchReleaseDelaySeconds),
            "release becomes due at pulse boundary");
        Assert(!CompanionAffordanceProtocol.IsMomentaryComplete(false, true),
            "release cannot hide a rejected down action");
        Assert(!CompanionAffordanceProtocol.IsMomentaryComplete(true, false),
            "down confirmation cannot hide a rejected release");
        Assert(CompanionAffordanceProtocol.IsMomentaryComplete(true, true),
            "momentary action completes after both phases confirm");
        Assert(!CompanionAffordanceProtocol.IsIgnoredNativeRepeat(
                true, false, 1, 1),
            "first fire at the initial state is not an ignored repeat");
        Assert(CompanionAffordanceProtocol.IsIgnoredNativeRepeat(
                true, true, 1, 1),
            "already-fired equal state is an ignored native repeat");
        Assert(!CompanionAffordanceProtocol.IsIgnoredNativeRepeat(
                false, true, 1, 1),
            "repeat-enabled systems are retriggered rather than ignored");
        Assert(!CompanionAffordanceProtocol.IsIgnoredNativeRepeat(
                true, true, 1, 2),
            "a state transition still requires a receipt");
        Assert(CompanionAffordanceProtocol.IsNativeRetrigger(
                false, true, 1, 1),
            "repeat-enabled same-state dispatch uses native retrigger semantics");
        Assert(!CompanionAffordanceProtocol.IsNativeRetrigger(
                true, true, 1, 1),
            "ignored same-state dispatch is not a retrigger");
        Assert(!CompanionAffordanceProtocol.IsNativeRetrigger(
                false, false, 1, 1),
            "first dispatch cannot be classified as a retrigger");
        Assert(!CompanionAffordanceProtocol.IsNativeRetrigger(
                false, true, 1, 2),
            "a state transition still requires an exact receipt");

        Assert(CompanionAffordanceProtocol.IsUnconditionedWorldSwitchOutcome(
                true, false, false, false, false),
            "plain world switch is eligible for carried capture");
        Assert(!CompanionAffordanceProtocol.IsUnconditionedWorldSwitchOutcome(
                true, true, false, false, false),
            "pose outcomes are never bypassed");
        Assert(!CompanionAffordanceProtocol.IsUnconditionedWorldSwitchOutcome(
                true, false, true, false, false),
            "placement outcomes are never bypassed");
        Assert(!CompanionAffordanceProtocol.IsUnconditionedWorldSwitchOutcome(
                true, false, false, true, false),
            "key requirements are never bypassed");
        Assert(!CompanionAffordanceProtocol.IsUnconditionedWorldSwitchOutcome(
                true, false, false, false, true),
            "pocket-prop requirements are never bypassed");
        Assert(CompanionAffordanceProtocol.HasSupportedPrerequisites(
                false, false, false),
            "pose and home primitives accept an unconditioned outcome");
        Assert(CompanionAffordanceProtocol.HasSupportedPrerequisites(
                true, false, true),
            "the switch primitive owns exact held-key support");
        Assert(!CompanionAffordanceProtocol.HasSupportedPrerequisites(
                true, false, false),
            "pose and home primitives cannot silently ignore a key");
        Assert(!CompanionAffordanceProtocol.HasSupportedPrerequisites(
                false, true, true),
            "world switches cannot silently ignore a pocket item");
        Assert(!CompanionAffordanceProtocol.HasSupportedPrerequisites(
                true, true, true),
            "key support cannot hide an unsupported pocket prerequisite");
        Assert(CompanionAffordanceProtocol.CanSelectCarriedSwitchFallback(
                true, 1, 1),
            "one exact carried switch is selectable");
        Assert(!CompanionAffordanceProtocol.CanSelectCarriedSwitchFallback(
                false, 1, 1),
            "ordinary interactions retain native outcome admission");
        Assert(!CompanionAffordanceProtocol.CanSelectCarriedSwitchFallback(
                true, 2, 1),
            "multiple structural switches remain ambiguous even when only one is constructible");
        Assert(!CompanionAffordanceProtocol.CanSelectCarriedSwitchFallback(
                true, 1, 0),
            "an unconstructible carried switch is not selectable");
        Assert(CompanionAffordanceProtocol.IsGroundedCarriedSwitchSource(
                true, false),
            "an exact direct human reference must support carried switch use");
        Assert(CompanionAffordanceProtocol.IsGroundedCarriedSwitchSource(
                false, true),
            "an exact turn-bound context ID must support carried switch use");
        Assert(!CompanionAffordanceProtocol.IsGroundedCarriedSwitchSource(
                false, false),
            "an ungrounded or held-item source must not enter carried switch use");
        Assert(CompanionAffordanceProtocol.CanRetainCarriedSwitchFallback(
                true, true, 1, true),
            "the same unique carried switch survives commit revalidation");
        Assert(!CompanionAffordanceProtocol.CanRetainCarriedSwitchFallback(
                false, true, 1, true),
            "carrier loss invalidates the fallback");
        Assert(!CompanionAffordanceProtocol.CanRetainCarriedSwitchFallback(
                true, false, 1, true),
            "an interrupted or failed outcome scan cannot retain the fallback");
        Assert(!CompanionAffordanceProtocol.CanRetainCarriedSwitchFallback(
                true, true, 2, true),
            "a replacement structural switch makes revalidation ambiguous");
        Assert(!CompanionAffordanceProtocol.CanRetainCarriedSwitchFallback(
                true, true, 1, false),
            "raw outcome replacement cannot silently substitute another switch");
        Assert(CompanionAffordanceProtocol.CanSelectStructuralWorldAffordance(
                true, 1),
            "one exact far world-switch driver is structurally selectable");
        Assert(CompanionAffordanceProtocol.CanSelectStructuralWorldAffordance(
                true, 1),
            "one exact far pose driver is structurally selectable");
        Assert(CompanionAffordanceProtocol.CanSelectStructuralWorldAffordance(
                true, 1),
            "one exact far prop-home driver with a bound held prop is structurally selectable");
        Assert(CompanionAffordanceProtocol.CanSelectStructuralWorldAffordance(
                true, 1),
            "one exact keyed switch with a bound held key is structurally selectable");
        Assert(!CompanionAffordanceProtocol.CanSelectStructuralWorldAffordance(
                true, 2),
            "multiple constructible drivers remain ambiguous");
        Assert(CompanionAffordanceProtocol.CanSelectStructuralWorldAffordance(
                true, 1),
            "unconstructible alternatives remain telemetry while the exact bound driver is unique");
        Assert(!CompanionAffordanceProtocol.CanSelectStructuralWorldAffordance(
                false, 1),
            "an IL2CPP outcome scan must complete before structural capture");
        Assert(
            CompanionAffordanceProtocol.ClassifyWorldReadiness(
                true,
                true,
                false,
                true) == CompanionAffordanceReadinessState.NeedsApproach,
            "a structurally captured far target approaches before dynamic outcome admission");
        Assert(
            CompanionAffordanceProtocol.ClassifyWorldReadiness(
                true,
                true,
                false,
                false) == CompanionAffordanceReadinessState.Unavailable,
            "a carried far target never turns into a locomoting interaction");
        Assert(
            CompanionAffordanceProtocol.ClassifyWorldReadiness(
                true,
                true,
                true,
                true) == CompanionAffordanceReadinessState.Ready,
            "a reachable target proceeds to dynamic native validation");

        Console.WriteLine("Companion affordance protocol probe passed.");
        return 0;
    }

    private static void Assert(bool condition, string description)
    {
        if (!condition)
            throw new InvalidOperationException("Failed: " + description);
    }
}
