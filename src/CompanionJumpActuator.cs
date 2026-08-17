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

    internal void Bind(CompanionBody body)
    {
        _body = body;
        _queued = false;
        _nextRequestAt = 0f;
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

        _queued = true;
        _nextRequestAt = now + RequestCooldown;
        Plugin.Logger.LogInfo("[ACTION] JUMP queued.");
        return AgentToolResult.Success(AgentToolCatalog.Jump, "queued");
    }

    internal void TickFixed(float now, CompanionPosture posture)
    {
        if (!_queued)
            return;

        _queued = false;
        string error;
        if (!CanJump(posture, out error))
        {
            Plugin.Logger.LogWarning($"[ACTION] JUMP cancelled error={error}.");
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
            "[ACTION] JUMP executed " +
            $"beforeVelocity={before}, afterVelocity={velocity}, " +
            $"jumpForce={_body.Character.tunings?.jumpForce}, " +
            $"justJumped={jumper.justJumped}, at={now:F2}.");
    }

    internal void Cancel(string reason)
    {
        if (_queued)
            Plugin.Logger.LogWarning($"[ACTION] JUMP cancelled reason={reason}.");
        _queued = false;
    }

    internal void Release()
    {
        _body = null;
        _queued = false;
        _nextRequestAt = 0f;
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
