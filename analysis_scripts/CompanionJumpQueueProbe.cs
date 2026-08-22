using System;

namespace Ramblers;

internal static class CompanionJumpQueueProbe
{
    private static int Main()
    {
        ExactActionOwnsItsRecovery();
        ToolClaimCannotBeCancelledByTheOldAction();
        FollowAndToolIntentsDoNotAlias();
        FollowCancellationIsOwnerScoped();
        ClearResetsEveryField();
        Console.WriteLine("Companion jump queue probe passed.");
        return 0;
    }

    private static void ExactActionOwnsItsRecovery()
    {
        var queue = new CompanionJumpQueue();
        queue.Set(
            CompanionJumpQueueOwner.Action,
            "pick_up_item:stuck");

        Expect(queue.IsQueued, "action queue was not retained");
        Expect(
            queue.CanSatisfyAction("pick_up_item"),
            "the owning action could not recognize its jump");
        Expect(
            !queue.CanSatisfyAction("pick_up"),
            "an action-name prefix aliased another action");
        Expect(
            !queue.TryCancelAction("interact_with_object"),
            "another action cancelled the queued jump");
        Expect(queue.IsQueued, "failed cancellation mutated the queue");
    }

    private static void ToolClaimCannotBeCancelledByTheOldAction()
    {
        var queue = new CompanionJumpQueue();
        queue.Set(
            CompanionJumpQueueOwner.Action,
            "pick_up_item:blocked_path");

        var previous = queue.ClaimForTool();
        Expect(
            previous == CompanionJumpQueueOwner.Action,
            "tool claim lost the previous owner");
        Expect(
            queue.Owner == CompanionJumpQueueOwner.Tool,
            "tool claim did not transfer ownership");
        Expect(queue.Reason == null, "tool claim retained a stale action reason");
        Expect(
            !queue.TryCancelAction("pick_up_item"),
            "old action cancelled an explicit jump");
    }

    private static void FollowAndToolIntentsDoNotAlias()
    {
        var queue = new CompanionJumpQueue();
        queue.Set(CompanionJumpQueueOwner.Follow, "recorded_human_jump");
        Expect(queue.CanSatisfyFollow, "follow could not recognize its jump");
        Expect(
            !queue.CanSatisfyAction("pick_up_item"),
            "pickup treated a follow jump as action recovery");

        queue.Set(CompanionJumpQueueOwner.Tool, null);
        Expect(!queue.CanSatisfyFollow, "follow treated an explicit jump as its own");
        Expect(
            !queue.CanSatisfyAction("pick_up_item"),
            "pickup treated an explicit jump as action recovery");
    }

    private static void ClearResetsEveryField()
    {
        var queue = new CompanionJumpQueue();
        queue.Set(CompanionJumpQueueOwner.Follow, "stuck_recovery");
        queue.Clear();

        Expect(!queue.IsQueued, "clear retained queued state");
        Expect(
            queue.Owner == CompanionJumpQueueOwner.None,
            "clear retained queue ownership");
        Expect(queue.Reason == null, "clear retained a queue reason");
    }

    private static void FollowCancellationIsOwnerScoped()
    {
        var queue = new CompanionJumpQueue();
        queue.Set(CompanionJumpQueueOwner.Tool, null);
        Expect(
            !queue.TryCancelFollow(),
            "stopping follow cancelled an explicit jump");
        Expect(queue.IsQueued, "failed follow cancellation mutated the queue");

        queue.Set(CompanionJumpQueueOwner.Follow, "blocked_route");
        Expect(queue.TryCancelFollow(), "follow could not cancel its own jump");
        Expect(!queue.IsQueued, "follow cancellation retained the queue");
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
