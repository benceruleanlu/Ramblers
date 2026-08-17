using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace BigWalkBotProbe;

/// <summary>
/// Adapts Big Walk's own voice state, microphone, and direct attenuation curve
/// into bounded PCM turns. It does not know about WebSockets or agent tools.
/// </summary>
internal sealed class GameVoiceInput
{
    private const int RealtimeSampleRate = 24000;
    private const int MinimumTurnSamples = RealtimeSampleRate / 10;
    private const float AudibilityThreshold = 0.0001f;
    private const float OpenMicrophoneRmsThreshold = 0.006f;
    private const float OpenMicrophoneSilenceHangover = 0.45f;
    private const float MicrophoneUnavailableGrace = 0.5f;

    private AudioClip _microphoneClip;
    private Il2CppStructArray<float> _voiceActivitySamples;
    private Il2CppStructArray<float> _captureSamples;
    private bool _capturingTurn;
    private int _capturedSamples;
    private int _microphoneReadPosition = -1;
    private int _microphoneFrequency;
    private int _microphoneChannels;
    private int _resampleAccumulator;
    private bool _microphoneReadyLogged;
    private float _microphoneUnavailableSince = -1f;
    private bool _voiceIntentWasActive;
    private bool _voiceStateUnavailableLogged;
    private bool _audibilityUnavailableLogged;
    private bool _voiceChannelStateInitialized;
    private bool _lastVoiceChannelOpen;
    private float _silenceStartedAt = -1f;
    private float _nextVoiceRouteResolveAt;
    private string _captureSource;
    private string _configuredVoiceSource;
    private float _captureDistance;
    private float _captureAudibility;
    private AnimationCurve _directVoiceAttenuationCurve;

    internal void Tick(IAgentAudioSink sink)
    {
        if (sink == null || !sink.IsReady)
        {
            CancelTurn(sink);
            return;
        }

        TickGameVoice(sink);
        if (_capturingTurn)
            CaptureMicrophoneSamples(sink);
    }

    internal void Stop(IAgentAudioSink sink)
    {
        CancelTurn(sink);
        _microphoneClip = null;
        _microphoneReadPosition = -1;
        _microphoneFrequency = 0;
        _microphoneChannels = 0;
        _resampleAccumulator = 0;
        _microphoneUnavailableSince = -1f;
        _directVoiceAttenuationCurve = null;
        _nextVoiceRouteResolveAt = 0f;
    }

    private void CancelTurn(IAgentAudioSink sink)
    {
        if (_capturingTurn)
            EndVoiceTurn(false, sink);
        _voiceIntentWasActive = false;
        _silenceStartedAt = -1f;
    }

    private void TickGameVoice(IAgentAudioSink sink)
    {
        bool voiceIntentActive;
        bool useSilenceHangover;
        string source;
        if (!TryGetGameVoiceIntent(
                out voiceIntentActive,
                out useSilenceHangover,
                out source))
        {
            if (_microphoneUnavailableSince >= 0f)
            {
                var unavailableSeconds =
                    Time.realtimeSinceStartup - _microphoneUnavailableSince;
                if (unavailableSeconds < MicrophoneUnavailableGrace)
                    return;

                if (_capturingTurn)
                {
                    Plugin.Logger.LogWarning(
                        $"[BOT-AGENT] Big Walk's microphone remained unavailable for " +
                        $"{unavailableSeconds:F2}s; submitting the captured portion.");
                    EndVoiceTurn(true, sink);
                    _voiceIntentWasActive = false;
                    return;
                }
            }

            CancelTurn(sink);
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
                EndVoiceTurn(false, sink);
            _voiceIntentWasActive = true;
            _silenceStartedAt = -1f;
            return;
        }

        if (voiceIntentActive)
        {
            _silenceStartedAt = -1f;
            if (!_capturingTurn)
                BeginVoiceTurn(source, distance, audibility, sink);
        }
        else if (_capturingTurn)
        {
            if (!useSilenceHangover)
            {
                EndVoiceTurn(true, sink);
            }
            else if (_silenceStartedAt < 0f)
            {
                _silenceStartedAt = Time.realtimeSinceStartup;
            }
            else if (Time.realtimeSinceStartup - _silenceStartedAt >=
                     OpenMicrophoneSilenceHangover)
            {
                EndVoiceTurn(true, sink);
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
        {
            ResetMicrophoneUnavailableState(false);
            return false;
        }

        var channelOpen = !world.forceMutedBySystem && !comms.IsMuted;
        LogVoiceChannelState(channelOpen);

        if (SettingsHelper.pushToTalkModeActive)
        {
            ResetMicrophoneUnavailableState(false);
            source = "game_ptt";
            LogConfiguredVoiceSource(source, "<none>");
            active = channelOpen;
            return true;
        }

        source = "game_voice_activity";
        var trigger = human.lips == null ? null : human.lips.broadcastTrigger;
        var triggerMode = trigger == null ? "<none>" : trigger.Mode.ToString();
        LogConfiguredVoiceSource(source, triggerMode);

        // In toggle mode, Big Walk owns the hard mute/open state. Raw RMS from
        // its existing MicManager capture supplies only the speech/silence edge.
        // Runtime testing showed Dissonance's local transmit flag and processed
        // amplitude are not reliable local speech signals in this game.
        if (!channelOpen)
        {
            ResetMicrophoneUnavailableState(false);
            return true;
        }

        float microphoneRms;
        if (TryGetRecentMicrophoneRms(out microphoneRms))
        {
            ResetMicrophoneUnavailableState(true);
            useSilenceHangover = true;
            active = microphoneRms >= OpenMicrophoneRmsThreshold;
            return true;
        }

        if (_microphoneUnavailableSince < 0f)
            _microphoneUnavailableSince = Time.realtimeSinceStartup;

        if (!_voiceStateUnavailableLogged)
        {
            _voiceStateUnavailableLogged = true;
            Plugin.Logger.LogWarning(
                "[BOT-AGENT] Big Walk's microphone state is temporarily unavailable; " +
                "an active capture will be retained briefly.");
        }
        return false;
    }

    private void ResetMicrophoneUnavailableState(bool logRecovery)
    {
        if (logRecovery && _microphoneUnavailableSince >= 0f)
        {
            Plugin.Logger.LogInfo(
                $"[BOT-AGENT] MICROPHONE_STATE_RECOVERED " +
                $"afterSeconds={Time.realtimeSinceStartup - _microphoneUnavailableSince:F3}");
        }

        _microphoneUnavailableSince = -1f;
        _voiceStateUnavailableLogged = false;
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
        if (clip == null || position < 0 || clip.frequency <= 0 ||
            clip.channels <= 0 || clip.samples <= 0)
        {
            return false;
        }

        var frameCount = Math.Min(clip.samples, Math.Max(1, clip.frequency / 50));
        var startFrame = position - frameCount;
        double sumSquares = 0;
        var measuredSamples = 0;
        if (startFrame >= 0)
        {
            if (!TryAccumulateMicrophoneRms(
                    clip,
                    startFrame,
                    frameCount,
                    clip.channels,
                    ref sumSquares,
                    ref measuredSamples))
            {
                return false;
            }
        }
        else
        {
            var tailFrameCount = -startFrame;
            if (!TryAccumulateMicrophoneRms(
                    clip,
                    clip.samples - tailFrameCount,
                    tailFrameCount,
                    clip.channels,
                    ref sumSquares,
                    ref measuredSamples))
            {
                return false;
            }

            if (position > 0 && !TryAccumulateMicrophoneRms(
                    clip,
                    0,
                    position,
                    clip.channels,
                    ref sumSquares,
                    ref measuredSamples))
            {
                return false;
            }
        }

        if (measuredSamples == 0)
            return false;

        rms = (float)Math.Sqrt(sumSquares / measuredSamples);
        return true;
    }

    private bool TryAccumulateMicrophoneRms(
        AudioClip clip,
        int startFrame,
        int frameCount,
        int channelCount,
        ref double sumSquares,
        ref int measuredSamples)
    {
        if (frameCount <= 0)
            return true;

        var sampleCount = frameCount * channelCount;
        if (_voiceActivitySamples == null || _voiceActivitySamples.Length != sampleCount)
            _voiceActivitySamples = new Il2CppStructArray<float>(sampleCount);

        if (!clip.GetData(_voiceActivitySamples, startFrame))
            return false;

        for (var index = 0; index < _voiceActivitySamples.Length; index++)
        {
            var sample = _voiceActivitySamples[index];
            sumSquares += sample * sample;
        }

        measuredSamples += _voiceActivitySamples.Length;
        return true;
    }

    private void LogConfiguredVoiceSource(string source, string triggerMode)
    {
        if (string.Equals(_configuredVoiceSource, source, StringComparison.Ordinal))
            return;

        _configuredVoiceSource = source;
        Plugin.Logger.LogInfo(
            $"[BOT-AGENT] GAME_VOICE_READY source={source}, triggerMode={triggerMode}");
    }

    private bool TryGetDirectVoiceAudibility(out float distance, out float audibility)
    {
        distance = -1f;
        audibility = -1f;

        PlayerCharacter human;
        PlayerCharacter bot;
        if (!BotController.TryGetVoiceParticipants(out human, out bot))
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
            _audibilityUnavailableLogged = false;
            audibility = _directVoiceAttenuationCurve.Evaluate(distance);
            return audibility > AudibilityThreshold;
        }

        if (!_audibilityUnavailableLogged)
        {
            _audibilityUnavailableLogged = true;
            Plugin.Logger.LogWarning(
                "[BOT-AGENT] Big Walk's direct-voice route is not ready; agent listening is paused.");
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
                $"[BOT-AGENT] Direct-voice route discovery failed: {exception.Message}");
        }
    }

    private bool TryUseAttenuationCurve(PlayerVoicePlaybackControl playback, string source)
    {
        var curve = playback == null ? null : playback.AttenuationCurve;
        if (curve == null)
            return false;

        _directVoiceAttenuationCurve = curve;
        Plugin.Logger.LogInfo($"[BOT-AGENT] DIRECT_VOICE_ROUTE source={source}");
        return true;
    }

    private void BeginVoiceTurn(
        string source,
        float distance,
        float audibility,
        IAgentAudioSink sink)
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
        sink.ClearInputAudio();
        Plugin.Logger.LogInfo(
            $"[BOT-AGENT] LISTENING source={source}, route=direct, " +
            $"distance={distance:F2}, audibility={audibility:F6}");
    }

    private void EndVoiceTurn(bool submit, IAgentAudioSink sink)
    {
        if (_capturingTurn && submit && sink != null && sink.IsReady)
            CaptureMicrophoneSamples(sink);

        _capturingTurn = false;
        _microphoneReadPosition = -1;
        _silenceStartedAt = -1f;

        if (!submit || sink == null || !sink.IsReady)
        {
            if (sink != null && sink.IsReady)
                sink.ClearInputAudio();
            return;
        }

        if (_capturedSamples < MinimumTurnSamples)
        {
            sink.ClearInputAudio();
            Plugin.Logger.LogWarning(
                $"[BOT-AGENT] Voice turn discarded: only {_capturedSamples} samples were captured.");
            return;
        }

        sink.CommitInputAudioAndRespond();
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
        _capturedSamples += outputSamples;
        sink.AppendInputAudio(pcm);
    }
}
