# Ramblers

> [!WARNING]
> **Under construction:** Ramblers is not ready for use. All `0.x.x` versions are development builds; please wait for the `1.0.0` release before installing or trying it.

An experimental host-side companion mod for [Big Walk](https://store.steampowered.com/app/1478500/). Ramblers spawns an AI-controlled party member inside the host's own game process — no second client, no second process. You talk to it using Big Walk's stock toggle-to-talk, and it walks itself using Big Walk's stock remote-player motor.

## Status

A companion spawns, keeps its own identity, and follows you on foot around bounded obstacles, leaving your camera, input, and local-player status untouched. Speech reaches OpenAI Realtime through the game's existing microphone path and comes back as calls into a small action allowlist. It only listens while you are close enough that Big Walk's own voice falloff has not already silenced you.

Not working yet: general navigation, stuck recovery, puzzle interaction, and speech that remote guests can hear. Synthetic voice plays from a local 3D source on the companion's body, but that path has not been runtime-verified.

## Compatibility

Tested against Big Walk `1.4.9` (build `2608141617`) on BepInEx IL2CPP `6.0.0-be.755`. Other versions are unverified.

## Build

Requires Windows PowerShell 5.1 or newer and a BepInEx IL2CPP install that has been launched at least once, so its interop assemblies exist.

```powershell
.\build.ps1
```

The build locates Big Walk through your registered Steam libraries and downloads a pinned Roslyn compiler into `.tools/` on first use. It installs nothing system-wide and leaves `PATH`, the registry, and system files alone. Override either path with a flag or an environment variable:

| Flag | Environment variable |
| --- | --- |
| `-GamePath` | `RAMBLERS_GAME_PATH` |
| `-CompilerPath` | `RAMBLERS_CSC_PATH` |

Add `-NoRestore` to keep the build offline and fail if no compiler is already available.

## Install

With Big Walk closed, copy `dist/Ramblers.dll` into `BepInEx/plugins/Ramblers` under the game directory. Rename the deployed file to `Ramblers.dll.disabled` to stop it loading.

Ramblers reads `OPENAI_API_KEY` from the process or current Windows user environment. No key is stored in this repository or in the BepInEx configuration. This local-key path is for development only.

## Design

The model never writes movement input and never touches a Unity object. It selects from a fixed tool allowlist, and C# does the driving.

Big Walk voice state and microphone → bounded audio turn → OpenAI Realtime → a validated tool call or model audio → the companion controller, or local 3D playback from the companion's body. Synthetic speech is local-only and does not reach remote guests.

Earlier probe experiments and the original host-only feasibility work are in [`docs/archive/`](docs/archive/).
