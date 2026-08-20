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

    private CompanionBody _body;
    private bool _queued;
    private float _nextRequestAt;
    private string _queuedSource;
    private string _queuedReason;

    internal bool IsQueued => _queued;

    internal void Bind(CompanionBody body)
    {
        _body = body;
        _queued = false;
        _nextRequestAt = 0f;
        _queuedSource = null;
        _queuedReason = null;
    }

    internal AgentToolResult Request(float now, CompanionPosture posture)
    {
        string error;
        if (!CanJump(posture, out error))
            return AgentToolResult.Failure(error);
        if (_queued)
            return AgentToolResult.Success(AgentToolCatalog.Jump, "already_queued");
        if (now < _nextRequestAt)
            return AgentToolResult.Failure("jump_cooldown");

        Queue(now, "tool", null);
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
        if (_queued)
            return true;
        if (now < _nextRequestAt)
        {
            error = "jump_cooldown";
            return false;
        }
        if (!CanJump(posture, out error))
            return false;

        Queue(now, "follow", reason);
        Plugin.Logger.LogInfo(
            $"[FOLLOW] JUMP_QUEUED reason={reason ?? "route"}.");
        return true;
    }

    internal void TickFixed(float now, CompanionPosture posture)
    {
        if (!_queued)
            return;

        var source = _queuedSource;
        var reason = _queuedReason;
        ClearQueue();
        string error;
        if (!CanJump(posture, out error))
        {
            Plugin.Logger.LogWarning(
                $"[{(source == "follow" ? "FOLLOW" : "ACTION")}] " +
                $"JUMP_CANCELLED error={error}, reason={reason ?? source ?? "unknown"}.");
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
            $"[{(source == "follow" ? "FOLLOW" : "ACTION")}] JUMP_EXECUTED " +
            $"reason={reason ?? source ?? "request"}, " +
            $"beforeVelocity={before}, afterVelocity={velocity}, " +
            $"jumpForce={_body.Character.tunings?.jumpForce}, " +
            $"justJumped={jumper.justJumped}, at={now:F2}.");
    }

    internal void Cancel(string reason)
    {
        if (_queued)
        {
            Plugin.Logger.LogWarning(
                $"[{(_queuedSource == "follow" ? "FOLLOW" : "ACTION")}] " +
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

    private void Queue(float now, string source, string reason)
    {
        _queued = true;
        _queuedSource = source;
        _queuedReason = reason;
        _nextRequestAt = now + RequestCooldown;
    }

    private void ClearQueue()
    {
        _queued = false;
        _queuedSource = null;
        _queuedReason = null;
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
