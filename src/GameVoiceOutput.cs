using System;
using System.Collections.Generic;
using System.IO;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace Ramblers;

/// <summary>
/// Buffers Realtime PCM by conversation item and plays completed utterances
/// from a dedicated 3D AudioSource attached to the companion's voice transform.
/// It also reports exactly how much audio was heard when playback is interrupted.
/// </summary>
internal sealed class GameVoiceOutput
{
    private const int OutputSampleRate = 24000;

    private sealed class BufferedVoiceClip
    {
        internal AudioClip Clip;
        internal string ItemId;
        internal int ContentIndex;
    }

    private readonly MemoryStream _currentItemAudio = new MemoryStream();
    private readonly Queue<BufferedVoiceClip> _pendingClips =
        new Queue<BufferedVoiceClip>();

    private PlayerCharacter _speaker;
    private AudioSource _source;
    private BufferedVoiceClip _playing;
    private string _currentItemId;
    private int _currentContentIndex;
    private int _utteranceNumber;

    internal void Accept(RealtimeAudioPacket packet)
    {
        if (packet == null)
            return;

        if (!string.IsNullOrEmpty(packet.ItemId))
        {
            if (_currentItemAudio.Length > 0 &&
                (!string.Equals(_currentItemId, packet.ItemId, StringComparison.Ordinal) ||
                 _currentContentIndex != packet.ContentIndex))
            {
                DropCurrentItem("audio_item_changed_before_done");
            }

            _currentItemId = packet.ItemId;
            _currentContentIndex = packet.ContentIndex;
        }

        if (packet.Pcm16 != null && packet.Pcm16.Length > 0)
            _currentItemAudio.Write(packet.Pcm16, 0, packet.Pcm16.Length);

        if (packet.EndsItem)
            QueueCompletedItem();
    }

    internal void Tick()
    {
        PlayerCharacter human;
        PlayerCharacter bot;
        if (!CompanionController.TryGetVoiceParticipants(out human, out bot) || bot == null)
        {
            DropPendingIfNecessary("companion_unavailable");
            ReleaseSource();
            return;
        }

        EnsureSource(bot);
        if (_source == null)
            return;

        if (_playing != null && !_source.isPlaying)
            DestroyPlayingClip();

        if (_source.isPlaying || _pendingClips.Count == 0)
            return;

        _playing = _pendingClips.Dequeue();
        _source.clip = _playing.Clip;
        _source.Play();
        Plugin.Logger.LogInfo(
            $"[AGENT] VOICE_PLAYING seconds={_playing.Clip.length:F2}, route=local_3d");
    }

    /// <summary>
    /// Stops all assistant speech and returns truncations for conversation
    /// items whose generated audio was not fully heard by the user.
    /// </summary>
    internal List<RealtimeAudioTruncation> Interrupt()
    {
        var truncations = new List<RealtimeAudioTruncation>();
        var seenItems = new HashSet<string>(StringComparer.Ordinal);

        if (_playing != null)
        {
            var playedSamples = _source == null
                ? 0
                : Math.Max(0, Math.Min(_source.timeSamples, _playing.Clip.samples));
            AddTruncation(
                truncations,
                seenItems,
                _playing.ItemId,
                _playing.ContentIndex,
                (int)Math.Round(playedSamples * 1000d / OutputSampleRate));
        }

        foreach (var pending in _pendingClips)
        {
            AddTruncation(
                truncations,
                seenItems,
                pending.ItemId,
                pending.ContentIndex,
                0);
        }

        if (_currentItemAudio.Length > 0)
        {
            AddTruncation(
                truncations,
                seenItems,
                _currentItemId,
                _currentContentIndex,
                0);
        }

        if (_source != null)
        {
            _source.Stop();
            _source.clip = null;
        }

        DestroyPlayingClip();
        ClearPendingClips();
        ResetCurrentItem();

        if (truncations.Count > 0)
            Plugin.Logger.LogInfo($"[AGENT] VOICE_INTERRUPTED items={truncations.Count}");

        return truncations;
    }

    internal void Stop()
    {
        ResetCurrentItem();
        ClearPendingClips();
        ReleaseSource();
    }

    private void QueueCompletedItem()
    {
        var pcm = _currentItemAudio.ToArray();
        var itemId = _currentItemId;
        var contentIndex = _currentContentIndex;
        ResetCurrentItem();

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
                    "[AGENT] VOICE_DROPPED reason=audio_clip_write_failed");
                return;
            }

            _pendingClips.Enqueue(new BufferedVoiceClip
            {
                Clip = clip,
                ItemId = itemId,
                ContentIndex = contentIndex
            });
            Plugin.Logger.LogInfo(
                $"[AGENT] VOICE_QUEUED seconds={sampleCount / (float)OutputSampleRate:F2}");
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning(
                $"[AGENT] VOICE_DROPPED reason=audio_clip_error detail={exception.Message}");
        }
    }

    private static void AddTruncation(
        ICollection<RealtimeAudioTruncation> truncations,
        ISet<string> seenItems,
        string itemId,
        int contentIndex,
        int audioEndMilliseconds)
    {
        if (string.IsNullOrEmpty(itemId))
            return;

        var key = itemId + ":" + contentIndex;
        if (!seenItems.Add(key))
            return;

        truncations.Add(new RealtimeAudioTruncation
        {
            ItemId = itemId,
            ContentIndex = contentIndex,
            AudioEndMilliseconds = audioEndMilliseconds
        });
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
                $"[AGENT] VOICE_ROUTE_READY source=" +
                $"{(playback == null ? "companion_body" : "player_voice_playback")}, " +
                "route=local_3d");
        }
        catch (Exception exception)
        {
            _source = null;
            Plugin.Logger.LogWarning(
                $"[AGENT] VOICE_ROUTE_UNAVAILABLE detail={exception.Message}");
        }
    }

    private void DropCurrentItem(string reason)
    {
        ResetCurrentItem();
        Plugin.Logger.LogWarning($"[AGENT] VOICE_DROPPED reason={reason}");
    }

    private void DropPendingIfNecessary(string reason)
    {
        if (_pendingClips.Count == 0 && _playing == null)
            return;

        ClearPendingClips();
        DestroyPlayingClip();
        Plugin.Logger.LogWarning($"[AGENT] VOICE_DROPPED reason={reason}");
    }

    private void ResetCurrentItem()
    {
        _currentItemAudio.SetLength(0);
        _currentItemAudio.Position = 0;
        _currentItemId = null;
        _currentContentIndex = 0;
    }

    private void ClearPendingClips()
    {
        while (_pendingClips.Count > 0)
        {
            var pending = _pendingClips.Dequeue();
            UnityEngine.Object.Destroy(pending.Clip);
        }
    }

    private void DestroyPlayingClip()
    {
        if (_playing == null)
            return;

        UnityEngine.Object.Destroy(_playing.Clip);
        _playing = null;
        if (_source != null)
            _source.clip = null;
    }

    private void ReleaseSource()
    {
        if (_source != null)
        {
            _source.Stop();
            _source.clip = null;
            UnityEngine.Object.Destroy(_source);
        }

        DestroyPlayingClip();
        _source = null;
        _speaker = null;
    }
}
