using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ramblers;

internal sealed class RealtimeAgentBridge : MonoBehaviour
{
    private const float ReconnectDelay = 5f;
    private const float CancellationSettlementMaximumSeconds = 5f;

    private sealed class PendingToolCall
    {
        internal RealtimeFunctionCall Call;
        internal string ResultJson;
    }

    private sealed class PendingToolBatch
    {
        internal OpenAIRealtimeClient Client;
        internal string ResponseId;
        internal long TurnId;
        internal PendingToolCall[] Calls;
        internal SequentialToolBatchCursor Cursor;
        internal int JobIndex = -1;
        internal long JobToken;
        internal float StartedAt;
        internal float TimeoutSeconds;
        internal bool AnyOutputSubmitted;
        internal int ContinuationSubmitted;
        internal bool RetainJobUntilAssistantAudio;
        internal bool CancellationRequested;
        internal float CancellationStartedAt;
        internal string CancellationError;
        internal string CancellationReason;
        internal bool SettlementAbandoned;
    }

    private readonly GameVoiceInput _gameVoice = new GameVoiceInput();
    private readonly GameVoiceOutput _gameVoiceOutput = new GameVoiceOutput();
    private readonly LogLatch _missingKeyLog = new LogLatch();
    private readonly Dictionary<long, CompanionTurnReference> _turnReferences =
        new Dictionary<long, CompanionTurnReference>();
    private readonly HashSet<long> _completedTurnIds = new HashSet<long>();
    private OpenAIRealtimeClient _client;
    private readonly List<PendingToolBatch> _activeToolBatches =
        new List<PendingToolBatch>();
    private float _nextConnectAt;
    private bool _userSpeaking;
    private bool _continuationHeld;
    private bool _concludeJobOnAssistantAudio;
    private long _lingeringJobToken;
    private long _lingeringJobTurnId;
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

        DrainClientEvents();
        EnsureClient();
        var voiceEvents = _gameVoice.Tick(_client);
        if ((voiceEvents & GameVoiceTickEvents.ManualTurnStarted) != 0)
            HandleHumanSpeechStarted("manual_ptt");
        if ((voiceEvents & GameVoiceTickEvents.ManualTurnSubmitted) != 0)
            CaptureTurnAndRequestResponse("manual_ptt");
        PollPendingToolBatches();
        DrainFunctionCallBatches();
        CleanupCompletedTurnReferences();

        ReleaseHeldContinuation();
        _gameVoiceOutput.Tick();

        CompanionController.SetConversationActive(
            IsHumanSpeaking() || _gameVoiceOutput.IsSpeaking);
    }

    private bool IsHumanSpeaking()
    {
        return _userSpeaking || _gameVoice.IsCapturingManualTurn;
    }

    private void ReleaseHeldContinuation()
    {
        if (!_continuationHeld || _client == null || IsHumanSpeaking())
            return;

        _continuationHeld = false;

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
            CancelActiveToolBatches();
            ReleaseLingeringJob("client_reconnect");
            _continuationHeld = false;
            _heldContinuationTurnId = 0;
            _userSpeaking = false;
            _turnReferences.Clear();
            _completedTurnIds.Clear();
            _gameVoice.Stop(_client);
            _gameVoiceOutput.Stop();
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

                apiKey = Environment.GetEnvironmentVariable(
                    "OPENAI_API_KEY",
                    EnvironmentVariableTarget.User);
            }
            catch (PlatformNotSupportedException)
            {

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

                CaptureTurnAndRequestResponse("semantic_vad");
            }
            else if (clientEvent.Type == RealtimeClientEventType.ResponseCompleted)
            {

                if (_concludeJobOnAssistantAudio)
                {
                    ReleaseLingeringJob("response_completed_without_audio");
                }

                if (clientEvent.TurnId > 0 &&
                    !TurnReferenceRetentionPolicy.ShouldRetain(
                        clientEvent.HasFunctionCallBatch))
                {
                    _completedTurnIds.Add(clientEvent.TurnId);
                }
            }
            else if (clientEvent.Type == RealtimeClientEventType.AudioPacket)
            {
                if (_concludeJobOnAssistantAudio &&
                    clientEvent.AudioPacket?.Pcm16 != null &&
                    clientEvent.AudioPacket.Pcm16.Length > 0)
                {
                    ReleaseLingeringJob("assistant_audio_started");
                }
                _gameVoiceOutput.Accept(clientEvent.AudioPacket);
            }
        }
    }

    private void DrainFunctionCallBatches()
    {
        if (_client == null)
            return;

        RealtimeFunctionCallBatch batch;
        while (_client.TryDequeueFunctionCallBatch(out batch))
            BeginToolBatch(batch);
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
            _turnReferences.Remove(turnId);
        }
    }

    private void HandleHumanSpeechStarted(string source)
    {
        var invalidated = _turnReferences.Count;
        _turnReferences.Clear();

        InterruptAssistantSpeech();
        ReleaseLingeringJob("human_interrupted_response");
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
        CompanionPropTarget target;
        string captureError;
        CompanionController.TryCapturePropTarget(
            out target,
            out captureError);
        CompanionPropTarget companionHeldTarget;
        string companionHeldCaptureError;
        CompanionController.TryCaptureCompanionHeldTarget(
            out companionHeldTarget,
            out companionHeldCaptureError);
        CompanionInspectionCandidates inspectionCandidates;
        string inspectionCaptureError;
        CompanionController.TryCaptureInspectionCandidates(
            out inspectionCandidates,
            out inspectionCaptureError);
        CompanionAffordanceCandidates affordanceCandidates;
        string affordanceCaptureError;
        Plugin.Logger.LogInfo(
            $"[AGENT] TURN_INTERACTION_REFERENCE_CAPTURE_STARTED source={source}, " +
            $"turnId={turnId}.");
        CompanionController.TryCaptureAffordanceCandidates(
            out affordanceCandidates,
            out affordanceCaptureError);
        CompanionPlayerTarget humanPlayerTarget;
        string humanPlayerCaptureError;
        CompanionController.TryCaptureHumanPlayerTarget(
            out humanPlayerTarget,
            out humanPlayerCaptureError);
        CompanionAwarenessTurnContext awarenessContext;
        string awarenessCaptureError;
        CompanionController.TryTakeAwarenessTurnContext(
            affordanceCandidates,
            out awarenessContext,
            out awarenessCaptureError);
        _turnReferences[turnId] = new CompanionTurnReference
        {
            TurnId = turnId,
            Target = target,
            CaptureError = captureError,
            CompanionHeldTarget = companionHeldTarget,
            CompanionHeldCaptureError = companionHeldCaptureError,
            InspectionCandidates = inspectionCandidates,
            InspectionCaptureError = inspectionCaptureError,
            AffordanceCandidates = affordanceCandidates,
            AffordanceCaptureError = affordanceCaptureError,
            HumanPlayerTarget = humanPlayerTarget,
            EntityReferences = awarenessContext?.EntityReferences
        };
        var awarenessQueued = false;
        if (awarenessContext != null)
        {
            try
            {
                awarenessQueued = _client.QueueTurnContext(awarenessContext.Message);
                if (awarenessQueued)
                {
                    CompanionController.ConfirmAwarenessTurnContextDelivered(
                        awarenessContext);
                }
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

        if (companionHeldTarget == null)
        {
            Plugin.Logger.LogInfo(
                $"[AGENT] TURN_HELD_REFERENCE_CAPTURED source={source}, " +
                $"turnId={turnId}, status=unavailable, " +
                $"reason={companionHeldCaptureError ?? "companion_held_item_unavailable"}.");
        }
        else
        {
            Plugin.Logger.LogInfo(
                $"[AGENT] TURN_HELD_REFERENCE_CAPTURED source={source}, " +
                $"turnId={turnId}, status=prop, " +
                $"referenceId={companionHeldTarget.ReferenceId}, " +
                $"netId={companionHeldTarget.NetworkId}.");
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

        if (affordanceCandidates == null)
        {
            Plugin.Logger.LogInfo(
                $"[AGENT] TURN_INTERACTION_REFERENCE_CAPTURED source={source}, " +
                $"turnId={turnId}, status=unavailable, " +
                $"reason={affordanceCaptureError ?? "interaction_reference_not_captured"}.");
        }
        else
        {
            Plugin.Logger.LogInfo(
                $"[AGENT] TURN_INTERACTION_REFERENCES_CAPTURED source={source}, " +
                $"turnId={turnId}, " +
                $"humanAvailable={affordanceCandidates.HumanReferenceAvailable}, " +
                $"humanReason={affordanceCandidates.HumanReferenceError ?? "none"}, " +
                $"humanReferenceId={affordanceCandidates.HumanReferenceId}, " +
                $"humanKind={affordanceCandidates.HumanReferenceKind}, " +
                $"humanNetId={affordanceCandidates.HumanReferenceNetworkId}, " +
                $"heldItemAvailable={affordanceCandidates.CompanionHeldItemAvailable}, " +
                $"heldItemReason={affordanceCandidates.CompanionHeldItemError ?? "none"}, " +
                $"heldReferenceId={affordanceCandidates.CompanionHeldItemReferenceId}, " +
                $"heldKind={affordanceCandidates.CompanionHeldItemKind}, " +
                $"heldNetId={affordanceCandidates.CompanionHeldItemNetworkId}.");
        }

        Plugin.Logger.LogInfo(
            $"[AGENT] TURN_HUMAN_PLAYER_CAPTURED source={source}, " +
            $"turnId={turnId}, " +
            $"available={humanPlayerTarget != null}, " +
            $"referenceId={humanPlayerTarget?.StableId ?? "none"}, " +
            $"netId={humanPlayerTarget?.NetworkId ?? 0u}, " +
            $"reason={humanPlayerCaptureError ?? "none"}.");

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
                $"nearbyInteractables={awarenessContext.NearbyInteractableCount}, " +
                $"rememberedInteractables={awarenessContext.RememberedInteractableCount}, " +
                $"actionableEntities={awarenessContext.EntityReferences?.Count ?? 0}, " +
                $"nearbyPlayers={awarenessContext.NearbyPlayerCount}, " +
                $"visualAttached={awarenessContext.HasImage}, " +
                $"visualAgeSeconds={awarenessContext.VisualAgeSeconds:F1}.");
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
            TurnId = batch.TurnId,
            Calls = new PendingToolCall[batch.Calls.Length],
            Cursor = new SequentialToolBatchCursor(batch.Calls.Length)
        };

        for (var index = 0; index < batch.Calls.Length; index++)
        {
            pending.Calls[index] = new PendingToolCall
            {
                Call = batch.Calls[index]
            };
        }

        _activeToolBatches.Add(pending);
        DispatchPendingToolCalls(pending);
    }

    private void DispatchPendingToolCalls(PendingToolBatch pending)
    {
        int index;
        while (pending.Cursor.TryBeginNext(out index))
        {
            var slot = pending.Calls[index];
            var functionCall = slot.Call;
            CompanionTurnReference turnReference;
            _turnReferences.TryGetValue(pending.TurnId, out turnReference);

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
                pending.JobIndex = index;
                pending.JobToken = dispatch.OperationToken;
                pending.StartedAt = Time.realtimeSinceStartup;
                pending.TimeoutSeconds = dispatch.TimeoutSeconds;
                Plugin.Logger.LogInfo(
                    $"[AGENT] CALL name={functionCall.Name}, " +
                    $"callId={functionCall.CallId ?? "none"}, " +
                    $"arguments={functionCall.Arguments}, " +
                    $"turnId={pending.TurnId}, responseId={pending.ResponseId}, " +
                    $"sequenceIndex={index}, result=pending");
                Plugin.Logger.LogInfo(
                    $"[AGENT] TOOL_BATCH_DEFERRED responseId={pending.ResponseId}, " +
                    $"turnId={pending.TurnId}, calls={pending.Calls.Length}, " +
                    $"waitingIndex={index}.");
                return;
            }

            var result = dispatch.Result ??
                         AgentToolResult.Failure("action_execution_failed");
            slot.ResultJson = result.ToJson();
            LogToolResult(
                functionCall,
                slot.ResultJson,
                result.Error,
                pending.TurnId,
                pending.ResponseId);
            SubmitCallOutput(pending, slot, null);
            if (!pending.Cursor.TryCompleteActive(index))
            {
                Plugin.Logger.LogError(
                    $"[AGENT] TOOL_BATCH_SEQUENCE_FAULT " +
                    $"responseId={pending.ResponseId}, turnId={pending.TurnId}, " +
                    $"activeIndex={pending.Cursor.ActiveIndex}, completedIndex={index}.");
                FailUndispatchedCalls(pending, "tool_batch_sequence_failed");
                FinalizeToolBatch(pending);
                return;
            }
            if (ShouldAbortRemainderAfterFailure(functionCall, result) &&
                pending.Cursor.HasUndispatched)
            {
                FailUndispatchedCalls(pending, "previous_action_failed");
                FinalizeToolBatch(pending);
                return;
            }
        }

        if (pending.Cursor.IsComplete)
            FinalizeToolBatch(pending);
    }

    private void PollPendingToolBatches()
    {
        for (var index = _activeToolBatches.Count - 1; index >= 0; index--)
            PollToolBatch(_activeToolBatches[index]);
    }

    private void PollToolBatch(PendingToolBatch pending)
    {
        if (pending.JobToken == 0)
            return;

        CompanionJobCompletion completion;
        if (CompanionController.TryTakeJobCompletion(
                pending.JobToken,
                out completion))
        {
            FinishPendingJob(pending, completion);
            return;
        }

        var now = Time.realtimeSinceStartup;
        if (!pending.CancellationRequested)
        {
            if (now - pending.StartedAt < pending.TimeoutSeconds)
                return;

            RequestPendingJobCancellation(
                pending,
                "job_timed_out",
                "job_timeout");
            return;
        }

        if (!CompanionJobSettlementProtocol.HasCancellationSettlementTimedOut(
                true,
                now - pending.CancellationStartedAt,
                CancellationSettlementMaximumSeconds))
        {
            return;
        }

        var abandoned = CompanionController.AbandonJobSettlement(
            pending.JobToken,
            pending.CancellationReason ?? "cancellation_timeout");
        Plugin.Logger.LogWarning(
            $"[AGENT] TOOL_BATCH_RECONCILIATION_BOUNDED " +
            $"responseId={pending.ResponseId}, turnId={pending.TurnId}, " +
            $"reason={pending.CancellationReason ?? "unknown"}, " +
            $"ownershipAbandoned={abandoned}.");
        if (!abandoned)
            return;
        pending.SettlementAbandoned = true;
        FinishPendingJob(
            pending,
            CompanionJobCompletion.Failed("cancelled"));
    }

    private void FinishPendingJob(
        PendingToolBatch pending,
        CompanionJobCompletion completion)
    {
        if (!_activeToolBatches.Contains(pending))
            return;

        if (pending.CancellationRequested && pending.SettlementAbandoned)
        {
            Plugin.Logger.LogWarning(
                $"[AGENT] TOOL_BATCH_RECONCILIATION_ABANDONED " +
                $"responseId={pending.ResponseId}, turnId={pending.TurnId}, " +
                $"reason={pending.CancellationReason ?? "unknown"}, " +
                "disposition=ownership_returned_to_stock_state.");
        }
        else if (pending.CancellationRequested)
        {
            Plugin.Logger.LogInfo(
                $"[AGENT] TOOL_BATCH_RECONCILED responseId={pending.ResponseId}, " +
                $"turnId={pending.TurnId}, " +
                $"reason={pending.CancellationReason ?? "unknown"}.");
        }

        var result = pending.CancellationRequested
            ? AgentToolResult.Failure(
                pending.CancellationError ?? "action_interrupted")
            : completion?.Result ??
              AgentToolResult.Failure("action_execution_failed");
        var slot = pending.Calls[pending.JobIndex];
        slot.ResultJson = result.ToJson();
        var continuation = pending.CancellationRequested
            ? null
            : completion?.Continuation;
        var transitionApplied = pending.CancellationRequested ||
                                TryApplyTurnTransition(
                                    pending,
                                    result,
                                    completion);
        LogToolResult(
            slot.Call,
            slot.ResultJson,
            result.Error,
            pending.TurnId,
            pending.ResponseId);
        SubmitCallOutput(pending, slot, continuation);

        var completedIndex = pending.JobIndex;
        if (!pending.Cursor.TryCompleteActive(completedIndex))
        {
            Plugin.Logger.LogError(
                $"[AGENT] TOOL_BATCH_SEQUENCE_FAULT " +
                $"responseId={pending.ResponseId}, turnId={pending.TurnId}, " +
                $"activeIndex={pending.Cursor.ActiveIndex}, " +
                $"completedIndex={completedIndex}.");
            CompleteCurrentJobBeforeNextCall(pending);
            FailUndispatchedCalls(pending, "tool_batch_sequence_failed");
            FinalizeToolBatch(pending);
            return;
        }

        if (pending.CancellationRequested)
        {
            var timedOut = string.Equals(
                pending.CancellationError,
                "job_timed_out",
                StringComparison.Ordinal);
            CompleteCurrentJobBeforeNextCall(pending);
            if (pending.Cursor.HasUndispatched)
            {
                FailUndispatchedCalls(
                    pending,
                    timedOut
                        ? "previous_action_timed_out"
                        : "action_interrupted");
            }
            if (timedOut)
            {
                Plugin.Logger.LogWarning(
                    $"[AGENT] TOOL_BATCH_TIMEOUT responseId={pending.ResponseId}, " +
                    $"turnId={pending.TurnId}, settlement=complete.");
            }
            FinalizeToolBatch(pending);
            return;
        }

        var retainPresentation =
            completion?.RetainUntilAssistantAudio == true;
        if (PresentationRetentionPolicy.MustEndBatchBeforeNextCall(
                retainPresentation,
                pending.Cursor.HasUndispatched))
        {
            FailUndispatchedCalls(pending, "presentation_requires_new_turn");
            pending.RetainJobUntilAssistantAudio = true;
            Plugin.Logger.LogInfo(
                $"[AGENT] PRESENTATION_SEQUENCE_TERMINATED " +
                $"responseId={pending.ResponseId}, turnId={pending.TurnId}, " +
                "reason=continuation_must_observe_presentation.");
            FinalizeToolBatch(pending);
            return;
        }

        if (pending.Cursor.HasUndispatched)
        {
            CompleteCurrentJobBeforeNextCall(pending);
            if (ShouldAbortRemainderAfterFailure(slot.Call, result))
            {
                FailUndispatchedCalls(pending, "previous_action_failed");
                FinalizeToolBatch(pending);
                return;
            }
            if (!transitionApplied)
            {
                FailUndispatchedCalls(
                    pending,
                    "turn_state_transition_failed");
                FinalizeToolBatch(pending);
                return;
            }

            DispatchPendingToolCalls(pending);
            return;
        }

        pending.RetainJobUntilAssistantAudio = retainPresentation;
        FinalizeToolBatch(pending);
    }

    private static void RequestPendingJobCancellation(
        PendingToolBatch pending,
        string resultError,
        string reason)
    {
        if (pending == null || pending.CancellationRequested)
            return;

        pending.CancellationRequested = true;
        pending.CancellationStartedAt = Time.realtimeSinceStartup;
        pending.CancellationError = resultError;
        pending.CancellationReason = reason;
        CompanionController.CancelJob(pending.JobToken);
        Plugin.Logger.LogInfo(
            $"[AGENT] TOOL_BATCH_RECONCILIATION_STARTED " +
            $"responseId={pending.ResponseId}, turnId={pending.TurnId}, " +
            $"waitingIndex={pending.JobIndex}, reason={reason}.");
    }

    private bool TryApplyTurnTransition(
        PendingToolBatch pending,
        AgentToolResult result,
        CompanionJobCompletion completion)
    {
        if (!result.Ok || completion == null ||
            completion.HandsTransition == CompanionTurnHandsTransition.None)
        {
            return true;
        }

        CompanionTurnReference turnReference;
        if (!_turnReferences.TryGetValue(pending.TurnId, out turnReference) ||
            turnReference == null)
        {
            Plugin.Logger.LogWarning(
                $"[AGENT] TURN_STATE_ADVANCE_FAILED turnId={pending.TurnId}, " +
                $"transition={completion.HandsTransition}, " +
                "error=turn_reference_invalidated.");
            return false;
        }

        string transitionError;
        if (!turnReference.TryApply(completion, out transitionError))
        {
            Plugin.Logger.LogWarning(
                $"[AGENT] TURN_STATE_ADVANCE_FAILED turnId={pending.TurnId}, " +
                $"transition={completion.HandsTransition}, " +
                $"error={transitionError ?? "unknown"}.");
            return false;
        }

        Plugin.Logger.LogInfo(
            $"[AGENT] TURN_STATE_ADVANCED turnId={pending.TurnId}, " +
            $"transition={completion.HandsTransition}.");
        return true;
    }

    private static bool ShouldAbortRemainderAfterFailure(
        RealtimeFunctionCall call,
        AgentToolResult result)
    {

        return result != null && !result.Ok &&
               string.Equals(
                   call?.Name,
                   AgentToolCatalog.PickUpPlayer,
                   StringComparison.Ordinal);
    }

    private static void SubmitCallOutput(
        PendingToolBatch pending,
        PendingToolCall slot,
        AgentContinuationItem[] continuation)
    {
        var output = new RealtimeFunctionOutput
        {
            CallId = slot.Call?.CallId,
            ResultJson = slot.ResultJson ??
                         AgentToolResult.Failure(
                             "action_execution_failed").ToJson()
        };
        var sent = pending.Client != null &&
                   pending.Client.SubmitFunctionOutput(output, continuation);
        if (sent)
        {
            pending.AnyOutputSubmitted = true;
            if (continuation != null)
                pending.ContinuationSubmitted += continuation.Length;
        }
        else
        {
            Plugin.Logger.LogWarning(
                $"[AGENT] TOOL_OUTPUT_DISCARDED responseId={pending.ResponseId}, " +
                $"turnId={pending.TurnId}, callId={slot.Call?.CallId ?? "none"}.");
        }
    }

    private static void CompleteCurrentJobBeforeNextCall(
        PendingToolBatch pending)
    {
        if (pending.JobToken != 0)
            CompanionController.ConcludeJob(pending.JobToken);
        pending.JobIndex = -1;
        pending.JobToken = 0;
        pending.StartedAt = 0f;
        pending.TimeoutSeconds = 0f;
        pending.RetainJobUntilAssistantAudio = false;
        pending.CancellationRequested = false;
        pending.CancellationStartedAt = 0f;
        pending.CancellationError = null;
        pending.CancellationReason = null;
        pending.SettlementAbandoned = false;
    }

    private static void FailUndispatchedCalls(
        PendingToolBatch pending,
        string error)
    {
        int index;
        while (pending.Cursor.TryBeginNext(out index))
        {
            var slot = pending.Calls[index];
            var result = AgentToolResult.Failure(error);
            slot.ResultJson = result.ToJson();
            LogToolResult(
                slot.Call,
                slot.ResultJson,
                result.Error,
                pending.TurnId,
                pending.ResponseId);
            SubmitCallOutput(pending, slot, null);
            if (!pending.Cursor.TryCompleteActive(index))
                return;
        }
    }

    private void FinalizeToolBatch(PendingToolBatch pending)
    {
        _activeToolBatches.Remove(pending);

        if (!pending.AnyOutputSubmitted)
        {
            Plugin.Logger.LogWarning(
                $"[AGENT] TOOL_BATCH_DISCARDED responseId={pending.ResponseId}, " +
                $"turnId={pending.TurnId}.");
        }
        else
        {
            Plugin.Logger.LogInfo(
                $"[AGENT] TOOL_BATCH_COMPLETED responseId={pending.ResponseId}, " +
                $"turnId={pending.TurnId}, calls={pending.Calls.Length}, " +
                $"continuation={pending.ContinuationSubmitted}.");
            if (IsHumanSpeaking())
            {
                _continuationHeld = true;
                _heldContinuationTurnId = pending.TurnId;
                Plugin.Logger.LogInfo(
                    $"[AGENT] CONTINUATION_HELD reason=human_speaking, " +
                    $"turnId={pending.TurnId}.");
            }
            else if (pending.Client != null)
            {
                pending.Client.RequestContinuation(pending.TurnId);
            }
        }

        var retainForAudio = pending.AnyOutputSubmitted &&
                             pending.RetainJobUntilAssistantAudio &&
                             pending.ContinuationSubmitted > 0;
        if (retainForAudio)
        {
            ReleaseLingeringJob("superseded_by_new_presentation");
            _concludeJobOnAssistantAudio = true;
            _lingeringJobToken = pending.JobToken;
            _lingeringJobTurnId = pending.TurnId;
            Plugin.Logger.LogInfo(
                $"[AGENT] PRESENTATION_JOB_RETAINED turnId={pending.TurnId}, " +
                "reason=awaiting_assistant_audio.");
        }
        else if (pending.JobToken != 0)
        {
            CompanionController.ConcludeJob(pending.JobToken);
        }
    }

    private static void LogToolResult(
        RealtimeFunctionCall functionCall,
        string resultJson,
        string diagnosticError,
        long turnId,
        string responseId)
    {
        Plugin.Logger.LogInfo(
            $"[AGENT] CALL name={functionCall?.Name}, " +
            $"callId={functionCall?.CallId ?? "none"}, " +
            $"arguments={functionCall?.Arguments}, turnId={turnId}, " +
            $"responseId={responseId}, " +
            $"result={resultJson}, " +
            $"diagnosticError={diagnosticError ?? "none"}");
    }

    private void CancelActiveToolBatches()
    {
        for (var index = _activeToolBatches.Count - 1; index >= 0; index--)
        {
            var pending = _activeToolBatches[index];
            if (!pending.CancellationRequested)
            {
                RequestPendingJobCancellation(
                    pending,
                    "action_interrupted",
                    "client_replaced");
            }
            var ownershipTransferred = CompanionController.DetachJob(
                pending.JobToken);
            if (ownershipTransferred)
            {
                Plugin.Logger.LogInfo(
                    $"[AGENT] TOOL_BATCH_RECONCILIATION_DETACHED " +
                    $"responseId={pending.ResponseId}, turnId={pending.TurnId}, " +
                    $"token={pending.JobToken}, " +
                    $"reason={pending.CancellationReason ?? "client_replaced"}.");
            }
            else
            {
                Plugin.Logger.LogInfo(
                    $"[AGENT] TOOL_BATCH_RECONCILED responseId={pending.ResponseId}, " +
                    $"turnId={pending.TurnId}, reason=client_replaced, " +
                    "disposition=no_live_job_lease.");
            }
            Plugin.Logger.LogInfo(
                $"[AGENT] TOOL_BATCH_CANCELLED responseId={pending.ResponseId}, " +
                $"turnId={pending.TurnId}, " +
                $"settlement={(ownershipTransferred ? "detached" : "no_live_lease")}.");
            _activeToolBatches.RemoveAt(index);
        }
        _continuationHeld = false;
        _heldContinuationTurnId = 0;
    }

    private void ReleaseLingeringJob(string reason)
    {
        var token = _lingeringJobToken;
        var turnId = _lingeringJobTurnId;
        _concludeJobOnAssistantAudio = false;
        _lingeringJobToken = 0;
        _lingeringJobTurnId = 0;
        if (token == 0)
            return;

        CompanionController.ConcludeJob(token);
        Plugin.Logger.LogInfo(
            $"[AGENT] PRESENTATION_JOB_RELEASED turnId={turnId}, " +
            $"reason={reason}.");
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
        CancelActiveToolBatches();
        ReleaseLingeringJob("client_stopped");
        _continuationHeld = false;
        _heldContinuationTurnId = 0;
        _userSpeaking = false;
        _turnReferences.Clear();
        _completedTurnIds.Clear();
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
