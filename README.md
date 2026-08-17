# Big Walk AI Teammate

An experimental host-side mod that creates an AI-controlled second player inside a [Big Walk](https://store.steampowered.com/app/1478500/) session. It runs in the host's game process; it does not require or launch a second client.

## Current state

Runtime tests have confirmed that the mod can:

- Spawn and register a connectionless copy of the real player prefab as a non-local player.
- Preserve the human player's camera, input, and local-player status.
- Drive the bot through Big Walk's stock remote-player motor.
- Follow a short human breadcrumb trail and steer around one bounded obstacle.
- Send game-microphone audio to OpenAI Realtime and execute allowlisted `follow` and `stay` tool calls on Unity's main thread.

This is still a bounded prototype. General navigation, stuck recovery, puzzle interactions, synthetic voice output, and voice heard by remote guests are not implemented. The native Big Walk voice-control and proximity adapter is still undergoing runtime validation.

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

With Big Walk closed, copy `probe/build/BigWalkBotProbe.dll` into the game's `BepInEx/plugins` directory. Rename the deployed DLL to `BigWalkBotProbe.dll.disabled` to prevent it from loading.

The Realtime integration reads `OPENAI_API_KEY` from the process or current Windows user environment. The key is not stored in this repository or the BepInEx configuration. This local-key path is for development only.

## Code map

- [`probe/BigWalkBotProbe.cs`](probe/BigWalkBotProbe.cs) — spawn, authority, motor control, and breadcrumb following.
- [`probe/OpenAIRealtimeBridge.cs`](probe/OpenAIRealtimeBridge.cs) — microphone, Realtime session, voice gating, and tool-call boundary.
- [`probe/build/compile.rsp`](probe/build/compile.rsp) — compiler inputs and game references.
- [`docs/archive/PROBE_HISTORY_0.2.0-0.5.2.md`](docs/archive/PROBE_HISTORY_0.2.0-0.5.2.md) — detailed experiment history.
- [`docs/archive/HOST_MOD_FEASIBILITY.md`](docs/archive/HOST_MOD_FEASIBILITY.md) — original feasibility evidence and recovered-game findings.

## Development rules

- Keep frame-level movement deterministic; the model chooses bounded tools rather than writing motor input.
- Touch Unity and game objects only on the Unity main thread.
- Record exact runtime evidence in the experiment history before promoting a capability to the confirmed list above.

## Next milestone

Runtime-verify the native Big Walk voice turn boundary and direct-proximity gate. Work beyond that is intentionally not committed as a roadmap.
