using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Dissonance.Integrations.MirrorIgnorance;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using LobbyNetworking;
using Mirror;
using UnityEngine;

namespace BigWalkBotProbe;

[BepInPlugin(Guid, Name, Version)]
public sealed class Plugin : BasePlugin
{
    public const string Guid = "local.bigwalk.botprobe";
    public const string Name = "Big Walk Bot Feasibility Probe";
    public const string Version = "0.2.0";

    internal static ManualLogSource Logger = null;

    public override void Load()
    {
        Logger = Log;
        ClassInjector.RegisterTypeInIl2Cpp<ProbeRunner>();

        var harmony = new Harmony(Guid);
        harmony.PatchAll(typeof(PlayerNetworkingStartPatch));
        harmony.PatchAll(typeof(HouseNetworkTransformIsOwnedPatch));

        AddComponent<ProbeRunner>();
        Logger.LogInfo("[BOT-PROBE] Loaded. Waiting for a host session and local player.");
    }
}

internal static class ProbeIdentity
{
    public const string ObjectName = "__NitrogenHostBotProbe";
    public const string NetworkIdentifier = "local-bot:nitrogen";

    public static bool IsBot(PlayerNetworking networking)
    {
        var gameObject = networking == null ? null : networking.gameObject;
        return networking != null &&
               (networking.Networkidentifier == NetworkIdentifier ||
                (gameObject != null && gameObject.name == ObjectName));
    }
}

[HarmonyPatch(typeof(PlayerNetworking), "Start")]
internal static class PlayerNetworkingStartPatch
{
    private static bool Prefix(PlayerNetworking __instance)
    {
        if (!ProbeIdentity.IsBot(__instance))
            return true;

        // The stock server-side Start path assumes connectionToClient and its
        // authenticationData are non-null. A server-owned bot intentionally has
        // neither, so identity is initialized before NetworkServer.Spawn instead.
        Plugin.Logger.LogInfo(
            "[BOT-PROBE] Bypassed connection-dependent PlayerNetworking.Start for bot.");
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
        if (ProbeIdentity.IsBot(networking))
            __result = true;
    }
}

internal sealed class ProbeRunner : MonoBehaviour
{
    private GameObject _bot;
    private float _nextPoll;
    private float _verifyAt;
    private bool _verificationLogged;

    public ProbeRunner(IntPtr pointer) : base(pointer)
    {
    }

    private void Update()
    {
        if (_bot != null)
        {
            if (!_verificationLogged && Time.realtimeSinceStartup >= _verifyAt)
                LogVerification();
            return;
        }

        if (Time.realtimeSinceStartup < _nextPoll)
            return;

        _nextPoll = Time.realtimeSinceStartup + 1f;

        if (!NetworkServer.active)
            return;

        var manager = NetworkManager.singleton;
        var localPlayer = WorldManager.localPlayerCharacter;
        if (manager == null || manager.playerPrefab == null || localPlayer == null)
            return;

        TrySpawn(manager, localPlayer);
    }

    private void TrySpawn(NetworkManager manager, PlayerCharacter localPlayer)
    {
        try
        {
            var position = localPlayer.transform.position
                         + localPlayer.transform.right * 2f
                         + Vector3.up * 0.25f;

            _bot = UnityEngine.Object.Instantiate(
                manager.playerPrefab,
                position,
                localPlayer.transform.rotation);
            _bot.name = ProbeIdentity.ObjectName;

            var playerCharacter = _bot.GetComponent<PlayerCharacter>();
            var playerNetworking = _bot.GetComponent<PlayerNetworking>();
            var networkIdentity = _bot.GetComponent<NetworkIdentity>();
            var networkTransform = _bot.GetComponent<HouseNetworkTransform>();
            var voiceIdentity = _bot.GetComponent<MirrorIgnorancePlayer>();

            if (playerCharacter == null || playerNetworking == null ||
                networkIdentity == null || networkTransform == null)
            {
                throw new InvalidOperationException(
                    "The configured playerPrefab is missing a required player/network component.");
            }

            SetSyntheticIdentity(playerNetworking, voiceIdentity);
            NetworkServer.Spawn(_bot);

            _verifyAt = Time.realtimeSinceStartup + 2f;
            Plugin.Logger.LogInfo(
                $"[BOT-PROBE] Spawn requested: netId={networkIdentity.netId}, " +
                $"connectionToClient={(networkIdentity.connectionToClient == null ? "null" : "non-null")}, " +
                $"position={position}.");
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogError($"[BOT-PROBE] Spawn failed: {exception}");
            if (_bot != null)
                UnityEngine.Object.Destroy(_bot);
            _bot = null;
        }
    }

    private static void SetSyntheticIdentity(
        PlayerNetworking networking,
        MirrorIgnorancePlayer voiceIdentity)
    {
        var username = "Nitrogen";
        var identifier = ProbeIdentity.NetworkIdentifier;
        var moderationName = "Nitrogen";
        var epicUserId = string.Empty;
        ulong platformUserId = 0;
        var isHost = false;

        networking.Networkusername = username;
        networking.Networkidentifier = identifier;
        networking.NetworkmoderationName = moderationName;
        networking.NetworkuserPlatformId = platformUserId;
        networking.NetworkepicUserId = epicUserId;
        networking.NetworkisHost = isHost;

        if (voiceIdentity != null)
        {
            var voicePlayerId = "NitrogenHostBot";
            voiceIdentity.Network_playerId = voicePlayerId;
        }
    }

    private void LogVerification()
    {
        _verificationLogged = true;

        if (_bot == null)
            return;

        var playerCharacter = _bot.GetComponent<PlayerCharacter>();
        var networking = _bot.GetComponent<PlayerNetworking>();
        var identity = _bot.GetComponent<NetworkIdentity>();
        var networkTransform = _bot.GetComponent<HouseNetworkTransform>();
        var voiceIdentity = _bot.GetComponent<MirrorIgnorancePlayer>();

        var registeredPlayers = PlayerCharacter.allPlayerCharacters == null
            ? -1
            : PlayerCharacter.allPlayerCharacters.Count;

        Plugin.Logger.LogInfo(
            "[BOT-PROBE] VERIFY " +
            $"netId={identity?.netId ?? 0}, " +
            $"isServer={networking?.isServer}, " +
            $"isClient={networking?.isClient}, " +
            $"isLocalPlayer={networking?.isLocalPlayer}, " +
            $"serverOwnsTransform={networkTransform?.isOwned}, " +
            $"connectionToClient={(identity?.connectionToClient == null ? "null" : "non-null")}, " +
            $"registeredPlayerCharacters={registeredPlayers}, " +
            $"voicePlayerId={voiceIdentity?.PlayerId ?? "<none>"}, " +
            $"voiceTracking={voiceIdentity?.IsTracking}, " +
            $"playerCharacterPresent={playerCharacter != null}.");
    }
}
