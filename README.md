# Big Walk AI Teammate

An AI-controlled companion that appears and behaves as a complete second player inside a host's [Big Walk](https://store.steampowered.com/app/1478500/) session — walking beside you, talking with you, and helping solve the game's co-op puzzles.

**Status: body creation, locomotion, and a bounded local-navigation slice are runtime-proven.** A host mod can spawn a fully registered, connectionless second player, follow a short human breadcrumb trail, and steer around one local obstacle through Big Walk's stock remote-player motor without taking locality from the human. General navigation and the rest of the control/cognition stack remain in progress.

## Hard constraints

1. **Host mod only.** No secondary Big Walk client or process, ever.
2. **No persistent modification of the game.** BepInEx loads beside the game; all behavior changes are mod-owned, in-memory Harmony hooks. Removing the mod restores stock behavior on next launch. The executable, IL2CPP binary, metadata, assets, and save format are never rewritten.
3. **Evidence discipline.** Runtime-confirmed facts, static-analysis findings, and untested design are never conflated. See [Working conventions](#working-conventions).

## What is proven (runtime-confirmed)

The primary bounded experiments ran on Big Walk `1.4.8 2608070648` (Steam build `24611934`, Unity `6000.3.17f1`, IL2CPP) with BepInEx IL2CPP `6.0.0-be.755`. Probes `0.2.0`, `0.3.2`, and `0.4.0` demonstrated:

- The host can clone the real player prefab and spawn it via `NetworkServer.Spawn` with **no client connection** (`connectionToClient=null`, valid `netId`).
- The synthetic player registers in `PlayerCharacter.allPlayerCharacters` (count = 2) and follows the normal **remote-player** code path (`isLocalPlayer=false`); the host camera and input stay on the human.
- Server transform ownership works (`serverOwnsTransform=true`) via a bot-only Harmony postfix on `HouseNetworkTransform.isOwned`.
- Connection-dependent init (`PlayerNetworking.Start`) is cleanly bypassed for the bot with a bot-only Harmony prefix.
- A separate **Dissonance voice identity** (`NitrogenHostBot`) can be assigned and is tracked.
- The host can drive the connectionless bot through the stock remote-player physics motor. In the bounded `0.3.2` test, the bot autonomously reached a point `1.5 m` from its start in `2.33 s`, stopped within the `0.65 m` tolerance, needed no recovery, remained non-local, and left the human as the local player.
- The bot can record a short human breadcrumb trail at 10 Hz, follow it while maintaining a `2.25 m` separation, detect an obstacle with the real player rigidbody sweep, select a clear alternate heading, and stop at the follow distance. In the bounded `0.4.0` diagnostic it chose a `50°` bypass and reached the hold state while the obstacle was still present.

A compatibility rerun on Big Walk `1.4.9 2608141617` used the same probe `0.4.0` binary (SHA-256 `464413222CFA1EAFFF8469EBC4938FA684B8EDBC0C042392FD42960D93B8094B`) after BepInEx regenerated the game's interop bindings. The probe loaded without a mod or Harmony startup failure, spawned and registered the connectionless bot, followed a manually driven 39-breadcrumb route, returned to `Holding`, and kept the human as the local player. This confirms the spawn and follow path on that build; it is not a full `1.4.9` regression suite, and the artificial obstacle-bypass diagnostic was not rerun.

Raw spawn logs, probe hashes, and the original evidence-boundary table are archived in [docs/archive/HOST_MOD_FEASIBILITY.md](docs/archive/HOST_MOD_FEASIBILITY.md). The detailed probe history through `0.5.2` is archived in [docs/archive/PROBE_HISTORY_0.2.0-0.5.2.md](docs/archive/PROBE_HISTORY_0.2.0-0.5.2.md).

## What is not yet proven

- General point selection, varied-terrain traversal, dynamic-obstacle behavior, and looking under server control. One artificial local obstacle bypass is proven; general world navigation is not.
- Stuck recovery. The probe can identify commanded movement with negligible displacement and log `POSSIBLY_STUCK`, but deliberately does not recover, jump, or teleport yet.
- Object and puzzle interactions at runtime (static paths identified).
- Audible synthetic speech (local 3D audio designed, not implemented).
- Bot speech heard by unmodified remote guests (Dissonance packet injection — deferred, hardest, optional).

## Key findings from the decompiled game

These shape the whole design (recovered C# lives in `.analysis/cpp2il-cs/`):

- **Movement input is a vector, with one connectionless-player gate.** Runtime confirms that writing `PlayerNetworking.controlsVelocity` and enabling `PlayerMover.applyVelocityForRemotePlayers` drives the bot after a bot-only override of `HouseNetworkTransform.IsRestingForPlayerMovement`. The override is needed because a connectionless body has no client interpolation goal and would otherwise be treated as permanently resting.
- **Interactions are a discrete verb list.** `PlayerActions` exposes `ActionPickUpProp`, `ActionUseWorldSwitch`, `ActionEnterPose`, `ActionPlaceInHome`, gestures, etc. The bot must use server-side action paths, not the client `Cmd*` wrappers (those assume client authority/transport).
- **No navmesh exists.** Nothing in `Assembly-CSharp` references `NavMesh`/pathfinding. Navigation must come from the mod (see Layer 1 below).
- **World layout comes from the save's player count** (`PlayerCountSwapper.playerCount`), not live connection count — a two-player world with one human + one bot is a supported world state.
- Gameplay systems (train, teleporter, text input, menus…) enumerate `PlayerCharacter.allPlayerCharacters`, so the bot is visible to them. Individual puzzles still need integration tests.

## Architecture

The central insight: **no model needs to be trained, and no model needs "motor skills."** Because the mod runs inside the process with server authority, perception is reading the scene graph (not vision) and motor control is writing a velocity vector (not learned control). The only hard 3D problem left — navigation — is classical algorithms. The AI model only does what models are already good at: conversation, social behavior, and choosing which verb to invoke.

```
Layer 4  VOICE            GPT Realtime 2.1: native audio in/out, lip sync via PlayerLips
Layer 3  COGNITION        same model, event-driven: goals, dialogue, tool calls
                          in: mic audio + compact JSON observations + screenshots
                          out: speech + tool calls from the verb vocabulary
              │
Layer 2  SKILLS           deterministic FSM/behavior tree (~10 Hz)
                          goto(x) · follow(player) · pickup(prop) · use_switch(s)
                          · pose · point_at · say(text) · stop
                          each reports success/failure upward
              │
Layer 1  NAVIGATION       classical (~10 Hz): path planning, local steering, stuck recovery
                          a) breadcrumb-follow the human's trail (MVP — humans prove traversability)
                          b) persistent "experience graph" of walked terrain + A* for independent goals
                          c) probe Unity NavMesh runtime API (likely IL2CPP-stripped; raycast grid fallback)
              │
Layer 0  MOTOR            50 Hz FixedUpdate: write controlsVelocity, look-at, jump, animation state
```

Frame-level control never touches the model. The model is invoked event-driven (player spoke, skill finished/failed, salient object in range, periodic heartbeat), acting through tools.

### Model layer: GPT Realtime 2.1

[gpt-realtime-2.1](https://developers.openai.com/api/docs/models/gpt-realtime-2.1) is the working choice for Layers 3–4 — a single speech-to-speech model that collapses STT → LLM → TTS into one low-latency loop, which is exactly the companion use case.

Why it fits:

- **Native audio in/out** — conversational latency without a pipeline; interruption handling and turn detection built in.
- **Function calling, including mid-conversation and async** — our skill verbs become its tools; it can keep talking while a `goto` executes, and the API provides placeholder language while a tool is pending.
- **Native image input** — we can feed screenshots (e.g., the bot's POV) for scenery commentary and visual puzzle context, on demand rather than continuously.
- **Configurable reasoning effort** — dial up for puzzle moments, down for banter.
- **128k context, sessions up to 60 min**, automatic context truncation with a tunable retention ratio (set ~0.8 to preserve prompt-cache efficiency).

Known constraints and how we absorb them:

| Constraint | Mitigation |
| --- | --- |
| 60-minute session cap | Session hand-off: summarize state/memory, re-open session with the summary in instructions |
| Audio pricing ($32/1M in, $64/1M out; cached input ~$0.4/1M) — real conversation costs tens of cents per active minute | Aggressive prompt caching; mute/idle detection so silence isn't streamed; [gpt-realtime-2.1-mini](https://developers.openai.com/api/docs/models/gpt-realtime-2.1-mini) as the default tier if quality allows |
| Optimized for speech, not deep reasoning | Give it an `ask_brain` tool that consults a stronger text model for hard puzzles and returns advice into the conversation |
| Instructions + tools token budget is capped; temperature control removed | Keep the system prompt and tool schemas compact; steer style via prompting |
| `v1/realtime` endpoint only (WebRTC/WebSocket/SIP) | The mod owns a persistent WebSocket client; nothing else needed |

The observation feed stays compact structured JSON (< ~1k tokens: positions, what the human holds/points at, nearby named interactables, active goal, recent events) injected as conversation items — screenshots are a supplement, not the primary sense.

## Roadmap

Current milestone status:

1. **Walk-to-point** — motor slice complete: the bot autonomously completed a bounded `1.5 m` traverse via `controlsVelocity`, stopped within tolerance, and left host locality untouched. Reachable-point selection remains.
2. **Breadcrumb follow** — bounded slice complete: the bot recorded and followed a short trail, used local rigidbody sweeps to steer around one obstacle, and maintained follow distance. Varied terrain, dynamic obstacles, and stuck recovery remain.
3. **First interaction** — walk to a `PeckSwitch` and use it via the server-side action path.
4. **Cognition loop** — GPT Realtime 2.1 session with three tools (`say`, `goto_player`, `use_switch`) driven by game events; text observations only.
5. **Voice round-trip** — host mic → model → bot 3D audio + `PlayerLips` sync (host-only audible speech).
6. **Screenshots + puzzle assist** — image input from bot POV; `ask_brain` escalation.
7. **(Optional, hard)** Distinct bot voice for unmodified remote guests via Dissonance packet injection.

## Repository map

```
README.md                  ← this hub
docs/
  archive/
    HOST_MOD_FEASIBILITY.md← original feasibility record: raw probe logs, hashes, evidence tables
    PROBE_HISTORY_0.2.0-0.5.2.md ← archived experiment history through probe 0.5.2
probe/
  BigWalkBotProbe.cs       ← active probe source (v0.4.0 breadcrumb follow and local obstacle avoidance)
  build/compile.rsp        ← Roslyn response file to rebuild the probe DLL
  build/BigWalkBotProbe.dll ← runtime-verified v0.4.0 build; deployed probes are preserved locally as disabled copies
.analysis/                 ← Cpp2IL output: recovered C# (cpp2il-cs/DiffableCs), dummy DLLs, ISIL, IL
analysis_scripts/
  dump_recovered_il.py     ← IL dump helper
.tools/                    ← Cpp2IL, BepInEx 6.0.755 package, Roslyn 4.14.0, python packages
```

Deployment state: BepInEx lives in the Big Walk install directory; deployed probes are renamed `*.disabled` when not under an explicit runtime test so they do not auto-load. Re-enable the active probe by restoring its `.dll` extension.

## Working conventions

- **Never silently promote** static-analysis conclusions or design intentions into runtime-confirmed facts. The three tiers (runtime-confirmed / static finding / untested design) are labeled everywhere.
- **Update this README at meaningful milestones:** record what became runtime-confirmed, what was falsified, and what remains untested without maintaining a per-run deployment diary.
- **Everything reversible.** Deployed artifacts are disabled (renamed), not deleted; stock game files are never rewritten.
