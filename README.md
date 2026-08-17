# Big Walk AI Teammate

An experimental host-side mod that creates an AI-controlled second player inside a [Big Walk](https://store.steampowered.com/app/1478500/) session. It runs in the host's game process; it does not require or launch a second client.

## Current state

Runtime tests have confirmed that the mod can:

- Spawn and register a connectionless copy of the real player prefab as a non-local player.
- Preserve the human player's camera, input, and local-player status.
- Drive the bot through Big Walk's stock remote-player motor.
- Follow a short human breadcrumb trail and steer around one bounded obstacle.
- Send game-microphone audio to OpenAI Realtime and execute allowlisted `follow` and `stay` tool calls on Unity's main thread.
- Use Big Walk's toggle-to-talk state near the bot to split consecutive utterances into bounded agent turns without a separate mod keybind.
- Reject speech when Big Walk's stock direct-voice attenuation curve reaches zero out of range.

This is still a bounded prototype. General navigation, stuck recovery, puzzle interactions, synthetic voice output, and voice heard by remote guests are not implemented. Toggle-off suppression, overlapping-response serialization, noise robustness, and radio routing remain runtime-unverified or unresolved.

## Constraints

- **Host mod only:** no second Big Walk client or process.
- **Reversible:** the mod uses BepInEx and in-memory Harmony hooks. It does not rewrite the game executable, IL2CPP files, assets, saves, or metadata.
- **Evidence-based:** compilation and static inspection do not count as runtime proof.

## Build and run

The current response file expects:

- Big Walk in the default Steam installation path.
- BepInEx IL2CPP `6.0.0-be.755`, initialized once so its interop assemblies exist.
- Roslyn `4.14.0` under `.tools/roslyn-4.14.0/expanded/`.

If the game or repository is elsewhere, update the absolute paths in `probe/build/compile.rsp`. From the repository root, build with PowerShell:

```powershell
& ".\.tools\roslyn-4.14.0\expanded\tasks\net472\csc.exe" "@probe\build\compile.rsp"
```

With Big Walk closed, copy `probe/build/BigWalkBotProbe.dll` into the game's `BepInEx/plugins/BigWalkBotProbe` directory. Rename the deployed DLL to `BigWalkBotProbe.dll.disabled` to prevent it from loading.

The Realtime integration reads `OPENAI_API_KEY` from the process or current Windows user environment. The key is not stored in this repository or the BepInEx configuration. This local-key path is for development only.

## Architecture

The model chooses from a small tool allowlist; it never writes movement input or touches Unity objects directly.

- [`probe/BigWalkBotProbe.cs`](probe/BigWalkBotProbe.cs) — host-only spawn/authority adapters and the deterministic `BotController` for breadcrumb following.
- [`probe/GameVoiceInput.cs`](probe/GameVoiceInput.cs) — Big Walk toggle/hold state, existing microphone capture, direct-voice attenuation, and bounded PCM turns.
- [`probe/OpenAIRealtimeBridge.cs`](probe/OpenAIRealtimeBridge.cs) — thin Unity-main-thread lifecycle coordinator.
- [`probe/AgentToolRouter.cs`](probe/AgentToolRouter.cs) — exact tool allowlist, argument validation, and dispatch to the controller.
- [`probe/OpenAIRealtimeClient.cs`](probe/OpenAIRealtimeClient.cs) — managed WebSocket/JSON/PCM transport with no Unity access.
- [`probe/build/compile.rsp`](probe/build/compile.rsp) — compiler inputs and game references.

The data path is: Big Walk voice state and microphone → bounded audio turn → OpenAI Realtime → validated tool call → deterministic bot controller. Future speech output belongs in a separate game-voice output adapter so the model transport does not depend on a particular in-game audio route.

## Evidence and development rules

- [`docs/EXPERIMENTS.md`](docs/EXPERIMENTS.md) records active runtime evidence after the archived probe history.
- [`docs/archive/PROBE_HISTORY_0.2.0-0.5.2.md`](docs/archive/PROBE_HISTORY_0.2.0-0.5.2.md) contains the earlier detailed experiments.
- [`docs/archive/HOST_MOD_FEASIBILITY.md`](docs/archive/HOST_MOD_FEASIBILITY.md) contains the original host-only feasibility evidence.
- Keep frame-level movement deterministic and touch Unity/game objects only on Unity's main thread.
- Compilation and static inspection are not runtime proof; promote capabilities only after recording exact runtime evidence.

## Next milestone

Runtime-verify toggle-off suppression and serialize overlapping model responses before adding radio or synthetic speech output. Work beyond that is intentionally not committed as a roadmap.
