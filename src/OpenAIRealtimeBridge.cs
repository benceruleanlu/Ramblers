using System;
using UnityEngine;

namespace Ramblers;

/// <summary>
/// Owns the agent lifecycle and keeps Unity work on the main thread. Game voice,
/// tool validation, and the WebSocket protocol live behind separate boundaries.
/// </summary>
internal sealed class RealtimeAgentBridge : MonoBehaviour
{
    private const float ReconnectDelay = 5f;
    private const float EmbodiedToolTimeout = 5f;
    private const float VoiceResumeDelay = 0.15f;

    private sealed class PendingToolCall
    {
        internal RealtimeFunctionCall Call;
        internal string ResultJson;
        internal bool AwaitsInspection;
    }

    private sealed class PendingToolBatch
    {
        internal OpenAIRealtimeClient Client;
        internal string ResponseId;
        internal PendingToolCall[] Calls;
        internal int InspectionIndex = -1;
        internal long InspectionToken;
        internal float StartedAt;
        internal CompanionVisionObservation Observation;
    }

    private readonly GameVoiceInput _gameVoice = new GameVoiceInput();
    private readonly GameVoiceOutput _gameVoiceOutput = new GameVoiceOutput();
    private readonly LogLatch _missingKeyLog = new LogLatch();
    private OpenAIRealtimeClient _client;
    private PendingToolBatch _pendingToolBatch;
    private float _nextConnectAt;
    private float _voiceResumeAt;
    private bool _suppressSpeechStops;
    private bool _voiceResumedAfterTool;
    private long _inputEpochBarrier;
    private bool _inputEpochBarrierObserved;
    private long _postToolAudioAppendBaseline;
    private bool _releaseInspectionOnAssistantAudio;
    private long _inspectionAttentionToken;

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
        PollPendingToolBatch();
        var mayTickVoice = _pendingToolBatch == null &&
                           Time.realtimeSinceStartup >= _voiceResumeAt;
        var manualInterruptionRequested = mayTickVoice && _gameVoice.Tick(_client);
        if (mayTickVoice && !_gameVoice.IsWaitingForManualRelease &&
            _inputEpochBarrierObserved && _client != null &&
            _client.AudioAppendSequence > _postToolAudioAppendBaseline)
        {
            _voiceResumedAfterTool = true;
        }
        if (manualInterruptionRequested)
            InterruptAssistantSpeech();
        _gameVoiceOutput.Tick();
    }

    private void EnsureClient()
    {
        if (_client != null && !_client.IsStopped)
            return;

        if (_client != null)
        {
            CancelPendingToolBatch();
            _gameVoice.Stop(_client);
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
                // Steam may have started before the variable was added and can
                // therefore pass a stale process environment to the game.
                apiKey = Environment.GetEnvironmentVariable(
                    "OPENAI_API_KEY",
                    EnvironmentVariableTarget.User);
            }
            catch (PlatformNotSupportedException)
            {
                // Big Walk currently targets Windows; retain process-only lookup
                // if this code is ever exercised elsewhere.
            }
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (_missingKeyLog.ShouldLog())
            {
                Plugin.Logger.LogWarning(
                    "[AGENT] OpenAI disabled for this run: OPENAI_API_KEY is not present " +
                    "in the process or Windows user environment. No microphone audio will be sent.");
            }
            return;
        }

        _missingKeyLog.Reset();
        var configuredModel = Plugin.OpenAIRealtimeModel == null
            ? null
            : Plugin.OpenAIRealtimeModel.Value;
        var model = string.IsNullOrWhiteSpace(configuredModel)
            ? "gpt-realtime-2.1"
            : configuredModel.Trim();
        _client = new OpenAIRealtimeClient(apiKey.Trim(), model);
        _client.Start();
        Plugin.Logger.LogInfo(
            $"[AGENT] Connecting to OpenAI Realtime model {model}. " +
            "Listening follows Big Walk voice controls and direct proximity.");
    }

    private void DrainClientEvents()
    {
        if (_client == null)
            return;

        string message;
        while (_client.TryDequeueLog(out message))
            Plugin.Logger.LogInfo($"[AGENT] {message}");

        RealtimeFunctionCallBatch batch;
        while (_pendingToolBatch == null &&
               _client.TryDequeueFunctionCallBatch(out batch))
        {
            BeginToolBatch(batch);
        }

        RealtimeClientEvent clientEvent;
        while (_client.TryDequeueClientEvent(out clientEvent))
        {
            if (clientEvent.Type == RealtimeClientEventType.InputSpeechStarted)
            {
                if (_suppressSpeechStops && _pendingToolBatch == null &&
                    _voiceResumedAfterTool)
                {
                    _suppressSpeechStops = false;
                    Plugin.Logger.LogInfo(
                        "[AGENT] AUDIO_EPOCH_READY source=fresh_speech_start.");
                }
                InterruptAssistantSpeech();
            }
            else if (clientEvent.Type == RealtimeClientEventType.InputSpeechStopped)
            {
                if (_suppressSpeechStops)
                    Plugin.Logger.LogInfo(
                        "[AGENT] VAD_RESPONSE_IGNORED reason=stale_audio_epoch.");
                else
                    _client.RequestResponse();
            }
            else if (clientEvent.Type == RealtimeClientEventType.InputAudioCleared)
            {
                if (_suppressSpeechStops &&
                    clientEvent.InputEpoch == _inputEpochBarrier)
                {
                    _inputEpochBarrierObserved = true;
                    Plugin.Logger.LogInfo(
                        $"[AGENT] AUDIO_EPOCH_BARRIER epoch={_inputEpochBarrier}.");
                }
            }
            else if (clientEvent.Type == RealtimeClientEventType.AudioPacket)
            {
                if (_releaseInspectionOnAssistantAudio &&
                    clientEvent.AudioPacket?.Pcm16 != null &&
                    clientEvent.AudioPacket.Pcm16.Length > 0)
                {
                    CompanionController.ReleaseInspectionAttention(
                        _inspectionAttentionToken);
                    _releaseInspectionOnAssistantAudio = false;
                    _inspectionAttentionToken = 0;
                }
                _gameVoiceOutput.Accept(clientEvent.AudioPacket);
            }
        }
    }

    private void BeginToolBatch(RealtimeFunctionCallBatch batch)
    {
        if (batch?.Calls == null || batch.Calls.Length == 0)
            return;

        var pending = new PendingToolBatch
        {
            Client = _client,
            ResponseId = batch.ResponseId,
            Calls = new PendingToolCall[batch.Calls.Length],
            StartedAt = Time.realtimeSinceStartup
        };

        for (var index = 0; index < batch.Calls.Length; index++)
        {
            var functionCall = batch.Calls[index];
            var slot = new PendingToolCall { Call = functionCall };
            pending.Calls[index] = slot;

            AgentToolDispatch dispatch;
            try
            {
                dispatch = AgentToolRouter.Execute(functionCall);
            }
            catch (Exception exception)
            {
                dispatch = AgentToolDispatch.Immediate(
                    AgentToolResult.Failure("action_execution_failed"));
                Plugin.Logger.LogError(
                    $"[AGENT] CALL_FAILED name={functionCall?.Name}: {exception}");
            }

            if (dispatch.IsPending)
            {
                slot.AwaitsInspection = true;
                pending.InspectionIndex = index;
                pending.InspectionToken = dispatch.OperationToken;
                Plugin.Logger.LogInfo(
                    $"[AGENT] CALL name={functionCall.Name}, " +
                    $"arguments={functionCall.Arguments}, result=pending");
                continue;
            }

            slot.ResultJson = dispatch.Result.ToJson();
            LogToolResult(functionCall, slot.ResultJson);
        }

        if (pending.InspectionIndex < 0)
        {
            CompleteToolBatch(pending);
            return;
        }

        _pendingToolBatch = pending;
        _suppressSpeechStops = true;
        _voiceResumedAfterTool = false;
        _voiceResumeAt = float.PositiveInfinity;
        _gameVoice.Pause(_client);
        _postToolAudioAppendBaseline = _client.AudioAppendSequence;
        _inputEpochBarrier = _client.BeginInputAudioEpochBarrier();
        _inputEpochBarrierObserved = false;
        Plugin.Logger.LogInfo(
            $"[AGENT] TOOL_BATCH_DEFERRED responseId={pending.ResponseId}, " +
            $"calls={pending.Calls.Length}.");
    }

    private void PollPendingToolBatch()
    {
        var pending = _pendingToolBatch;
        if (pending == null)
            return;

        CompanionInspectionCompletion completion;
        if (CompanionController.TryTakeInspectionCompletion(
                pending.InspectionToken,
                out completion))
        {
            var result = completion?.Result ??
                         AgentToolResult.Failure("action_execution_failed");
            var slot = pending.Calls[pending.InspectionIndex];
            slot.ResultJson = result.ToJson();
            slot.AwaitsInspection = false;
            pending.Observation = completion?.Observation;
            LogToolResult(slot.Call, slot.ResultJson);
            CompleteToolBatch(pending);
            return;
        }

        if (Time.realtimeSinceStartup - pending.StartedAt < EmbodiedToolTimeout)
            return;

        CompanionController.CancelInspection(pending.InspectionToken);
        var timedOutSlot = pending.Calls[pending.InspectionIndex];
        timedOutSlot.ResultJson = AgentToolResult.Failure(
            "inspection_timed_out").ToJson();
        timedOutSlot.AwaitsInspection = false;
        LogToolResult(timedOutSlot.Call, timedOutSlot.ResultJson);
        Plugin.Logger.LogWarning(
            $"[AGENT] TOOL_BATCH_TIMEOUT responseId={pending.ResponseId}.");
        CompleteToolBatch(pending);
    }

    private void CompleteToolBatch(PendingToolBatch pending)
    {
        var outputs = new RealtimeFunctionOutput[pending.Calls.Length];
        for (var index = 0; index < pending.Calls.Length; index++)
        {
            var slot = pending.Calls[index];
            outputs[index] = new RealtimeFunctionOutput
            {
                CallId = slot.Call?.CallId,
                ResultJson = slot.ResultJson ??
                             AgentToolResult.Failure(
                                 "action_execution_failed").ToJson()
            };
        }

        var sent = pending.Client != null &&
                   pending.Client.CompleteFunctionCallBatch(
                       pending.ResponseId,
                       outputs,
                       pending.Observation);
        if (!sent)
        {
            Plugin.Logger.LogWarning(
                $"[AGENT] TOOL_BATCH_DISCARDED responseId={pending.ResponseId}.");
        }
        else
        {
            Plugin.Logger.LogInfo(
                $"[AGENT] TOOL_BATCH_COMPLETED responseId={pending.ResponseId}, " +
                $"calls={pending.Calls.Length}, image={pending.Observation != null}.");
        }

        if (ReferenceEquals(_pendingToolBatch, pending))
        {
            _pendingToolBatch = null;
            _voiceResumeAt = Time.realtimeSinceStartup + VoiceResumeDelay;
            _releaseInspectionOnAssistantAudio = pending.Observation != null;
            _inspectionAttentionToken = pending.Observation != null
                ? pending.InspectionToken
                : 0;
        }
    }

    private static void LogToolResult(
        RealtimeFunctionCall functionCall,
        string resultJson)
    {
        Plugin.Logger.LogInfo(
            $"[AGENT] CALL name={functionCall?.Name}, " +
            $"arguments={functionCall?.Arguments}, result={resultJson}");
    }

    private void CancelPendingToolBatch()
    {
        if (_pendingToolBatch == null)
            return;
        CompanionController.CancelInspection(_pendingToolBatch.InspectionToken);
        Plugin.Logger.LogInfo(
            $"[AGENT] TOOL_BATCH_CANCELLED responseId={_pendingToolBatch.ResponseId}.");
        _pendingToolBatch = null;
        _voiceResumeAt = 0f;
        _suppressSpeechStops = false;
        _voiceResumedAfterTool = false;
        _inputEpochBarrier = 0;
        _inputEpochBarrierObserved = false;
        _postToolAudioAppendBaseline = 0;
        _releaseInspectionOnAssistantAudio = false;
        _inspectionAttentionToken = 0;
    }

    private void InterruptAssistantSpeech()
    {
        var truncations = _gameVoiceOutput.Interrupt();
        if (_client == null)
            return;

        for (var index = 0; index < truncations.Count; index++)
            _client.TruncateAudio(truncations[index]);
    }

    private void StopClient()
    {
        CancelPendingToolBatch();
        CompanionController.CancelInspection(_inspectionAttentionToken);
        _inspectionAttentionToken = 0;
        _voiceResumeAt = 0f;
        _suppressSpeechStops = false;
        _voiceResumedAfterTool = false;
        _inputEpochBarrier = 0;
        _inputEpochBarrierObserved = false;
        _postToolAudioAppendBaseline = 0;
        _releaseInspectionOnAssistantAudio = false;
        _gameVoice.Stop(_client);
        _gameVoiceOutput.Stop();
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
