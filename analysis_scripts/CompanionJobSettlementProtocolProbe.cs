using System;
using Ramblers;

namespace Ramblers
{
    internal enum CompanionTurnHandsTransition
    {
        None,
        HoldingExactProp,
        HandsEmpty,
        HoldingExactPlayer
    }
}

internal static class CompanionJobSettlementProtocolProbe
{
    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static int Main()
    {
        Require(
            !CompanionJobSettlementProtocol.CanPublishCompletion(
                true, true, false),
            "an active physical reconciliation published early");
        Require(
            !CompanionJobSettlementProtocol.CanPublishCompletion(
                false, true, false),
            "an inactive job published while retaining resources");
        Require(
            CompanionJobSettlementProtocol.CanPublishCompletion(
                false, false, false),
            "a fully settled physical job did not publish");
        Require(
            CompanionJobSettlementProtocol.CanPublishCompletion(
                true, true, true),
            "an explicit holding/presentation completion did not publish");

        Require(
            !CompanionJobSettlementProtocol.IsSettled(true, false),
            "an active job was considered settled");
        Require(
            !CompanionJobSettlementProtocol.IsSettled(false, true),
            "a resource-owning job was considered settled");
        Require(
            CompanionJobSettlementProtocol.IsSettled(false, false),
            "an inactive resource-free job was not settled");
        Require(!CompanionJobSettlementProtocol.CanReleaseLeaseAfterConclude(
                false),
            "a broken Conclude released its live operation lease");
        Require(CompanionJobSettlementProtocol.CanReleaseLeaseAfterConclude(
                true),
            "a settled Conclude did not release its operation lease");

        Require(CompanionJobSettlementProtocol.IsCompletionTransitionCurrent(
                CompanionTurnHandsTransition.None, false, false, false),
            "a completion without a hands transition was rejected");
        Require(CompanionJobSettlementProtocol.IsCompletionTransitionCurrent(
                CompanionTurnHandsTransition.HoldingExactProp,
                true, false, false),
            "an exact held prop transition was rejected");
        Require(!CompanionJobSettlementProtocol.IsCompletionTransitionCurrent(
                CompanionTurnHandsTransition.HoldingExactProp,
                false, false, true),
            "a stale held prop transition was published");
        Require(CompanionJobSettlementProtocol.IsCompletionTransitionCurrent(
                CompanionTurnHandsTransition.HoldingExactPlayer,
                false, true, false),
            "an exact held player transition was rejected");
        Require(!CompanionJobSettlementProtocol.IsCompletionTransitionCurrent(
                CompanionTurnHandsTransition.HoldingExactPlayer,
                false, false, true),
            "a stale held player transition was published");
        Require(CompanionJobSettlementProtocol.IsCompletionTransitionCurrent(
                CompanionTurnHandsTransition.HandsEmpty,
                false, false, true),
            "a confirmed empty-hands transition was rejected");
        Require(!CompanionJobSettlementProtocol.IsCompletionTransitionCurrent(
                CompanionTurnHandsTransition.HandsEmpty,
                true, false, false),
            "a stale empty-hands transition was published");

        Require(
            !CompanionJobSettlementProtocol.HasCancellationSettlementTimedOut(
                false, 10f, 5f),
            "an uncancelled job entered the settlement bound");
        Require(
            !CompanionJobSettlementProtocol.HasCancellationSettlementTimedOut(
                true, 4.99f, 5f),
            "cancellation was bounded before its settlement budget");
        Require(
            CompanionJobSettlementProtocol.HasCancellationSettlementTimedOut(
                true, 5f, 5f),
            "cancellation did not enter its deterministic settlement bound");

        var lease = new CompanionJobLease();
        Require(lease.TryBegin(41, "pick_up_item"),
            "new operation lease was rejected");
        Require(lease.Matches(41) && lease.HasValue,
            "new operation did not own its exact token");
        Require(!lease.TryBegin(42, "kick_item") && lease.Matches(41) &&
                lease.JobName == "pick_up_item",
            "a second operation overwrote the live lease");
        lease.MarkCancellationRequested(10f);
        Require(lease.Matches(41) && lease.CancellationRequested,
            "cancellation detached the operation token");
        lease.MarkDetached(11f);
        Require(lease.Matches(41) && lease.IsDetached,
            "client detachment dropped controller ownership");
        Require(!lease.DetachedSettlementTimedOut(15.99f, 5f),
            "detached ownership was bounded too early");
        Require(lease.DetachedSettlementTimedOut(16f, 5f),
            "detached ownership was not deterministically bounded");
        lease.Clear();
        Require(!lease.HasValue && !lease.Matches(41) &&
                !lease.CancellationRequested && !lease.IsDetached,
            "terminal settlement did not clear every lease field");
        Require(lease.TryBegin(42, "kick_item") && lease.Matches(42),
            "a settled lease did not admit the next operation");

        Console.WriteLine("Companion job settlement protocol probe passed.");
        return 0;
    }
}
