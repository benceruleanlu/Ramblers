namespace Ramblers;

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

    internal static bool IsNativeRetrigger(
        bool ignoreStateRepeats,
        bool hasPreviousContext,
        int previousState,
        int expectedState)
    {
        return !ignoreStateRepeats && hasPreviousContext &&
               expectedState == previousState;
    }

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
