namespace Ramblers;

internal sealed class AgentToolDispatch
{
    private AgentToolDispatch(
        bool isPending,
        AgentToolResult result,
        long operationToken,
        float timeoutSeconds)
    {
        IsPending = isPending;
        Result = result;
        OperationToken = operationToken;
        TimeoutSeconds = timeoutSeconds;
    }

    internal bool IsPending { get; }
    internal AgentToolResult Result { get; }
    internal long OperationToken { get; }

    internal float TimeoutSeconds { get; }

    internal static AgentToolDispatch Immediate(AgentToolResult result)
    {
        return new AgentToolDispatch(
            false,
            result ?? AgentToolResult.Failure("action_execution_failed"),
            0,
            0f);
    }

    internal static AgentToolDispatch Pending(
        long operationToken,
        float timeoutSeconds)
    {
        return new AgentToolDispatch(true, null, operationToken, timeoutSeconds);
    }
}
