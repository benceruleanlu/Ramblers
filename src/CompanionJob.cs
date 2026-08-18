using System;

namespace Ramblers;

/// <summary>
/// The companion capabilities a job needs exclusive use of. Every action
/// declares what it claims, so mutual exclusion is stated once per action
/// instead of being written out by hand for each pair of actions.
/// </summary>
[Flags]
internal enum JobResources
{
    None = 0,
    Locomotion = 1 << 0,
    Gaze = 1 << 1,
    Hands = 1 << 2
}

/// <summary>
/// A terminal job outcome: the tool result the model receives, plus any extra
/// conversation items the job wants delivered alongside it.
/// </summary>
internal sealed class CompanionJobCompletion
{
    internal AgentToolResult Result;
    internal AgentContinuationItem[] Continuation;

    internal static CompanionJobCompletion Failed(string error)
    {
        return new CompanionJobCompletion
        {
            Result = AgentToolResult.Failure(error),
            Continuation = null
        };
    }
}

/// <summary>
/// What the agent boundary needs to keep tracking a job it has just started.
/// </summary>
internal sealed class CompanionJobHandle
{
    internal long Token;
    internal float TimeoutSeconds;
}

/// <summary>
/// A companion action that runs across frames rather than finishing inside the
/// tool call. The coordinator arbitrates jobs purely through this interface, so
/// adding an action does not add branches to the coordinator.
/// </summary>
internal interface ICompanionJob
{
    /// <summary>The model-facing tool name, also used in arbitration errors.</summary>
    string Name { get; }

    /// <summary>What the job must claim to start.</summary>
    JobResources Requires { get; }

    /// <summary>
    /// What the job currently holds. This narrows as a job winds down: an
    /// inspection stops holding locomotion once it has captured, keeping only
    /// gaze for its settle hold.
    /// </summary>
    JobResources Held { get; }

    bool IsActive { get; }

    /// <summary>How long the agent boundary should wait before giving up.</summary>
    float TimeoutSeconds { get; }

    void Bind(CompanionBody body, PlayerCharacter human);

    bool TryBegin(float now, out AgentToolResult failure);

    void Tick(float now);

    bool TryTakeCompletion(out CompanionJobCompletion completion);

    /// <summary>
    /// The model has acted on this job's report. Any hold the job kept past
    /// completion can be dropped now.
    /// </summary>
    void Conclude(float now);

    void Cancel(float now);

    void Fail(string error, float now);

    void Release();
}
