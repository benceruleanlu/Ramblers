using System;
using Ramblers;

internal static class SequentialToolBatchCursorProbe
{
    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static int Main()
    {
        var cursor = new SequentialToolBatchCursor(3);
        Require(!cursor.IsComplete, "new cursor was complete");
        Require(cursor.HasUndispatched, "new cursor had no work");

        int index;
        Require(cursor.TryBeginNext(out index) && index == 0,
            "first call was not index zero");
        Require(!cursor.TryBeginNext(out index),
            "second call began before the first completed");
        Require(!cursor.TryCompleteActive(1),
            "wrong call completed the active slot");
        Require(cursor.TryCompleteActive(0),
            "first call did not complete");

        Require(cursor.TryBeginNext(out index) && index == 1,
            "second call was not dispatched in order");
        Require(cursor.TryCompleteActive(1),
            "second call did not complete");
        Require(cursor.TryBeginNext(out index) && index == 2,
            "third call was not dispatched in order");
        Require(!cursor.HasUndispatched,
            "cursor reported undispatched calls after third dispatch");
        Require(!cursor.IsComplete,
            "active final call was treated as complete");
        Require(cursor.TryCompleteActive(2) && cursor.IsComplete,
            "cursor did not finish after the final result");
        Require(!cursor.TryBeginNext(out index),
            "cursor dispatched beyond the batch boundary");

        Require(TurnReferenceRetentionPolicy.ShouldRetain(true),
            "tool response did not retain its turn for continuation");
        Require(!TurnReferenceRetentionPolicy.ShouldRetain(false),
            "terminal response without tools retained its turn");
        Require(PresentationRetentionPolicy.MustEndBatchBeforeNextCall(
                true, true),
            "a presentation allowed an unseen later call to execute");
        Require(!PresentationRetentionPolicy.MustEndBatchBeforeNextCall(
                true, false),
            "a terminal presentation was treated as a sequence conflict");
        Require(!PresentationRetentionPolicy.MustEndBatchBeforeNextCall(
                false, true),
            "an ordinary completion unnecessarily stopped its sequence");

        Console.WriteLine("Sequential tool-batch cursor probe passed.");
        return 0;
    }
}
