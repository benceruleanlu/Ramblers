namespace Ramblers;

/// <summary>
/// The semantic intent for a native primary interaction. Most affordances use
/// their primary action; a sittable PlayerPose needs an explicit sit intent so
/// capability data is not mistaken for what the human asked the companion to do.
/// </summary>
internal enum CompanionInteractionIntent
{
    Use,
    Sit
}

internal enum CompanionAffordanceReadinessState
{
    Ready,
    NeedsApproach,
    Unavailable
}

/// <summary>
/// Pure transaction rules shared by every switch-like affordance. Keeping these
/// rules independent of Unity makes same-state receipts and release timing
/// executable protocol tests rather than source-shape assumptions.
/// </summary>
internal static class CompanionAffordanceProtocol
{
    internal const float SwitchReleaseDelaySeconds = 0.12f;

    internal static int NextActionNumber(int previousActionNumber)
    {
        return unchecked(previousActionNumber + 1);
    }

    internal static int NextDistinctActionNumber(
        int previousPlayerActionNumber,
        int previousTrackedActionNumber)
    {
        var next = NextActionNumber(previousPlayerActionNumber);
        return next == previousTrackedActionNumber
            ? NextActionNumber(next)
            : next;
    }

    internal static bool MatchesSwitchReceipt(
        bool hasCurrentContext,
        bool identityMatches,
        int currentState,
        int currentActionNumber,
        int expectedState,
        int expectedActionNumber,
        int previousState,
        int previousActionNumber)
    {
        return hasCurrentContext && identityMatches &&
               currentState == expectedState &&
               currentActionNumber == expectedActionNumber &&
               (expectedState != previousState ||
                expectedActionNumber != previousActionNumber);
    }

    internal static bool IsReleaseDue(
        bool downDispatched,
        bool hasRelease,
        float secondsSinceDownDispatched)
    {
        return downDispatched && hasRelease &&
               secondsSinceDownDispatched >= SwitchReleaseDelaySeconds;
    }

    internal static bool IsMomentaryComplete(
        bool downConfirmed,
        bool releaseConfirmed)
    {
        return downConfirmed && releaseConfirmed;
    }

    internal static bool IsIgnoredNativeRepeat(
        bool ignoreStateRepeats,
        bool hasPreviousContext,
        int previousState,
        int expectedState)
    {
        return ignoreStateRepeats && hasPreviousContext &&
               expectedState == previousState;
    }

    /// <summary>
    /// TrackedPeckState intentionally leaves currentPeckContext unchanged when
    /// a same-state action is retriggered. With ignoreStateRepeats disabled the
    /// authoritative dispatch calls PeckManager.ServerRetrigger, so a missing
    /// action-number mutation is the native receipt rather than a timeout.
    /// </summary>
    internal static bool IsNativeRetrigger(
        bool ignoreStateRepeats,
        bool hasPreviousContext,
        int previousState,
        int expectedState)
    {
        return !ignoreStateRepeats && hasPreviousContext &&
               expectedState == previousState;
    }

    /// <summary>
    /// The only CastableOutcome shape whose normal admission may be deferred
    /// while the companion is physically carried. Key, pocket-item, placement,
    /// and pose conditions remain exclusively game-owned and are never bypassed.
    /// </summary>
    internal static bool IsUnconditionedWorldSwitchOutcome(
        bool hasSwitch,
        bool hasPlayerPose,
        bool hasPropHome,
        bool needsKey,
        bool needsPocketProp)
    {
        return hasSwitch && !hasPlayerPose && !hasPropHome &&
               !needsKey && !needsPocketProp;
    }

    /// <summary>
    /// Ramblers currently binds keys only to the switch primitive that owns
    /// the stock held-key command. Pocket-item prerequisites have no exact
    /// companion-side binding yet. Apply this once at the central outcome
    /// discriminator so near dynamic capture and far structural capture cannot
    /// disagree about the same native outcome.
    /// </summary>
    internal static bool HasSupportedPrerequisites(
        bool needsKey,
        bool needsPocketProp,
        bool selectedPrimitiveIsSwitch)
    {
        return !needsPocketProp &&
               (!needsKey || selectedPrimitiveIsSwitch);
    }

    internal static bool CanSelectCarriedSwitchFallback(
        bool companionIsCarriedByHuman,
        int structuralOutcomeCount,
        int eligibleOutcomeCount)
    {
        return companionIsCarriedByHuman &&
               structuralOutcomeCount == 1 &&
               eligibleOutcomeCount == 1;
    }

    internal static bool IsGroundedCarriedSwitchSource(
        bool isHumanReference,
        bool isContextEntity)
    {
        return isHumanReference || isContextEntity;
    }

    internal static bool CanRetainCarriedSwitchFallback(
        bool companionIsCarriedByHuman,
        bool outcomeScanCompleted,
        int structuralOutcomeCount,
        bool exactSwitchRetained)
    {
        return companionIsCarriedByHuman && outcomeScanCompleted &&
               structuralOutcomeCount == 1 && exactSwitchRetained;
    }

    internal static bool CanSelectStructuralWorldAffordance(
        bool outcomeScanCompleted,
        int constructibleDriverCount)
    {
        return outcomeScanCompleted &&
               constructibleDriverCount == 1;
    }

    /// <summary>
    /// Reach is intentionally classified before dynamic CastableOutcome
    /// availability. An exact far target may approach only while the companion
    /// owns its locomotion; a human-carried companion must remain in place.
    /// Ready here means the reach phase passed. Callers still validate the
    /// dynamic native outcome and its preconditions before activation.
    /// </summary>
    internal static CompanionAffordanceReadinessState ClassifyWorldReadiness(
        bool exactIdentityAvailable,
        bool pointAvailable,
        bool withinNativeReach,
        bool canSelfApproach)
    {
        if (!exactIdentityAvailable || !pointAvailable)
            return CompanionAffordanceReadinessState.Unavailable;
        if (!withinNativeReach)
        {
            return canSelfApproach
                ? CompanionAffordanceReadinessState.NeedsApproach
                : CompanionAffordanceReadinessState.Unavailable;
        }
        return CompanionAffordanceReadinessState.Ready;
    }
}
