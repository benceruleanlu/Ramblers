# Archived Probe History: 0.5.3-0.7.5

> **Archived 2026-08-17.** This record preserves runtime evidence after [`PROBE_HISTORY_0.2.0-0.5.2.md`](PROBE_HISTORY_0.2.0-0.5.2.md). Compilation and static inspection are not runtime proof.

## 2026-08-16: probe 0.5.3 spatial route repaired; local transmit signal falsified

- **Setup:** Big Walk `1.4.9 2608141617`; BepInEx IL2CPP build 755; probe `0.5.3`; one host process; OpenAI Realtime `gpt-realtime-2.1`; Big Walk voice mode set to toggle.
- **Observed:** the plugin loaded, OpenAI logged `CONNECTED` and `READY tools=set_follow_mode`, and Rambler spawned with `connectionToClient=null`, `isLocalPlayer=False`, registry count `2`, and voice tracking active. The new route resolver logged `DIRECT_VOICE_ROUTE source=loaded_playback_asset`, proving the stock attenuation curve was available at runtime. The adapter logged `GAME_VOICE_READY source=game_vad, triggerMode=VoiceActivation`, but no `LISTENING` or `IGNORED` transition occurred during speech.
- **Result:** the `0.5.2` spatial failure was repaired. The hypothesis that local `VoiceBroadcastTrigger.IsTransmitting` provides a usable speech-activity signal in Big Walk toggle mode was falsified.
- **Artifact:** DLL SHA-256 `6468F10CE371043152A776287F6296F2F76ABBFE3ADB98F1E476D91D15AAB13C`.

## 2026-08-16: probe 0.5.4 toggle-to-talk follow/stay round-trip passed

- **Hypothesis:** Big Walk's game-owned mute/toggle state can be the hard input gate while Dissonance processed amplitude and recent samples from the existing `MicManager` clip split an open toggle period into bounded agent turns.
- **Setup:** same game, BepInEx, model, host-only process, and toggle voice mode; probe `0.5.4`; Rambler approximately `2.5-3.5 m` from the human.
- **Observed:** runtime logged `GAME_VOICE_STATE channelOpen=True, mode=toggle_or_open`, `GAME_VOICE_READY source=game_voice_activity`, and `DIRECT_VOICE_ROUTE source=loaded_playback_asset`. Two separate speech turns began and submitted after silence: `0.83 s` at `2.48 m` with evaluated audibility `0.772418`, then `0.68 s` at `3.48 m` with audibility `0.691166`. OpenAI selected `set_follow_mode(follow)` for the first turn and `set_follow_mode(stay)` for the second. Both tools returned structured success; follow produced stock-motor movement and stay stopped it.
- **Result:** confirmed for near-range toggle-open speech activity, consecutive silence-separated turns without retoggling, OpenAI tool selection, structured results, and deterministic follow/stay execution. The human remained the local player.
- **Not established:** toggle-off suppression, direct out-of-range rejection, false-positive rate under background noise, radio routing, or audible bot speech.
- **Artifact:** the repository build produced from this source and the active deployed DLL both matched SHA-256 `DDA7DF6B573D0E4634CFB5588C41845A0DDB2C0B16878E846F717300920557ED` at checkpoint time.

## 2026-08-16: probe 0.6.0 architecture cleanup regression passed

- **Change:** removed the automated leader/obstacle diagnostic and automatic-follow bypass; removed the falsified local Dissonance transmit/amplitude heuristics and arbitrary proximity-range fallback; separated the bot controller, game-voice input, tool router, Realtime lifecycle bridge, and WebSocket client; enabled deterministic compilation.
- **Observed:** the plugin loaded as `0.6.0`, OpenAI reached `CONNECTED` and `READY`, and Rambler verified as the same connectionless server-owned non-local player. Near-range turns at `2.86 m` and `6.22 m` selected `follow` then `stay`, with both structured tool results succeeding. At `55.14 m`, the loaded stock attenuation curve evaluated to zero and three speech attempts logged `IGNORED reason=out_of_range`; returning in range restored listening and tool execution. The user accepted the cleanup regression test as working.
- **Result:** the cleanup preserved spawn, toggle-mode speech segmentation, tool routing, follow/stay execution, and direct-voice spatial rejection. Toggle-off suppression still lacks an explicit `channelOpen=False` log in this run.
- **Follow-up:** one rapid conversational sequence produced `API_ERROR Conversation already has an active response in progress`. It did not invalidate the successful tool calls, but input/response serialization remains an explicit reliability task.
- **Artifact:** two consecutive deterministic builds produced identical artifacts, and the repository/deployed DLL matched SHA-256 `960886B6567645E27E61CC3A4DECEB90C62CE0EDBB4735DEC46737D67B5A3196` at checkpoint time.

## 2026-08-16: Ramblers 0.6.1 project rename loaded

- **Change:** renamed the public project and BepInEx display name to **Ramblers**; replaced the obsolete feasibility-probe runtime label and companion identity markers. The plugin GUID remains `local.bigwalk.botprobe` so the existing OpenAI configuration is retained.
- **Observed:** a fresh launch logged `Loading [Ramblers 0.6.1]`, emitted `[RAMBLERS] Loaded version 0.6.1`, and reached OpenAI `CONNECTED` and `READY tools=set_follow_mode` without a startup error.
- **Artifact:** two consecutive deterministic builds matched, and the deployed DLL matched SHA-256 `3BAA5BC5404294AE0716B6E674C2038B774A2A054449583F1FC6E9944E8A0470`.

## 2026-08-17: Ramblers 0.7.3 gait speeds partially runtime-verified

- **Static finding:** `PlayerNetworking.controlsVelocity` is a world-space velocity in metres per second, not a normalized intent. `PlayerMover.FixedUpdate` gates the motion block on `isLocalPlayer || applyVelocityForRemotePlayers`, then feeds `correctedControlsVelocity` through `PlayerGround.GetSlopedMoveForce` into the rigidbody for a remote body on the same path as a local one. For a local player the magnitude comes from `PlayerMover.GetForwardSpeed()`, which returns `PlayerTunings.forwardSpeed` when walking and `PlayerTunings.forwardSprintSpeed` when `PlayerSprinter.isSprinting`; `LocalFixedUpdate` multiplies the clamped stick vector by it to produce `localControlsVelocity`.
- **Defect:** `BotController` commanded `steeringDirection * intentMagnitude` with `intentMagnitude` clamped to `[0.35, 1.0]`, so the companion was capped at `1 m/s` regardless of the game's walking speed and had no faster gait.
- **Change:** the controller now reads `forwardSpeed`/`forwardSprintSpeed` from the spawned bot's own `PlayerTunings` and commands speed in game units. The gait ramps continuously from walk at `4.5 m` of remaining breadcrumb trail to full sprint at `9 m`, so distance selects walking or running without a binary toggle that could oscillate. Arrival slowdown became a fraction of the selected gait, and the clearance term became a braking limit of `clearance / 0.45 s` with the obstacle sweep lengthened to match the requested gait.
- **Avoidance preservation:** steering candidates are still scored on `Mathf.Min(clearance, 1.5 m)`, so a longer sweep at running speed cannot outweigh the turn penalty and change which detour is chosen. The `0.7 m` blocked gate is unchanged, and the sweep never shortens below the previous `1.5 m`, so the runtime-verified walking obstacle bypass should be unaffected.
- **Observed:** a fresh host run loaded `Ramblers 0.7.3`, spawned and verified the same server-owned non-local companion, and read `tuningForwardSpeed=2` plus `tuningForwardSprintSpeed=3.5` from the live player prefab. OpenAI accepted `set_follow_mode(follow)`, and `START` reported `walkSpeed=2.00`, `runSpeed=3.50`, `gaitSpeedsFromTunings=True`, `walkGaitDistance=4.50`, and `runGaitDistance=9.00` before holding correctly at `2.00 m` from the human.
- **Result:** partial. Runtime now confirms the serialized tuning values, tuning-based speed resolution, follow activation, and stationary hold. The moving follow pace, stock walk-to-run animation, and obstacle/breadcrumb behavior while running remain unverified because the automated input path could not move the human during this run.
- **Instrumentation:** `VERIFY` now reports the raw tuning speeds, `START` reports the resolved walk/run speeds and gait distances, and `STATUS` reports `trailDistance`, `commandedSpeed`, and `gait`.
- **Artifact:** two consecutive portable deterministic builds matched at DLL SHA-256 `4229577DEADB8C5307B2B665A12368EA153ECB18D19202AC35CC7285204FC8C6`.

## 2026-08-17: Ramblers 0.7.4 discrete human gait and player-directed gaze

- **User finding:** the continuous walk-to-run blend looked artificial and produced a run-to-walk transition that a Big Walk player cannot perform while still moving. The companion also kept a fixed gaze while following instead of looking toward the human.
- **Change:** removed distance-based speed interpolation and arrival slowdown. Below `6.75 m` of remaining trail, a stopped companion begins at the live `forwardSpeed`; at or above that threshold it uses `forwardSprintSpeed`. Walking may promote to running as the trail grows, but running is latched until a complete hold, stay, blocked, failed, or teardown stop. Immediate obstacle clearance remains the only safety cap on the selected stock speed.
- **Animation state:** gait transitions also set the spawned player's `PlayerSprinter.isSprinting` and `sprintIsToggledOn`, rather than relying on velocity magnitude alone.
- **Gaze:** native IL2CPP disassembly established that `PlayerHead.headState` and `PlayerNetworking.headState` carry `(body-relative yaw, pitch)`. Every navigation tick now turns that state toward the human player's camera position, within human-scale yaw/pitch limits, and writes the `NetworkheadState` SyncVar for remote replication.
- **Evidence boundary:** compilation and native binding inspection establish member availability and coordinate use, not visible runtime behavior. Human-directed gaze, discrete walk/run animation, the one-way run latch, arrival stopping, and obstacle behavior in `0.7.4` require runtime testing.
- **Instrumentation:** `START` reports `runStartDistance` and the run-latch rule; gait transitions emit `GAIT walk` or `GAIT run`; `STATUS` reports the latched gait and replicated head state. There is no `jog` state.
- **Artifact:** two consecutive portable deterministic builds matched at DLL SHA-256 `D5DB014C7605E72684519C28A43AC76D9E91DE38EE4AD5D265850E308528F310` and PDB SHA-256 `EE888A52EEBDF9792A0638CFD97D420DE61BB291E53BC5B436192B839F0786EC`.

## 2026-08-17: Ramblers 0.7.5 stock-style whole-body facing

- **Correction:** `0.7.4` wrote only the replicated `PlayerHead` yaw/pitch state. That can animate a remote head, but it omitted the local player's separate whole-body turn, so it did not fully reproduce how a real Big Walk player looks around.
- **Native finding:** `PlayerMover.UpdatePerFrameRotation` consumes horizontal `PlayerHead.headState.x` into `PlayerCharacter.kernal` rotation at `180 degrees/s`. It is guarded by `NetworkIdentity.isLocalPlayer`, so the connectionless non-local companion never runs that stock body step. Remote `PlayerHead.Update` instead reads `PlayerNetworking.headState` into the animator's body-relative look parameters.
- **Change:** every navigation tick now turns the companion's authoritative `HouseNetworkTransform.targetRotation` toward the human camera at the same `180 degrees/s` rate. The yaw not yet absorbed by the body, plus vertical pitch, remains in the stock replicated head state and is clamped with the spawned player's `sideLookLimit`, `upperLookLimit`, and `lowerLookLimit` tunings.
- **Network path:** the existing companion-only ownership patch makes `HouseNetworkTransform.isOwned` true. Its normal owner update therefore samples and replicates the body rotation; no new transform RPC or second authority path was added.
- **Evidence boundary:** native inspection and successful compilation establish the intended stock-equivalent paths and member availability, not visible behavior. Whole-body facing, residual pose, discrete gait animation, the one-way run latch, arrival stopping, and obstacle behavior require runtime testing.
- **Instrumentation:** `VERIFY` reports the live look limits; `START` reports the `180 degrees/s` body rate; `STATUS` reports body yaw, target yaw, and residual head state.
- **Artifact:** after rebasing onto the Rambler naming change, two consecutive portable deterministic builds matched at DLL SHA-256 `9543EB89B07F0275CFEF885DB0045D41972D0CC588E81234B724DF8FD8DBBB3F` and PDB SHA-256 `648C6CCE382189776D3496A26DFCA6886710411C0BB6F0E6659DB106725FB957`.
