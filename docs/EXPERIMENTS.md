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

Full raw probe logs and build hashes: [archive/HOST_MOD_FEASIBILITY.md](archive/HOST_MOD_FEASIBILITY.md).

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
