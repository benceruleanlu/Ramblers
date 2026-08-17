# Experiment Log

Append-only record of runtime experiments. Convention (from the project's start): every entry records date and game/mod versions, hypothesis, exact runtime setup, observed log/result, whether the result confirms / falsifies / leaves the hypothesis unresolved, and new artifacts with their deployment state.

Static-analysis conclusions and architectural expectations are never promoted into runtime-confirmed facts without an entry here.

---

## 2026-08-15: clean connectionless spawn and ownership

- **Hypothesis:** a host mod can create a complete remote-style second player without a second client, provided it replaces connection-only initialization and grants bot-only server transform ownership.
- **Setup:** Big Walk `1.4.8 2608070648`; BepInEx IL2CPP build 755; two-player `BotProbe` host save; probe `0.2.0`; one Big Walk process.
- **Observed:** spawn received `netId=580`; server and host client both recognized it; `isLocalPlayer=False`; `connectionToClient=null`; live player registry count was `2`; `serverOwnsTransform=True`; Dissonance identity `NitrogenHostBot` was tracking; no connection-dependent lifecycle exception occurred.
- **Result:** hypothesis confirmed for body creation, registration, connectionless lifecycle, transform ownership, and voice-identity tracking.
- **Not established:** autonomous locomotion, complete interaction compatibility, audible synthetic speech, or remote-guest voice delivery.
- **Cleanup:** Big Walk was closed and the temporary deployed probe was renamed to `.disabled`; original game binaries and data were not modified.

Full raw probe logs and build hashes: [HOST_MOD_FEASIBILITY.md](HOST_MOD_FEASIBILITY.md).

---

## Next planned: walk-to-point (Roadmap #1)

Make the bot walk to a host-selected point while remaining a remote player. Success criteria:

1. The bot moves under server control without becoming `isLocalPlayer`.
2. The host camera and input remain attached exclusively to the human player.
3. Remote-body animation and network transform state remain coherent.
4. The bot stops within a defined tolerance of the target.
5. The bot recovers from a blocked straight-line route without teleporting.
6. No stock game files are rewritten.

---

## 2026-08-16: autonomous walk-to-point probe prepared (runtime pending)

- **Hypothesis:** the connectionless server-owned bot can use the stock remote-player motor when the host writes a world-space movement intent to `PlayerNetworking.NetworkcontrolsVelocity` and enables `PlayerMover.applyVelocityForRemotePlayers` for the bot.
- **Prepared setup:** probe `0.3.0` selects one bounded goal six metres ahead of the host's pose four seconds after spawn. It steers in `FixedUpdate`, slows near the goal, stops at `0.65 m`, and detects lack of progress. Recovery alternates bounded `55°` detours without teleporting and fails closed after four attempts or 30 seconds.
- **Diagnostics:** one-second status records include bot position, remaining distance, requested and replicated movement intent, rigidbody velocity, bot locality, and whether the human remains the local player. Terminal records are `ARRIVED` or `FAILED`.
- **Static basis:** recovered native flow shows that remote `PlayerMover.correctedControlsVelocity` reads the networked world vector and transforms it through the player kernel; the remote physics path is gated by `applyVelocityForRemotePlayers`.
- **Build:** compilation succeeded against the installed Big Walk/BepInEx interop assemblies. DLL SHA-256: `CD77C675BDC2C8461B3C6511C11B2BAE82EC7D56EA30A35BF7746B15F063F86A`.
- **Deployment state:** the `0.3.0` DLL/PDB are copied to the BepInEx plugin folder with `.disabled` suffixes. The runtime-verified `0.2.0` pair remains beside them as versioned `.disabled` archives. No probe is currently loadable.
- **Result:** unresolved until an in-game host run. Compilation and static analysis are not runtime locomotion evidence.

---

## 2026-08-16: autonomous walk-to-point probe 0.3.0 — motor remained gated

- **Setup:** Big Walk `1.4.8 2608070648`; BepInEx IL2CPP build 755; two-player `BotProbe` host save; probe `0.3.0`; one Big Walk process.
- **Observed:** the bot spawned cleanly as `netId=580`, stayed `isLocalPlayer=False`, retained server transform ownership, and registered as the second player. `NetworkcontrolsVelocity` tracked each requested world-space intent exactly, while bot position stayed `(-240.98, 37.01, -483.17)` and rigidbody velocity stayed `(0,0,0)` through four alternating detours. The probe failed closed after the fourth recovery attempt. The human remained the local player throughout.
- **Result:** the simple hypothesis was falsified. A server-written movement SyncVar plus `applyVelocityForRemotePlayers=True` is not sufficient for connectionless locomotion.
- **Cause isolated:** recovered `PlayerMover.FixedUpdate` zeros remote movement when `HouseNetworkTransform.IsRestingForPlayerMovement` is true. That getter reports true when the transform has no valid client interpolation goal, which is the connectionless bot's state.
- **Follow-up build:** probe `0.3.1` adds a bot-only Harmony postfix that reports `IsRestingForPlayerMovement=False`; stock behavior remains unchanged for the human and real remote players. Runtime result pending.
- **Follow-up artifact:** compilation succeeded. DLL SHA-256: `0ED8469C758E14FDD4340AEB6D68CF1CEF3E6894B5AC45F7A8A5972271561273`. It is not yet deployed because `0.3.0` remains loaded in the open test process.

---

## 2026-08-16: autonomous walk-to-point probe 0.3.1 — motor unlocked, long goal not reached

- **Hypothesis:** overriding `HouseNetworkTransform.IsRestingForPlayerMovement` only for the connectionless bot will allow the existing remote-player motor to consume the replicated movement intent.
- **Setup:** Big Walk `1.4.8 2608070648`; BepInEx IL2CPP build 755; two-player `BotProbe` host save; probe `0.3.1`; one Big Walk process; automatic goal `6.32 m` from the bot in the enclosed staging room.
- **Observed:** verification reported `movementResting=False`, `remoteMotorEnabled=True`, `isLocalPlayer=False`, and registry count `2`. The bot moved from `(-240.98, 37.01, -483.17)` to approximately `(-243.11, 37.01, -482.41)` with nonzero rigidbody velocity while requested and replicated movement intents matched. The human remained local. Progress stopped with `4.07 m` remaining; four bounded detours did not clear the stall, and the probe failed closed.
- **Result:** the motor hypothesis was confirmed: the connectionless remote body moved through stock player physics. The six-metre arrival hypothesis was not confirmed. The stall in the staging room is consistent with nearby collision geometry, but this run did not instrument contacts and therefore does not prove that cause.
- **Cleanup:** Big Walk was closed. The `0.3.1` DLL/PDB were preserved as versioned `.disabled` archives before the next build was deployed. No stock game files were rewritten.

---

## 2026-08-16: autonomous walk-to-point probe 0.3.2 — bounded local traverse passed

- **Hypothesis:** now that the motor is live, a deliberately local unobstructed target can prove autonomous start, steering, slowdown, arrival, and stop separately from navigation.
- **Setup:** same game, BepInEx, two-player save, and single-process host configuration; probe `0.3.2`; target defined as `botStart + hostForward * 1.5 m`; arrival tolerance `0.65 m`.
- **Observed:** the bot spawned as `netId=580`, `connectionToClient=null`, `isLocalPlayer=False`, `serverOwnsTransform=True`, `movementResting=False`, and registry count `2`. It began at `(-240.98, 37.01, -483.17)`, produced matched requested/network movement intents and nonzero rigidbody velocity, slowed as distance fell, and emitted `ARRIVED` at `(-241.62, 37.01, -482.61)`. Final target distance was `0.64 m`; elapsed time was `2.33 s`; recoveries were `0`; `botIsLocalPlayer=False`; `hostStillLocal=True`.
- **Result:** hypothesis confirmed for a bounded local autonomous traverse through the stock remote-player motor. This does not establish general path planning, obstacle avoidance, terrain traversal, or animation coherence for remote guests.
- **Build:** compilation succeeded. DLL SHA-256: `B47D70879EDBF07629522FBF9DBA6CA4BA804819B19C5A1E53E70C7D9609D866`.
- **Cleanup/deployment:** Big Walk was closed and the active `0.3.2` DLL/PDB were renamed to `BigWalkBotProbe.dll.disabled` / `BigWalkBotProbe.pdb.disabled`. Versioned disabled archives of `0.2.0`, `0.3.0`, and `0.3.1` remain beside them. No probe is loadable and no stock game files were rewritten.

---

## 2026-08-16: breadcrumb follow probe 0.4.0 — bounded obstacle bypass passed

- **Hypothesis:** a connectionless remote bot can follow the human without an internet-model decision on every tick by recording local breadcrumbs and using the stock physics body itself to test a small fan of candidate headings.
- **Setup:** same game, BepInEx, two-player `BotProbe` save, and single-process host configuration; probe `0.4.0`; breadcrumb sampling and navigation at 10 Hz; `0.65 m` breadcrumb spacing; `2.25 m` follow distance; `2.50 m` resume distance. An opt-in diagnostic moved the real host body along a short `2.5 m` path and placed a temporary `0.70 × 1.50 × 0.70 m` cube between bot and the first breadcrumb. The diagnostic defaults to off.
- **Control:** the follower targets the oldest unreached breadcrumb and evaluates headings at `0°`, `±25°`, `±50°`, `±75°`, and `±95°`. Clearance comes from `Rigidbody.SweepTest`, so the query uses the same colliders and collision matrix as the stock motor. A short side-choice hold prevents left/right oscillation.
- **Observed:** the bot again spawned as `netId=580`, `connectionToClient=null`, `isLocalPlayer=False`, `serverOwnsTransform=True`, `movementResting=False`, and registry count `2`. Three breadcrumbs were recorded. The direct sweep reported blocked; the follower logged `AVOID steeringAngle=50, clearance=1.50`, moved from `(-240.98, 37.01, -483.17)` to `(-241.34, 37.01, -483.19)`, and entered `Holding` at `2.24 m` from the human while the cube still had roughly six seconds before removal. Requested/network intent matched and rigidbody velocity was nonzero during the bypass. The successful run emitted no `POSSIBLY_STUCK` warning.
- **Sensor falsification:** earlier builds reconstructed a smaller `Physics.SphereCast`; those queries reported `1.50 m` clear while the cube physically stopped the bot. Replacing the approximation with the character rigidbody sweep removed that sensor/motor disagreement. `Physics.CapsuleCastAll` was also rejected because its IL2CPP wrapper failed at runtime with `Method unstripping failed`.
- **Stuck boundary:** `0.4.0` detects intent of at least `0.35` with less than `0.15 m` displacement over `2.5 s` and logs `POSSIBLY_STUCK`. Detection was exercised by the falsified sensor builds, but recovery is intentionally not implemented: there is no backtrack, jump, respawn, or teleport.
- **Result:** hypothesis confirmed only for a short recorded trail and one artificial static obstacle in the staging room. This does not establish long trails, varied terrain, moving obstacles, ledges, doors, full route planning, or stuck recovery.
- **Build:** compilation succeeded. DLL SHA-256: `464413222CFA1EAFFF8469EBC4938FA684B8EDBC0C042392FD42960D93B8094B`.
- **Cleanup/deployment:** Big Walk was closed, `AutomatedLeaderWalk` was reset to `false`, and the verified DLL/PDB were renamed to versioned `.disabled` archives. No probe is loadable and no stock game files were rewritten.

---

## 2026-08-16: probe 0.4.0 compatibility rerun on Big Walk 1.4.9

- **Hypothesis:** the runtime-verified `0.4.0` binary built and tested against Big Walk `1.4.8` remains compatible with the updated game's regenerated IL2CPP interop bindings.
- **Setup:** Big Walk `1.4.9 2608141617`; BepInEx IL2CPP build 755; two-player `BotProbe` host save; the unchanged probe `0.4.0` binary with SHA-256 `464413222CFA1EAFFF8469EBC4938FA684B8EDBC0C042392FD42960D93B8094B`; `AutomatedLeaderWalk=false`; one Big Walk process. BepInEx detected the game update and regenerated its interop assemblies before this run.
- **Observed:** the plugin loaded without a mod or Harmony startup failure. The bot spawned as `netId=580`, `connectionToClient=null`, `isLocalPlayer=False`, `serverOwnsTransform=True`, `movementResting=False`, and registry count `2`. Manual host movement produced 39 breadcrumbs and 24 logged `Following` status samples with matched requested/network movement intent and nonzero rigidbody velocity. The bot returned to `Holding`; the human remained the local player. The run emitted one `POSSIBLY_STUCK` detection while commanded displacement was low; as designed, no recovery or teleport was attempted.
- **Result:** compatibility confirmed on `1.4.9 2608141617` for plugin initialization, bot spawn/registration, host-locality preservation, stock-motor locomotion, breadcrumb following, and hold behavior. This was not a full regression suite: the artificial obstacle-bypass diagnostic was not rerun, so its existing runtime proof remains the `1.4.8` experiment above.
- **Deployment state:** the verified `0.4.0` DLL/PDB were left active in the BepInEx plugin folder for continued manual testing. No stock game files were rewritten.

---

## 2026-08-16: OpenAI follow-tool probe 0.5.0 prepared (in-game runtime pending)

- **Hypothesis:** a bounded speech turn can be sent from Big Walk's existing processed microphone stream to OpenAI Realtime; the model can map natural-language follow/stay requests to one allowlisted tool; and the host mod can execute that tool on Unity's main thread by starting or stopping the already-proven breadcrumb follower.
- **Prepared setup:** probe `0.5.0`; `gpt-realtime-2.1`; direct local WebSocket authenticated from `OPENAI_API_KEY` in the process or current Windows user environment; hold `F8` for push-to-talk and release to commit; Dissonance `BaseMicrophoneSubscriber` input reduced to mono 24 kHz PCM16; session VAD disabled; output modality text; single `set_follow_mode` tool with a closed `follow | stay` enum. Automatic following now defaults off so movement requires an explicit tool request (or a separately enabled diagnostic setting).
- **Authority boundary:** the WebSocket worker only exchanges audio/JSON and queues function calls. `ProbeRunner` validates the tool name and arguments on Unity's main thread, enforces bot/player availability, starts or stops the deterministic follow skill, and returns structured success/error JSON. The model never writes transforms or movement vectors.
- **Build and offline checks:** compilation succeeded. The serialized `session.update` contract was parsed back and verified for model, 24 kHz PCM input, disabled VAD, text output, tool name, closed enum, and `additionalProperties=false`. Argument parsing accepted `follow` and `stay` and rejected an unknown mode and malformed JSON.
- **Live API-only check:** using the user-level environment key, the compiled client connected to the OpenAI Realtime endpoint and received `session.updated` with `READY tools=set_follow_mode`. This check sent the session instructions/tool schema only: it sent no microphone audio and invoked no game tool.
- **Result:** API authentication, WebSocket connection, and session/tool-schema acceptance are confirmed outside the game process. In-game Dissonance subscription, audio capture/resampling, speech understanding, function selection, main-thread dispatch, follow start/stop, and continued model acknowledgement remain unresolved until a controlled Big Walk run.
- **Build:** DLL SHA-256 `3B1BCD104A34E6BC76E020DF32C7B51336CB8EAD20486AEEB3C7898652AFF5D4` (`57,856` bytes).
- **Deployment state:** Big Walk was already running with runtime-verified `0.4.0`, so no hot replacement was attempted. The `0.5.0` DLL/PDB were copied beside it as `BigWalkBotProbe-v0.5.0-openai-follow.*.disabled`; the staged DLL hash matches the build. Active `BigWalkBotProbe.dll` remains the `0.4.0` binary until the game is closed for an explicit swap. No stock game files were rewritten.

---

## 2026-08-16: OpenAI follow-tool probe 0.5.0 activated

- **Precondition:** Big Walk was confirmed stopped before any plugin file was changed.
- **Verification before activation:** active `BigWalkBotProbe.dll` matched the runtime-verified `0.4.0` SHA-256 `464413222CFA1EAFFF8469EBC4938FA684B8EDBC0C042392FD42960D93B8094B`; staged `0.5.0` matched SHA-256 `3B1BCD104A34E6BC76E020DF32C7B51336CB8EAD20486AEEB3C7898652AFF5D4`.
- **Activation:** the existing active DLL/PDB were copied to `BigWalkBotProbe-v0.4.0-active-before-v0.5.0.*.disabled`, then the staged `0.5.0` pair was copied to the active `BigWalkBotProbe.dll` / `.pdb` paths.
- **Verification after activation:** the active DLL matched the expected `0.5.0` hash and the disabled backup matched the prior `0.4.0` hash. Big Walk remained stopped. Existing `AutomatedLeaderWalk=false`; new `OpenAI` and `StartFollowingAutomatically` entries will be emitted with code defaults on the next launch.
- **Evidence boundary:** deployment is confirmed. Plugin startup, user-environment key lookup, Dissonance subscription, speech input, tool selection, and in-game follow/stay execution remain pending until the next controlled run.

---

## 2026-08-16: probe 0.5.0 load failure and 0.5.1 microphone repair prepared

- **Observed failure:** BepInEx began loading probe `0.5.0`, then `ClassInjector.RegisterTypeInIl2Cpp<RealtimeMicrophoneSubscriber>` failed with `No method found for vtable entry ProcessAudio`. The plugin `Load()` method aborted before Harmony patches or the probe runner were installed, which is why Nitrogen did not spawn.
- **Cause boundary:** `RealtimeMicrophoneSubscriber` inherited Dissonance's `BaseMicrophoneSubscriber` across the managed/IL2CPP boundary. Its `ProcessAudio(ArraySegment<float>)` override could compile but could not be represented as the native vtable entry expected by the regenerated Big Walk `1.4.9` interop assemblies.
- **Repair:** probe `0.5.1` removes the injected subscriber entirely. While `F8` is held, the already-injected `RealtimeAgentBridge` polls Big Walk's existing `MicManager` looping `AudioClip`, reads only newly written frames (including ring-buffer wrap), reduces multichannel input to mono, resamples the game's 48 kHz capture to 24 kHz PCM16, and sends the bounded turn to the otherwise unchanged Realtime client. This does not open a second operating-system microphone capture.
- **Static verification:** compilation succeeded; the built assembly reports plugin version `0.5.1`, references `AudioSystem` and `UnityEngine.AudioModule`, contains no `RealtimeMicrophoneSubscriber` type or `ProcessAudio` method, and exposes only the pointer constructor from `RealtimeAgentBridge` to IL2CPP. The session schema and `set_follow_mode(follow | stay)` tool boundary are unchanged from the API-verified `0.5.0` client.
- **Build:** DLL SHA-256 `11DD32EE0998F7999489EA43E40F64056E036A4F4CA8E8F1CAB16DE4BBEA709F`.
- **Evidence boundary:** this confirms the implementation and static load-contract repair only. Plugin startup, microphone reads, speech understanding, model tool selection, and in-game follow/stay remain pending until `0.5.1` is activated after Big Walk closes and a controlled run is observed.

---

## 2026-08-16: probe 0.5.1 activated

- **Precondition:** Big Walk was confirmed stopped before the plugin files were changed.
- **Verification before activation:** the active DLL matched failed-load probe `0.5.0` SHA-256 `3B1BCD104A34E6BC76E020DF32C7B51336CB8EAD20486AEEB3C7898652AFF5D4`; staged `0.5.1` matched SHA-256 `11DD32EE0998F7999489EA43E40F64056E036A4F4CA8E8F1CAB16DE4BBEA709F`.
- **Activation:** the active `0.5.0` DLL/PDB were preserved as `BigWalkBotProbe-v0.5.0-failed-load-before-v0.5.1.*.disabled`, then the staged `0.5.1` pair was copied to the active `BigWalkBotProbe.dll` / `.pdb` paths.
- **Verification after activation:** the active DLL matches the expected `0.5.1` hash; the rollback copy matches `0.5.0`; Big Walk remains stopped for the user-controlled launch and runtime test.
- **Evidence boundary:** activation is confirmed. Runtime plugin load, Nitrogen spawn, `MicManager` capture, speech understanding, model tool selection, and follow/stay execution remain pending until the next observed run.

---

## 2026-08-16: probe 0.5.1 OpenAI follow/stay round-trip passed

- **Hypothesis:** the repaired `MicManager` path can capture bounded in-game speech, submit it to OpenAI Realtime, dispatch the allowlisted follow/stay tool on Unity's main thread, and return the structured result without compromising the bot or host-player invariants.
- **Setup:** Big Walk `1.4.9 2608141617`; BepInEx IL2CPP build 755; probe `0.5.1`; `gpt-realtime-2.1`; one host process; temporary F8 hold-to-talk turn boundary; OpenAI key read from the Windows user environment.
- **Observed:** the plugin loaded, the Realtime client logged `CONNECTED` and `READY tools=set_follow_mode`, and Nitrogen spawned as `netId=580`, `connectionToClient=null`, `isLocalPlayer=False`, with registry count `2`. The game-owned microphone was reported as 48 kHz mono with a 480,000-frame ring buffer. Four submitted turns lasted `1.23`, `0.89`, `1.01`, and `0.97` seconds. The model selected `follow`, `stay`, `stay`, then `follow`; all calls were accepted and received structured success results. Follow produced nonzero matched movement intents and stock-motor movement; stay stopped the skill; the human remained local.
- **Result:** hypothesis confirmed for in-game OpenAI authentication, processed microphone reads, speech understanding, model tool selection, main-thread follow/stay execution, structured tool results, and response continuation. This run did not test game-native V controls, proximity gating, synthetic speech output, or radio routing.
- **Artifact:** runtime-tested DLL SHA-256 `11DD32EE0998F7999489EA43E40F64056E036A4F4CA8E8F1CAB16DE4BBEA709F`.

---

## 2026-08-16: probe 0.5.2 native game-voice adapter prepared

- **Hypothesis:** the agent can share Big Walk's existing voice UX instead of owning an F8 keybind: the game's Dissonance mute/transmit state can bound PTT turns, native voice activation can bound open-mic turns, and the stock player-voice attenuation curve can suppress direct speech when Nitrogen would not hear it.
- **Implementation:** all raw keyboard polling and the `PushToTalkKey` configuration were removed. PTT mode observes `DissonanceComms.IsMuted` after Big Walk processes its own V binding. Open/toggle mode observes `VoiceBroadcastTrigger.IsTransmitting` when its activation mode is Dissonance VAD, with processed local amplitude plus a 450 ms silence hangover only when the game leaves the room channel open. Before capture, the bridge evaluates the bot prefab's `PlayerVoicePlaybackControl.AttenuationCurve` at the human-to-bot distance and fails closed when that game-owned route is unavailable or inaudible.
- **Authority boundary:** the adapter is read-only with respect to Big Walk voice state. It does not intercept V, synthesize input, mutate mute mode, open a second microphone, or change the deterministic tool/motor boundary. Walkie-talkie routing is deliberately deferred because it is a distinct game route rather than direct proximity.
- **Static verification:** Roslyn compilation succeeded against the installed `1.4.9` interop assemblies. Mono.Cecil inspection reports plugin version `0.5.2`, no `PushToTalkKey`, and references to `SettingsHelper.pushToTalkModeActive`, `DissonanceComms.IsMuted`, `VoiceBroadcastTrigger.Mode/IsTransmitting`, `VoicePlayerState.Amplitude`, `PlayerVoicePlaybackControl.AttenuationCurve`, and the existing `MicManager` APIs.
- **Build:** DLL SHA-256 `76078A1E3B0A24289534736EF615F7CDCA7E6D97D971D8AE58D0E004281E5000` (`59,904` bytes).
- **Evidence boundary:** compilation and static API wiring are confirmed. Plugin startup and actual behavior under PTT, open/toggle voice, silence, direct-range transitions, and unavailable attenuation state remain unresolved until the next runtime test.

---

## 2026-08-16: probe 0.5.2 activated

- **Precondition:** Big Walk was confirmed stopped; no process termination was required.
- **Verification before activation:** the active DLL matched runtime-proven probe `0.5.1` SHA-256 `11DD32EE0998F7999489EA43E40F64056E036A4F4CA8E8F1CAB16DE4BBEA709F`; the final built `0.5.2` DLL matched SHA-256 `76078A1E3B0A24289534736EF615F7CDCA7E6D97D971D8AE58D0E004281E5000`.
- **Activation:** active `0.5.1` DLL/PDB were preserved as `BigWalkBotProbe-v0.5.1-runtime-verified-before-v0.5.2.*.disabled`; the `0.5.2` pair was preserved as a versioned disabled archive and copied to the active paths. The `0.5.1` config was moved to `local.bigwalk.botprobe-v0.5.1.cfg.disabled` so the obsolete F8 setting is absent when `0.5.2` emits its clean config.
- **Verification after activation:** active and staged `0.5.2` DLL hashes match the expected build; the rollback DLL matches `0.5.1`; Big Walk remains stopped for the user-controlled runtime test.
- **Evidence boundary:** activation is confirmed. Load, spawn, native voice intent, direct audibility, and the existing OpenAI tool round-trip remain pending for this build.
