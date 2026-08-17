# Active Experiment Log

Runtime evidence after the archived probe history in [`archive/PROBE_HISTORY_0.2.0-0.5.2.md`](archive/PROBE_HISTORY_0.2.0-0.5.2.md). Compilation and static inspection are not runtime proof.

## 2026-08-16: probe 0.5.3 spatial route repaired; local transmit signal falsified

- **Setup:** Big Walk `1.4.9 2608141617`; BepInEx IL2CPP build 755; probe `0.5.3`; one host process; OpenAI Realtime `gpt-realtime-2.1`; Big Walk voice mode set to toggle.
- **Observed:** the plugin loaded, OpenAI logged `CONNECTED` and `READY tools=set_follow_mode`, and Nitrogen spawned with `connectionToClient=null`, `isLocalPlayer=False`, registry count `2`, and voice tracking active. The new route resolver logged `DIRECT_VOICE_ROUTE source=loaded_playback_asset`, proving the stock attenuation curve was available at runtime. The adapter logged `GAME_VOICE_READY source=game_vad, triggerMode=VoiceActivation`, but no `LISTENING` or `IGNORED` transition occurred during speech.
- **Result:** the `0.5.2` spatial failure was repaired. The hypothesis that local `VoiceBroadcastTrigger.IsTransmitting` provides a usable speech-activity signal in Big Walk toggle mode was falsified.
- **Artifact:** DLL SHA-256 `6468F10CE371043152A776287F6296F2F76ABBFE3ADB98F1E476D91D15AAB13C`.

## 2026-08-16: probe 0.5.4 toggle-to-talk follow/stay round-trip passed

- **Hypothesis:** Big Walk's game-owned mute/toggle state can be the hard input gate while Dissonance processed amplitude and recent samples from the existing `MicManager` clip split an open toggle period into bounded agent turns.
- **Setup:** same game, BepInEx, model, host-only process, and toggle voice mode; probe `0.5.4`; Nitrogen approximately `2.5-3.5 m` from the human.
- **Observed:** runtime logged `GAME_VOICE_STATE channelOpen=True, mode=toggle_or_open`, `GAME_VOICE_READY source=game_voice_activity`, and `DIRECT_VOICE_ROUTE source=loaded_playback_asset`. Two separate speech turns began and submitted after silence: `0.83 s` at `2.48 m` with evaluated audibility `0.772418`, then `0.68 s` at `3.48 m` with audibility `0.691166`. OpenAI selected `set_follow_mode(follow)` for the first turn and `set_follow_mode(stay)` for the second. Both tools returned structured success; follow produced stock-motor movement and stay stopped it.
- **Result:** confirmed for near-range toggle-open speech activity, consecutive silence-separated turns without retoggling, OpenAI tool selection, structured results, and deterministic follow/stay execution. The human remained the local player.
- **Not established:** toggle-off suppression, direct out-of-range rejection, false-positive rate under background noise, radio routing, or audible bot speech.
- **Artifact:** the repository build produced from this source and the active deployed DLL both matched SHA-256 `DDA7DF6B573D0E4634CFB5588C41845A0DDB2C0B16878E846F717300920557ED` at checkpoint time.

## 2026-08-16: probe 0.6.0 architecture cleanup regression passed

- **Change:** removed the automated leader/obstacle diagnostic and automatic-follow bypass; removed the falsified local Dissonance transmit/amplitude heuristics and arbitrary proximity-range fallback; separated the bot controller, game-voice input, tool router, Realtime lifecycle bridge, and WebSocket client; enabled deterministic compilation.
- **Observed:** the plugin loaded as `0.6.0`, OpenAI reached `CONNECTED` and `READY`, and Nitrogen verified as the same connectionless server-owned non-local player. Near-range turns at `2.86 m` and `6.22 m` selected `follow` then `stay`, with both structured tool results succeeding. At `55.14 m`, the loaded stock attenuation curve evaluated to zero and three speech attempts logged `IGNORED reason=out_of_range`; returning in range restored listening and tool execution. The user accepted the cleanup regression test as working.
- **Result:** the cleanup preserved spawn, toggle-mode speech segmentation, tool routing, follow/stay execution, and direct-voice spatial rejection. Toggle-off suppression still lacks an explicit `channelOpen=False` log in this run.
- **Follow-up:** one rapid conversational sequence produced `API_ERROR Conversation already has an active response in progress`. It did not invalidate the successful tool calls, but input/response serialization remains an explicit reliability task.
- **Artifact:** two consecutive deterministic builds produced identical artifacts, and the repository/deployed DLL matched SHA-256 `960886B6567645E27E61CC3A4DECEB90C62CE0EDBB4735DEC46737D67B5A3196` at checkpoint time.
