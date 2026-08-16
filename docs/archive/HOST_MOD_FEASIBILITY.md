# Big Walk AI Companion: Host-Mod Feasibility Record

> **Archived 2026-08-15.** Feasibility is proven; this record is kept for its raw runtime evidence, probe hashes, and detailed static-analysis notes. The living project hub is now [README.md](../../README.md) and the experiment log continues in [EXPERIMENTS.md](../EXPERIMENTS.md).

Last updated: 2026-08-15

This is the living technical record for the Big Walk AI companion project. It distinguishes runtime-confirmed behavior from static-analysis findings and untested design proposals.

## Goal and constraint

Create an AI-controlled companion that appears and behaves as a complete second player inside the host's Big Walk session.

Hard constraint: the implementation must be a host mod. It must not launch or require a secondary Big Walk client.

### Modification boundary

The approach does **not** rewrite Big Walk's executable, IL2CPP binary, metadata, assets, or save format. BepInEx adds loader/mod files beside the game, and the mod uses Harmony hooks to alter selected method behavior only in the running process. Removing or disabling the mod restores the stock method behavior on the next launch.

This document uses "patch" to mean a mod-owned, in-memory Harmony hook unless it explicitly says otherwise. No persistent binary patch to the game is planned.

## Current verdict

**A host-only synthetic second player is feasible on the tested build.**

The runtime probe created a connectionless clone of Big Walk's configured player prefab and spawned it through Mirror. The object was registered as a second live `PlayerCharacter`, existed on both the host server and host client, remained a non-local player, and acquired a separately tracked Dissonance player identity.

This proves the basic body/network-identity mechanism. It does not yet prove autonomous movement, the complete interaction surface, or transmission of synthetic speech as a distinct remote speaker.

## Tested environment

| Item | Tested value |
| --- | --- |
| Game | Big Walk, Steam app `1478500` |
| Game version | `1.4.8 2608070648` |
| Steam build ID | `24611934` |
| Unity | `6000.3.17f1` |
| Scripting backend | IL2CPP, metadata version 39, 64-bit |
| Host mod loader | BepInEx IL2CPP `6.0.0-be.755` / package `6.0.755` |
| Test mode | Two-player host game, one Big Walk process |
| Test save | `BotProbe` |

## Runtime evidence

The first runtime probe produced this result:

```text
[BOT-PROBE] Spawn requested: netId=580,
connectionToClient=null,
position=(-240.98, 37.26, -483.17).

[BOT-PROBE] VERIFY
netId=580,
isServer=True,
isClient=True,
isLocalPlayer=False,
serverOwnsTransform=False,
connectionToClient=null,
registeredPlayerCharacters=2,
voicePlayerId=NitrogenHostBot,
voiceTracking=True,
playerCharacterPresent=True.
```

The revised `0.2.0` probe then produced a clean result:

```text
[BOT-PROBE] Spawn requested: netId=580,
connectionToClient=null,
position=(-240.98, 37.26, -483.17).

[BOT-PROBE] Bypassed connection-dependent PlayerNetworking.Start for bot.

[BOT-PROBE] VERIFY
netId=580,
isServer=True,
isClient=True,
isLocalPlayer=False,
serverOwnsTransform=True,
connectionToClient=null,
registeredPlayerCharacters=2,
voicePlayerId=NitrogenHostBot,
voiceTracking=True,
playerCharacterPresent=True.
```

No `PlayerNetworking.Start()` exception occurred in this second run.

### What this proves

- The host can instantiate the real player prefab without creating another process.
- Mirror accepts `NetworkServer.Spawn(bot)` with no `connectionToClient`.
- The object receives a valid network ID.
- The host's client sees the object as a remote/non-local player.
- `PlayerCharacter.allPlayerCharacters` contains both the human host and bot.
- A separate Dissonance player ID can be assigned and tracked for the bot body.

### What the first run exposed

The stock `PlayerNetworking.Start()` path threw a `NullReferenceException` because it assumes every server-side player has a client connection and authentication data:

```text
System.NullReferenceException
  at PlayerNetworking.Start()
```

The body survived and the verification completed. Version `0.2.0` resolved the issue with a bot-only Harmony prefix that bypasses the stock connection-dependent `Start` method. It does not alter the game binary on disk.

`HouseNetworkTransform.isOwned` also initially returned `false`. Its stock logic treats player objects differently from ordinary server-owned objects: player transforms are normally owned only by their local client. Version `0.2.0` resolved this with a bot-only Harmony postfix; the second runtime test returned `serverOwnsTransform=True`.

## Probe status

Source: [`probe/BigWalkBotProbe.cs`](../../probe/BigWalkBotProbe.cs)

Build response file: [`probe/build/compile.rsp`](../../probe/build/compile.rsp)

Two probe builds currently exist:

| Build | Location | SHA-256 | Status |
| --- | --- | --- | --- |
| `0.1.0` | Historical deployed build; replaced by `0.2.0` | `169D74094B627EF0D737D3A268FDE496501E4785C52081DA007E812EE5D4EF70` | Loaded during the first test; exposed two stock assumptions |
| `0.2.0` | `probe/build/BigWalkBotProbe.dll` and disabled deployed copy | `5E74C889692D88E422341D68FC1969489D146D74C68DA7D9844A1798D4775B43` | Runtime verified successfully, then disabled |

Version `0.2.0` changes bot detection from object-name-only matching to the synthetic network identifier and adds a bot-specific postfix for `HouseNetworkTransform.isOwned`.

After the clean test, the deployed artifacts were reversibly renamed to:

- `BigWalkBotProbe.dll.disabled`
- `BigWalkBotProbe.pdb.disabled`

BepInEx will not load the auto-spawn DLL in that state. The compiled project copy remains available for development.

## Static-analysis findings

### Normal player creation

`HouseNetworkManager.OnServerAddPlayer` starts an `AddPlayerDelayed` flow. The normal flow:

1. Waits for the connection and scenes to become ready.
2. Reads `HouseAuthenticator.InitialialAuthRequestMessage` from `connection.authenticationData`.
3. Selects a corpse or start position.
4. Instantiates `NetworkManager.playerPrefab`.
5. Calls `NetworkServer.AddPlayerForConnection(connection, gameObject)`.

The connection supplies user identity and authority. It is not required for the prefab's existence, which is why the host-only `NetworkServer.Spawn` experiment works once connection-dependent initialization is replaced.

### Player prefab behavior

- `PlayerCharacter.Awake` initializes the full player component stack: hands, movement, looks, gestures, lips, cameras, and collision modules.
- `PlayerCharacter.Start` has a remote-player branch when `isLocalPlayer == false`; it does not take the host camera or normal local input.
- `PlayerCharacter.OnEnable` registers the instance in `PlayerCharacter.allPlayerCharacters`.
- The runtime result confirms that the synthetic player follows this remote-body path and enters the live registry.

### Player enumeration and puzzle selection

- The chosen two/three/four-player world layout comes from `PlayerCountSwapper.playerCount` and is selected when the host creates or loads the game.
- It is not derived from Mirror's live connection count.
- Systems found consuming `PlayerCharacter.allPlayerCharacters` include train, teleporter, text-input, moderation, menu, and networking code.

This means a connectionless bot can be visible to gameplay systems that enumerate actual player characters. Individual puzzles and scripted interactions still require integration tests.

### Interaction surface

`PlayerNetworking` exposes commands and underlying server-side implementations for actions including:

- Pick up, drop, pose, and use held objects
- Peck and release switches
- Gestures, waves, and pointing
- Movement-control, velocity, head-state, and appearance synchronization
- Text chat
- Jump, sit, sleep, and ghost state

The bot should not call the ordinary client `Cmd*` wrappers because those assume client authority and transport. A host mod can call or reimplement the corresponding server-side action paths for the synthetic player.

### Voice

The player prefab includes `Dissonance.Integrations.MirrorIgnorance.MirrorIgnorancePlayer`, whose player ID can be assigned before spawn. The first test confirmed `PlayerId=NitrogenHostBot` and `IsTracking=True`.

That proves spatial identity tracking, not audible speech transport.

Two voice scopes should be treated separately:

1. **Host-only audible companion:** generate or stream speech into a 3D audio source attached to the bot and feed amplitude/state into the lip system. This is the smaller first milestone.
2. **Distinct bot voice audible to unmodified remote guests:** inject Dissonance protocol audio under the bot's player ID into the host's existing relay. This appears technically possible but requires protocol-level implementation and has not been proven.

## Proposed production architecture

The smallest credible host-only architecture is:

1. **Bot bootstrap**
   - Wait for an active host session and local player.
   - Clone `NetworkManager.playerPrefab` near the host.
   - Assign synthetic identity SyncVars and a bot marker.
   - Bypass connection-auth initialization.
   - Spawn with `NetworkServer.Spawn`.

2. **Bot driver**
   - Own the bot transform on the server.
   - Supply desired movement, look, posture, gesture, and interaction state.
   - Drive the existing player physics/components where practical rather than replacing the character controller.

3. **World-state adapter**
   - Read live Unity objects directly: transforms, nearby props, switches, held items, players, and salient level state.
   - Produce a compact structured observation for the behavior system.

4. **Behavior stack**
   - Use deterministic/reactive control for locomotion, collision recovery, following, and interaction execution.
   - Use a higher-level model for social choices, goals, dialogue, and deciding what to attend to.
   - Avoid placing frame-level motor control in an LLM loop.

5. **Social and voice layer**
   - Begin with local spatial TTS and lip synchronization.
   - Treat multiplayer Dissonance packet injection as a later, independent milestone.

No secondary client is part of this architecture.

## Evidence boundaries

| Capability | Status |
| --- | --- |
| Host-only second player object | Runtime confirmed |
| Mirror network identity without client connection | Runtime confirmed |
| Host client renders/registers it as remote player | Runtime confirmed at object/registry level |
| Separate Dissonance tracked identity | Runtime confirmed |
| Clean connectionless lifecycle | Runtime confirmed with mod-owned Harmony hook |
| Server authority over player transform | Runtime confirmed with mod-owned Harmony hook |
| Controlled walking and looking | Not implemented |
| Object and puzzle interactions | Static path identified; runtime tests pending |
| Local 3D synthetic speech | Design identified; not implemented |
| Distinct bot speech for remote guests | Not proven |
| General AI/world model | Not designed beyond component boundaries |

## Immediate next experiment

Build the next bounded proof: make the bot walk to a host-selected point while remaining a remote player.

Success criteria:

1. The bot moves under server control without becoming `isLocalPlayer`.
2. The host camera and input remain attached exclusively to the human player.
3. Remote-body animation and network transform state remain coherent.
4. The bot stops within a defined tolerance of the target.
5. The bot recovers from a blocked straight-line route without teleporting.
6. No stock game files are rewritten.

## Experiment log

### 2026-08-15: clean connectionless spawn and ownership

- **Hypothesis:** a host mod can create a complete remote-style second player without a second client, provided it replaces connection-only initialization and grants bot-only server transform ownership.
- **Setup:** Big Walk `1.4.8 2608070648`; BepInEx IL2CPP build 755; two-player `BotProbe` host save; probe `0.2.0`; one Big Walk process.
- **Observed:** spawn received `netId=580`; server and host client both recognized it; `isLocalPlayer=False`; `connectionToClient=null`; live player registry count was `2`; `serverOwnsTransform=True`; Dissonance identity `NitrogenHostBot` was tracking; no connection-dependent lifecycle exception occurred.
- **Result:** hypothesis confirmed for body creation, registration, connectionless lifecycle, transform ownership, and voice-identity tracking.
- **Not established:** autonomous locomotion, complete interaction compatibility, audible synthetic speech, or remote-guest voice delivery.
- **Cleanup:** Big Walk was closed and the temporary deployed probe was renamed to `.disabled`; original game binaries and data were not modified.

## Local analysis inventory

- Cpp2IL executable: `.tools/cpp2il/Cpp2IL-2022.1.0-pre-release.21-Windows.exe`
- Cpp2IL dummy assemblies: `.analysis/cpp2il-dummydll`
- Recovered C# output: `.analysis/cpp2il-cs`
- ISIL output: `.analysis/cpp2il-isil`
- IL recovery output: `.analysis/cpp2il-ilrecovery`
- IL helper: `analysis_scripts/dump_recovered_il.py`
- Roslyn compiler package: `.tools/roslyn-4.14.0`

These artifacts are local to the project except for BepInEx and the disabled deployed probe copy, which live in the Big Walk installation directory. BepInEx added files to the installation directory but did not rewrite the original game executable or data files.

## Update convention

For every subsequent experiment, append:

- Date and game/mod version
- Hypothesis
- Exact runtime setup
- Observed log/result
- Whether the result confirms, falsifies, or leaves the hypothesis unresolved
- New artifacts and their deployment state

Do not silently promote static-analysis conclusions or architectural expectations into runtime-confirmed facts.
