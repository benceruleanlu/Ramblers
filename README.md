# Ramblers

An experimental AI companion mod for [Big Walk](https://bigwalk.game/) by House House.

The goal is to play the whole game with Rambler as your teammate: exploring together, communicating, and working through puzzles without having to direct its every move.

> **Under active development.** Rambler cannot yet complete Big Walk with you. All `0.x` builds are experimental and are not ready for general use.

## Current state

Ramblers runs inside the host's game process and spawns one companion that follows you by default. It does not require a second game client.

The current implementation supports:

- Talking through Big Walk's voice controls, with spoken replies from the companion.
- Following, staying, changing posture, jumping, and walking to a place you indicate.
- Picking up, dropping, and kicking props; carrying and setting down the host player; and using supported switches, seats, and item holders.
- Looking at something you indicate and receiving an image from the companion's own point of view.
- Receiving nearby world updates between conversations and continuing compatible actions while you talk.

These are building blocks for a teammate. Self-directed play, navigation to remembered places, coordinated puzzle actions, complete in-game communication support, and memory across sessions remain unfinished. Individual actions working does not establish that a puzzle or the campaign is playable.

The goal and remaining work are tracked in [the AI teammate roadmap (#53)](https://github.com/benceruleanlu/Ramblers/issues/53).

## Development setup

Development has used Windows, Big Walk `1.4.10` (build `2608201131`), and BepInEx IL2CPP `6.0.0-be.755`. Other game or loader versions are unverified.

You need a BepInEx installation that has been launched at least once to generate the game's interop assemblies, Windows PowerShell 5.1 or newer, and your own OpenAI API key.

1. Set `OPENAI_API_KEY` in the game's process environment or your Windows user environment before launching the game. Ramblers reads it from there; do not put it in the repository.
2. Build from the repository root:

   ```powershell
   .\build.ps1
   ```

3. With Big Walk closed, copy `dist/Ramblers.dll` and `dist/StbImageWriteSharp.dll` into `BepInEx/plugins/Ramblers` under the game directory.
4. Launch Big Walk and host a session. Rambler spawns automatically. Use the game's normal toggle-to-talk or push-to-talk controls to speak to it.

To disable the mod, close the game and rename the installed `Ramblers.dll` to `Ramblers.dll.disabled`.

Voice and perception use OpenAI Realtime. Microphone audio, captured images, and game context are sent to OpenAI, and API usage is billed to your account. The current speech output is audible only on the host's client; remote guests cannot hear Rambler.

## Building and checking changes

[build.ps1](build.ps1) discovers Big Walk through Steam and downloads a pinned Roslyn compiler into `.tools/` on first use. It writes the plugin and its JPEG dependency to `dist/`; it does not deploy them into the game.

Use `-GamePath` or `RAMBLERS_GAME_PATH` to select the game directory, and `-CompilerPath` or `RAMBLERS_CSC_PATH` to select the compiler. Add `-NoRestore` to build without downloading a compiler.

Run the protocol checks with PowerShell 7 after the compiler is available:

```powershell
pwsh -NoProfile -File .\analysis_scripts\Run-ProtocolTests.ps1
```

[Audit-LatestRun.ps1](analysis_scripts/Audit-LatestRun.ps1) rebuilds `dist/` and compares the build, installed plugin, and latest runtime log. It also checks recorded action and conversation behavior. Add `-RequireTurn` to require evidence of a spoken turn.

Builds and protocol checks validate implementation details. In-game testing is still needed to verify movement, interactions, communication, and puzzle completion.
