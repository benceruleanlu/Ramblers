using LobbyNetworking;
using Mirror;
using UnityEngine;

namespace Ramblers;

internal sealed class CompanionBody
{
    internal CompanionBody(
        GameObject gameObject,
        PlayerCharacter character,
        PlayerNetworking networking,
        NetworkIdentity identity,
        HouseNetworkTransform networkTransform)
    {
        GameObject = gameObject;
        Character = character;
        Networking = networking;
        Identity = identity;
        NetworkTransform = networkTransform;
    }

    internal GameObject GameObject { get; }
    internal PlayerCharacter Character { get; }
    internal PlayerNetworking Networking { get; }
    internal NetworkIdentity Identity { get; }
    internal HouseNetworkTransform NetworkTransform { get; }

    internal bool IsAlive =>
        GameObject != null && Character != null && Networking != null;

    internal Transform Transform => GameObject.transform;

    internal Vector3 Position => GameObject.transform.position;

    internal Vector3 HeadPosition => HeadPositionOf(Character);

    internal bool Contains(Transform candidate)
    {
        var root = Transform;
        return candidate != null && root != null &&
               (candidate == root || candidate.IsChildOf(root));
    }

    internal static Vector3 HeadPositionOf(PlayerCharacter character)
    {
        return character.cameraTransform == null
            ? character.transform.position + Vector3.up * 1.5f
            : character.cameraTransform.position;
    }
}
