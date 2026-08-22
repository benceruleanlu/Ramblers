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
    public const string Version = "0.13.1";

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
        Logger.LogInfo(
            $"[RAMBLERS] Loaded version {Version}. Waiting for a host session and local player.");
    }
}
