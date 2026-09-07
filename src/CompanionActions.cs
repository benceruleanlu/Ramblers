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
    private CompanionBody _body;
    private PlayerCharacter _human;

    internal CompanionActionCoordinator()
    {
        _attention = new CompanionAttention(_facing);
        _follow = new CompanionFollowBehavior(_locomotion, _attention, _jump);
        _ambientGaze = new CompanionAmbientGaze(_attention);
        _jobs = new ICompanionJob[]
        {
            new CompanionInspectionBehavior(_attention),
            new CompanionMoveToLocationBehavior(_locomotion, _attention, _jump),
            new CompanionInteractBehavior(_locomotion, _attention, _jump),
            new CompanionPickupBehavior(_locomotion, _attention, _jump),
            new CompanionPlayerCarryBehavior(_locomotion, _attention, _jump),
            new CompanionKickBehavior(_locomotion, _attention, _jump)
        };
    }

    internal bool FollowRequested => _follow.IsRequested;
    internal string FollowStateLabel => _follow.StateLabel;
    internal bool IsCarried => _follow.IsCarried;
    internal bool IsCarryingHuman =>
        CompanionFollowBehavior.IsBodyCarryingHuman(_body, CurrentHuman);
    internal CompanionPosture Posture => _posture.Current;
    internal bool JumpQueued => _jump.IsQueued;
    internal bool IsMoving =>
        _locomotion.LastMovementIntent.sqrMagnitude > 0.01f;
    internal string ActiveJobName => FindActiveJob()?.ActiveName;

    internal void Bind(CompanionBody body, PlayerCharacter human, float now)
    {
        _body = body;
        _human = human;
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
        if (_posture.SynchronizeFromGame())
        {
            _locomotion.SetPosture(_posture.Current);
            _attention.SetBodyTurnAllowed(BodyTurnAllowed);
        }

        _ambientGaze.Tick(now, _locomotion.LastMovementIntent);
        _attention.Tick(now);

        RefreshMovementGate(now);
    }

    internal bool TryTakeAmbientObservation(
        float now,
        out CompanionAmbientObservationCandidate candidate)
    {
        return _ambientGaze.TryTakeSettledGlance(now, out candidate);
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

    internal void RebaseAfterExternalReposition(PlayerCharacter human, float now)
    {
        _follow.RebaseAfterExternalReposition(human, now, MovementAllowed, MovementBlocker);
    }

    internal AgentToolResult SetPosture(CompanionPosture posture, float now)
    {
        var locomotionHolder = FindHolder(JobResources.Locomotion);
        var conflictsWithLocomotionJob =
            posture == CompanionPosture.Sitting ||
            (posture != CompanionPosture.Standing &&
             locomotionHolder is ICompanionStandingJob);
        if (conflictsWithLocomotionJob && locomotionHolder != null)
        {
            return AgentToolResult.Failure(
                locomotionHolder.ActiveName + "_in_progress");
        }
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

    private bool BodyTurnAllowed => !_posture.BlocksBodyTurn;

    internal void SetConversationActive(bool active)
    {
        _ambientGaze.SetConversationActive(active);
    }

    internal AgentToolResult RequestJump(float now)
    {
        string preflightError;
        if (!_jump.CanRequest(now, out preflightError))
            return AgentToolResult.Failure(preflightError);

        AgentToolResult standFailure;
        if (!TryAutoStand("jump", now, out standFailure))
            return standFailure;
        return _jump.Request(now, _posture.Current);
    }

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

        if (job.IsActive)
        {
            failure = AgentToolResult.Failure(job.ActiveName + "_in_progress");
            return false;
        }

        if (!TryReserve(job, request, out failure))
            return false;
        if (!job.TryBegin(now, request, out failure))
            return false;
        var requiresLocomotion =
            (job.RequiredFor(request) & JobResources.Locomotion) != 0;
        var requiresStanding = job is ICompanionStandingJob;
        if (requiresLocomotion &&
            (_posture.BlocksMovement || requiresStanding) &&
            !TryAutoStand(job.ActiveName, now, out failure))
        {
            job.Cancel(now);
            RefreshMovementGate(now);
            return false;
        }

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
        if (job == null ||
            !CompanionJobSettlementProtocol.CanPublishCompletion(
                job.IsActive,
                job.Held != JobResources.None,
                job.MayPublishCompletionWhileActive) ||
            !job.TryTakeCompletion(out completion))
            return false;

        RefreshMovementGate(now);
        return true;
    }

    internal bool IsJobSettled(string jobName)
    {
        var job = FindJob(jobName);
        return job == null || CompanionJobSettlementProtocol.IsSettled(
            job.IsActive,
            job.Held != JobResources.None);
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
        _body = null;
        _human = null;
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

    private bool TryAutoStand(
        string reason,
        float now,
        out AgentToolResult failure)
    {
        failure = null;
        if (_posture.Current == CompanionPosture.Standing &&
            !_posture.NativePoseActive)
            return true;

        var standResult = _posture.Set(CompanionPosture.Standing);
        if (!standResult.Ok)
        {
            failure = standResult;
            return false;
        }

        _locomotion.SetPosture(_posture.Current);
        _attention.SetBodyTurnAllowed(BodyTurnAllowed);
        Plugin.Logger.LogInfo(
            $"[ACTION] AUTO_STAND reason={reason}, at={now:F2}.");
        return true;
    }

    private void RefreshMovementGate(float now)
    {
        _follow.SetMovementAllowed(MovementAllowed, now, MovementBlocker);
    }

    private bool MovementAllowed =>
        !_posture.BlocksMovement &&
        FindHolder(JobResources.Locomotion) == null &&
        !IsCarryingHuman;

    private string MovementBlocker
    {
        get
        {
            if (_posture.BlocksMovement)
                return "posture";
            var holder = FindHolder(JobResources.Locomotion);
            if (holder != null)
                return holder.Name;
            return IsCarryingHuman
                ? "carrying_player"
                : null;
        }
    }

    private PlayerCharacter CurrentHuman
    {
        get
        {
            var human = WorldManager.localPlayerCharacter;
            if (human == null)
                human = _human;
            return human == null || (_body != null && human == _body.Character)
                ? null
                : human;
        }
    }
}
