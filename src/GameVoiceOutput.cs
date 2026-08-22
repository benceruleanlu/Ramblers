using System;
using System.Collections.Generic;
using System.Text;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace Ramblers;

/// <summary>
/// Plays Realtime PCM out of a fixed looping AudioClip used as a ring buffer,
/// so the companion starts speaking on the first audio delta instead of waiting
/// for the model to finish generating the utterance. It also reports exactly how
/// much audio was heard when playback is interrupted.
/// </summary>
internal sealed class GameVoiceOutput
{
    private const int OutputSampleRate = 24000;

    // The ring holds a whole number of fixed-size blocks and every write is
    // exactly one block, so a write is always block-aligned and no single write
    // ever straddles the wrap. That removes the split-at-the-boundary case
    // rather than relying on SetData's behaviour there.
    private const int BlockSamples = 1024;                 // ~42.7 ms
    private const int RingBlocks = 48;                     // ~2.05 s
    private const int RingSamples = BlockSamples * RingBlocks;

    // Silence kept written ahead of the write cursor. An underrun shorter than
    // this plays as a gap instead of replaying whatever the previous lap left
    // behind in the ring.
    private const int GuardBlocks = 6;                     // ~256 ms
    private const int MaxLeadBlocks = RingBlocks - GuardBlocks;

    // Jitter buffer: how much audio is held before the first sample is heard.
    private const int StartBlocks = 4;                     // ~171 ms

    /// <summary>
    /// One conversation item's audio: decoded samples waiting for the ring, plus
    /// the accounting needed to report how much of it the human actually heard.
    /// </summary>
    private sealed class VoiceSegment
    {
        internal string ItemId;
        internal int ContentIndex;
        internal float[] Samples = new float[BlockSamples * 8];
        internal int SampleCount;
        internal int SampleHead;
        internal bool Complete;
        internal bool HasCarryByte;
        internal byte CarryByte;

        // Absolute ring position of this segment's first sample, and the count
        // of real (unpadded) samples copied in. Together they turn the shared
        // play cursor into a per-item offset for conversation.item.truncate.
        internal long RingStartSample = -1;
        internal int WrittenSamples;

        // Samples banked from ring timelines this segment has already outlived.
        // An underrun restart rebases the play cursor, so what came before it
        // can no longer be measured against RingStartSample.
        internal int DeliveredSamples;
    }

    private readonly List<VoiceSegment> _segments = new List<VoiceSegment>();
    private readonly LogLatch _ringWriteFailureLog = new LogLatch();

    private PlayerCharacter _speaker;
    private AudioSource _source;
    private AnimationCurve _attenuationCurve;
    private float _nextRouteLevelLog;
    private AudioClip _ring;
    private Il2CppStructArray<float> _writeBlock;
    private Il2CppStructArray<float> _zeroBlock;

    private VoiceSegment _incoming;
    private int _fillIndex;
    private long _writeBlocksTotal;
    private long _zeroedThroughBlocks;
    private long _playedBase;
    private int _lastTimeSamples;
    private bool _playing;
    private int _utteranceNumber;

    /// <summary>True while synthetic speech is actually coming out of the body.</summary>
    internal bool IsSpeaking => _playing;

    internal void Accept(RealtimeAudioPacket packet)
    {
        if (packet == null)
            return;

        var segment = ResolveSegment(packet);
        if (segment == null)
            return;

        if (packet.Pcm16 != null && packet.Pcm16.Length > 0)
            AppendPcm(segment, packet.Pcm16);

        if (!packet.EndsItem)
            return;

        segment.Complete = true;
        if (ReferenceEquals(_incoming, segment))
            _incoming = null;
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
        if (_source == null || _ring == null)
            return;
        UpdateVoiceAttenuation(human, bot);

        if (_playing)
        {
            ObservePlayback();

            // A stall longer than the buffered lead leaves the play head past
            // the write cursor. Writing on that timeline would land behind the
            // read head, so rebase before FillRing rather than after.
            if (!IsFullyStaged() &&
                PlayedSamples() >= _writeBlocksTotal * BlockSamples)
            {
                RestartAfterUnderrun();
            }
        }

        FillRing();
        MaintainSilenceGuard();

        if (!_playing)
        {
            if (_writeBlocksTotal >= StartBlocks ||
                (_writeBlocksTotal > 0 && IsFullyStaged()))
            {
                StartPlayback();
            }
            return;
        }

        if (IsFullyStaged() && PlayedSamples() >= _writeBlocksTotal * BlockSamples)
            StopPlayback();
    }

    /// <summary>
    /// Stops all assistant speech and returns truncations for conversation
    /// items whose generated audio was not fully heard by the user.
    /// </summary>
    internal List<RealtimeAudioTruncation> Interrupt()
    {
        var truncations = new List<RealtimeAudioTruncation>();
        var seenItems = new HashSet<string>(StringComparer.Ordinal);
        var heardReport = new StringBuilder();

        if (_playing)
            ObservePlayback();
        var played = _playing ? PlayedSamples() : 0L;

        for (var index = 0; index < _segments.Count; index++)
        {
            var segment = _segments[index];
            var heard = (long)segment.DeliveredSamples;
            if (segment.RingStartSample >= 0)
            {
                var reached = played - segment.RingStartSample;
                if (reached > 0)
                {
                    heard += Math.Min(
                        reached,
                        segment.WrittenSamples - segment.DeliveredSamples);
                }
            }

            var before = truncations.Count;
            AddTruncation(
                truncations,
                seenItems,
                segment.ItemId,
                segment.ContentIndex,
                (int)(heard * 1000L / OutputSampleRate));
            if (truncations.Count == before)
                continue;

            // Heard against generated, so the truncation offset can be read
            // back rather than inferred from the item count alone.
            if (heardReport.Length > 0)
                heardReport.Append(' ');
            heardReport
                .Append(heard * 1000L / OutputSampleRate)
                .Append('/')
                .Append(segment.WrittenSamples * 1000L / OutputSampleRate)
                .Append("ms");
        }

        StopSource();
        ClearSegments();
        PrepareRing();

        if (truncations.Count > 0)
            Plugin.Logger.LogInfo(
                $"[AGENT] VOICE_INTERRUPTED items={truncations.Count}, heard={heardReport}");

        return truncations;
    }

    internal void Stop()
    {
        ClearSegments();
        ReleaseSource();
    }

    /// <summary>
    /// Routes a packet to the segment it belongs to, opening one when the model
    /// starts a new conversation item.
    /// </summary>
    private VoiceSegment ResolveSegment(RealtimeAudioPacket packet)
    {
        if (_incoming != null)
        {
            var sameItem =
                string.IsNullOrEmpty(packet.ItemId) ||
                (string.Equals(_incoming.ItemId, packet.ItemId, StringComparison.Ordinal) &&
                 _incoming.ContentIndex == packet.ContentIndex);
            if (sameItem)
                return _incoming;

            // The previous item's audio may already be playing, so it is closed
            // and the new item queues behind it rather than being discarded.
            _incoming.Complete = true;
            _incoming = null;
            Plugin.Logger.LogInfo("[AGENT] VOICE_ITEM_CLOSED reason=next_item_started");
        }

        // A done event carrying no audio of its own has nothing left to open.
        if (packet.EndsItem && (packet.Pcm16 == null || packet.Pcm16.Length == 0))
            return null;

        var segment = new VoiceSegment
        {
            ItemId = packet.ItemId,
            ContentIndex = packet.ContentIndex
        };
        _segments.Add(segment);
        _incoming = segment;
        return segment;
    }

    private static void AppendPcm(VoiceSegment segment, byte[] pcm16)
    {
        var offset = 0;
        if (segment.HasCarryByte)
        {
            EnsureCapacity(segment, 1);
            segment.Samples[segment.SampleCount++] =
                ToUnitFloat((short)(segment.CarryByte | (pcm16[0] << 8)));
            segment.HasCarryByte = false;
            offset = 1;
        }

        var available = pcm16.Length - offset;
        var whole = available / 2;
        if (whole > 0)
        {
            EnsureCapacity(segment, whole);
            for (var index = 0; index < whole; index++)
            {
                var low = pcm16[offset + index * 2];
                var high = pcm16[offset + index * 2 + 1];
                segment.Samples[segment.SampleCount++] =
                    ToUnitFloat((short)(low | (high << 8)));
            }
        }

        // Deltas carry whole samples in practice; holding the odd byte keeps a
        // split sample from shifting every sample after it by one byte.
        if ((available & 1) == 1)
        {
            segment.CarryByte = pcm16[pcm16.Length - 1];
            segment.HasCarryByte = true;
        }
    }

    private static float ToUnitFloat(short value)
    {
        return value / (value < 0 ? 32768f : 32767f);
    }

    private static void EnsureCapacity(VoiceSegment segment, int additional)
    {
        if (segment.Samples == null)
            segment.Samples = new float[Math.Max(BlockSamples * 8, additional)];

        if (segment.SampleCount + additional <= segment.Samples.Length)
            return;

        // Reclaim whatever already reached the ring before growing the buffer.
        if (segment.SampleHead > 0)
        {
            var remaining = segment.SampleCount - segment.SampleHead;
            Array.Copy(segment.Samples, segment.SampleHead, segment.Samples, 0, remaining);
            segment.SampleCount = remaining;
            segment.SampleHead = 0;
            if (segment.SampleCount + additional <= segment.Samples.Length)
                return;
        }

        var capacity = segment.Samples.Length;
        while (capacity < segment.SampleCount + additional)
            capacity *= 2;
        Array.Resize(ref segment.Samples, capacity);
    }

    /// <summary>
    /// Copies staged audio into the ring one block at a time, stopping when the
    /// buffered lead would run past the read cursor's safe window.
    /// </summary>
    private void FillRing()
    {
        while (_fillIndex < _segments.Count)
        {
            var segment = _segments[_fillIndex];
            var pending = segment.Samples == null
                ? 0
                : segment.SampleCount - segment.SampleHead;

            if (pending <= 0)
            {
                if (!segment.Complete)
                    return;
                ReleaseSegmentBuffer(segment);
                _fillIndex++;
                continue;
            }

            // A partial block is held back until the item ends, so a full block
            // of real audio is written whenever more of it is still coming.
            if (pending < BlockSamples && !segment.Complete)
                return;

            if (!HasRoomForBlock())
                return;

            if (!WriteBlock(segment, Math.Min(pending, BlockSamples)))
                return;
        }
    }

    private bool HasRoomForBlock()
    {
        var leadBlocks = _playing
            ? _writeBlocksTotal - PlayedSamples() / BlockSamples
            : _writeBlocksTotal;
        return leadBlocks < MaxLeadBlocks;
    }

    private bool WriteBlock(VoiceSegment segment, int count)
    {
        for (var index = 0; index < count; index++)
            _writeBlock[index] = segment.Samples[segment.SampleHead + index];

        // Only the final block of an item is ever short. Padding it with silence
        // keeps every write block-aligned, and WrittenSamples still counts real
        // samples, so the padding cannot inflate a truncation offset.
        for (var index = count; index < BlockSamples; index++)
            _writeBlock[index] = 0f;

        if (segment.RingStartSample < 0)
            segment.RingStartSample = _writeBlocksTotal * BlockSamples;

        if (!_ring.SetData(_writeBlock, (int)(_writeBlocksTotal % RingBlocks) * BlockSamples))
        {
            if (_ringWriteFailureLog.ShouldLog())
                Plugin.Logger.LogWarning("[AGENT] VOICE_DROPPED reason=ring_write_failed");
            return false;
        }

        segment.SampleHead += count;
        segment.WrittenSamples += count;
        _writeBlocksTotal++;
        return true;
    }

    private void MaintainSilenceGuard()
    {
        var target = _writeBlocksTotal + GuardBlocks;
        if (_zeroedThroughBlocks >= target)
            return;

        // The lead is capped at RingBlocks - GuardBlocks, so the guard can never
        // reach far enough forward to land on audio that has not been heard yet.
        for (var block = Math.Max(_zeroedThroughBlocks, _writeBlocksTotal);
             block < target;
             block++)
        {
            _ring.SetData(_zeroBlock, (int)(block % RingBlocks) * BlockSamples);
        }

        _zeroedThroughBlocks = target;
    }

    private bool IsFullyStaged()
    {
        return _fillIndex >= _segments.Count;
    }

    /// <summary>
    /// Converts the looping clip's wrapping play head into a monotonic sample
    /// count. It is polled every frame, so a lap can never be missed.
    /// </summary>
    private void ObservePlayback()
    {
        if (_source == null)
            return;

        var position = _source.timeSamples;
        if (position < 0)
            position = 0;
        else if (position >= RingSamples)
            position = RingSamples - 1;

        if (position < _lastTimeSamples)
            _playedBase += RingSamples;
        _lastTimeSamples = position;
    }

    private long PlayedSamples()
    {
        return _playedBase + _lastTimeSamples;
    }

    private void StartPlayback()
    {
        // Stop rewinds the looping clip, so playback begins at ring sample 0
        // without needing the timeSamples setter.
        _source.Stop();
        _playedBase = 0;
        _lastTimeSamples = 0;
        _source.Play();
        _playing = true;
        _utteranceNumber++;
        Plugin.Logger.LogInfo(
            $"[AGENT] VOICE_STREAM_STARTED utterance={_utteranceNumber}, " +
            $"bufferedMs={_writeBlocksTotal * BlockSamples * 1000L / OutputSampleRate}, " +
            "route=local_3d");
    }

    /// <summary>
    /// Recovers from the play head overtaking the write cursor. Playback stops
    /// and the ring is rebased, so staged audio re-enters at the head of a fresh
    /// timeline behind a full jitter buffer instead of being written into slots
    /// the read head has already passed.
    /// </summary>
    private void RestartAfterUnderrun()
    {
        for (var index = 0; index < _segments.Count; index++)
        {
            var segment = _segments[index];

            // Everything written before the rebase counts as delivered. A gap
            // in the middle cannot be expressed as one truncation offset, and
            // under-reporting would make the model repeat what was mostly heard.
            segment.DeliveredSamples = segment.WrittenSamples;
            segment.RingStartSample = -1;
        }

        var starvedMs = (PlayedSamples() - _writeBlocksTotal * BlockSamples) *
                        1000L / OutputSampleRate;
        StopSource();
        PrepareRing();
        Plugin.Logger.LogWarning(
            $"[AGENT] VOICE_UNDERRUN starvedMs={starvedMs}, action=rebased");
    }

    private void StopPlayback()
    {
        var seconds = _writeBlocksTotal * BlockSamples / (float)OutputSampleRate;
        StopSource();
        ClearSegments();
        PrepareRing();
        Plugin.Logger.LogInfo($"[AGENT] VOICE_STREAM_DRAINED seconds={seconds:F2}");
    }

    private void StopSource()
    {
        if (_source != null)
            _source.Stop();
        _playing = false;
    }

    private void EnsureSource(PlayerCharacter bot)
    {
        if (_speaker == bot && _source != null && _ring != null)
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

            _ring = AudioClip.Create("RamblersVoiceRing", RingSamples, 1, OutputSampleRate, false);
            _writeBlock = new Il2CppStructArray<float>(BlockSamples);
            _zeroBlock = new Il2CppStructArray<float>(BlockSamples);

            _source = voiceObject.AddComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.loop = true;
            _source.clip = _ring;
            _source.spatialBlend = 1f;
            _source.dopplerLevel = 0f;
            _source.volume = 1f;

            // AttenuationCurve is the same direct field path used by the stable
            // pre-0.13 output and by GameVoiceInput. Do not introspect
            // Dissonance SourceController or enumerate live playback controls:
            // those generated IL2CPP wrapper chains were newly present in the
            // first deployed 0.13 speech turn that ended in a native CoreCLR
            // access violation, and such a failure cannot be caught here.
            _attenuationCurve = playback == null ? null : playback.AttenuationCurve;
            if (_attenuationCurve != null)
            {
                ConfigureMetreAttenuation();
            }
            else
            {
                _source.rolloffMode = AudioRolloffMode.Logarithmic;
                _source.minDistance = 1f;
                _source.maxDistance = 60f;
            }

            PrepareRing();
            Plugin.Logger.LogInfo(
                $"[AGENT] VOICE_ROUTE_READY source=" +
                $"{(playback == null ? "companion_body" : "player_voice_playback")}, " +
                $"route=local_3d_safe, spatialBlend={_source.spatialBlend:F2}, " +
                $"rolloff={_source.rolloffMode}, curveMode=" +
                $"{(_attenuationCurve == null ? "unity_fallback" : "game_metre_curve")}, " +
                $"curve0={EvaluateAttenuation(0f):F3}, " +
                $"curve2_5={EvaluateAttenuation(2.5f):F3}, " +
                $"curve5={EvaluateAttenuation(5f):F3}, " +
                $"curve10={EvaluateAttenuation(10f):F3}, " +
                $"curve20={EvaluateAttenuation(20f):F3}, " +
                $"ringMs={RingSamples * 1000L / OutputSampleRate}");
        }
        catch (Exception exception)
        {
            ReleaseSource();
            Plugin.Logger.LogWarning(
                $"[AGENT] VOICE_ROUTE_UNAVAILABLE detail={exception.Message}");
        }
    }

    private void ConfigureMetreAttenuation()
    {
        // Big Walk's curve is keyed in metres and evaluated directly by
        // PlayerVoicePlaybackControl. Unity rescales AudioSource custom curves
        // over maxDistance, so installing that same curve changes its meaning.
        // Keep Unity's rolloff flat and apply the game-owned curve per frame.
        _source.rolloffMode = AudioRolloffMode.Custom;
        _source.minDistance = 0.01f;
        _source.maxDistance = 1000f;
        _source.SetCustomCurve(
            AudioSourceCurveType.CustomRolloff,
            AnimationCurve.Linear(0f, 1f, 1f, 1f));
    }

    private void UpdateVoiceAttenuation(PlayerCharacter human, PlayerCharacter bot)
    {
        if (_source == null || human == null || bot == null)
            return;
        if (_attenuationCurve == null)
            return;

        var distance = Vector3.Distance(
            human.transform.position,
            bot.transform.position);
        var attenuation = EvaluateAttenuation(distance);
        _source.volume = Mathf.Clamp01(attenuation);
        if (_playing && Time.realtimeSinceStartup >= _nextRouteLevelLog)
        {
            _nextRouteLevelLog = Time.realtimeSinceStartup + 1f;
            Plugin.Logger.LogInfo(
                $"[AGENT] VOICE_ROUTE_LEVEL distance={distance:F2}, " +
                $"gameAttenuation={attenuation:F3}, " +
                $"sourceVolume={_source.volume:F3}, route=local_3d_safe.");
        }
    }

    private float EvaluateAttenuation(float distance)
    {
        return _attenuationCurve == null
            ? 1f
            : Mathf.Max(0f, _attenuationCurve.Evaluate(Mathf.Max(0f, distance)));
    }

    /// <summary>
    /// Silences the whole ring and rebases the cursors, so the next utterance
    /// cannot run into audio left behind by the previous one.
    /// </summary>
    private void PrepareRing()
    {
        _writeBlocksTotal = 0;
        _zeroedThroughBlocks = RingBlocks;
        _playedBase = 0;
        _lastTimeSamples = 0;

        if (_ring == null || _zeroBlock == null)
            return;

        for (var block = 0; block < RingBlocks; block++)
            _ring.SetData(_zeroBlock, block * BlockSamples);
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

    private static void ReleaseSegmentBuffer(VoiceSegment segment)
    {
        segment.Samples = null;
        segment.SampleCount = 0;
        segment.SampleHead = 0;
    }

    private void DropPendingIfNecessary(string reason)
    {
        if (_segments.Count == 0 && !_playing)
            return;

        StopSource();
        ClearSegments();
        Plugin.Logger.LogWarning($"[AGENT] VOICE_DROPPED reason={reason}");
    }

    private void ClearSegments()
    {
        _segments.Clear();
        _fillIndex = 0;
        _incoming = null;
    }

    private void ReleaseSource()
    {
        if (_source != null)
        {
            _source.Stop();
            _source.clip = null;
            UnityEngine.Object.Destroy(_source);
        }

        if (_ring != null)
            UnityEngine.Object.Destroy(_ring);

        _source = null;
        _ring = null;
        _writeBlock = null;
        _zeroBlock = null;
        _speaker = null;
        _attenuationCurve = null;
        _nextRouteLevelLog = 0f;
        _playing = false;
        _writeBlocksTotal = 0;
        _zeroedThroughBlocks = 0;
        _playedBase = 0;
        _lastTimeSamples = 0;
        _ringWriteFailureLog.Reset();
    }
}
