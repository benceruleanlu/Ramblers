using LobbyNetworking;
using UnityEngine;

namespace Ramblers;

internal static class DevelopmentCraneShortcut
{
    private static readonly Vector3 HumanDestination =
        new Vector3(-288.25f, 4.55f, -597.87f);

    private static readonly Vector3 CompanionDestination =
        new Vector3(-287.05f, 5.05f, -596.97f);

    internal static bool TryApply(
        CompanionBody body,
        PlayerCharacter human,
        out string failure)
    {
        failure = null;
        if (body == null || !body.IsAlive || body.NetworkTransform == null)
        {
            failure = "companion_body_unavailable";
            return false;
        }

        if (human == null || human.gameObject == body.GameObject)
        {
            failure = "local_human_unavailable";
            return false;
        }

        var humanNetworking = human.GetComponent<PlayerNetworking>();
        var humanNetworkTransform = human.GetComponent<HouseNetworkTransform>();
        if (human.rb == null || human.mover == null ||
            humanNetworking == null || humanNetworkTransform == null ||
            !humanNetworking.isLocalPlayer || !humanNetworking.isServer)
        {
            failure = "local_human_reposition_capability_unavailable";
            return false;
        }

        if (body.Character.rb == null || body.Character.mover == null)
        {
            failure = "companion_reposition_capability_unavailable";
            return false;
        }

        RepositionPlayer(human, humanNetworking, humanNetworkTransform, HumanDestination);
        RepositionPlayer(body.Character, body.Networking, body.NetworkTransform, CompanionDestination);
        return true;
    }

    private static void RepositionPlayer(
        PlayerCharacter character,
        PlayerNetworking networking,
        HouseNetworkTransform networkTransform,
        Vector3 destination)
    {
        networking.NetworkcontrolsVelocity = Vector3.zero;
        character.rb.linearVelocity = Vector3.zero;
        character.rb.angularVelocity = Vector3.zero;
        character.mover.ResetPosition();
        character.rb.position = destination;
        character.transform.position = destination;
        networkTransform.targetPosition = destination;
        networkTransform.targetRotation = character.transform.rotation;
        character.rb.WakeUp();
    }
}
