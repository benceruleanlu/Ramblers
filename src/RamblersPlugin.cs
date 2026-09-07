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
    public const string Version = "0.20.4";

    internal static ManualLogSource Logger = null;
    internal static ConfigEntry<bool> EnableRealtimeAgent = null;
    internal static ConfigEntry<string> OpenAIRealtimeModel = null;
    internal static ConfigEntry<bool> EnableCraneShortcut = null;
    internal static ConfigEntry<bool> EnableFollowDiagnostics = null;
    internal static bool FollowDiagnosticsEnabled => EnableFollowDiagnostics?.Value == true;
    internal static ConfigEntry<bool> EnableNavigationRepro = null;
    internal static bool NavigationReproEnabled => EnableNavigationRepro?.Value == true;

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
        EnableCraneShortcut = Config.Bind(
            "Development",
            "EnableCraneShortcut",
            false,
            "Enable F8 to move the host and Rambler beside the crane puzzle for testing.");
        Logger.LogInfo($"[QA] CRANE_SHORTCUT enabled={EnableCraneShortcut.Value}, hotkey=F8.");
        EnableFollowDiagnostics = Config.Bind(
            "Diagnostics",
            "FollowNavigation",
            false,
            "Log detailed follow positions, route searches, geometry and native movement for testing.");
        Logger.LogInfo($"[FOLLOW] DIAGNOSTICS enabled={FollowDiagnosticsEnabled}.");
        EnableNavigationRepro = Config.Bind(
            "Development",
            "NavigationReproProbe",
            false,
            "Capture read-only navigation geometry at the recorded failure site after companion spawn.");
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
