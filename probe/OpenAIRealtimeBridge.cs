extern alias websocketclient;
extern alias websockets;
extern alias privateuri;

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dissonance;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using ClientWebSocket = websocketclient::System.Net.WebSockets.ClientWebSocket;
using WebSocketMessageType = websockets::System.Net.WebSockets.WebSocketMessageType;
using WebSocketReceiveResult = websockets::System.Net.WebSockets.WebSocketReceiveResult;
using WebSocketState = websockets::System.Net.WebSockets.WebSocketState;
using RuntimeUri = privateuri::System.Uri;

namespace BigWalkBotProbe;

/// <summary>
/// Main-thread boundary between OpenAI tool calls and the deterministic bot
/// controller. The network client never touches Unity objects directly.
/// </summary>
internal sealed class RealtimeAgentBridge : MonoBehaviour
{
    private const int RealtimeSampleRate = 24000;
    private const int MinimumTurnSamples = RealtimeSampleRate / 10;
    private const float ReconnectDelay = 5f;
    private const float AudibilityThreshold = 0.0001f;
    private const float OpenMicrophoneAmplitudeThreshold = 0.01f;
    private const float OpenMicrophoneRmsThreshold = 0.006f;
    private const float OpenMicrophoneSilenceHangover = 0.45f;

    private OpenAIRealtimeClient _client;
    private AudioClip _microphoneClip;
    private Il2CppStructArray<float> _voiceActivitySamples;
    private bool _capturingTurn;
    private int _capturedSamples;
    private int _microphoneReadPosition = -1;
    private int _microphoneFrequency;
    private int _microphoneChannels;
    private int _resampleAccumulator;
    private float _nextConnectAt;
    private bool _missingKeyLogged;
    private bool _microphoneReadyLogged;
    private bool _voiceIntentWasActive;
    private bool _voiceStateUnavailableLogged;
    private bool _audibilityUnavailableLogged;
    private bool _voiceChannelStateInitialized;
    private bool _lastVoiceChannelOpen;
    private float _silenceStartedAt = -1f;
    private float _nextVoiceActivityDiagnosticAt;
    private float _nextVoiceRouteResolveAt;
    private string _captureSource;
    private string _configuredVoiceSource;
    private string _directVoiceRouteSource;
    private float _captureDistance;
    private float _captureAudibility;
    private float _directVoiceRange = -1f;
    private AnimationCurve _directVoiceAttenuationCurve;

    public RealtimeAgentBridge(IntPtr pointer) : base(pointer)
    {
    }

    private void Update()
    {
        if (Plugin.EnableRealtimeAgent == null || !Plugin.EnableRealtimeAgent.Value)
        {
            StopClient();
            return;
        }

        EnsureClient();
        DrainClientEvents();
        TickGameVoice();
        if (_capturingTurn)
            CaptureMicrophoneSamples();
    }

    private void EnsureClient()
    {
        if (_client != null && !_client.IsStopped)
            return;

        if (_client != null)
        {
            _client.Dispose();
            _client = null;
            _nextConnectAt = Time.realtimeSinceStartup + ReconnectDelay;
        }

        if (Time.realtimeSinceStartup < _nextConnectAt)
            return;

        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            try
            {
                // Steam may have been running before the user added the variable,
                // so its child process can inherit a stale environment block.
                apiKey = Environment.GetEnvironmentVariable(
                    "OPENAI_API_KEY",
                    EnvironmentVariableTarget.User);
            }
            catch (PlatformNotSupportedException)
            {
                // Big Walk currently targets Windows; retain the process-only
                // behavior if this code is ever exercised elsewhere.
            }
        }
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (!_missingKeyLogged)
            {
                _missingKeyLogged = true;
                Plugin.Logger.LogWarning(
                    "[BOT-AGENT] OpenAI disabled for this run: OPENAI_API_KEY is not present " +
                    "in the process or Windows user environment. " +
                    "No microphone audio will be sent.");
            }
            return;
        }

        _missingKeyLogged = false;
        var model = string.IsNullOrWhiteSpace(Plugin.OpenAIRealtimeModel.Value)
            ? "gpt-realtime-2.1"
            : Plugin.OpenAIRealtimeModel.Value.Trim();
        _client = new OpenAIRealtimeClient(apiKey.Trim(), model);
        _client.Start();
        Plugin.Logger.LogInfo(
            $"[BOT-AGENT] Connecting to OpenAI Realtime model {model}. " +
            "Listening follows Big Walk voice controls and direct proximity.");
    }

    private void DrainClientEvents()
    {
        if (_client == null)
            return;

        string message;
        while (_client.TryDequeueLog(out message))
            Plugin.Logger.LogInfo($"[BOT-AGENT] {message}");

        RealtimeFunctionCall functionCall;
        while (_client.TryDequeueFunctionCall(out functionCall))
        {
            string mode;
            string result;
            if (!TryReadFollowMode(functionCall.Arguments, out mode))
            {
                result = "{\"ok\":false,\"error\":\"invalid_arguments\"}";
            }
            else
            {
                result = ProbeRunner.ExecuteAgentTool(functionCall.Name, mode);
            }

            Plugin.Logger.LogInfo(
                $"[BOT-AGENT] CALL name={functionCall.Name}, arguments={functionCall.Arguments}, " +
                $"result={result}");
            _client.SendFunctionResult(functionCall.CallId, result);
        }
    }

    private static bool TryReadFollowMode(string arguments, out string mode)
    {
        mode = null;
        try
        {
            using var document = JsonDocument.Parse(arguments);
            JsonElement value;
            if (!document.RootElement.TryGetProperty("mode", out value) ||
                value.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            mode = value.GetString();
            return string.Equals(mode, "follow", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(mode, "stay", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void TickGameVoice()
    {
        if (_client == null || !_client.IsReady)
        {
            if (_capturingTurn)
                EndVoiceTurn(false);
            _voiceIntentWasActive = false;
            _silenceStartedAt = -1f;
            return;
        }

        bool voiceIntentActive;
        bool useSilenceHangover;
        string source;
        if (!TryGetGameVoiceIntent(
                out voiceIntentActive,
                out useSilenceHangover,
                out source))
        {
            if (_capturingTurn)
                EndVoiceTurn(false);
            _voiceIntentWasActive = false;
            _silenceStartedAt = -1f;
            return;
        }

        float distance;
        float audibility;
        var directVoiceAudible = TryGetDirectVoiceAudibility(out distance, out audibility);

        if (voiceIntentActive && !directVoiceAudible)
        {
            if (!_voiceIntentWasActive)
            {
                var reason = audibility < 0f ? "route_unavailable" : "out_of_range";
                Plugin.Logger.LogInfo(
                    $"[BOT-AGENT] IGNORED source={source}, route=direct, " +
                    $"reason={reason}, distance={distance:F2}, audibility={audibility:F6}");
            }

            if (_capturingTurn)
                EndVoiceTurn(false);
            _voiceIntentWasActive = true;
            _silenceStartedAt = -1f;
            return;
        }

        if (voiceIntentActive)
        {
            _silenceStartedAt = -1f;
            if (!_capturingTurn)
                BeginVoiceTurn(source, distance, audibility);
        }
        else if (_capturingTurn)
        {
            if (!useSilenceHangover)
            {
                EndVoiceTurn(true);
            }
            else if (_silenceStartedAt < 0f)
            {
                _silenceStartedAt = Time.realtimeSinceStartup;
            }
            else if (Time.realtimeSinceStartup - _silenceStartedAt >=
                     OpenMicrophoneSilenceHangover)
            {
                EndVoiceTurn(true);
            }
        }

        _voiceIntentWasActive = voiceIntentActive;
    }

    private bool TryGetGameVoiceIntent(
        out bool active,
        out bool useSilenceHangover,
        out string source)
    {
        active = false;
        useSilenceHangover = false;
        source = "game_voice";

        var world = WorldManager.instance;
        var human = WorldManager.localPlayerCharacter;
        var comms = world == null ? null : world.dissonanceComms;
        if (world == null || human == null || comms == null)
            return false;

        var channelOpen = !world.forceMutedBySystem && !comms.IsMuted;
        LogVoiceChannelState(channelOpen);

        if (SettingsHelper.pushToTalkModeActive)
        {
            _voiceStateUnavailableLogged = false;
            source = "game_ptt";
            LogConfiguredVoiceSource(source, null);
            active = channelOpen;
            return true;
        }

        var trigger = human.lips == null ? null : human.lips.broadcastTrigger;
        var localVoice = string.IsNullOrWhiteSpace(comms.LocalPlayerName)
            ? null
            : comms.FindPlayer(comms.LocalPlayerName);
        float microphoneRms;
        var hasMicrophoneLevel = TryGetRecentMicrophoneRms(out microphoneRms);
        if (trigger != null || localVoice != null || hasMicrophoneLevel)
        {
            _voiceStateUnavailableLogged = false;
            source = "game_voice_activity";
            LogConfiguredVoiceSource(source, trigger);
            useSilenceHangover = channelOpen;
            var triggerActive = trigger != null && trigger.IsTransmitting;
            var processedAmplitude = localVoice == null ? 0f : localVoice.Amplitude;
            active = channelOpen &&
                     (triggerActive ||
                      processedAmplitude >= OpenMicrophoneAmplitudeThreshold ||
                      (hasMicrophoneLevel && microphoneRms >= OpenMicrophoneRmsThreshold));

            if (channelOpen && !active &&
                Time.realtimeSinceStartup >= _nextVoiceActivityDiagnosticAt)
            {
                _nextVoiceActivityDiagnosticAt = Time.realtimeSinceStartup + 1f;
                Plugin.Logger.LogInfo(
                    $"[BOT-AGENT] VOICE_ACTIVITY trigger={triggerActive}, " +
                    $"amplitude={processedAmplitude:F4}, micRms={microphoneRms:F4}");
            }
            return true;
        }

        if (!_voiceStateUnavailableLogged)
        {
            _voiceStateUnavailableLogged = true;
            Plugin.Logger.LogWarning(
                "[BOT-AGENT] Big Walk voice state is not ready; agent listening is paused.");
        }
        return false;
    }

    private void LogVoiceChannelState(bool channelOpen)
    {
        if (_voiceChannelStateInitialized && _lastVoiceChannelOpen == channelOpen)
            return;

        _voiceChannelStateInitialized = true;
        _lastVoiceChannelOpen = channelOpen;
        Plugin.Logger.LogInfo(
            $"[BOT-AGENT] GAME_VOICE_STATE channelOpen={channelOpen}, " +
            $"mode={(SettingsHelper.pushToTalkModeActive ? "hold" : "toggle_or_open")}");
    }

    private bool TryGetRecentMicrophoneRms(out float rms)
    {
        rms = 0f;
        if (!MicManager.IsRecording(null))
            return false;

        var clip = MicManager.GetClip(null);
        var position = MicManager.GetPosition(null);
        if (clip == null || position <= 0 || clip.frequency <= 0 ||
            clip.channels <= 0 || clip.samples <= 0)
        {
            return false;
        }

        var frameCount = Math.Max(1, clip.frequency / 50);
        if (position < frameCount)
            return false;

        var sampleCount = frameCount * clip.channels;
        if (_voiceActivitySamples == null || _voiceActivitySamples.Length != sampleCount)
            _voiceActivitySamples = new Il2CppStructArray<float>(sampleCount);

        if (!clip.GetData(_voiceActivitySamples, position - frameCount))
            return false;

        double sumSquares = 0;
        for (var index = 0; index < _voiceActivitySamples.Length; index++)
        {
            var sample = _voiceActivitySamples[index];
            sumSquares += sample * sample;
        }

        rms = (float)Math.Sqrt(sumSquares / Math.Max(1, _voiceActivitySamples.Length));
        return true;
    }

    private void LogConfiguredVoiceSource(string source, VoiceBroadcastTrigger trigger)
    {
        if (string.Equals(_configuredVoiceSource, source, StringComparison.Ordinal))
            return;

        _configuredVoiceSource = source;
        var triggerMode = trigger == null ? "<none>" : trigger.Mode.ToString();
        Plugin.Logger.LogInfo(
            $"[BOT-AGENT] GAME_VOICE_READY source={source}, triggerMode={triggerMode}");
    }

    private bool TryGetDirectVoiceAudibility(out float distance, out float audibility)
    {
        distance = -1f;
        audibility = -1f;

        PlayerCharacter human;
        PlayerCharacter bot;
        if (!ProbeRunner.TryGetVoiceParticipants(out human, out bot))
            return false;

        distance = Vector3.Distance(human.transform.position, bot.transform.position);
        if (_directVoiceAttenuationCurve == null && _directVoiceRange <= 0f &&
            Time.realtimeSinceStartup >= _nextVoiceRouteResolveAt)
        {
            _nextVoiceRouteResolveAt = Time.realtimeSinceStartup + 1f;
            ResolveDirectVoiceRoute(bot);
        }

        if (_directVoiceAttenuationCurve != null)
        {
            _audibilityUnavailableLogged = false;
            audibility = _directVoiceAttenuationCurve.Evaluate(distance);
            return audibility > AudibilityThreshold;
        }

        if (_directVoiceRange > 0f)
        {
            _audibilityUnavailableLogged = false;
            audibility = Mathf.Clamp01(1f - distance / _directVoiceRange);
            return distance <= _directVoiceRange;
        }

        if (!_audibilityUnavailableLogged)
        {
            _audibilityUnavailableLogged = true;
            Plugin.Logger.LogWarning(
                "[BOT-AGENT] Big Walk's direct-voice route is not ready; " +
                "agent listening is paused.");
        }
        return false;
    }

    private void ResolveDirectVoiceRoute(PlayerCharacter bot)
    {
        try
        {
            var playback = bot.lips == null ? null : bot.lips.playerVoicePlaybackControl;
            if (playback == null)
                playback = bot.GetComponentInChildren<PlayerVoicePlaybackControl>(true);

            if (TryUseAttenuationCurve(playback, "bot_playback"))
                return;

            var playbackAssets = Resources.FindObjectsOfTypeAll<PlayerVoicePlaybackControl>();
            for (var index = 0; index < playbackAssets.Length; index++)
            {
                if (TryUseAttenuationCurve(playbackAssets[index], "loaded_playback_asset"))
                    return;
            }

            var proximityTriggers = Resources.FindObjectsOfTypeAll<VoiceProximityBroadcastTrigger>();
            for (var index = 0; index < proximityTriggers.Length; index++)
            {
                var trigger = proximityTriggers[index];
                if (trigger == null || trigger.Range <= 0)
                    continue;

                _directVoiceRange = trigger.Range;
                _directVoiceRouteSource = "dissonance_proximity_range";
                Plugin.Logger.LogInfo(
                    $"[BOT-AGENT] DIRECT_VOICE_ROUTE source={_directVoiceRouteSource}, " +
                    $"range={_directVoiceRange:F2}");
                return;
            }
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning(
                $"[BOT-AGENT] Direct-voice route discovery failed: {exception.Message}");
        }
    }

    private bool TryUseAttenuationCurve(PlayerVoicePlaybackControl playback, string source)
    {
        var curve = playback == null ? null : playback.AttenuationCurve;
        if (curve == null)
            return false;

        _directVoiceAttenuationCurve = curve;
        _directVoiceRouteSource = source;
        Plugin.Logger.LogInfo(
            $"[BOT-AGENT] DIRECT_VOICE_ROUTE source={_directVoiceRouteSource}");
        return true;
    }

    private void BeginVoiceTurn(string source, float distance, float audibility)
    {
        if (!TryBeginMicrophoneRead())
        {
            Plugin.Logger.LogWarning(
                "[BOT-AGENT] Cannot listen yet: Big Walk's microphone capture is not ready.");
            return;
        }

        _capturingTurn = true;
        _capturedSamples = 0;
        _captureSource = source;
        _captureDistance = distance;
        _captureAudibility = audibility;
        _client.ClearInputAudio();
        Plugin.Logger.LogInfo(
            $"[BOT-AGENT] LISTENING source={source}, route=direct, " +
            $"distance={distance:F2}, audibility={audibility:F6}");
    }

    private void EndVoiceTurn(bool submit)
    {
        if (_capturingTurn && submit)
            CaptureMicrophoneSamples();

        _capturingTurn = false;
        _microphoneReadPosition = -1;
        _silenceStartedAt = -1f;

        if (!submit || _client == null || !_client.IsReady)
        {
            if (_client != null && _client.IsReady)
                _client.ClearInputAudio();
            return;
        }

        if (_capturedSamples < MinimumTurnSamples)
        {
            _client.ClearInputAudio();
            Plugin.Logger.LogWarning(
                $"[BOT-AGENT] Voice turn discarded: only {_capturedSamples} samples were captured.");
            return;
        }

        _client.CommitInputAudioAndRespond();
        Plugin.Logger.LogInfo(
            $"[BOT-AGENT] SUBMITTED source={_captureSource}, route=direct, " +
            $"distance={_captureDistance:F2}, audibility={_captureAudibility:F6}, " +
            $"audioSeconds={_capturedSamples / (float)RealtimeSampleRate:F2}");
    }

    private bool TryBeginMicrophoneRead()
    {
        if (!MicManager.IsRecording(null))
            return false;

        var clip = MicManager.GetClip(null);
        var position = MicManager.GetPosition(null);
        if (clip == null || position < 0 || clip.samples <= 0 ||
            clip.frequency <= 0 || clip.channels <= 0)
        {
            return false;
        }

        _microphoneClip = clip;
        _microphoneReadPosition = position;
        _microphoneFrequency = clip.frequency;
        _microphoneChannels = clip.channels;
        _resampleAccumulator = 0;

        if (!_microphoneReadyLogged)
        {
            _microphoneReadyLogged = true;
            Plugin.Logger.LogInfo(
                $"[BOT-AGENT] MICROPHONE_READY rate={_microphoneFrequency}, " +
                $"channels={_microphoneChannels}, bufferFrames={clip.samples}");
        }

        return true;
    }

    private void CaptureMicrophoneSamples()
    {
        if (_client == null || !_client.IsReady || _microphoneReadPosition < 0)
            return;

        if (!MicManager.IsRecording(null))
            return;

        var clip = MicManager.GetClip(null);
        var currentPosition = MicManager.GetPosition(null);
        if (clip == null || currentPosition < 0 || currentPosition >= clip.samples)
            return;

        if (clip != _microphoneClip || clip.frequency != _microphoneFrequency ||
            clip.channels != _microphoneChannels)
        {
            _microphoneClip = clip;
            _microphoneReadPosition = currentPosition;
            _microphoneFrequency = Math.Max(1, clip.frequency);
            _microphoneChannels = Math.Max(1, clip.channels);
            _resampleAccumulator = 0;
            return;
        }

        if (currentPosition == _microphoneReadPosition)
            return;

        if (currentPosition > _microphoneReadPosition)
        {
            ReadMicrophoneFrames(clip, _microphoneReadPosition,
                currentPosition - _microphoneReadPosition);
        }
        else
        {
            ReadMicrophoneFrames(clip, _microphoneReadPosition,
                clip.samples - _microphoneReadPosition);
            if (currentPosition > 0)
                ReadMicrophoneFrames(clip, 0, currentPosition);
        }

        _microphoneReadPosition = currentPosition;
    }

    private void ReadMicrophoneFrames(AudioClip clip, int startFrame, int frameCount)
    {
        if (frameCount <= 0 || _microphoneChannels <= 0 || _microphoneFrequency <= 0)
            return;

        var source = new Il2CppStructArray<float>(frameCount * _microphoneChannels);
        if (!clip.GetData(source, startFrame))
        {
            Plugin.Logger.LogWarning(
                $"[BOT-AGENT] Microphone buffer read failed at frame {startFrame}.");
            return;
        }

        var maximumOutputSamples =
            (int)Math.Ceiling(frameCount * (double)RealtimeSampleRate / _microphoneFrequency) + 2;
        var pcm = new byte[maximumOutputSamples * 2];
        var outputSamples = 0;

        for (var frame = 0; frame < frameCount; frame++)
        {
            var mono = 0f;
            var frameOffset = frame * _microphoneChannels;
            for (var channel = 0; channel < _microphoneChannels; channel++)
                mono += source[frameOffset + channel];
            mono /= _microphoneChannels;

            if (mono > 1f)
                mono = 1f;
            else if (mono < -1f)
                mono = -1f;

            _resampleAccumulator += RealtimeSampleRate;
            while (_resampleAccumulator >= _microphoneFrequency)
            {
                _resampleAccumulator -= _microphoneFrequency;
                var sample = (short)Math.Round(mono * short.MaxValue);
                pcm[outputSamples * 2] = (byte)(sample & 0xff);
                pcm[outputSamples * 2 + 1] = (byte)((sample >> 8) & 0xff);
                outputSamples++;
            }
        }

        if (outputSamples == 0)
            return;

        if (outputSamples * 2 != pcm.Length)
            Array.Resize(ref pcm, outputSamples * 2);
        _capturedSamples += outputSamples;
        _client.AppendInputAudio(pcm);
    }

    private void StopClient()
    {
        if (_capturingTurn)
            EndVoiceTurn(false);
        if (_client == null)
            return;
        _client.Dispose();
        _client = null;
    }

    private void OnDestroy()
    {
        StopClient();
    }
}

internal sealed class RealtimeFunctionCall
{
    internal string Name;
    internal string CallId;
    internal string Arguments;
}

/// <summary>
/// Pure managed WebSocket client. It only exchanges JSON/PCM and queues model
/// decisions for the Unity main thread.
/// </summary>
internal sealed class OpenAIRealtimeClient : IDisposable
{
    private readonly string _apiKey;
    private readonly string _model;
    private readonly ClientWebSocket _socket = new ClientWebSocket();
    private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
    private readonly ConcurrentQueue<string> _outbound = new ConcurrentQueue<string>();
    private readonly ConcurrentQueue<string> _logs = new ConcurrentQueue<string>();
    private readonly ConcurrentQueue<RealtimeFunctionCall> _functionCalls =
        new ConcurrentQueue<RealtimeFunctionCall>();
    private readonly SemaphoreSlim _outboundSignal = new SemaphoreSlim(0);

    private Task _runTask;
    private volatile bool _ready;
    private volatile bool _stopped;
    private bool _disposed;

    internal OpenAIRealtimeClient(string apiKey, string model)
    {
        _apiKey = apiKey;
        _model = model;
    }

    internal bool IsReady => _ready;
    internal bool IsStopped => _stopped;

    internal void Start()
    {
        if (_runTask != null)
            return;
        _runTask = Task.Run(RunAsync);
    }

    internal bool TryDequeueLog(out string message)
    {
        return _logs.TryDequeue(out message);
    }

    internal bool TryDequeueFunctionCall(out RealtimeFunctionCall functionCall)
    {
        return _functionCalls.TryDequeue(out functionCall);
    }

    internal void ClearInputAudio()
    {
        QueueJson(new { type = "input_audio_buffer.clear" });
    }

    internal void AppendInputAudio(byte[] pcm16)
    {
        QueueJson(new
        {
            type = "input_audio_buffer.append",
            audio = Convert.ToBase64String(pcm16)
        });
    }

    internal void CommitInputAudioAndRespond()
    {
        QueueJson(new { type = "input_audio_buffer.commit" });
        QueueJson(new { type = "response.create" });
    }

    internal void SendFunctionResult(string callId, string resultJson)
    {
        QueueJson(new
        {
            type = "conversation.item.create",
            item = new
            {
                type = "function_call_output",
                call_id = callId,
                output = resultJson
            }
        });
        QueueJson(new { type = "response.create" });
    }

    private async Task RunAsync()
    {
        try
        {
            _socket.Options.SetRequestHeader("Authorization", "Bearer " + _apiKey);
            var uri = new RuntimeUri(
                "wss://api.openai.com/v1/realtime?model=" + RuntimeUri.EscapeDataString(_model));
            await _socket.ConnectAsync(uri, _cancellation.Token).ConfigureAwait(false);
            _logs.Enqueue("CONNECTED");

            QueueRaw(BuildSessionUpdate());
            var sendTask = SendLoopAsync();
            var receiveTask = ReceiveLoopAsync();
            await Task.WhenAny(sendTask, receiveTask).ConfigureAwait(false);
            _cancellation.Cancel();
            await Task.WhenAll(IgnoreCancellation(sendTask), IgnoreCancellation(receiveTask))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal during shutdown.
        }
        catch (Exception exception)
        {
            _logs.Enqueue("CONNECTION_ERROR " + exception.Message);
        }
        finally
        {
            _ready = false;
            _stopped = true;
        }
    }

    private string BuildSessionUpdate()
    {
        var payload = new
        {
            type = "session.update",
            session = new
            {
                type = "realtime",
                model = _model,
                output_modalities = new[] { "text" },
                instructions =
                    "You are Nitrogen, a concise cooperative teammate inside Big Walk. " +
                    "When the human asks you to follow, come with them, walk with them, or keep up, " +
                    "call set_follow_mode with mode follow. When they ask you to stop, stay, wait, " +
                    "or hold position, call it with mode stay. Never claim an action happened until " +
                    "the tool result confirms it. Do not invent tools. Keep acknowledgements brief.",
                audio = new
                {
                    input = new
                    {
                        format = new
                        {
                            type = "audio/pcm",
                            rate = 24000
                        },
                        turn_detection = (object)null
                    }
                },
                tools = new[]
                {
                    new
                    {
                        type = "function",
                        name = "set_follow_mode",
                        description =
                            "Start or stop the companion's verified breadcrumb-follow behavior.",
                        parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                mode = new
                                {
                                    type = "string",
                                    description =
                                        "Use follow to walk behind the human; use stay to stop and hold position.",
                                    @enum = new[] { "follow", "stay" }
                                }
                            },
                            required = new[] { "mode" },
                            additionalProperties = false
                        }
                    }
                },
                tool_choice = "auto"
            }
        };
        return JsonSerializer.Serialize(payload);
    }

    private async Task SendLoopAsync()
    {
        while (!_cancellation.IsCancellationRequested &&
               _socket.State == WebSocketState.Open)
        {
            await _outboundSignal.WaitAsync(_cancellation.Token).ConfigureAwait(false);
            string json;
            while (_outbound.TryDequeue(out json))
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                await _socket.SendAsync(
                        new ArraySegment<byte>(bytes),
                        WebSocketMessageType.Text,
                        true,
                        _cancellation.Token)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[16384];
        while (!_cancellation.IsCancellationRequested &&
               _socket.State == WebSocketState.Open)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(
                        new ArraySegment<byte>(buffer),
                        _cancellation.Token)
                    .ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;
                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            HandleServerEvent(Encoding.UTF8.GetString(message.ToArray()));
        }
    }

    private void HandleServerEvent(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            JsonElement typeElement;
            if (!root.TryGetProperty("type", out typeElement))
                return;
            var type = typeElement.GetString();

            if (type == "session.updated")
            {
                _ready = true;
                _logs.Enqueue("READY tools=set_follow_mode");
                return;
            }

            if (type == "response.output_text.done")
            {
                JsonElement textElement;
                if (root.TryGetProperty("text", out textElement))
                    _logs.Enqueue("SAY " + textElement.GetString());
                return;
            }

            if (type == "conversation.item.input_audio_transcription.completed")
            {
                JsonElement transcriptElement;
                if (root.TryGetProperty("transcript", out transcriptElement))
                    _logs.Enqueue("HEARD " + transcriptElement.GetString());
                return;
            }

            if (type == "response.done")
            {
                QueueFunctionCalls(root);
                return;
            }

            if (type == "error")
            {
                JsonElement error;
                JsonElement message;
                if (root.TryGetProperty("error", out error) &&
                    error.TryGetProperty("message", out message))
                {
                    _logs.Enqueue("API_ERROR " + message.GetString());
                }
                else
                {
                    _logs.Enqueue("API_ERROR " + json);
                }
            }
        }
        catch (JsonException exception)
        {
            _logs.Enqueue("INVALID_EVENT_JSON " + exception.Message);
        }
    }

    private void QueueFunctionCalls(JsonElement root)
    {
        JsonElement response;
        JsonElement output;
        if (!root.TryGetProperty("response", out response) ||
            !response.TryGetProperty("output", out output) ||
            output.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in output.EnumerateArray())
        {
            JsonElement type;
            if (!item.TryGetProperty("type", out type) || type.GetString() != "function_call")
                continue;

            JsonElement name;
            JsonElement callId;
            JsonElement arguments;
            if (!item.TryGetProperty("name", out name) ||
                !item.TryGetProperty("call_id", out callId) ||
                !item.TryGetProperty("arguments", out arguments))
            {
                continue;
            }

            _functionCalls.Enqueue(new RealtimeFunctionCall
            {
                Name = name.GetString(),
                CallId = callId.GetString(),
                Arguments = arguments.GetString()
            });
        }
    }

    private void QueueJson(object payload)
    {
        QueueRaw(JsonSerializer.Serialize(payload));
    }

    private void QueueRaw(string json)
    {
        if (_disposed || _cancellation.IsCancellationRequested)
            return;
        _outbound.Enqueue(json);
        _outboundSignal.Release();
    }

    private static async Task IgnoreCancellation(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _ready = false;
        _cancellation.Cancel();
        try
        {
            _socket.Abort();
        }
        catch
        {
        }
        _socket.Dispose();
        // The background loops observe cancellation and finish asynchronously.
        // Their wait handles remain valid until then; this client is process-local
        // and short-lived, so eagerly disposing them would introduce a shutdown race.
    }
}
