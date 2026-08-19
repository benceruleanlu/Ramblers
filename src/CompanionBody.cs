using LobbyNetworking;
using Mirror;
using UnityEngine;

namespace Ramblers;

/// <summary>
/// The spawned companion's components, resolved once at spawn instead of being
/// re-queried from the GameObject by every behaviour. This is the handle each
/// behaviour is given, so none of them need to know how the body was created.
/// </summary>
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

    /// <summary>
    /// Whether a transform belongs to the companion's own hierarchy. Any cast
    /// that starts at the companion's head needs this to tell a self-hit from a
    /// real one.
    /// </summary>
    internal bool Contains(Transform candidate)
    {
        var root = Transform;
        return candidate != null && root != null &&
               (candidate == root || candidate.IsChildOf(root));
    }

    /// <summary>
    /// A player's eye position, falling back to a nominal standing eye height
    /// when the character has no camera transform.
    /// </summary>
    internal static Vector3 HeadPositionOf(PlayerCharacter character)
    {
        return character.cameraTransform == null
            ? character.transform.position + Vector3.up * 1.5f
            : character.cameraTransform.position;
    }
}
