# Ramblers

> [!WARNING]
> **Under construction:** Ramblers is not ready for use. All `0.x.x` versions are development builds; please wait for the `1.0.0` release before installing or trying it.

An experimental host-side companion mod for [Big Walk](https://store.steampowered.com/app/1478500/). Ramblers spawns an AI-controlled party member in follow mode inside the host's own game process — no second client, no second process. You talk to it using Big Walk's stock toggle-to-talk, and it walks itself using Big Walk's stock remote-player motor.

## Install

With Big Walk closed, copy `dist/Ramblers.dll` into `BepInEx/plugins/Ramblers` under the game directory. Rename the deployed file to `Ramblers.dll.disabled` to stop it loading.

Ramblers reads `OPENAI_API_KEY` from the process or current Windows user environment. No key is stored in this repository or in the BepInEx configuration. This local-key path is for development only.

## Compatibility

Tested against Big Walk `1.4.9` (build `2608141617`) on BepInEx IL2CPP `6.0.0-be.755`, which is Unity `6000.3.17f1` on URP. Ramblers `0.7.5`, `0.8.0`, and `0.12.0` are runtime-verified on that combination. `0.12.0` verification covers follow, posture, jump, the job layer's capability arbitration, deferred tool dispatch with the microphone epoch barrier, and one full `inspect_reference()` producing a described image. The current development source additionally has user-confirmed visual runtime proof for default follow, grounded exact-target pickup, exact-target cancellation, explicit held-item drop, and staged kick strength and direction. Other game or loader versions are unverified, and the survey in [Stripped Unity APIs](docs/STRIPPED_UNITY_APIS.md) is specific to this build of the game rather than to Unity 6.

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
| `kick_item(target: human_reference, strength?, direction?)` | Grabs only the frozen prop, holds it through a game-tuned light/normal/hard charge, then kicks it away or toward the human. |
| `drop_item()` | Drops only the exact prop already held by the companion. |
| `cancel_action()` | Stops running work, a queued jump, and follow intent, without changing posture. |

Anything that cannot finish inside a single tool call is a job. A job declares the capabilities it claims — locomotion, gaze, hands — and the coordinator admits it only when those are free, so an action needs no hand-written exclusion check against every other action; gaze in particular is arbitrated on priority channels rather than per-behaviour fields. A job reports a terminal result plus any conversation items it wants delivered alongside it — a text report, an image, or both — which is how a bot-eye snapshot reaches the model without the WebSocket transport knowing what produced it.

`inspect_reference()` returns pending while the companion turns and captures over following frames. Persistent follow intent and posture stay outside the job layer as long-lived state rather than jobs.

The current development source adds deliberately narrow physical-action slices. `pick_up_item(target: human_reference)` and `kick_item(target: human_reference, strength?, direction?)` freeze the single prop under the player's gaze at the end of the utterance and bind it to that exact Realtime response turn. Both revalidate the immutable object's identity, reachability, hands, and host authority immediately before acting. Kick deliberately passes the exact prop through Big Walk's stock authoritative pickup, waits in a separate charge phase using the live `maxWindUpDuration` tuning, then launches it with a bounded light, normal, or hard charge either away from the companion or toward the human. It confirms release and motion and never applies an unnetworked Rigidbody shove. A new utterance invalidates any undispatched reference and can cancel a charge before release; unavailable, stale, or mismatched targets fail instead of falling back to a nearby prop. `drop_item()` snapshots the exact prop already in the companion's hands, validates it again at the parameterless host drop boundary, and succeeds only after empty hands remain stable. Pickup, exact-target cancellation, explicit drop, and staged kick mechanics are runtime-verified.

The traversal follower replays the route the human actually took instead of inventing a global NavMesh over Big Walk's irregular world. Breadcrumb reach and route length retain height, while walk-off transitions retain the human's horizontal route tangent. Human jump input is not copied: airborne samples are collapsed into their landing, same-level recreational jumps become ordinary route movement, and only a materially higher landing retains a jump hint. Traversal lookahead selects real transition markers before obstacle sweeping can make the companion pace around the final point at an edge; the stock grounded jump path handles retained jump markers, and direct route commitment handles ledge exits. Steering asks Big Walk's own slope solver whether each otherwise-clear heading would produce movement, filters the closest upward floor/seam contact so a harmless mesh edge does not masquerade as a wall, tries a traversable contour, then permits a bounded grounded recovery jump if the motor still stalls. If the human carries the companion, follow pauses, discards the obsolete route, and starts a fresh route only after release so it cannot walk back to the pickup location. Falling and jumping count as spatial progress, and teleport recovery remains deliberately disabled. The latest visual run and structured trace verify ordinary follow, upward floor-contact filtering, same-level jump suppression, and recovery across the observed slope and ledge route; carry rebasing and higher-landing jump replay were not exercised in that run.

Earlier probe experiments and the original host-only feasibility work are in [`docs/archive/`](docs/archive/).
