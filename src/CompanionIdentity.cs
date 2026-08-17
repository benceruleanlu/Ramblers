using Dissonance.Integrations.MirrorIgnorance;
using HarmonyLib;
using LobbyNetworking;

namespace Ramblers;

/// <summary>
/// How a companion body is recognised, and the synthetic identity it is given in
/// place of the connection-derived one a real player would carry.
/// </summary>
internal static class CompanionIdentity
{
    public const string ObjectName = "__RamblersHostCompanion";
    public const string NetworkIdentifier = "ramblers:companion:rambler";

    private const string Username = "Rambler";
    private const string ModerationName = "Rambler";
    private const string VoicePlayerId = "RamblerHost";

    public static bool IsBot(PlayerNetworking networking)
    {
        var gameObject = networking == null ? null : networking.gameObject;
        return networking != null &&
               (networking.Networkidentifier == NetworkIdentifier ||
                (gameObject != null && gameObject.name == ObjectName));
    }

    /// <summary>
    /// Assigned before NetworkServer.Spawn, because the stock server-side path
    /// derives these from a connection the companion deliberately does not have.
    /// </summary>
    public static void Apply(
        PlayerNetworking networking,
        MirrorIgnorancePlayer voiceIdentity)
    {
        networking.Networkusername = Username;
        networking.Networkidentifier = NetworkIdentifier;
        networking.NetworkmoderationName = ModerationName;
        networking.NetworkuserPlatformId = 0;
        networking.NetworkepicUserId = string.Empty;
        networking.NetworkisHost = false;

        if (voiceIdentity != null)
            voiceIdentity.Network_playerId = VoicePlayerId;
    }
}

[HarmonyPatch(typeof(PlayerNetworking), "Start")]
internal static class PlayerNetworkingStartPatch
{
    private static bool Prefix(PlayerNetworking __instance)
    {
        if (!CompanionIdentity.IsBot(__instance))
            return true;

        // The stock server-side Start path assumes connectionToClient and its
        // authenticationData are non-null. A server-owned bot intentionally has
        // neither, so identity is initialized before NetworkServer.Spawn instead.
        Plugin.Logger.LogInfo(
            "[RAMBLERS] Bypassed connection-dependent PlayerNetworking.Start for companion.");
        return false;
    }
}

[HarmonyPatch(typeof(HouseNetworkTransform), "get_isOwned")]
internal static class HouseNetworkTransformIsOwnedPatch
{
    private static void Postfix(HouseNetworkTransform __instance, ref bool __result)
    {
        if (__result || __instance == null)
            return;

        var networking = __instance.GetComponent<PlayerNetworking>();
        if (CompanionIdentity.IsBot(networking))
            __result = true;
    }
}

[HarmonyPatch(typeof(HouseNetworkTransform), "get_IsRestingForPlayerMovement")]
internal static class HouseNetworkTransformIsRestingPatch
{
    private static void Postfix(HouseNetworkTransform __instance, ref bool __result)
    {
        if (!__result || __instance == null)
            return;

        // A connectionless player never receives the normal client interpolation
        // goal, so the stock getter reports it as permanently resting and
        // PlayerMover zeros the otherwise valid controlsVelocity. The bot is
        // server-owned and driven directly by the host, so that remote-only gate
        // does not apply.
        var networking = __instance.GetComponent<PlayerNetworking>();
        if (CompanionIdentity.IsBot(networking))
            __result = false;
    }
}
