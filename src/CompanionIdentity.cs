using Dissonance.Integrations.MirrorIgnorance;
using HarmonyLib;
using LobbyNetworking;

namespace Ramblers;

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

        var networking = __instance.GetComponent<PlayerNetworking>();
        if (CompanionIdentity.IsBot(networking))
            __result = false;
    }
}
