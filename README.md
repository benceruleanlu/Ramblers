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

This is still a bounded prototype. General navigation, stuck recovery, puzzle interactions, synthetic voice output, and voice heard by remote guests are not implemented. Toggle-off suppression, out-of-range rejection, noise robustness, and radio routing remain runtime-unverified.

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
