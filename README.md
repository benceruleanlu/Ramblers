# Ramblers

> [!WARNING]
> **Under construction:** Ramblers is not ready for use. All `0.x.x` versions are development builds; please wait for the `1.0.0` release before installing or trying it.

An AI companion mod for [Big Walk](https://bigwalk.game/) by House House.

The goal is to play the whole game with Rambler as your teammate: exploring together, communicating, and working through puzzles without having to direct its every move.

Follow development in [GitHub issues](https://github.com/benceruleanlu/Ramblers/issues).

## Setup

You need Windows, a BepInEx IL2CPP installation for Big Walk, PowerShell, and your own OpenAI API key. Launch the game with BepInEx once to generate its interop assemblies before building.

1. Set `OPENAI_API_KEY` in the game's process environment or your Windows user environment before launching the game. Do not put the key in the repository.
2. Build from the repository root:

   ```powershell
   .\build.ps1
   ```

3. With Big Walk closed, copy `dist/Ramblers.dll` and `dist/StbImageWriteSharp.dll` into `BepInEx/plugins/Ramblers` under the game directory.
4. Launch Big Walk and host a session. Use the game's voice controls to talk to Rambler.

To disable the mod, close the game and rename the installed `Ramblers.dll` to `Ramblers.dll.disabled`.

Ramblers uses OpenAI Realtime. Microphone audio, captured images, and game context are sent to OpenAI, and API usage is billed to your account.

## Development

See [build.ps1](build.ps1) for build options and prerequisites. To run the protocol checks with PowerShell 7 after building:

```powershell
pwsh -NoProfile -File .\analysis_scripts\Run-ProtocolTests.ps1
```

[Audit-LatestRun.ps1](analysis_scripts/Audit-LatestRun.ps1) rebuilds `dist/` and checks the build, installed plugin, and latest runtime log.
