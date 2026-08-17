using System;
using System.Collections.Concurrent;
using System.IO;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace BigWalkBotProbe;

/// <summary>
/// Buffers one Realtime PCM response at a time and plays completed utterances
/// from a dedicated 3D AudioSource attached to the companion's voice transform.
/// This first output adapter is intentionally local-only; it does not inject
/// synthetic packets into Big Walk's multiplayer voice transport.
/// </summary>
internal sealed class GameVoiceOutput
{
    private const int OutputSampleRate = 24000;

    private readonly MemoryStream _currentResponse = new MemoryStream();
    private readonly ConcurrentQueue<AudioClip> _pendingClips =
        new ConcurrentQueue<AudioClip>();

    private PlayerCharacter _speaker;
    private AudioSource _source;
    private AudioClip _playingClip;
    private int _utteranceNumber;

    internal void Accept(RealtimeAudioPacket packet)
    {
        if (packet == null)
            return;

        if (packet.Pcm16 != null && packet.Pcm16.Length > 0)
            _currentResponse.Write(packet.Pcm16, 0, packet.Pcm16.Length);

        if (packet.EndsResponse)
            QueueCompletedResponse();
    }

    internal void Tick()
    {
        PlayerCharacter human;
        PlayerCharacter bot;
        if (!BotController.TryGetVoiceParticipants(out human, out bot) || bot == null)
        {
            DropPendingIfNecessary("companion_unavailable");
            ReleaseSource();
            return;
        }

        EnsureSource(bot);
        if (_source == null)
            return;

        if (_playingClip != null && !_source.isPlaying)
        {
            UnityEngine.Object.Destroy(_playingClip);
            _playingClip = null;
            _source.clip = null;
        }

        if (_source.isPlaying)
            return;

        AudioClip nextClip;
        if (!_pendingClips.TryDequeue(out nextClip))
            return;

        _playingClip = nextClip;
        _source.clip = _playingClip;
        _source.Play();
        Plugin.Logger.LogInfo(
            $"[BOT-AGENT] VOICE_PLAYING seconds={_playingClip.length:F2}, route=local_3d");
    }

    internal void Stop()
    {
        _currentResponse.SetLength(0);
        _currentResponse.Position = 0;
        ClearPendingClips();
        ReleaseSource();
    }

    private void QueueCompletedResponse()
    {
        var pcm = _currentResponse.ToArray();
        _currentResponse.SetLength(0);
        _currentResponse.Position = 0;

        var sampleCount = pcm.Length / 2;
        if (sampleCount == 0)
            return;

        try
        {
            var samples = new Il2CppStructArray<float>(sampleCount);
            for (var index = 0; index < sampleCount; index++)
            {
                var low = pcm[index * 2];
                var high = pcm[index * 2 + 1];
                var value = (short)(low | (high << 8));
                samples[index] = value / (value < 0 ? 32768f : 32767f);
            }

            _utteranceNumber++;
            var clip = AudioClip.Create(
                $"RamblersVoice_{_utteranceNumber}",
                sampleCount,
                1,
                OutputSampleRate,
                false);
            if (!clip.SetData(samples, 0))
            {
                UnityEngine.Object.Destroy(clip);
                Plugin.Logger.LogWarning(
                    "[BOT-AGENT] VOICE_DROPPED reason=audio_clip_write_failed");
                return;
            }

            _pendingClips.Enqueue(clip);
            Plugin.Logger.LogInfo(
                $"[BOT-AGENT] VOICE_QUEUED seconds={sampleCount / (float)OutputSampleRate:F2}");
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning(
                $"[BOT-AGENT] VOICE_DROPPED reason=audio_clip_error detail={exception.Message}");
        }
    }

    private void EnsureSource(PlayerCharacter bot)
    {
        if (_speaker == bot && _source != null)
            return;

        ReleaseSource();
        _speaker = bot;

        try
        {
            var playback = bot.lips == null ? null : bot.lips.playerVoicePlaybackControl;
            if (playback == null)
                playback = bot.GetComponentInChildren<PlayerVoicePlaybackControl>(true);

            var voiceObject = playback != null
                ? playback.gameObject
                : bot.gameObject;

            _source = voiceObject.AddComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.loop = false;
            _source.spatialBlend = 1f;
            _source.dopplerLevel = 0f;
            _source.volume = 1f;
            _source.minDistance = 1f;
            _source.maxDistance = 60f;

            var attenuationCurve = playback == null ? null : playback.AttenuationCurve;
            if (attenuationCurve != null)
            {
                _source.rolloffMode = AudioRolloffMode.Custom;
                _source.SetCustomCurve(
                    AudioSourceCurveType.CustomRolloff,
                    attenuationCurve);
            }
            else
            {
                _source.rolloffMode = AudioRolloffMode.Logarithmic;
            }

            Plugin.Logger.LogInfo(
                $"[BOT-AGENT] VOICE_ROUTE_READY source=" +
                $"{(playback == null ? "companion_body" : "player_voice_playback")}, " +
                "route=local_3d");
        }
        catch (Exception exception)
        {
            _source = null;
            Plugin.Logger.LogWarning(
                $"[BOT-AGENT] VOICE_ROUTE_UNAVAILABLE detail={exception.Message}");
        }
    }

    private void DropPendingIfNecessary(string reason)
    {
        if (_pendingClips.IsEmpty)
            return;

        ClearPendingClips();
        Plugin.Logger.LogWarning($"[BOT-AGENT] VOICE_DROPPED reason={reason}");
    }

    private void ClearPendingClips()
    {
        AudioClip clip;
        while (_pendingClips.TryDequeue(out clip))
            UnityEngine.Object.Destroy(clip);

        if (_playingClip != null)
        {
            UnityEngine.Object.Destroy(_playingClip);
            _playingClip = null;
        }
    }

    private void ReleaseSource()
    {
        if (_source != null)
        {
            _source.Stop();
            _source.clip = null;
            UnityEngine.Object.Destroy(_source);
        }

        if (_playingClip != null)
        {
            UnityEngine.Object.Destroy(_playingClip);
            _playingClip = null;
        }

        _source = null;
        _speaker = null;
    }
}
