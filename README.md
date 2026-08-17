# Ramblers

> [!WARNING]
> **Under construction:** Ramblers is not ready for use. All `0.x.x` versions are development builds; please wait for the `1.0.0` release before installing or trying it.

An experimental host-side companion mod for [Big Walk](https://store.steampowered.com/app/1478500/). Ramblers creates AI-controlled party members inside the host's game process without requiring or launching additional clients.

## Current state

Runtime tests have confirmed that the mod can:

- Spawn and register a connectionless copy of the real player prefab as a non-local player.
- Preserve the human player's camera, input, and local-player status.
- Drive the bot through Big Walk's stock remote-player motor.
- Follow a short human breadcrumb trail and steer around one bounded obstacle.
- Send game-microphone audio to OpenAI Realtime and execute allowlisted `follow` and `stay` tool calls on Unity's main thread.
- Use Big Walk's toggle-to-talk state near the bot to split consecutive utterances into bounded agent turns without a separate mod keybind.
- Reject speech when Big Walk's stock direct-voice attenuation curve reaches zero out of range.

This is still a bounded prototype. General navigation, stuck recovery, puzzle interactions, and voice heard by remote guests are not implemented. Local 3D synthetic voice output is implemented but not yet runtime-verified. Toggle-off suppression, overlapping-response serialization, noise robustness, and radio routing remain runtime-unverified or unresolved.

## Constraints

- **Host mod only:** no second Big Walk client or process.
- **Reversible:** the mod uses BepInEx and in-memory Harmony hooks. It does not rewrite the game executable, IL2CPP files, assets, saves, or metadata.
- **Evidence-based:** compilation and static inspection do not count as runtime proof.

## Compatibility

Ramblers `0.6.1` is tested with Big Walk `1.4.9` (build `2608141617`) and BepInEx IL2CPP `6.0.0-be.755`. Other game versions are unverified.

## Build and run

Requirements:

- BepInEx IL2CPP `6.0.0-be.755`, initialized once so its interop assemblies exist.
- Windows PowerShell 5.1 or newer.

From the repository root, build with PowerShell:

```powershell
.\build.ps1
```

The build discovers Big Walk across registered Steam libraries. On first use it downloads the pinned official `Microsoft.Net.Compilers.Toolset 4.14.0` NuGet package into the ignored `.tools/` directory and verifies its SHA-256 before extraction. It does not install software or change `PATH`, the registry, or system files.

For a non-Steam or otherwise custom setup, pass paths explicitly:

```powershell
.\build.ps1 -GamePath "D:\Games\Big Walk"
.\build.ps1 -CompilerPath "D:\Tools\Roslyn\csc.exe"
```

`RAMBLERS_GAME_PATH` and `RAMBLERS_CSC_PATH` provide equivalent environment-variable overrides. Use `-NoRestore` when the build must stay offline and fail if the compiler is not already available.

With Big Walk closed, copy `probe/build/BigWalkBotProbe.dll` into the game's `BepInEx/plugins/BigWalkBotProbe` directory. Rename the deployed DLL to `BigWalkBotProbe.dll.disabled` to prevent it from loading.

The Realtime integration reads `OPENAI_API_KEY` from the process or current Windows user environment. The key is not stored in this repository or the BepInEx configuration. This local-key path is for development only.

## Architecture

The model chooses from a small tool allowlist; it never writes movement input or touches Unity objects directly.

- [`probe/BigWalkBotProbe.cs`](probe/BigWalkBotProbe.cs) — host-only spawn/authority adapters and the deterministic `BotController` for breadcrumb following.
- [`probe/GameVoiceInput.cs`](probe/GameVoiceInput.cs) — Big Walk toggle/hold state, existing microphone capture, direct-voice attenuation, and bounded PCM turns.
- [`probe/GameVoiceOutput.cs`](probe/GameVoiceOutput.cs) — completed Realtime PCM utterances played by a local 3D audio source attached to the companion body.
- [`probe/OpenAIRealtimeBridge.cs`](probe/OpenAIRealtimeBridge.cs) — thin Unity-main-thread lifecycle coordinator.
- [`probe/AgentToolRouter.cs`](probe/AgentToolRouter.cs) — exact tool allowlist, argument validation, and dispatch to the controller.
- [`probe/OpenAIRealtimeClient.cs`](probe/OpenAIRealtimeClient.cs) — managed WebSocket/JSON/PCM transport with no Unity access.
- [`build.ps1`](build.ps1) — portable compiler provisioning, Steam discovery, dependency validation, and compilation.

The data path is: Big Walk voice state and microphone → bounded audio turn → OpenAI Realtime → either a validated tool call or model audio → deterministic bot controller or the separate game-voice output adapter. The first voice-output route is local-only and does not send synthetic speech to remote guests.

## Evidence and development rules

- [`docs/EXPERIMENTS.md`](docs/EXPERIMENTS.md) records active runtime evidence after the archived probe history.
- [`docs/archive/PROBE_HISTORY_0.2.0-0.5.2.md`](docs/archive/PROBE_HISTORY_0.2.0-0.5.2.md) contains the earlier detailed experiments.
- [`docs/archive/HOST_MOD_FEASIBILITY.md`](docs/archive/HOST_MOD_FEASIBILITY.md) contains the original host-only feasibility evidence.
- Keep frame-level movement deterministic and touch Unity/game objects only on Unity's main thread.
- Compilation and static inspection are not runtime proof; promote capabilities only after recording exact runtime evidence.

## Next milestone

Runtime-verify local 3D synthetic speech from Rambler's body. Radio and remote-guest speech remain separate later milestones. Work beyond that is intentionally not committed as a roadmap.
