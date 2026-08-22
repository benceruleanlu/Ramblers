using System;
using System.IO;
using System.Security.Cryptography;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;

namespace Ramblers;

[BepInPlugin(Guid, Name, Version)]
public sealed class Plugin : BasePlugin
{
    public const string Guid = "local.bigwalk.ramblers";
    public const string Name = "Ramblers";
    public const string Version = "0.13.2";

    internal static ManualLogSource Logger = null;
    internal static ConfigEntry<bool> EnableRealtimeAgent = null;
    internal static ConfigEntry<string> OpenAIRealtimeModel = null;

    public override void Load()
    {
        Logger = Log;
        EnableRealtimeAgent = Config.Bind(
            "OpenAI",
            "Enabled",
            true,
            "Connect to the OpenAI Realtime API when OPENAI_API_KEY is present. " +
            "Listening follows Big Walk's voice controls and direct-voice audibility.");
        OpenAIRealtimeModel = Config.Bind(
            "OpenAI",
            "Model",
            "gpt-realtime-2.1",
            "Realtime model ID. Keep the documented default unless deliberately testing another model.");
        ClassInjector.RegisterTypeInIl2Cpp<CompanionController>();
        ClassInjector.RegisterTypeInIl2Cpp<RealtimeAgentBridge>();

        var harmony = new Harmony(Guid);
        harmony.PatchAll(typeof(PlayerNetworkingStartPatch));
        harmony.PatchAll(typeof(HouseNetworkTransformIsOwnedPatch));
        harmony.PatchAll(typeof(HouseNetworkTransformIsRestingPatch));

        AddComponent<CompanionController>();
        AddComponent<RealtimeAgentBridge>();
        var assemblySha256 = ResolveAssemblySha256();
        Logger.LogInfo(
            $"[RAMBLERS] Loaded version {Version}, " +
            $"assemblySha256={assemblySha256}. " +
            "Waiting for a host session and local player.");
    }

    private static string ResolveAssemblySha256()
    {
        try
        {
            var path = typeof(Plugin).Assembly.Location;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return "unavailable";
            using var stream = File.OpenRead(path);
            using var sha256 = SHA256.Create();
            return BitConverter.ToString(sha256.ComputeHash(stream))
                .Replace("-", string.Empty);
        }
        catch (Exception exception)
        {
            Logger.LogWarning(
                $"[RAMBLERS] Assembly identity unavailable: {exception.Message}");
            return "unavailable";
        }
    }
}
