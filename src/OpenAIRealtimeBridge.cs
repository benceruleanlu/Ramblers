using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ramblers;

/// <summary>
/// Owns the agent lifecycle and keeps Unity work on the main thread. Game voice,
/// tool validation, and the WebSocket protocol live behind separate boundaries.
/// </summary>
internal sealed class RealtimeAgentBridge : MonoBehaviour
{
    private const float ReconnectDelay = 5f;

    private sealed class PendingToolCall
    {
        internal RealtimeFunctionCall Call;
        internal string ResultJson;
        internal bool AwaitsJob;
    }

    private sealed class PendingToolBatch
    {
        internal OpenAIRealtimeClient Client;
        internal string ResponseId;
        internal long TurnId;
        internal PendingToolCall[] Calls;
        internal int JobIndex = -1;
        internal long JobToken;
        internal float StartedAt;
        internal float TimeoutSeconds;
        internal AgentContinuationItem[] Continuation;
        internal bool RetainJobUntilAssistantAudio;
    }

    private readonly GameVoiceInput _gameVoice = new GameVoiceInput();
    private readonly GameVoiceOutput _gameVoiceOutput = new GameVoiceOutput();
    private readonly LogLatch _missingKeyLog = new LogLatch();
    private readonly Dictionary<long, CompanionTurnReference> _turnReferences =
        new Dictionary<long, CompanionTurnReference>();
    private readonly HashSet<long> _completedTurnIds = new HashSet<long>();
    private readonly HashSet<long> _toolBatchTurnsThisFrame = new HashSet<long>();
    private OpenAIRealtimeClient _client;
    private PendingToolBatch _pendingToolBatch;
    private float _nextConnectAt;
    private bool _userSpeaking;
    private bool _continuationHeld;
    private bool _concludeJobOnAssistantAudio;
    private long _lingeringJobToken;
    private long _heldContinuationTurnId;
    private long _nextTurnId;

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
        var voiceEvents = _gameVoice.Tick(_client);
        if ((voiceEvents & GameVoiceTickEvents.ManualTurnStarted) != 0)
            HandleHumanSpeechStarted("manual_ptt");
        if ((voiceEvents & GameVoiceTickEvents.ManualTurnSubmitted) != 0)
            CaptureTurnAndRequestResponse("manual_ptt");
        DrainFunctionCallBatches();
        CleanupCompletedTurnReferences();
        PollPendingToolBatch();

        // Listening never stops for a tool call. Response ordering is held by
        // the outstanding-batch guard in the client, not by going deaf.
        ReleaseHeldContinuation();
        _gameVoiceOutput.Tick();

        // Speech on either side is what the companion's idle attention yields
        // to. Sampled after the output tick so playback that just stopped is
        // not reported as still speaking.
        CompanionController.SetConversationActive(
            IsHumanSpeaking() || _gameVoiceOutput.IsSpeaking);
    }

    /// <summary>
    /// True while the human is mid-utterance in either turn mode. Semantic VAD
    /// reports its boundaries as server events; a push-to-talk hold has none, so
    /// it is read from the capture itself.
    /// </summary>
    private bool IsHumanSpeaking()
    {
        return _userSpeaking || _gameVoice.IsCapturingManualTurn;
    }

    /// <summary>
    /// Requests the response a completed tool batch had to hold back. Driving it
    /// from one place covers a push-to-talk release too short to commit, which
    /// would otherwise strand the outputs with nothing left to ask for them.
    /// </summary>
    private void ReleaseHeldContinuation()
    {
        if (!_continuationHeld || _client == null || IsHumanSpeaking())
            return;

        _continuationHeld = false;
        // A continuation request is refused when a newer user response already
        // owns or is waiting for the response slot.
        _client.RequestContinuation(_heldContinuationTurnId);
        Plugin.Logger.LogInfo(
            $"[AGENT] CONTINUATION_RELEASED turnId={_heldContinuationTurnId}.");
        _heldContinuationTurnId = 0;
    }

    private void EnsureClient()
    {
        if (_client != null && !_client.IsStopped)
            return;

        if (_client != null)
        {
            CancelPendingToolBatch();
            _turnReferences.Clear();
            _completedTurnIds.Clear();
            _toolBatchTurnsThisFrame.Clear();
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

        RealtimeClientEvent clientEvent;
        while (_client.TryDequeueClientEvent(out clientEvent))
        {
            if (clientEvent.Type == RealtimeClientEventType.InputSpeechStarted)
            {
                _userSpeaking = true;
                HandleHumanSpeechStarted("semantic_vad");
            }
            else if (clientEvent.Type == RealtimeClientEventType.InputSpeechStopped)
            {
                _userSpeaking = false;
                // Refused while a tool batch is outstanding, and latched rather
                // than lost, so this turn is answered once the outputs land.
                CaptureTurnAndRequestResponse("semantic_vad");
            }
            else if (clientEvent.Type == RealtimeClientEventType.ResponseCompleted)
            {
                if (clientEvent.TurnId > 0)
                    _completedTurnIds.Add(clientEvent.TurnId);
            }
            else if (clientEvent.Type == RealtimeClientEventType.AudioPacket)
            {
                if (_concludeJobOnAssistantAudio &&
                    clientEvent.AudioPacket?.Pcm16 != null &&
                    clientEvent.AudioPacket.Pcm16.Length > 0)
                {
                    CompanionController.ConcludeJob(
                        _lingeringJobToken);
                    _concludeJobOnAssistantAudio = false;
                    _lingeringJobToken = 0;
                }
                _gameVoiceOutput.Accept(clientEvent.AudioPacket);
            }
        }
    }

    /// <summary>
    /// Function calls are drained only after both semantic-VAD events and the
    /// local push-to-talk edge have been sampled. That ordering makes a queued
    /// old physical call observe reference invalidation before dispatch.
    /// </summary>
    private void DrainFunctionCallBatches()
    {
        if (_client == null)
            return;

        _toolBatchTurnsThisFrame.Clear();
        RealtimeFunctionCallBatch batch;
        while (_pendingToolBatch == null &&
               _client.TryDequeueFunctionCallBatch(out batch))
        {
            if (batch != null && batch.TurnId > 0)
                _toolBatchTurnsThisFrame.Add(batch.TurnId);
            BeginToolBatch(batch);
        }
    }

    private void CleanupCompletedTurnReferences()
    {
        if (_completedTurnIds.Count == 0)
            return;

        var completed = new long[_completedTurnIds.Count];
        _completedTurnIds.CopyTo(completed);
        _completedTurnIds.Clear();
        for (var index = 0; index < completed.Length; index++)
        {
            var turnId = completed[index];
            // A tool continuation is still part of this same user turn. Keep
            // its frozen referent until a later response completes without a
            // function-call batch or new speech invalidates it.
            if (_toolBatchTurnsThisFrame.Contains(turnId))
                continue;
            _turnReferences.Remove(turnId);
        }
    }

    private void HandleHumanSpeechStarted(string source)
    {
        var invalidated = _turnReferences.Count;
        _turnReferences.Clear();

        // If the prior turn's physical action already began, a correction must
        // cross its exact-target reconciliation path. If a reference-based
        // action is merely queued, invalidation makes dispatch fail closed.
        if (PendingBatchContainsPhysicalAction(_pendingToolBatch))
        {
            CompanionController.CancelJob(_pendingToolBatch.JobToken);
            Plugin.Logger.LogInfo(
                $"[AGENT] PHYSICAL_CALL_INTERRUPTED source={source}, " +
                $"turnId={_pendingToolBatch.TurnId}.");
        }

        InterruptAssistantSpeech();
        if (invalidated > 0)
        {
            Plugin.Logger.LogInfo(
                $"[AGENT] TURN_REFERENCE_INVALIDATED source={source}, " +
                $"count={invalidated}.");
        }
    }

    private void CaptureTurnAndRequestResponse(string source)
    {
        if (_client == null)
            return;

        var turnId = ++_nextTurnId;
        CompanionInteractionTarget target;
        string captureError;
        CompanionController.TryCaptureInteractionTarget(
            out target,
            out captureError);
        CompanionInspectionCandidates inspectionCandidates;
        string inspectionCaptureError;
        CompanionController.TryCaptureInspectionCandidates(
            out inspectionCandidates,
            out inspectionCaptureError);
        CompanionPeckCandidates peckCandidates;
        string peckCaptureError;
        CompanionController.TryCapturePeckCandidates(
            out peckCandidates,
            out peckCaptureError);
        CompanionAwarenessTurnContext awarenessContext;
        string awarenessCaptureError;
        CompanionController.TryTakeAwarenessTurnContext(
            out awarenessContext,
            out awarenessCaptureError);
        _turnReferences[turnId] = new CompanionTurnReference
        {
            TurnId = turnId,
            Target = target,
            CaptureError = captureError,
            InspectionCandidates = inspectionCandidates,
            InspectionCaptureError = inspectionCaptureError,
            PeckCandidates = peckCandidates,
            PeckCaptureError = peckCaptureError,
            EntityReferences = awarenessContext?.EntityReferences
        };
        var awarenessQueued = false;
        if (awarenessContext != null)
        {
            try
            {
                awarenessQueued = _client.QueueTurnContext(awarenessContext.Message);
            }
            catch (Exception exception)
            {
                awarenessCaptureError = "awareness_context_queue_failed";
                Plugin.Logger.LogWarning(
                    $"[AWARENESS] TURN_CONTEXT_QUEUE_FAILED error={exception.Message}");
            }
        }
        _client.RequestResponse(turnId);

        if (target == null)
        {
            Plugin.Logger.LogInfo(
                $"[AGENT] TURN_REFERENCE_CAPTURED source={source}, " +
                $"turnId={turnId}, status=unavailable, " +
                $"reason={captureError ?? "human_reference_not_captured"}.");
        }
        else
        {
            Plugin.Logger.LogInfo(
                $"[AGENT] TURN_REFERENCE_CAPTURED source={source}, " +
                $"turnId={turnId}, status=prop, " +
                $"referenceId={target.ReferenceId}, netId={target.NetworkId}.");
        }

        if (inspectionCandidates == null)
        {
            Plugin.Logger.LogInfo(
                $"[AGENT] TURN_INSPECTION_REFERENCES_CAPTURED source={source}, " +
                $"turnId={turnId}, status=unavailable, " +
                $"reason={inspectionCaptureError ?? "inspection_reference_not_captured"}.");
        }
        else
        {
            Plugin.Logger.LogInfo(
                $"[AGENT] TURN_INSPECTION_REFERENCES_CAPTURED source={source}, " +
                $"turnId={turnId}, gazeAvailable={inspectionCandidates.GazeAvailable}, " +
                $"gazeRayHit={inspectionCandidates.GazeRayHit}, " +
                $"gazeReason={inspectionCandidates.GazeCaptureError ?? "none"}, " +
                $"heldItemAvailable={inspectionCandidates.HeldItemAvailable}, " +
                $"heldItemReason={inspectionCandidates.HeldItemCaptureError ?? "none"}, " +
                $"heldReferenceId={inspectionCandidates.HeldItemReferenceId}, " +
                $"heldNetId={inspectionCandidates.HeldItemNetworkId}.");
        }

        if (peckCandidates == null)
        {
            Plugin.Logger.LogInfo(
                $"[AGENT] TURN_INTERACTION_REFERENCE_CAPTURED source={source}, " +
                $"turnId={turnId}, status=unavailable, " +
                $"reason={peckCaptureError ?? "interaction_reference_not_captured"}.");
        }
        else
        {
            Plugin.Logger.LogInfo(
                $"[AGENT] TURN_INTERACTION_REFERENCES_CAPTURED source={source}, " +
                $"turnId={turnId}, " +
                $"humanAvailable={peckCandidates.HumanReferenceAvailable}, " +
                $"humanReason={peckCandidates.HumanReferenceError ?? "none"}, " +
                $"humanReferenceId={peckCandidates.HumanReferenceId}, " +
                $"humanNetId={peckCandidates.HumanReferenceNetworkId}, " +
                $"heldItemAvailable={peckCandidates.CompanionHeldItemAvailable}, " +
                $"heldItemReason={peckCandidates.CompanionHeldItemError ?? "none"}, " +
                $"heldReferenceId={peckCandidates.CompanionHeldItemReferenceId}, " +
                $"heldNetId={peckCandidates.CompanionHeldItemNetworkId}.");
        }

        if (!awarenessQueued)
        {
            Plugin.Logger.LogInfo(
                $"[AWARENESS] TURN_CONTEXT_CAPTURED source={source}, " +
                $"turnId={turnId}, status=unavailable, " +
                $"reason={awarenessCaptureError ?? "awareness_context_not_captured"}.");
        }
        else
        {
            Plugin.Logger.LogInfo(
                $"[AWARENESS] TURN_CONTEXT_CAPTURED source={source}, " +
                $"turnId={turnId}, status=queued, " +
                $"textChars={awarenessContext.Message.Text.Length}, " +
                $"events={awarenessContext.EventCount}, " +
                $"nearbyProps={awarenessContext.NearbyPropCount}, " +
                $"rememberedProps={awarenessContext.RememberedPropCount}, " +
                $"actionableEntities={awarenessContext.EntityReferences?.Count ?? 0}, " +
                $"nearbyPlayers={awarenessContext.NearbyPlayerCount}, " +
                $"visualAttached={awarenessContext.HasImage}, " +
                $"visualAgeSeconds={awarenessContext.VisualAgeSeconds:F1}.");
        }
    }

    private static bool PendingBatchContainsPhysicalAction(PendingToolBatch pending)
    {
        if (pending?.Calls == null)
            return false;
        for (var index = 0; index < pending.Calls.Length; index++)
        {
            var name = pending.Calls[index]?.Call?.Name;
            if (pending.Calls[index]?.AwaitsJob == true &&
                (string.Equals(
                     name,
                     AgentToolCatalog.InteractWithObject,
                     StringComparison.Ordinal) ||
                 string.Equals(
                     name,
                     AgentToolCatalog.PickUpItem,
                     StringComparison.Ordinal) ||
                 string.Equals(
                     name,
                     AgentToolCatalog.KickItem,
                     StringComparison.Ordinal) ||
                 string.Equals(
                     name,
                     AgentToolCatalog.DropItem,
                     StringComparison.Ordinal)))
            {
                return true;
            }
        }
        return false;
    }

    private void BeginToolBatch(RealtimeFunctionCallBatch batch)
    {
        if (batch?.Calls == null || batch.Calls.Length == 0)
            return;

        var pending = new PendingToolBatch
        {
            Client = _client,
            ResponseId = batch.ResponseId,
            TurnId = batch.TurnId,
            Calls = new PendingToolCall[batch.Calls.Length],
            StartedAt = Time.realtimeSinceStartup
        };

        CompanionTurnReference turnReference;
        _turnReferences.TryGetValue(batch.TurnId, out turnReference);

        for (var index = 0; index < batch.Calls.Length; index++)
        {
            var functionCall = batch.Calls[index];
            var slot = new PendingToolCall { Call = functionCall };
            pending.Calls[index] = slot;

            AgentToolDispatch dispatch;
            try
            {
                dispatch = AgentToolRouter.Execute(
                    functionCall,
                    turnReference);
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
                // One deferred job at a time. The coordinator refuses a second
                // while one is running, so a batch cannot reach this branch
                // twice and silently drop the first job's token.
                slot.AwaitsJob = true;
                pending.JobIndex = index;
                pending.JobToken = dispatch.OperationToken;
                pending.TimeoutSeconds = dispatch.TimeoutSeconds;
                Plugin.Logger.LogInfo(
                    $"[AGENT] CALL name={functionCall.Name}, " +
                    $"arguments={functionCall.Arguments}, " +
                    $"turnId={batch.TurnId}, result=pending");
                continue;
            }

            slot.ResultJson = dispatch.Result.ToJson();
            LogToolResult(functionCall, slot.ResultJson);
        }

        if (pending.JobIndex < 0)
        {
            CompleteToolBatch(pending);
            return;
        }

        _pendingToolBatch = pending;
        Plugin.Logger.LogInfo(
            $"[AGENT] TOOL_BATCH_DEFERRED responseId={pending.ResponseId}, " +
            $"turnId={pending.TurnId}, calls={pending.Calls.Length}.");
    }

    private void PollPendingToolBatch()
    {
        var pending = _pendingToolBatch;
        if (pending == null)
            return;

        CompanionJobCompletion completion;
        if (CompanionController.TryTakeJobCompletion(
                pending.JobToken,
                out completion))
        {
            var result = completion?.Result ??
                         AgentToolResult.Failure("action_execution_failed");
            var slot = pending.Calls[pending.JobIndex];
            slot.ResultJson = result.ToJson();
            slot.AwaitsJob = false;
            pending.Continuation = completion?.Continuation;
            pending.RetainJobUntilAssistantAudio =
                completion?.RetainUntilAssistantAudio == true;
            LogToolResult(slot.Call, slot.ResultJson);
            CompleteToolBatch(pending);
            return;
        }

        if (Time.realtimeSinceStartup - pending.StartedAt < pending.TimeoutSeconds)
            return;

        CompanionController.CancelJob(pending.JobToken);
        var timedOutSlot = pending.Calls[pending.JobIndex];
        timedOutSlot.ResultJson = AgentToolResult.Failure(
            "job_timed_out").ToJson();
        timedOutSlot.AwaitsJob = false;
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

        // Outputs are always submitted. Creating the response is held back while
        // the human is mid-utterance, so the continuation cannot start talking
        // over speech the model has not heard the end of yet.
        var humanSpeaking = IsHumanSpeaking();
        var sent = pending.Client != null &&
                   pending.Client.CompleteFunctionCallBatch(
                       pending.ResponseId,
                       outputs,
                       pending.Continuation,
                       !humanSpeaking);
        if (sent && humanSpeaking)
        {
            _continuationHeld = true;
            _heldContinuationTurnId = pending.TurnId;
            Plugin.Logger.LogInfo(
                $"[AGENT] CONTINUATION_HELD reason=human_speaking, " +
                $"turnId={pending.TurnId}.");
        }
        var continuationCount = pending.Continuation == null
            ? 0
            : pending.Continuation.Length;
        if (!sent)
        {
            Plugin.Logger.LogWarning(
                $"[AGENT] TOOL_BATCH_DISCARDED responseId={pending.ResponseId}.");
        }
        else
        {
            Plugin.Logger.LogInfo(
                $"[AGENT] TOOL_BATCH_COMPLETED responseId={pending.ResponseId}, " +
                $"turnId={pending.TurnId}, calls={pending.Calls.Length}, " +
                $"continuation={continuationCount}.");
        }

        if (ReferenceEquals(_pendingToolBatch, pending))
        {
            _pendingToolBatch = null;
            // Only a completion that explicitly retained a presentation hold
            // may survive until audio. Physical actions are already concluded
            // before their output continuation, including tool-only chains.
            var retainForAudio = pending.RetainJobUntilAssistantAudio &&
                                 continuationCount > 0;
            _concludeJobOnAssistantAudio = retainForAudio;
            _lingeringJobToken = retainForAudio ? pending.JobToken : 0;
            if (!retainForAudio && pending.JobToken != 0)
                CompanionController.ConcludeJob(pending.JobToken);
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
        CompanionController.CancelJob(_pendingToolBatch.JobToken);
        Plugin.Logger.LogInfo(
            $"[AGENT] TOOL_BATCH_CANCELLED responseId={_pendingToolBatch.ResponseId}.");
        _pendingToolBatch = null;
        _continuationHeld = false;
        _heldContinuationTurnId = 0;
        _concludeJobOnAssistantAudio = false;
        _lingeringJobToken = 0;
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
        CompanionController.CancelJob(_lingeringJobToken);
        _lingeringJobToken = 0;
        _concludeJobOnAssistantAudio = false;
        _continuationHeld = false;
        _heldContinuationTurnId = 0;
        _userSpeaking = false;
        _turnReferences.Clear();
        _completedTurnIds.Clear();
        _toolBatchTurnsThisFrame.Clear();
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
