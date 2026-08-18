using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace Ramblers;

internal enum AgentTurnDetectionMode
{
    SemanticVad,
    ManualPushToTalk
}

/// <summary>
/// Adapts Big Walk's voice channel, microphone, and direct attenuation curve
/// into a continuous Realtime PCM stream. Open-microphone turn boundaries are
/// owned by server semantic VAD; Big Walk push-to-talk retains manual commits.
/// </summary>
internal sealed class GameVoiceInput
{
    private const int RealtimeSampleRate = 24000;
    private const int MinimumManualTurnSamples = RealtimeSampleRate / 10;
    private const float AudibilityThreshold = 0.0001f;
    private const float MicrophoneUnavailableGrace = 0.5f;

    private readonly LogLatch _microphoneReadyLog = new LogLatch();
    private readonly LogLatch _voiceStateUnavailableLog = new LogLatch();
    private readonly LogLatch _audibilityUnavailableLog = new LogLatch();
    private readonly LogChange<bool> _voiceChannelStateLog = new LogChange<bool>();
    private readonly LogChange<string> _voiceSourceLog = new LogChange<string>();

    private AudioClip _microphoneClip;
    private Il2CppStructArray<float> _captureSamples;
    private bool _streaming;
    private int _streamedSamples;
    private int _microphoneReadPosition = -1;
    private int _microphoneFrequency;
    private int _microphoneChannels;
    private int _resampleAccumulator;
    private float _microphoneUnavailableSince = -1f;
    private bool _voiceIntentWasActive;
    private bool _hasConfiguredTurnMode;
    private AgentTurnDetectionMode _configuredTurnMode;
    private float _nextVoiceRouteResolveAt;
    private string _streamSource;
    private float _streamDistance;
    private float _streamAudibility;
    private AnimationCurve _directVoiceAttenuationCurve;

    /// <summary>
    /// Returns true when a manual push-to-talk press should immediately stop
    /// any locally queued or playing assistant speech.
    /// </summary>
    internal bool Tick(IAgentAudioSink sink)
    {
        if (sink == null || !sink.IsReady)
        {
            StopStreaming(false, sink);
            return false;
        }

        bool channelOpen;
        string source;
        AgentTurnDetectionMode turnMode;
        if (!TryGetGameVoiceState(out channelOpen, out source, out turnMode))
        {
            HandleUnavailableMicrophone(sink);
            return false;
        }

        ConfigureTurnMode(turnMode, sink);

        if (!channelOpen)
        {
            if (_streaming)
                StopStreaming(turnMode == AgentTurnDetectionMode.ManualPushToTalk, sink);
            _voiceIntentWasActive = false;
            return false;
        }

        float distance;
        float audibility;
        if (!TryGetDirectVoiceAudibility(out distance, out audibility))
        {
            if (!_voiceIntentWasActive)
            {
                var reason = audibility < 0f ? "route_unavailable" : "out_of_range";
                Plugin.Logger.LogInfo(
                    $"[AGENT] AUDIO_IGNORED source={source}, route=direct, " +
                    $"reason={reason}, distance={distance:F2}, audibility={audibility:F6}");
            }

            StopStreaming(false, sink);
            _voiceIntentWasActive = true;
            return false;
        }

        var manualTurnStarted = false;
        if (!_streaming)
        {
            if (turnMode == AgentTurnDetectionMode.ManualPushToTalk)
            {
                sink.CancelActiveResponse();
                manualTurnStarted = true;
            }

            BeginStreaming(source, turnMode, distance, audibility, sink);
        }

        if (_streaming)
            CaptureMicrophoneSamples(sink);

        _voiceIntentWasActive = true;
        return manualTurnStarted;
    }

    internal void Stop(IAgentAudioSink sink)
    {
        StopStreaming(false, sink);
        _microphoneClip = null;
        _microphoneReadPosition = -1;
        _microphoneFrequency = 0;
        _microphoneChannels = 0;
        _resampleAccumulator = 0;
        _microphoneUnavailableSince = -1f;
        _voiceIntentWasActive = false;
        _hasConfiguredTurnMode = false;
        _directVoiceAttenuationCurve = null;
        _nextVoiceRouteResolveAt = 0f;
    }

    private static bool IsManualVoiceGateOpen()
    {
        var world = WorldManager.instance;
        var comms = world == null ? null : world.dissonanceComms;
        return SettingsHelper.pushToTalkModeActive && world != null &&
               comms != null && !world.forceMutedBySystem && !comms.IsMuted;
    }

    private bool TryGetGameVoiceState(
        out bool channelOpen,
        out string source,
        out AgentTurnDetectionMode turnMode)
    {
        channelOpen = false;
        source = "game_voice";
        turnMode = AgentTurnDetectionMode.SemanticVad;

        var world = WorldManager.instance;
        var human = WorldManager.localPlayerCharacter;
        var comms = world == null ? null : world.dissonanceComms;
        if (world == null || human == null || comms == null)
        {
            ResetMicrophoneUnavailableState(false);
            return false;
        }

        channelOpen = !world.forceMutedBySystem && !comms.IsMuted;
        turnMode = SettingsHelper.pushToTalkModeActive
            ? AgentTurnDetectionMode.ManualPushToTalk
            : AgentTurnDetectionMode.SemanticVad;
        source = turnMode == AgentTurnDetectionMode.ManualPushToTalk
            ? "game_ptt"
            : "game_semantic_vad";

        LogVoiceChannelState(channelOpen, turnMode);
        var trigger = human.lips == null ? null : human.lips.broadcastTrigger;
        LogConfiguredVoiceSource(source, trigger == null ? "<none>" : trigger.Mode.ToString());

        if (!channelOpen)
        {
            ResetMicrophoneUnavailableState(false);
            return true;
        }

        if (MicManager.IsRecording(null) && MicManager.GetClip(null) != null &&
            MicManager.GetPosition(null) >= 0)
        {
            ResetMicrophoneUnavailableState(true);
            return true;
        }

        if (_microphoneUnavailableSince < 0f)
            _microphoneUnavailableSince = Time.realtimeSinceStartup;

        if (_voiceStateUnavailableLog.ShouldLog())
        {
            Plugin.Logger.LogWarning(
                "[AGENT] Big Walk's microphone state is temporarily unavailable; " +
                "an active stream will be retained briefly.");
        }
        return false;
    }

    private void HandleUnavailableMicrophone(IAgentAudioSink sink)
    {
        if (_microphoneUnavailableSince < 0f)
        {
            StopStreaming(false, sink);
            return;
        }

        var unavailableSeconds = Time.realtimeSinceStartup - _microphoneUnavailableSince;
        if (unavailableSeconds < MicrophoneUnavailableGrace)
            return;

        var submitManualTurn =
            _configuredTurnMode == AgentTurnDetectionMode.ManualPushToTalk;
        if (_streaming)
        {
            Plugin.Logger.LogWarning(
                $"[AGENT] Big Walk's microphone remained unavailable for " +
                $"{unavailableSeconds:F2}s; " +
                (submitManualTurn
                    ? "submitting the captured push-to-talk audio."
                    : "stopping the semantic VAD stream."));
        }

        StopStreaming(submitManualTurn, sink);
        _voiceIntentWasActive = false;
    }

    private void ConfigureTurnMode(AgentTurnDetectionMode turnMode, IAgentAudioSink sink)
    {
        if (_hasConfiguredTurnMode && _configuredTurnMode == turnMode)
            return;

        if (_streaming)
            StopStreaming(false, sink);

        _configuredTurnMode = turnMode;
        _hasConfiguredTurnMode = true;
        sink.SetTurnDetectionMode(turnMode);
        Plugin.Logger.LogInfo(
            $"[AGENT] TURN_DETECTION mode=" +
            $"{(turnMode == AgentTurnDetectionMode.SemanticVad ? "semantic_vad_client_response" : "manual_ptt")}");
    }

    private void ResetMicrophoneUnavailableState(bool logRecovery)
    {
        if (logRecovery && _microphoneUnavailableSince >= 0f)
        {
            Plugin.Logger.LogInfo(
                $"[AGENT] MICROPHONE_STATE_RECOVERED " +
                $"afterSeconds={Time.realtimeSinceStartup - _microphoneUnavailableSince:F3}");
        }

        _microphoneUnavailableSince = -1f;
        _voiceStateUnavailableLog.Reset();
    }

    private void LogVoiceChannelState(
        bool channelOpen,
        AgentTurnDetectionMode turnMode)
    {
        if (!_voiceChannelStateLog.ShouldLog(channelOpen))
            return;

        Plugin.Logger.LogInfo(
            $"[AGENT] GAME_VOICE_STATE channelOpen={channelOpen}, " +
            $"mode={(turnMode == AgentTurnDetectionMode.ManualPushToTalk ? "hold" : "toggle_or_open")}");
    }

    private void LogConfiguredVoiceSource(string source, string triggerMode)
    {
        if (!_voiceSourceLog.ShouldLog(source))
            return;

        Plugin.Logger.LogInfo(
            $"[AGENT] GAME_VOICE_READY source={source}, triggerMode={triggerMode}");
    }

    private bool TryGetDirectVoiceAudibility(out float distance, out float audibility)
    {
        distance = -1f;
        audibility = -1f;

        PlayerCharacter human;
        PlayerCharacter bot;
        if (!CompanionController.TryGetVoiceParticipants(out human, out bot))
            return false;

        distance = Vector3.Distance(human.transform.position, bot.transform.position);
        if (_directVoiceAttenuationCurve == null &&
            Time.realtimeSinceStartup >= _nextVoiceRouteResolveAt)
        {
            _nextVoiceRouteResolveAt = Time.realtimeSinceStartup + 1f;
            ResolveDirectVoiceRoute(bot);
        }

        if (_directVoiceAttenuationCurve != null)
        {
            _audibilityUnavailableLog.Reset();
            audibility = _directVoiceAttenuationCurve.Evaluate(distance);
            return audibility > AudibilityThreshold;
        }

        if (_audibilityUnavailableLog.ShouldLog())
        {
            Plugin.Logger.LogWarning(
                "[AGENT] Big Walk's direct-voice route is not ready; agent listening is paused.");
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
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning(
                $"[AGENT] Direct-voice route discovery failed: {exception.Message}");
        }
    }

    private bool TryUseAttenuationCurve(PlayerVoicePlaybackControl playback, string source)
    {
        var curve = playback == null ? null : playback.AttenuationCurve;
        if (curve == null)
            return false;

        _directVoiceAttenuationCurve = curve;
        Plugin.Logger.LogInfo($"[AGENT] DIRECT_VOICE_ROUTE source={source}");
        return true;
    }

    private void BeginStreaming(
        string source,
        AgentTurnDetectionMode turnMode,
        float distance,
        float audibility,
        IAgentAudioSink sink)
    {
        if (!TryBeginMicrophoneRead())
        {
            Plugin.Logger.LogWarning(
                "[AGENT] Cannot listen yet: Big Walk's microphone capture is not ready.");
            return;
        }

        _streaming = true;
        _streamedSamples = 0;
        _streamSource = source;
        _streamDistance = distance;
        _streamAudibility = audibility;
        sink.ClearInputAudio();
        Plugin.Logger.LogInfo(
            $"[AGENT] AUDIO_STREAM_STARTED source={source}, route=direct, " +
            $"turnDetection=" +
            $"{(turnMode == AgentTurnDetectionMode.SemanticVad ? "semantic_vad_client_response" : "manual_ptt")}, " +
            $"distance={distance:F2}, audibility={audibility:F6}");
    }

    private void StopStreaming(bool submitManualTurn, IAgentAudioSink sink)
    {
        if (!_streaming)
            return;

        if (submitManualTurn && sink != null && sink.IsReady)
            CaptureMicrophoneSamples(sink);

        _streaming = false;
        _microphoneReadPosition = -1;

        var submitted = false;
        if (submitManualTurn && sink != null && sink.IsReady &&
            _streamedSamples >= MinimumManualTurnSamples)
        {
            sink.CommitInputAudioAndRespond();
            submitted = true;
        }
        else if (sink != null && sink.IsReady)
        {
            sink.ClearInputAudio();
        }

        Plugin.Logger.LogInfo(
            $"[AGENT] AUDIO_STREAM_STOPPED source={_streamSource}, route=direct, " +
            $"status={(submitted ? "submitted" : "cleared")}, " +
            $"distance={_streamDistance:F2}, audibility={_streamAudibility:F6}, " +
            $"audioSeconds={_streamedSamples / (float)RealtimeSampleRate:F2}");
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

        if (_microphoneReadyLog.ShouldLog())
        {
            Plugin.Logger.LogInfo(
                $"[AGENT] MICROPHONE_READY rate={_microphoneFrequency}, " +
                $"channels={_microphoneChannels}, bufferFrames={clip.samples}");
        }

        return true;
    }

    private void CaptureMicrophoneSamples(IAgentAudioSink sink)
    {
        if (sink == null || !sink.IsReady || _microphoneReadPosition < 0)
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
            ReadMicrophoneFrames(
                clip,
                _microphoneReadPosition,
                currentPosition - _microphoneReadPosition,
                sink);
        }
        else
        {
            ReadMicrophoneFrames(
                clip,
                _microphoneReadPosition,
                clip.samples - _microphoneReadPosition,
                sink);
            if (currentPosition > 0)
                ReadMicrophoneFrames(clip, 0, currentPosition, sink);
        }

        _microphoneReadPosition = currentPosition;
    }

    private void ReadMicrophoneFrames(
        AudioClip clip,
        int startFrame,
        int frameCount,
        IAgentAudioSink sink)
    {
        if (frameCount <= 0 || _microphoneChannels <= 0 || _microphoneFrequency <= 0)
            return;

        var sourceSampleCount = frameCount * _microphoneChannels;
        if (_captureSamples == null || _captureSamples.Length != sourceSampleCount)
            _captureSamples = new Il2CppStructArray<float>(sourceSampleCount);
        if (!clip.GetData(_captureSamples, startFrame))
        {
            Plugin.Logger.LogWarning(
                $"[AGENT] Microphone buffer read failed at frame {startFrame}.");
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
                mono += _captureSamples[frameOffset + channel];
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
        _streamedSamples += outputSamples;
        sink.AppendInputAudio(pcm);
    }
}
