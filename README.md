# Ramblers

> [!WARNING]
> **Under construction:** Ramblers is not ready for use. All `0.x.x` versions are development builds; please wait for the `1.0.0` release before installing or trying it.

An experimental host-side companion mod for [Big Walk](https://store.steampowered.com/app/1478500/). Ramblers spawns an AI-controlled party member inside the host's own game process — no second client, no second process. You talk to it using Big Walk's stock toggle-to-talk, and it walks itself using Big Walk's stock remote-player motor.

## Status

A companion spawns, keeps its own identity, and follows you on foot around bounded obstacles, leaving your camera, input, and local-player status untouched. Speech reaches OpenAI Realtime through the game's existing microphone path and comes back as calls into a small action allowlist. It only listens while you are close enough that Big Walk's own voice falloff has not already silenced you.

Not working yet: general navigation, stuck recovery, puzzle interaction, and speech that remote guests can hear. Synthetic voice plays from a runtime-verified local 3D source on the companion's body.

Near-field noise reduction, automatic semantic VAD, WebSocket interruption and truncation, and serialized response creation are implemented and runtime-verified in the `0.8.0` baseline.

The `0.8.0` baseline also includes runtime-verified standing, crouching, sitting, and one grounded jump as model-selected actions. Sitting suspends locomotion without erasing a follow request; standing resumes it.

The `0.9.0` source adds `inspect_reference()`. After the model selects it, the companion briefly looks toward the local player, follows the player's camera ray to the referenced point, turns toward it, and captures one bot-eye image. Ramblers keeps the snapshot in process memory, does not write it to local disk, sends it to the existing OpenAI Realtime conversation as PNG, and does not broadcast it to remote guests. This path is runtime-verified as of `0.12.0`: the companion turned to a raycast-latched reference, captured a 640×360 PNG, and the model described the scene from it. The capture killed the game process on its first attempt in `0.9.0`; see [Stripped Unity APIs](#stripped-unity-apis).

The `0.10.0` source turns multi-frame actions into a declared job layer. Each action states which companion capabilities it claims — locomotion, gaze, hands — and the coordinator admits a job only when they are free, so actions no longer need a hand-written exclusion check against every other action. Gaze is arbitrated on priority channels rather than per-behaviour fields. `cancel_action()` is added on top of that layer: it stops any running job, drops a queued jump, and clears any follow intent, without changing posture.

## Compatibility

Tested against Big Walk `1.4.9` (build `2608141617`) on BepInEx IL2CPP `6.0.0-be.755`, which is Unity `6000.3.17f1` on URP. Ramblers `0.7.5`, `0.8.0`, and `0.12.0` are runtime-verified on that combination. `0.12.0` verification covers follow, posture, jump, the job layer's capability arbitration, deferred tool dispatch with the microphone epoch barrier, and one full `inspect_reference()` producing a described image. `cancel_action()` is still compile-verified only. Other game or loader versions are unverified, and the API survey below is specific to this build of the game rather than to Unity 6.

## Stripped Unity APIs

Big Walk ships a managed-stripped IL2CPP build, and BepInEx generates its interop assemblies from the full Unity API surface. A Unity method the game itself never calls therefore still compiles against those assemblies and is simply absent at runtime, where Il2CppInterop resolves a null method pointer and the failure path corrupts memory. The process dies on an access violation before any `catch` runs — compiling proves nothing, and neither does a `try`/`catch`.

This is what killed `0.9.0`: a single line setting `Camera.stereoTargetEye`, whose setter is stripped while its getter survives. Three more were waiting behind it.

| API | Consequence if used |
| --- | --- |
| `Camera.stereoTargetEye` setter | the `0.9.0` crash |
| `RenderPipeline.SupportsRenderRequest` | the natural guard for the render call would crash identically |
| `ImageConversion.EncodeToJPG` / `EncodeToPNG` / `LoadImage` | capture would die at encode after a successful render |
| `Camera.CopyFrom` | silently does nothing, so the capture camera keeps Unity's defaults |

The capture therefore uses none of them: no stereo settings, an unguarded `RenderPipeline.SubmitRenderRequest` in place of `Camera.Render()` (a built-in-pipeline entry point, unsupported under URP), a managed PNG encoder over `System.IO.Compression`, and Unity's default camera configuration instead of `CopyFrom`.

`UnityApiProbe` asks the IL2CPP runtime whether each dependency exists. Only four are treated as required — `SubmitRenderRequest`, `Camera.set_targetTexture`, `Texture2D.ReadPixels`, and `GetRawTextureData` — so a missing optional API degrades the image and logs `[VISION] DEGRADED` rather than disabling the capability. Any future capability reaching for an uncommon Unity API should probe it the same way.

**Do not survey the API surface by string-searching `global-metadata.dat`.** IL2CPP stores method names unqualified, so a bare name cannot be attributed to a type — `CopyFrom` occurs 28 times there and none of the hits are `Camera`'s. Use `UnityApiProbe.DescribeType`, which enumerates a type's methods through the runtime; a non-zero method count also proves the type resolved, separating a real strip from a failed class lookup.

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

The current model-facing surface is `set_follow_mode(follow | stay)`, `set_posture(standing | crouching | sitting)`, `jump()`, `inspect_reference()`, and `cancel_action()`. Tool arguments are validated before Unity is touched, and multiple tool outputs are returned before one continuation response is requested.

Actions that cannot finish inside a single tool call are jobs. A job declares the capabilities it claims and reports a terminal result plus any conversation items it wants delivered — a text report, an image, or both — which is how a bot-eye snapshot reaches the model without the WebSocket transport knowing what produced it. Persistent follow intent and posture stay outside that layer as long-lived state rather than jobs.

Earlier probe experiments and the original host-only feasibility work are in [`docs/archive/`](docs/archive/).
