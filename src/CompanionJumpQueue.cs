namespace Ramblers;

internal enum CompanionJumpQueueOwner
{
    None,
    Tool,
    Follow,
    Action
}

internal sealed class CompanionJumpQueue
{
    internal CompanionJumpQueueOwner Owner { get; private set; }
    internal string Reason { get; private set; }
    internal bool IsQueued => Owner != CompanionJumpQueueOwner.None;

    internal void Set(CompanionJumpQueueOwner owner, string reason)
    {
        Owner = owner;
        Reason = reason;
    }

    internal CompanionJumpQueueOwner ClaimForTool()
    {
        var previous = Owner;
        Owner = CompanionJumpQueueOwner.Tool;
        Reason = null;
        return previous;
    }

    internal bool CanSatisfyFollow => Owner == CompanionJumpQueueOwner.Follow;

    internal bool CanSatisfyAction(string actionName)
    {
        return Owner == CompanionJumpQueueOwner.Action &&
               !string.IsNullOrEmpty(actionName) &&
               !string.IsNullOrEmpty(Reason) &&
               Reason.StartsWith(
                   actionName + ":",
                   System.StringComparison.Ordinal);
    }

    internal bool TryCancelAction(string actionName)
    {
        if (!CanSatisfyAction(actionName))
            return false;
        Clear();
        return true;
    }

    internal bool TryCancelFollow()
    {
        if (!CanSatisfyFollow)
            return false;
        Clear();
        return true;
    }

    internal void Clear()
    {
        Owner = CompanionJumpQueueOwner.None;
        Reason = null;
    }
}
