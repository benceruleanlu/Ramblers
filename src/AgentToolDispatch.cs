namespace Ramblers;

/// <summary>
/// Main-thread dispatch result for a model-selected tool. Most actions finish
/// immediately; embodied observations remain pending while Unity turns and
/// captures a frame over subsequent updates.
/// </summary>
internal sealed class AgentToolDispatch
{
    private AgentToolDispatch(
        bool isPending,
        AgentToolResult result,
        long operationToken)
    {
        IsPending = isPending;
        Result = result;
        OperationToken = operationToken;
    }

    internal bool IsPending { get; }
    internal AgentToolResult Result { get; }
    internal long OperationToken { get; }

    internal static AgentToolDispatch Immediate(AgentToolResult result)
    {
        return new AgentToolDispatch(
            false,
            result ?? AgentToolResult.Failure("action_execution_failed"),
            0);
    }

    internal static AgentToolDispatch Pending(long operationToken)
    {
        return new AgentToolDispatch(true, null, operationToken);
    }
}
