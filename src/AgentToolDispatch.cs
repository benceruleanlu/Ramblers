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

    /// <summary>
    /// How long the agent boundary should wait for this job. It belongs to the
    /// job rather than the boundary because a gaze turn and a walk across a
    /// courtyard are not the same wait.
    /// </summary>
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
