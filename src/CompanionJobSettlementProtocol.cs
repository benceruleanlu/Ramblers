namespace Ramblers;

/// <summary>
/// Pure lifecycle rules shared by the Unity coordinator and executable probes.
/// A result is model-visible only after the job has released its capabilities,
/// unless that job explicitly publishes a successful hold for synchronous
/// conclusion (pickup/carry) or retained presentation (inspection).
/// </summary>
internal static class CompanionJobSettlementProtocol
{
    internal static bool CanPublishCompletion(
        bool isActive,
        bool holdsResources,
        bool mayPublishWhileActive)
    {
        return mayPublishWhileActive || (!isActive && !holdsResources);
    }

    internal static bool IsSettled(bool isActive, bool holdsResources)
    {
        return !isActive && !holdsResources;
    }

    internal static bool CanReleaseLeaseAfterConclude(bool jobIsSettled)
    {
        return jobIsSettled;
    }

    /// <summary>
    /// Revalidates the native hands fact represented by a completion at the
    /// instant it becomes model-visible. This closes the Update/LateUpdate
    /// window between behavior confirmation and bridge consumption.
    /// </summary>
    internal static bool IsCompletionTransitionCurrent(
        CompanionTurnHandsTransition transition,
        bool exactPropHeld,
        bool exactPlayerHeld,
        bool handsEmpty)
    {
        switch (transition)
        {
            case CompanionTurnHandsTransition.None:
                return true;
            case CompanionTurnHandsTransition.HoldingExactProp:
                return exactPropHeld;
            case CompanionTurnHandsTransition.HandsEmpty:
                return handsEmpty;
            case CompanionTurnHandsTransition.HoldingExactPlayer:
                return exactPlayerHeld;
            default:
                return false;
        }
    }

    internal static bool HasCancellationSettlementTimedOut(
        bool cancellationRequested,
        float elapsedSeconds,
        float maximumSeconds)
    {
        return cancellationRequested && maximumSeconds > 0f &&
               elapsedSeconds >= maximumSeconds;
    }
}

/// <summary>
/// Pure ownership record for the one model-facing job the controller exposes.
/// Cancellation does not clear this lease; only observed settlement, explicit
/// conclusion, or verified bounded abandonment does.
/// </summary>
internal sealed class CompanionJobLease
{
    internal long Token { get; private set; }
    internal string JobName { get; private set; }
    internal bool IsDetached { get; private set; }
    internal float DetachedAt { get; private set; }
    internal bool CancellationRequested { get; private set; }
    internal float CancellationRequestedAt { get; private set; }
    internal bool HasValue => Token != 0;

    internal bool Matches(long token)
    {
        return token != 0 && Token == token;
    }

    internal bool TryBegin(long token, string jobName)
    {
        if (HasValue || token == 0 || string.IsNullOrEmpty(jobName))
            return false;
        Token = token;
        JobName = jobName;
        IsDetached = false;
        DetachedAt = 0f;
        CancellationRequested = false;
        CancellationRequestedAt = 0f;
        return true;
    }

    internal void MarkCancellationRequested(float now)
    {
        if (!HasValue || CancellationRequested)
            return;
        CancellationRequested = true;
        CancellationRequestedAt = now;
    }

    internal void MarkDetached(float now)
    {
        if (!HasValue)
            return;
        MarkCancellationRequested(now);
        IsDetached = true;
        DetachedAt = now;
    }

    internal bool DetachedSettlementTimedOut(float now, float maximumSeconds)
    {
        return CompanionJobSettlementProtocol.HasCancellationSettlementTimedOut(
            IsDetached,
            now - DetachedAt,
            maximumSeconds);
    }

    internal void Clear()
    {
        Token = 0;
        JobName = null;
        IsDetached = false;
        DetachedAt = 0f;
        CancellationRequested = false;
        CancellationRequestedAt = 0f;
    }
}
