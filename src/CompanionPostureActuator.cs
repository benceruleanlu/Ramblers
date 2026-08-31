namespace Ramblers;

internal sealed class CompanionPostureActuator
{
    private CompanionBody _body;
    private bool _lastNativePoseActive;

    internal CompanionPosture Current { get; private set; } = CompanionPosture.Standing;
    internal bool NativePoseActive =>
        _body?.Character?.poser?.currentPose != null;
    internal bool BlocksMovement =>
        Current == CompanionPosture.Sitting || NativePoseActive;
    internal bool BlocksBodyTurn => BlocksMovement;

    internal void Bind(CompanionBody body)
    {
        _body = body;
        Current = ReadCurrentPosture();
        _lastNativePoseActive = NativePoseActive;
        Apply(Current);
    }

    internal AgentToolResult Set(CompanionPosture posture)
    {
        if (_body == null || !_body.IsAlive)
            return AgentToolResult.Failure("bot_not_spawned");

        string exitError;
        var currentPose = _body.Character.poser == null
            ? null
            : _body.Character.poser.currentPose;
        var maySitInCurrentPose = currentPose != null &&
                                  currentPose.allowSitting;
        var exitedNativePose = currentPose != null &&
                               (posture != CompanionPosture.Sitting ||
                                !maySitInCurrentPose);
        if (exitedNativePose && !TryExitNativePose(out exitError))
        {
            return AgentToolResult.Failure(exitError);
        }

        var unchanged = !exitedNativePose && Current == posture &&
                        ReadCurrentPosture() == posture;
        Apply(posture);
        Current = posture;
        _lastNativePoseActive = NativePoseActive;

        var state = Describe(posture);
        Plugin.Logger.LogInfo(
            $"[ACTION] POSTURE state={state}, status={(unchanged ? "unchanged" : "applied")}, " +
            $"trueCrouchness={_body.Networking.NetworktrueCrouchness:F1}, " +
            $"isSitting={_body.Networking.NetworkisSitting}, " +
            $"nativePoseActive={NativePoseActive}.");
        return AgentToolResult.Success(
            AgentToolCatalog.SetPosture,
            unchanged ? "unchanged" : "applied",
            state);
    }

    internal void Release()
    {
        _body = null;
        Current = CompanionPosture.Standing;
        _lastNativePoseActive = false;
    }

    internal bool SynchronizeFromGame()
    {
        if (_body == null || !_body.IsAlive)
            return false;
        var observed = ReadCurrentPosture();
        var nativePoseActive = NativePoseActive;
        if (observed == Current &&
            nativePoseActive == _lastNativePoseActive)
            return false;
        Current = observed;
        _lastNativePoseActive = nativePoseActive;
        Plugin.Logger.LogInfo(
            $"[ACTION] POSTURE_SYNC state={Describe(Current)}, " +
            $"nativePoseActive={NativePoseActive}.");
        return true;
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

    private bool TryExitNativePose(out string error)
    {
        error = null;
        if (!NativePoseActive)
            return true;
        if (!Mirror.NetworkServer.active || !_body.Networking.isServer ||
            _body.Networking.isLocalPlayer)
        {
            error = "interaction_authority_unavailable";
            return false;
        }

        try
        {
            _body.Networking.ServerExitPoseAuto();
            Plugin.Logger.LogInfo("[ACTION] NATIVE_POSE_EXIT_REQUESTED.");
            return true;
        }
        catch (System.Exception exception)
        {
            error = "interaction_authority_failed";
            Plugin.Logger.LogWarning(
                $"[ACTION] NATIVE_POSE_EXIT_FAILED error={exception.Message}");
            return false;
        }
    }

    internal static string Describe(CompanionPosture posture)
    {
        return posture.ToString().ToLowerInvariant();
    }
}
