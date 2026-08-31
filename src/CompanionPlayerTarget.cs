using Mirror;
using UnityEngine;

namespace Ramblers;

internal sealed class CompanionPlayerTarget
{
    private readonly NetworkIdentity _networkIdentity;
    private readonly uint _networkId;

    private CompanionPlayerTarget(PlayerCharacter player)
    {
        Player = player;
        ReferenceId = player.GetInstanceID();
        _networkIdentity = player.GetComponentInParent<NetworkIdentity>();
        _networkId = _networkIdentity == null ? 0u : _networkIdentity.netId;
        StableId = StableIdFor(player);
    }

    internal PlayerCharacter Player { get; }
    internal int ReferenceId { get; }
    internal uint NetworkId => _networkId;
    internal string StableId { get; }

    internal static string StableIdFor(PlayerCharacter player)
    {
        if (player == null)
            return "player:unavailable";
        var identity = player.GetComponentInParent<NetworkIdentity>();
        return identity != null && identity.netId != 0u
            ? "player:net:" + identity.netId
            : "player:local:" + player.GetInstanceID();
    }

    internal static bool TryCaptureLocalHuman(
        PlayerCharacter human,
        CompanionBody body,
        out CompanionPlayerTarget target,
        out string error)
    {
        target = null;
        error = null;
        if (body == null || !body.IsAlive)
        {
            error = "bot_not_spawned";
            return false;
        }
        if (human == null || human.gameObject == null ||
            !human.gameObject.activeInHierarchy)
        {
            error = "human_player_unavailable";
            return false;
        }
        if (human == body.Character || human.gameObject == body.GameObject)
        {
            error = "human_player_unavailable";
            return false;
        }

        target = new CompanionPlayerTarget(human);
        return true;
    }

    internal static bool TryCapture(
        PlayerCharacter human,
        CompanionBody body,
        out CompanionPlayerTarget target,
        out string error)
    {
        return TryCaptureLocalHuman(human, body, out target, out error);
    }

    internal bool IsStillTheSamePlayer(PlayerCharacter candidate)
    {
        if (candidate == null || candidate != Player ||
            candidate.GetInstanceID() != ReferenceId)
        {
            return false;
        }

        if (_networkIdentity == null)
            return _networkId == 0u;

        var candidateIdentity =
            candidate.GetComponentInParent<NetworkIdentity>();
        return candidateIdentity == _networkIdentity &&
               candidateIdentity != null &&
               candidateIdentity.netId == _networkId;
    }

    internal bool TryGetCurrentPosition(out Vector3 position)
    {
        position = Vector3.zero;
        if (!IsAvailable)
            return false;
        position = Player.transform.position;
        return true;
    }

    internal bool TryGetCurrentLookPoint(out Vector3 point)
    {
        point = Vector3.zero;
        if (!IsAvailable)
            return false;
        point = CompanionBody.HeadPositionOf(Player);
        return true;
    }

    internal bool TryGetCarrierGrabPose(
        CompanionBody body,
        out PlayerPose grabPose,
        out string error)
    {
        grabPose = null;
        error = null;
        if (body == null || !body.IsAlive || body.Character.registry == null)
        {
            error = "player_carry_capability_unavailable";
            return false;
        }

        grabPose = body.Character.registry.grabPose;
        if (grabPose == null || grabPose.gameObject == null ||
            !grabPose.gameObject.activeInHierarchy)
        {
            grabPose = null;
            error = "player_carry_capability_unavailable";
            return false;
        }
        return true;
    }

    internal bool IsPickupAdmittedByGame(
        CompanionBody body,
        out string error)
    {
        error = null;
        if (!IsAvailable)
        {
            error = "human_player_unavailable";
            return false;
        }
        if (Player.poser == null)
        {
            error = "player_pose_unavailable";
            return false;
        }

        PlayerPose grabPose;
        if (!TryGetCarrierGrabPose(body, out grabPose, out error))
            return false;

        try
        {
            if (!Player.poser.PoseIsSafe(grabPose, null))
            {
                error = "player_cannot_be_picked_up";
                return false;
            }
        }
        catch (System.Exception exception)
        {
            Plugin.Logger.LogWarning(
                $"[ACTION] PLAYER_PICKUP_ADMISSION_FAILED " +
                $"referenceId={ReferenceId}, exception={exception}");
            error = "player_pickup_admission_failed";
            return false;
        }
        return true;
    }

    internal bool IsAvailable =>
        IsStillTheSamePlayer(Player) && Player.gameObject != null &&
        Player.gameObject.activeInHierarchy;
}
