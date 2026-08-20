using System;
using UnityEngine;

namespace Ramblers;

internal enum FollowMode
{
    Follow,
    Stay
}

internal enum CompanionPosture
{
    Standing,
    Crouching,
    Sitting
}

/// <summary>
/// Arbitrates independent companion capabilities. Navigation retains the
/// human's requested goal while a posture temporarily prevents movement, and
/// multi-frame jobs are admitted only when the capabilities they claim are
/// free. Adding an action means adding a job to <see cref="_jobs"/>, not a
/// branch to this class.
/// </summary>
internal sealed class CompanionActionCoordinator
{
    private readonly CompanionLocomotion _locomotion = new CompanionLocomotion();
    private readonly CompanionFacing _facing = new CompanionFacing(CompanionFollowBehavior.NavigationInterval);
    private readonly CompanionAttention _attention;
    private readonly CompanionFollowBehavior _follow;
    private readonly CompanionAmbientGaze _ambientGaze;
    private readonly CompanionPostureActuator _posture = new CompanionPostureActuator();
    private readonly CompanionJumpActuator _jump = new CompanionJumpActuator();
    private readonly ICompanionJob[] _jobs;

    internal CompanionActionCoordinator()
    {
        _attention = new CompanionAttention(_facing);
        _follow = new CompanionFollowBehavior(_locomotion, _attention, _jump);
        _ambientGaze = new CompanionAmbientGaze(_attention);
        _jobs = new ICompanionJob[]
        {
            new CompanionInspectionBehavior(_attention),
            new CompanionPickupBehavior(_attention),
            new CompanionKickBehavior(_attention)
        };
    }

    internal void Bind(CompanionBody body, PlayerCharacter human, float now)
    {
        _locomotion.ResolveGaitSpeeds(body.Character);
        _locomotion.Bind(body, now);
        _attention.Bind(body, now);
        _posture.Bind(body);
        _jump.Bind(body);
        for (var index = 0; index < _jobs.Length; index++)
            _jobs[index].Bind(body, human);
        _locomotion.SetPosture(_posture.Current);
        _attention.SetBodyTurnAllowed(BodyTurnAllowed);
        _follow.Bind(body, human, now, MovementAllowed, MovementBlocker);
        _ambientGaze.Bind(body, human, now);
    }

    internal void TickFrame(float now)
    {
        _follow.TickFrame(now);
    }

    internal void TickLateFrame(float now)
    {
        for (var index = 0; index < _jobs.Length; index++)
            _jobs[index].Tick(now);
        // The idle habit publishes underneath whatever a job is claiming, so it
        // resolves by channel priority rather than by asking what else is running.
        _ambientGaze.Tick(now, _locomotion.LastMovementIntent);
        _attention.Tick(now);
        // A job can release locomotion part-way through its own lifetime, so the
        // navigation gate is re-evaluated every frame rather than only when a
        // tool call happens to run.
        RefreshMovementGate(now);
    }

    internal void TickFixed(float now)
    {
        try
        {
            _follow.TickFixed(now, MovementAllowed, MovementBlocker);
        }
        catch (Exception exception)
        {
            _follow.Fail($"navigation exception: {exception}");
        }

        try
        {
            _jump.TickFixed(now, _posture.Current);
        }
        catch (Exception exception)
        {
            _jump.Cancel("jump execution exception");
            Plugin.Logger.LogError($"[ACTION] JUMP failed: {exception}");
        }
    }

    internal AgentToolResult SetFollowMode(FollowMode mode, float now)
    {
        return _follow.SetMode(mode, now, MovementAllowed, MovementBlocker);
    }

    internal AgentToolResult SetPosture(CompanionPosture posture, float now)
    {
        var result = _posture.Set(posture);
        if (!result.Ok)
            return result;

        _locomotion.SetPosture(_posture.Current);
        _attention.SetBodyTurnAllowed(BodyTurnAllowed);
        if (_posture.BlocksMovement)
            _locomotion.Stop(now);
        RefreshMovementGate(now);
        return result;
    }

    /// <summary>
    /// Whether the companion may turn its body to look at something. Sitting is
    /// the one posture where it may not: stock PlayerMover.UpdatePerFrameRotation
    /// skips its head-yaw drain while PlayerSitter reports sitting, so a seated
    /// player looks around with their head alone.
    /// </summary>
    private bool BodyTurnAllowed => _posture.Current != CompanionPosture.Sitting;

    /// <summary>
    /// Reports whether the human and companion are mid-conversation, which pins
    /// the idle gaze to the human for as long as it lasts.
    /// </summary>
    internal void SetConversationActive(bool active)
    {
        _ambientGaze.SetConversationActive(active);
    }

    internal AgentToolResult RequestJump(float now)
    {
        var holder = FindHolder(JobResources.Locomotion);
        if (holder != null)
            return AgentToolResult.Failure(holder.ActiveName + "_in_progress");
        return _jump.Request(now, _posture.Current);
    }

    /// <summary>
    /// Stops every running job, the queued jump, and any outstanding navigation
    /// intent. This is the substrate for the model-facing cancel_action tool.
    /// </summary>
    internal AgentToolResult CancelActiveWork(float now)
    {
        var cancelled = 0;
        var reconciliationPending = false;
        for (var index = 0; index < _jobs.Length; index++)
        {
            if (!_jobs[index].IsActive)
                continue;
            _jobs[index].Cancel(now);
            cancelled++;
            if (_jobs[index].IsActive || _jobs[index].Held != JobResources.None)
                reconciliationPending = true;
        }

        if (_jump.IsQueued)
        {
            _jump.Cancel("cancel_action");
            cancelled++;
        }

        if (_follow.IsRequested)
        {
            _follow.Stop(now);
            cancelled++;
        }

        RefreshMovementGate(now);
        Plugin.Logger.LogInfo($"[ACTION] CANCEL_ALL stopped={cancelled}.");
        return AgentToolResult.Success(
            AgentToolCatalog.CancelAction,
            reconciliationPending
                ? "cancel_requested"
                : cancelled > 0
                    ? "cancelled"
                    : "nothing_to_cancel",
            reconciliationPending ? "cancelling" : "idle");
    }

    internal bool TryBeginJob(
        string jobName,
        CompanionJobRequest request,
        float now,
        out float timeoutSeconds,
        out AgentToolResult failure)
    {
        timeoutSeconds = 0f;
        var job = FindJob(jobName);
        if (job == null)
        {
            failure = AgentToolResult.Failure("unknown_tool");
            return false;
        }

        // The agent boundary tracks one job at a time, so admitting a second
        // concurrent job would orphan the first. Lifting this needs a real
        // token-to-job map there, not a change here. The check reads live job
        // state rather than a latched flag, so a job that ends on its own timer
        // always frees the slot.
        var running = FindActiveJob();
        if (running != null && !ReferenceEquals(running, job))
        {
            failure = AgentToolResult.Failure(running.ActiveName + "_in_progress");
            return false;
        }

        if (!TryReserve(job, request, out failure))
            return false;
        if (!job.TryBegin(now, request, out failure))
            return false;

        timeoutSeconds = job.TimeoutSeconds;
        RefreshMovementGate(now);
        return true;
    }

    internal bool TryTakeJobCompletion(
        string jobName,
        float now,
        out CompanionJobCompletion completion)
    {
        completion = null;
        var job = FindJob(jobName);
        if (job == null || !job.TryTakeCompletion(out completion))
            return false;

        RefreshMovementGate(now);
        return true;
    }

    internal void ConcludeJob(string jobName, float now)
    {
        var job = FindJob(jobName);
        if (job == null)
            return;
        job.Conclude(now);
        RefreshMovementGate(now);
    }

    internal void CancelJob(string jobName, float now)
    {
        var job = FindJob(jobName);
        if (job == null)
            return;
        job.Cancel(now);
        RefreshMovementGate(now);
    }

    internal void FailActiveJobs(string error, float now)
    {
        for (var index = 0; index < _jobs.Length; index++)
            _jobs[index].Fail(error, now);
        RefreshMovementGate(now);
    }

    internal void Release()
    {
        for (var index = 0; index < _jobs.Length; index++)
            _jobs[index].Release();
        _ambientGaze.Release();
        _follow.Release();
        _jump.Release();
        _posture.Release();
        _locomotion.Release();
        _attention.Release();
    }

    internal void StopQuietly()
    {
        var now = Time.realtimeSinceStartup;
        for (var index = 0; index < _jobs.Length; index++)
            _jobs[index].Cancel(now);
        _jump.Cancel("controller shutdown");
        _locomotion.StopQuietly();
    }

    private ICompanionJob FindJob(string jobName)
    {
        if (string.IsNullOrEmpty(jobName))
            return null;
        for (var index = 0; index < _jobs.Length; index++)
        {
            if (_jobs[index].Handles(jobName))
                return _jobs[index];
        }

        return null;
    }

    private ICompanionJob FindActiveJob()
    {
        for (var index = 0; index < _jobs.Length; index++)
        {
            if (_jobs[index].IsActive)
                return _jobs[index];
        }

        return null;
    }

    /// <summary>
    /// The running job currently holding the given capability, if any. Pairwise
    /// exclusion checks between specific actions are not needed: every action
    /// asks this one question about the capability it wants.
    /// </summary>
    private ICompanionJob FindHolder(JobResources resource)
    {
        for (var index = 0; index < _jobs.Length; index++)
        {
            if ((_jobs[index].Held & resource) != 0)
                return _jobs[index];
        }

        return null;
    }

    private bool TryReserve(
        ICompanionJob job,
        CompanionJobRequest request,
        out AgentToolResult failure)
    {
        var wanted = job.RequiredFor(request);
        if ((wanted & JobResources.Locomotion) != 0 && _jump.IsQueued)
        {
            failure = AgentToolResult.Failure("jump_in_progress");
            return false;
        }

        for (var index = 0; index < _jobs.Length; index++)
        {
            var other = _jobs[index];
            if (ReferenceEquals(other, job) || (other.Held & wanted) == 0)
                continue;
            failure = AgentToolResult.Failure(other.ActiveName + "_in_progress");
            return false;
        }

        failure = null;
        return true;
    }

    private void RefreshMovementGate(float now)
    {
        _follow.SetMovementAllowed(MovementAllowed, now, MovementBlocker);
    }

    private bool MovementAllowed =>
        !_posture.BlocksMovement && FindHolder(JobResources.Locomotion) == null;

    private string MovementBlocker
    {
        get
        {
            if (_posture.BlocksMovement)
                return "posture";
            var holder = FindHolder(JobResources.Locomotion);
            return holder == null ? null : holder.Name;
        }
    }
}
