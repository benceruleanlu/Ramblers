# Ramblers

> [!WARNING]
> **Under construction:** Ramblers is not ready for use. All `0.x.x` versions are development builds; please wait for the `1.0.0` release before installing or trying it.

An experimental host-side companion mod for [Big Walk](https://store.steampowered.com/app/1478500/). Ramblers spawns an AI-controlled party member inside the host's own game process — no second client, no second process. You talk to it using Big Walk's stock toggle-to-talk, and it walks itself using Big Walk's stock remote-player motor.

## Status

A companion spawns, keeps its own identity, and follows you on foot around bounded obstacles, leaving your camera, input, and local-player status untouched. Speech reaches OpenAI Realtime through the game's existing microphone path and comes back as calls into a small action allowlist. It only listens while you are close enough that Big Walk's own voice falloff has not already silenced you.

Not working yet: general navigation, stuck recovery, puzzle interaction, and speech that remote guests can hear. Synthetic voice plays from a runtime-verified local 3D source on the companion's body.

Near-field noise reduction, automatic semantic VAD, WebSocket interruption and truncation, and serialized response creation are implemented and runtime-verified in the `0.8.0` baseline.

The `0.8.0` baseline also includes runtime-verified standing, crouching, sitting, and one grounded jump as model-selected actions. Sitting suspends locomotion without erasing a follow request; standing resumes it.

The `0.9.0` source adds `inspect_reference()`. After the model selects it, the companion briefly looks toward the local player, follows the player's camera ray to the referenced point, turns toward it, and captures one bot-eye image. Ramblers keeps the snapshot in process memory, does not write it to local disk, sends it to the existing OpenAI Realtime conversation, and does not broadcast it to remote guests. This inspection path is implemented but not yet runtime-verified.

## Compatibility

Tested against Big Walk `1.4.9` (build `2608141617`) on BepInEx IL2CPP `6.0.0-be.755`. Ramblers `0.7.5` and `0.8.0` are runtime-verified on that combination; `0.8.0` is the current verified baseline. The local `0.9.0` source targets the same bindings, but its new visual inspection path awaits runtime verification. Other game or loader versions are unverified.

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

Big Walk voice state and microphone → a continuous semantic-VAD stream or manual push-to-talk turn → OpenAI Realtime → a validated tool call or model audio → the companion controller, or local 3D playback from the companion's body. Visual inspection takes a separate deferred path: the tool call drives the companion's attention, a bot-eye snapshot is returned to the same Realtime conversation, and only then is one continuation response requested. Synthetic speech is local-only and does not reach remote guests.

The current model-facing surface is `set_follow_mode(follow | stay)`, `set_posture(standing | crouching | sitting)`, `jump()`, and `inspect_reference()`. Typed C# components arbitrate persistent follow intent, posture, transient jump requests, and temporary visual attention. Tool arguments are validated before Unity is touched, and multiple tool outputs are returned before one continuation response is requested.

Earlier probe experiments and the original host-only feasibility work are in [`docs/archive/`](docs/archive/).
