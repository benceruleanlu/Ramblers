using Mirror;
using UnityEngine;

namespace Ramblers;

/// <summary>
/// Queues one stock jump calculation for the next physics tick. Big Walk's
/// public ForceAJump helper rejects non-local players, so the server-owned bot
/// feeds the stock queued-jump calculation its current rigidbody velocity.
/// </summary>
internal sealed class CompanionJumpActuator
{
    private const float RequestCooldown = 0.5f;

    private readonly CompanionJumpQueue _queue = new CompanionJumpQueue();
    private CompanionBody _body;
    private float _nextRequestAt;

    internal bool IsQueued => _queue.IsQueued;

    internal void Bind(CompanionBody body)
    {
        _body = body;
        _queue.Clear();
        _nextRequestAt = 0f;
    }

    /// <summary>
    /// Preflights an explicit jump as though the coordinator had already stood
    /// the companion. This lets an invalid request leave posture unchanged.
    /// </summary>
    internal bool CanRequest(float now, out string error)
    {
        error = null;
        if (!CanJump(CompanionPosture.Standing, out error))
            return false;
        if (_queue.IsQueued)
            return true;
        if (now < _nextRequestAt)
        {
            error = "jump_cooldown";
            return false;
        }
        return true;
    }

    internal AgentToolResult Request(float now, CompanionPosture posture)
    {
        string error;
        if (!CanJump(posture, out error))
            return AgentToolResult.Failure(error);
        if (_queue.IsQueued)
        {
            var previousOwner = _queue.ClaimForTool();
            if (previousOwner != CompanionJumpQueueOwner.Tool)
            {
                Plugin.Logger.LogInfo(
                    $"[ACTION] JUMP_CLAIMED previousOwner={previousOwner}.");
            }
            return AgentToolResult.Success(AgentToolCatalog.Jump, "already_queued");
        }
        if (now < _nextRequestAt)
            return AgentToolResult.Failure("jump_cooldown");

        Queue(now, CompanionJumpQueueOwner.Tool, null);
        Plugin.Logger.LogInfo("[ACTION] JUMP queued.");
        return AgentToolResult.Success(AgentToolCatalog.Jump, "queued");
    }

    /// <summary>
    /// Internal route-replay entry point. The model does not decide when a
    /// recorded jump is needed; deterministic follow code may queue the same
    /// stock jump path after validating live ground and posture state.
    /// </summary>
    internal bool TryRequestTraversal(
        float now,
        CompanionPosture posture,
        string reason,
        out string error)
    {
        error = null;
        if (_queue.IsQueued)
        {
            if (_queue.CanSatisfyFollow)
                return true;
            error = "jump_in_progress";
            return false;
        }
        if (now < _nextRequestAt)
        {
            error = "jump_cooldown";
            return false;
        }
        if (!CanJump(posture, out error))
            return false;

        Queue(now, CompanionJumpQueueOwner.Follow, reason);
        Plugin.Logger.LogInfo(
            $"[FOLLOW] JUMP_QUEUED reason={reason ?? "route"}.");
        return true;
    }

    internal bool TryRequestActionRecovery(
        float now,
        CompanionPosture posture,
        string actionName,
        string reason,
        out string error)
    {
        error = null;
        if (_queue.IsQueued)
        {
            if (_queue.CanSatisfyAction(actionName))
                return true;
            error = "jump_in_progress";
            return false;
        }
        if (now < _nextRequestAt)
        {
            error = "jump_cooldown";
            return false;
        }
        if (!CanJump(posture, out error))
            return false;

        Queue(
            now,
            CompanionJumpQueueOwner.Action,
            actionName + ":" + reason);
        Plugin.Logger.LogInfo(
            $"[ACTION] APPROACH_JUMP_QUEUED action={actionName ?? "unknown"}, " +
            $"reason={reason ?? "path_recovery"}.");
        return true;
    }

    internal static bool IsDeferredRecoveryError(string error)
    {
        return string.Equals(
                   error,
                   "jump_in_progress",
                   System.StringComparison.Ordinal) ||
               string.Equals(
                   error,
                   "jump_cooldown",
                   System.StringComparison.Ordinal) ||
               string.Equals(
                   error,
                   "not_on_jumpable_ground",
                   System.StringComparison.Ordinal);
    }

    internal void CancelActionRecovery(string actionName)
    {
        if (!_queue.TryCancelAction(actionName))
            return;

        Plugin.Logger.LogInfo(
            $"[ACTION] APPROACH_JUMP_CANCELLED action={actionName}.");
    }

    internal void CancelFollow(string reason)
    {
        if (!_queue.TryCancelFollow())
            return;

        Plugin.Logger.LogWarning(
            $"[FOLLOW] JUMP_CANCELLED reason={reason}.");
    }

    internal void TickFixed(float now, CompanionPosture posture)
    {
        if (!_queue.IsQueued)
            return;

        var source = _queue.Owner;
        var reason = _queue.Reason;
        ClearQueue();
        string error;
        if (!CanJump(posture, out error))
        {
            Plugin.Logger.LogWarning(
                $"[{(source == CompanionJumpQueueOwner.Follow ? "FOLLOW" : "ACTION")}] " +
                $"JUMP_CANCELLED error={error}, " +
                $"reason={reason ?? source.ToString().ToLowerInvariant()}.");
            return;
        }

        var rigidbody = _body.Character.rb;
        var jumper = _body.Character.jumper;
        var before = rigidbody.linearVelocity;
        var velocity = before;

        jumper.jumpInQueue = true;
        jumper.LocalFixedUpdate(ref velocity);
        rigidbody.linearVelocity = velocity;

        Plugin.Logger.LogInfo(
            $"[{(source == CompanionJumpQueueOwner.Follow ? "FOLLOW" : "ACTION")}] JUMP_EXECUTED " +
            $"reason={reason ?? source.ToString().ToLowerInvariant()}, " +
            $"beforeVelocity={before}, afterVelocity={velocity}, " +
            $"jumpForce={_body.Character.tunings?.jumpForce}, " +
            $"justJumped={jumper.justJumped}, at={now:F2}.");
    }

    internal void Cancel(string reason)
    {
        if (_queue.IsQueued)
        {
            Plugin.Logger.LogWarning(
                $"[{(_queue.Owner == CompanionJumpQueueOwner.Follow ? "FOLLOW" : "ACTION")}] " +
                $"JUMP_CANCELLED reason={reason}.");
        }
        ClearQueue();
    }

    internal void Release()
    {
        _body = null;
        ClearQueue();
        _nextRequestAt = 0f;
    }

    private void Queue(
        float now,
        CompanionJumpQueueOwner owner,
        string reason)
    {
        _queue.Set(owner, reason);
        _nextRequestAt = now + RequestCooldown;
    }

    private void ClearQueue()
    {
        _queue.Clear();
    }

    private bool CanJump(CompanionPosture posture, out string error)
    {
        error = null;
        if (_body == null || !_body.IsAlive)
            error = "bot_not_spawned";
        else if (!NetworkServer.active || !_body.Networking.isServer || _body.Networking.isLocalPlayer)
            error = "bot_authority_unavailable";
        else if (posture != CompanionPosture.Standing)
            error = "jump_requires_standing";
        else if (_body.Character.rb == null || _body.Character.jumper == null)
            error = "jump_components_unavailable";
        else if (_body.Character.ground == null ||
                 !_body.Character.ground.isGrounded ||
                 !_body.Character.ground.isOnJumpableGround)
            error = "not_on_jumpable_ground";

        return error == null;
    }
}
