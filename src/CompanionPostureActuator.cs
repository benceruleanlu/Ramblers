namespace Ramblers;

/// <summary>
/// Owns persistent replicated posture state for the connectionless remote body.
/// It writes server SyncVars directly instead of invoking local-player Commands.
/// </summary>
internal sealed class CompanionPostureActuator
{
    private CompanionBody _body;

    internal CompanionPosture Current { get; private set; } = CompanionPosture.Standing;
    internal bool BlocksMovement => Current == CompanionPosture.Sitting;

    internal void Bind(CompanionBody body)
    {
        _body = body;
        Current = ReadCurrentPosture();
        Apply(Current);
    }

    internal AgentToolResult Set(CompanionPosture posture)
    {
        if (_body == null || !_body.IsAlive)
            return AgentToolResult.Failure("bot_not_spawned");

        var unchanged = Current == posture && ReadCurrentPosture() == posture;
        Apply(posture);
        Current = posture;

        var state = Describe(posture);
        Plugin.Logger.LogInfo(
            $"[ACTION] POSTURE state={state}, status={(unchanged ? "unchanged" : "applied")}, " +
            $"trueCrouchness={_body.Networking.NetworktrueCrouchness:F1}, " +
            $"isSitting={_body.Networking.NetworkisSitting}.");
        return AgentToolResult.Success(
            AgentToolCatalog.SetPosture,
            unchanged ? "unchanged" : "applied",
            state);
    }

    internal void Release()
    {
        _body = null;
        Current = CompanionPosture.Standing;
    }

    private CompanionPosture ReadCurrentPosture()
    {
        if (_body?.Networking == null)
            return CompanionPosture.Standing;
        if (_body.Networking.NetworkisSitting)
            return CompanionPosture.Sitting;
        return _body.Networking.NetworktrueCrouchness >= 0.5f
            ? CompanionPosture.Crouching
            : CompanionPosture.Standing;
    }

    private void Apply(CompanionPosture posture)
    {
        if (_body?.Networking == null)
            return;

        switch (posture)
        {
            case CompanionPosture.Sitting:
                _body.Networking.NetworktrueCrouchness = 0f;
                _body.Networking.NetworkisSitting = true;
                break;
            case CompanionPosture.Crouching:
                _body.Networking.NetworkisSitting = false;
                _body.Networking.NetworktrueCrouchness = 1f;
                break;
            default:
                _body.Networking.NetworkisSitting = false;
                _body.Networking.NetworktrueCrouchness = 0f;
                break;
        }
    }

    internal static string Describe(CompanionPosture posture)
    {
        return posture.ToString().ToLowerInvariant();
    }
}
