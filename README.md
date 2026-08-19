# Ramblers

> [!WARNING]
> **Under construction:** Ramblers is not ready for use. All `0.x.x` versions are development builds; please wait for the `1.0.0` release before installing or trying it.

An experimental host-side companion mod for [Big Walk](https://store.steampowered.com/app/1478500/). Ramblers spawns an AI-controlled party member in follow mode inside the host's own game process — no second client, no second process. You talk to it using Big Walk's stock toggle-to-talk, and it walks itself using Big Walk's stock remote-player motor.

## Install

With Big Walk closed, copy `dist/Ramblers.dll` into `BepInEx/plugins/Ramblers` under the game directory. Rename the deployed file to `Ramblers.dll.disabled` to stop it loading.

Ramblers reads `OPENAI_API_KEY` from the process or current Windows user environment. No key is stored in this repository or in the BepInEx configuration. This local-key path is for development only.

## Compatibility

Tested against Big Walk `1.4.9` (build `2608141617`) on BepInEx IL2CPP `6.0.0-be.755`, which is Unity `6000.3.17f1` on URP. Ramblers `0.7.5`, `0.8.0`, and `0.12.0` are runtime-verified on that combination. `0.12.0` verification covers follow, posture, jump, the job layer's capability arbitration, deferred tool dispatch with the microphone epoch barrier, and one full `inspect_reference()` producing a described image. The current development candidate additionally has user-confirmed visual runtime proof for default follow, grounded exact-target pickup, and exact-target cancellation. Explicit `drop_item()` remains compile- and protocol-verified until its visible release is exercised in game. Other game or loader versions are unverified, and the survey in [Stripped Unity APIs](docs/STRIPPED_UNITY_APIS.md) is specific to this build of the game rather than to Unity 6.

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

## Design

The model never writes movement input and never touches a Unity object. It selects from a fixed tool allowlist, and C# does the driving. Arguments are validated into typed commands before anything Unity-side runs, so a malformed call comes back as a tool failure rather than as an exception inside the game loop.

The voice path reuses what Big Walk already has. The game's own voice state and microphone open the turn — a toggle starts a continuous semantic-VAD stream, a hold makes it a manual push-to-talk turn — and the audio goes to OpenAI Realtime. At the utterance boundary, any physical reference is frozen and bound to that exact response turn. What comes back is either speech, played from a local 3D source on the companion's body, or a tool call. Synthetic speech is local-only and never reaches remote guests. When one turn produces several tool calls, every result is returned before a single continuation response is requested, so one turn stays one reply.

The model-facing surface is deliberately small:

| Tool | Effect |
| --- | --- |
| `set_follow_mode(follow \| stay)` | Long-lived follow intent, walked out by the breadcrumb follower. |
| `set_posture(standing \| crouching \| sitting)` | Long-lived posture. Sitting suspends locomotion without erasing a follow request; standing resumes it. |
| `jump()` | Queues one grounded jump for the next physics tick. |
| `inspect_reference()` | Looks where you are looking, then captures one image from the companion's own point of view. |
| `pick_up_item(target: human_reference)` | Picks up only the prop frozen under the human's gaze for that response turn. |
| `drop_item()` | Drops only the exact prop already held by the companion. |
| `cancel_action()` | Stops running work, a queued jump, and follow intent, without changing posture. |

Anything that cannot finish inside a single tool call is a job. A job declares the capabilities it claims — locomotion, gaze, hands — and the coordinator admits it only when those are free, so an action needs no hand-written exclusion check against every other action; gaze in particular is arbitrated on priority channels rather than per-behaviour fields. A job reports a terminal result plus any conversation items it wants delivered alongside it — a text report, an image, or both — which is how a bot-eye snapshot reaches the model without the WebSocket transport knowing what produced it.

`inspect_reference()` returns pending while the companion turns and captures over following frames. Persistent follow intent and posture stay outside the job layer as long-lived state rather than jobs.

The current development source adds a deliberately narrow held-item slice. `pick_up_item(target: human_reference)` freezes the single prop under the player's gaze at the end of the utterance and binds it to that exact Realtime response turn. Pickup revalidates the immutable object's identity, reachability, hands, and host authority immediately before acting, and succeeds only after the bot's hands confirm the same prop. A new utterance invalidates any undispatched reference; unavailable, stale, or mismatched targets fail instead of falling back to a nearby prop. `drop_item()` snapshots the exact prop already in the companion's hands, validates it again at the parameterless host drop boundary, and succeeds only after empty hands remain stable. Cancellation after either host command begins reconciles only that exact prop. Pickup and exact-target cancellation are runtime-verified; explicit drop is compile- and protocol-verified pending one visual release check.

Earlier probe experiments and the original host-only feasibility work are in [`docs/archive/`](docs/archive/).
