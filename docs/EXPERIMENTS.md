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
