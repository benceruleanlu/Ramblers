extern alias websocketclient;
extern alias websockets;
extern alias privateuri;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClientWebSocket = websocketclient::System.Net.WebSockets.ClientWebSocket;
using WebSocketMessageType = websockets::System.Net.WebSockets.WebSocketMessageType;
using WebSocketReceiveResult = websockets::System.Net.WebSockets.WebSocketReceiveResult;
using WebSocketState = websockets::System.Net.WebSockets.WebSocketState;
using RuntimeUri = privateuri::System.Uri;

namespace Ramblers;

internal interface IAgentAudioSink
{
    bool IsReady { get; }
    void SetTurnDetectionMode(AgentTurnDetectionMode mode);
    void ClearInputAudio();
    void AppendInputAudio(byte[] pcm16);
    void CommitInputAudio();
    void CancelActiveResponse();
}

internal sealed class RealtimeFunctionCall
{
    internal string Name;
    internal string CallId;
    internal string Arguments;
}

internal sealed class RealtimeFunctionCallBatch
{
    internal string ResponseId;
    internal long TurnId;
    internal RealtimeFunctionCall[] Calls;
}

internal sealed class RealtimeFunctionOutput
{
    internal string CallId;
    internal string ResultJson;
}

internal sealed class RealtimeAudioPacket
{
    internal byte[] Pcm16;
    internal bool EndsItem;
    internal string ItemId;
    internal int ContentIndex;
}

internal enum RealtimeClientEventType
{
    AudioPacket,
    InputSpeechStarted,
    InputSpeechStopped,
    ResponseCompleted
}

internal sealed class RealtimeClientEvent
{
    internal RealtimeClientEventType Type;
    internal RealtimeAudioPacket AudioPacket;
    internal long TurnId;
    internal bool HasFunctionCallBatch;
}

internal sealed class RealtimeAudioTruncation
{
    internal string ItemId;
    internal int ContentIndex;
    internal int AudioEndMilliseconds;
}

internal sealed class OpenAIRealtimeClient : IAgentAudioSink, IDisposable
{
    private readonly string _apiKey;
    private readonly string _model;
    private readonly ClientWebSocket _socket = new ClientWebSocket();
    private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
    private readonly ConcurrentQueue<string> _outbound = new ConcurrentQueue<string>();
    private readonly ConcurrentQueue<string> _logs = new ConcurrentQueue<string>();
    private readonly ConcurrentQueue<RealtimeFunctionCallBatch> _functionCallBatches =
        new ConcurrentQueue<RealtimeFunctionCallBatch>();
    private readonly ConcurrentQueue<RealtimeClientEvent> _clientEvents =
        new ConcurrentQueue<RealtimeClientEvent>();
    private readonly SemaphoreSlim _outboundSignal = new SemaphoreSlim(0);
    private readonly object _responseSync = new object();

    private Task _runTask;
    private volatile bool _ready;
    private volatile bool _stopped;
    private bool _initialSessionConfigured;
    private bool _responseActive;
    private bool _responseCreateQueued;
    private bool _responseRequested;
    private long _responseRequestedTurnId;
    private long _responseRequestedAt;
    private long _reservedResponseTurnId;
    private long _reservedResponseRequestedAt;
    private long _activeResponseTurnId;
    private long _activeResponseRequestedAt;
    private long _activeResponseCreatedAt;
    private bool _activeResponseFirstAudioLogged;
    private string _responseCreateEventId;
    private long _eventSequence;
    private bool _disposed;

    internal OpenAIRealtimeClient(string apiKey, string model)
    {
        _apiKey = apiKey;
        _model = model;
    }

    public bool IsReady => _ready;
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

    internal bool TryDequeueFunctionCallBatch(out RealtimeFunctionCallBatch batch)
    {
        return _functionCallBatches.TryDequeue(out batch);
    }

    internal bool TryDequeueClientEvent(out RealtimeClientEvent clientEvent)
    {
        return _clientEvents.TryDequeue(out clientEvent);
    }

    public void SetTurnDetectionMode(AgentTurnDetectionMode mode)
    {
        QueueJson(new
        {
            type = "session.update",
            session = new
            {
                type = "realtime",
                audio = new
                {
                    input = new
                    {
                        turn_detection = mode == AgentTurnDetectionMode.SemanticVad
                            ? BuildSemanticVadConfiguration()
                            : null
                    }
                }
            }
        });
    }

    public void ClearInputAudio()
    {
        QueueInputAudioClear();
    }

    public void AppendInputAudio(byte[] pcm16)
    {
        if (pcm16 == null || pcm16.Length == 0)
            return;
        QueueJson(new
        {
            type = "input_audio_buffer.append",
            audio = Convert.ToBase64String(pcm16)
        });
    }

    public void CommitInputAudio()
    {
        QueueJson(new { type = "input_audio_buffer.commit" });
    }

    public void CancelActiveResponse()
    {
        var shouldCancel = false;
        var turnId = 0L;
        var requestedAt = 0L;
        lock (_responseSync)
        {
            _responseRequested = false;
            _responseRequestedTurnId = 0;
            _responseRequestedAt = 0;
            shouldCancel = _responseActive || _responseCreateQueued;
            turnId = _responseActive
                ? _activeResponseTurnId
                : _reservedResponseTurnId;
            requestedAt = _responseActive
                ? _activeResponseRequestedAt
                : _reservedResponseRequestedAt;
        }

        if (shouldCancel)
        {
            var now = Stopwatch.GetTimestamp();
            _logs.Enqueue(
                $"TURN_LATENCY turnId={turnId}, stage=cancel_requested, " +
                $"requestToCancelMs={ElapsedMilliseconds(requestedAt, now):F0}");
            QueueJson(new { type = "response.cancel" });
        }
    }

    internal bool SubmitFunctionOutput(
        RealtimeFunctionOutput output,
        AgentContinuationItem[] continuation)
    {
        if (output == null || string.IsNullOrEmpty(output.CallId))
            return false;

        var queued = QueueJson(new
        {
            event_id = NextEventId("function_output"),
            type = "conversation.item.create",
            item = new
            {
                type = "function_call_output",
                call_id = output.CallId,
                output = output.ResultJson ??
                         AgentToolResult.Failure("action_execution_failed").ToJson()
            }
        });
        if (!queued)
            return false;

        if (continuation != null)
        {
            for (var index = 0; index < continuation.Length; index++)
            {
                var content = BuildContinuationContent(continuation[index]);
                if (content == null)
                    continue;
                QueueJson(new
                {
                    event_id = NextEventId("continuation"),
                    type = "conversation.item.create",
                    item = new
                    {
                        type = "message",
                        role = "user",
                        content
                    }
                });
            }
        }

        return true;
    }

    internal bool QueueTurnContext(AgentContinuationItem item)
    {
        var content = BuildContinuationContent(item);
        if (content == null)
            return false;
        return QueueJson(new
        {
            event_id = NextEventId("game_context"),
            type = "conversation.item.create",
            item = new
            {
                type = "message",
                role = "user",
                content
            }
        });
    }

    internal bool QueueUnsolicitedContext(AgentContinuationItem item)
    {
        var content = BuildContinuationContent(item);
        if (content == null)
            return false;
        return QueueJson(new
        {
            event_id = NextEventId("game_event"),
            type = "conversation.item.create",
            item = new
            {
                type = "message",
                role = "user",
                content
            }
        });
    }

    internal void TruncateAudio(RealtimeAudioTruncation truncation)
    {
        if (truncation == null || string.IsNullOrEmpty(truncation.ItemId))
            return;

        QueueJson(new
        {
            type = "conversation.item.truncate",
            item_id = truncation.ItemId,
            content_index = truncation.ContentIndex,
            audio_end_ms = Math.Max(0, truncation.AudioEndMilliseconds)
        });
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

        }
        catch (Exception exception)
        {
            _logs.Enqueue("CONNECTION_ERROR " + exception.Message);
        }
        finally
        {
            _ready = false;
            _logs.Enqueue("CONNECTION_STOPPED");

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
                output_modalities = new[] { "audio" },
                instructions = AgentPrompt.Instructions,
                audio = new
                {
                    input = new
                    {
                        format = new
                        {
                            type = "audio/pcm",
                            rate = 24000
                        },
                        noise_reduction = new
                        {
                            type = "near_field"
                        },
                        turn_detection = BuildSemanticVadConfiguration()
                    },
                    output = new
                    {
                        format = new
                        {
                            type = "audio/pcm",
                            rate = 24000
                        },
                        voice = "marin"
                    }
                },
                tools = AgentToolCatalog.RealtimeDefinitions,
                tool_choice = "auto"
            }
        };
        return JsonSerializer.Serialize(payload);
    }

    private static object BuildSemanticVadConfiguration()
    {
        return new
        {
            type = "semantic_vad",
            eagerness = "auto",

            create_response = false,
            interrupt_response = true
        };
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
                if (!_initialSessionConfigured)
                {
                    _initialSessionConfigured = true;
                    _logs.Enqueue(
                        "READY tools=" + AgentToolCatalog.NamesForLog +
                        ", noiseReduction=near_field, " +
                        "turnDetection=semantic_vad_client_response");
                }
                else
                {
                    _logs.Enqueue("SESSION_UPDATED");
                }
                return;
            }

            if (type == "input_audio_buffer.speech_started")
            {
                _clientEvents.Enqueue(new RealtimeClientEvent
                {
                    Type = RealtimeClientEventType.InputSpeechStarted
                });
                _logs.Enqueue("INPUT_SPEECH_STARTED");
                return;
            }

            if (type == "input_audio_buffer.speech_stopped")
            {
                _clientEvents.Enqueue(new RealtimeClientEvent
                {
                    Type = RealtimeClientEventType.InputSpeechStopped
                });
                _logs.Enqueue("INPUT_SPEECH_STOPPED");
                return;
            }

            if (type == "response.created")
            {
                MarkResponseCreated();
                return;
            }

            if (type == "response.output_audio.delta")
            {
                JsonElement deltaElement;
                if (root.TryGetProperty("delta", out deltaElement))
                {
                    var delta = deltaElement.GetString();
                    if (!string.IsNullOrEmpty(delta))
                    {
                        LogFirstResponseAudio();
                        _clientEvents.Enqueue(new RealtimeClientEvent
                        {
                            Type = RealtimeClientEventType.AudioPacket,
                            AudioPacket = new RealtimeAudioPacket
                            {
                                Pcm16 = Convert.FromBase64String(delta),
                                EndsItem = false,
                                ItemId = GetString(root, "item_id"),
                                ContentIndex = GetInt32(root, "content_index")
                            }
                        });
                    }
                }
                return;
            }

            if (type == "response.output_audio.done")
            {
                _clientEvents.Enqueue(new RealtimeClientEvent
                {
                    Type = RealtimeClientEventType.AudioPacket,
                    AudioPacket = new RealtimeAudioPacket
                    {
                        Pcm16 = null,
                        EndsItem = true,
                        ItemId = GetString(root, "item_id"),
                        ContentIndex = GetInt32(root, "content_index")
                    }
                });
                return;
            }

            if (type == "response.output_audio_transcript.done")
            {
                JsonElement transcriptElement;
                if (root.TryGetProperty("transcript", out transcriptElement))
                    _logs.Enqueue("SAY " + MakeTranscriptConsoleSafe(transcriptElement.GetString()));
                return;
            }

            if (type == "response.done")
            {

                var completedTurnId = GetActiveResponseTurnId();
                JsonElement completedResponse;
                var responseStatus = root.TryGetProperty(
                        "response",
                        out completedResponse)
                    ? GetString(completedResponse, "status")
                    : null;
                var hasFunctionCallBatch = QueueFunctionCallBatch(
                    root,
                    completedTurnId);
                _clientEvents.Enqueue(new RealtimeClientEvent
                {
                    Type = RealtimeClientEventType.ResponseCompleted,
                    TurnId = completedTurnId,
                    HasFunctionCallBatch = hasFunctionCallBatch
                });
                MarkResponseDone(responseStatus);
                return;
            }

            if (type == "error")
            {
                JsonElement error;
                JsonElement message;
                if (root.TryGetProperty("error", out error) &&
                    error.TryGetProperty("message", out message))
                {
                    var errorMessage = message.GetString();
                    HandleResponseCreateError(root, error, errorMessage);
                    _logs.Enqueue("API_ERROR " + errorMessage);
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

    internal void RequestResponse(long turnId)
    {
        var shouldCreate = false;
        var requestedAt = Stopwatch.GetTimestamp();
        var queueState = "waiting_for_response_slot";
        lock (_responseSync)
        {
            _responseRequested = true;
            _responseRequestedTurnId = turnId;
            _responseRequestedAt = requestedAt;
            shouldCreate = TryReserveResponseCreate();
            if (shouldCreate)
                queueState = "create_queued";
        }

        _logs.Enqueue(
            $"TURN_LATENCY turnId={turnId}, stage=response_requested, " +
            $"queue={queueState}");
        if (shouldCreate)
            QueueResponseCreate();
    }

    internal void RequestContinuation(long turnId)
    {
        var shouldCreate = false;
        var requestedAt = Stopwatch.GetTimestamp();
        lock (_responseSync)
        {
            if (_responseRequested)
                return;
            _responseRequested = true;
            _responseRequestedTurnId = turnId;
            _responseRequestedAt = requestedAt;
            shouldCreate = TryReserveResponseCreate();
        }

        _logs.Enqueue(
            $"TURN_LATENCY turnId={turnId}, stage=continuation_requested, " +
            $"queue={(shouldCreate ? "create_queued" : "waiting_for_response_slot")}");
        if (shouldCreate)
            QueueResponseCreate();
    }

    private void MarkResponseCreated()
    {
        long turnId;
        long requestedAt;
        long createdAt;
        lock (_responseSync)
        {
            createdAt = Stopwatch.GetTimestamp();
            _responseActive = true;
            _responseCreateQueued = false;
            _responseCreateEventId = null;
            _activeResponseTurnId = _reservedResponseTurnId;
            _activeResponseRequestedAt = _reservedResponseRequestedAt;
            _activeResponseCreatedAt = createdAt;
            _activeResponseFirstAudioLogged = false;
            _reservedResponseTurnId = 0;
            _reservedResponseRequestedAt = 0;
            turnId = _activeResponseTurnId;
            requestedAt = _activeResponseRequestedAt;
        }

        _logs.Enqueue(
            $"TURN_LATENCY turnId={turnId}, stage=response_created, " +
            $"requestToCreatedMs={ElapsedMilliseconds(requestedAt, createdAt):F0}");
    }

    private void MarkResponseDone(string responseStatus)
    {
        var shouldCreate = false;
        long turnId;
        long requestedAt;
        long createdAt;
        bool firstAudioSeen;
        var doneAt = Stopwatch.GetTimestamp();
        lock (_responseSync)
        {
            turnId = _activeResponseTurnId;
            requestedAt = _activeResponseRequestedAt;
            createdAt = _activeResponseCreatedAt;
            firstAudioSeen = _activeResponseFirstAudioLogged;
            _responseActive = false;
            _responseCreateQueued = false;
            _responseCreateEventId = null;
            _activeResponseTurnId = 0;
            _activeResponseRequestedAt = 0;
            _activeResponseCreatedAt = 0;
            _activeResponseFirstAudioLogged = false;
            _reservedResponseTurnId = 0;
            _reservedResponseRequestedAt = 0;
            shouldCreate = TryReserveResponseCreate();
        }

        _logs.Enqueue(
            $"TURN_LATENCY turnId={turnId}, stage=response_done, " +
            $"status={responseStatus ?? "missing"}, " +
            $"requestToDoneMs={ElapsedMilliseconds(requestedAt, doneAt):F0}, " +
            $"createdToDoneMs={ElapsedMilliseconds(createdAt, doneAt):F0}, " +
            $"firstAudioSeen={firstAudioSeen}");
        if (shouldCreate)
            QueueResponseCreate();
    }

    private void LogFirstResponseAudio()
    {
        long turnId;
        long requestedAt;
        long createdAt;
        var firstAudioAt = Stopwatch.GetTimestamp();
        lock (_responseSync)
        {
            if (!_responseActive || _activeResponseFirstAudioLogged)
                return;
            _activeResponseFirstAudioLogged = true;
            turnId = _activeResponseTurnId;
            requestedAt = _activeResponseRequestedAt;
            createdAt = _activeResponseCreatedAt;
        }

        _logs.Enqueue(
            $"TURN_LATENCY turnId={turnId}, stage=first_audio, " +
            $"requestToFirstAudioMs={ElapsedMilliseconds(requestedAt, firstAudioAt):F0}, " +
            $"createdToFirstAudioMs={ElapsedMilliseconds(createdAt, firstAudioAt):F0}");
    }

    private bool TryReserveResponseCreate()
    {
        if (!_responseRequested || _responseActive || _responseCreateQueued)
            return false;

        _responseRequested = false;
        _responseCreateQueued = true;
        _reservedResponseTurnId = _responseRequestedTurnId;
        _reservedResponseRequestedAt = _responseRequestedAt;
        _responseRequestedTurnId = 0;
        _responseRequestedAt = 0;
        return true;
    }

    private long GetActiveResponseTurnId()
    {
        lock (_responseSync)
            return _activeResponseTurnId;
    }

    private void HandleResponseCreateError(
        JsonElement root,
        JsonElement error,
        string message)
    {
        JsonElement codeElement;
        var code = error.TryGetProperty("code", out codeElement)
            ? codeElement.GetString()
            : null;
        var activeResponseConflict =
            string.Equals(code, "conversation_already_has_active_response", StringComparison.Ordinal) ||
            (!string.IsNullOrEmpty(message) &&
             message.IndexOf("active response", StringComparison.OrdinalIgnoreCase) >= 0);

        var eventId = GetString(error, "event_id");

        lock (_responseSync)
        {
            if (!string.IsNullOrEmpty(eventId) &&
                !string.Equals(
                    eventId,
                    _responseCreateEventId,
                    StringComparison.Ordinal))
            {
                return;
            }
            if (!_responseCreateQueued)
                return;

            _responseCreateQueued = false;
            _responseCreateEventId = null;
            if (activeResponseConflict)
            {

                _responseActive = true;
                _responseRequested = true;
                _responseRequestedTurnId = _reservedResponseTurnId;
                _responseRequestedAt = _reservedResponseRequestedAt;
                _reservedResponseTurnId = 0;
                _reservedResponseRequestedAt = 0;
                _activeResponseTurnId = 0;
            }
            else
            {
                _reservedResponseTurnId = 0;
                _reservedResponseRequestedAt = 0;
            }
        }
    }

    private static double ElapsedMilliseconds(long startedAt, long endedAt)
    {
        if (startedAt <= 0 || endedAt < startedAt)
            return -1d;
        return (endedAt - startedAt) * 1000d / Stopwatch.Frequency;
    }

    private static string GetString(JsonElement root, string propertyName)
    {
        JsonElement value;
        return root.TryGetProperty(propertyName, out value)
            ? value.GetString()
            : null;
    }

    private static int GetInt32(JsonElement root, string propertyName)
    {
        JsonElement value;
        int parsed;
        return root.TryGetProperty(propertyName, out value) && value.TryGetInt32(out parsed)
            ? parsed
            : 0;
    }

    private static string MakeTranscriptConsoleSafe(string transcript)
    {
        if (string.IsNullOrEmpty(transcript))
            return transcript;

        return transcript
            .Replace("\u2018", "'")
            .Replace("\u2019", "'")
            .Replace("\u201c", "\"")
            .Replace("\u201d", "\"")
            .Replace("\u2013", "-")
            .Replace("\u2014", "-")
            .Replace("\u2026", "...")
            .Replace("\u00a0", " ");
    }

    private bool QueueFunctionCallBatch(JsonElement root, long turnId)
    {
        JsonElement response;
        JsonElement output;
        if (!root.TryGetProperty("response", out response) ||
            !response.TryGetProperty("output", out output) ||
            output.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        JsonElement responseStatus;
        if (response.TryGetProperty("status", out responseStatus) &&
            !string.Equals(
                responseStatus.GetString(),
                "completed",
                StringComparison.Ordinal))
        {
            return false;
        }

        var calls = new List<RealtimeFunctionCall>();
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

            JsonElement itemStatus;
            if (item.TryGetProperty("status", out itemStatus) &&
                !string.Equals(
                    itemStatus.GetString(),
                    "completed",
                    StringComparison.Ordinal))
            {
                continue;
            }

            calls.Add(new RealtimeFunctionCall
            {
                Name = name.GetString(),
                CallId = callId.GetString(),
                Arguments = arguments.GetString()
            });
        }

        if (calls.Count == 0)
            return false;

        var responseId = GetString(response, "id");
        if (string.IsNullOrEmpty(responseId))
            responseId = "tool_batch_" + calls[0].CallId;

        _functionCallBatches.Enqueue(new RealtimeFunctionCallBatch
        {
            ResponseId = responseId,
            TurnId = turnId,
            Calls = calls.ToArray()
        });
        return true;
    }

    private static object[] BuildContinuationContent(AgentContinuationItem item)
    {
        if (item == null)
            return null;

        var hasText = !string.IsNullOrWhiteSpace(item.Text);
        var hasImage = item.ImageBytes != null && item.ImageBytes.Length > 0 &&
                       !string.IsNullOrWhiteSpace(item.ImageMediaType);
        if (!hasText && !hasImage)
            return null;

        var content = new object[(hasText ? 1 : 0) + (hasImage ? 1 : 0)];
        var next = 0;
        if (hasText)
            content[next++] = new { type = "input_text", text = item.Text };
        if (hasImage)
        {
            content[next] = new
            {
                type = "input_image",

                detail = "high",
                image_url = "data:" + item.ImageMediaType + ";base64," +
                            Convert.ToBase64String(item.ImageBytes)
            };
        }

        return content;
    }

    private void QueueResponseCreate()
    {
        var eventId = NextEventId("response_create");
        lock (_responseSync)
            _responseCreateEventId = eventId;
        QueueJson(new { event_id = eventId, type = "response.create" });
    }

    private void QueueInputAudioClear()
    {
        QueueJson(new
        {
            event_id = NextEventId("input_clear"),
            type = "input_audio_buffer.clear"
        });
    }

    private string NextEventId(string category)
    {
        return "ramblers_" + category + "_" +
               Interlocked.Increment(ref _eventSequence).ToString();
    }

    private bool QueueJson(object payload)
    {
        return QueueRaw(JsonSerializer.Serialize(payload));
    }

    private bool QueueRaw(string json)
    {
        if (_disposed || _cancellation.IsCancellationRequested)
            return false;
        _outbound.Enqueue(json);
        _outboundSignal.Release();
        return true;
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
        lock (_responseSync)
        {
            _responseCreateEventId = null;
            _responseRequestedTurnId = 0;
            _responseRequestedAt = 0;
            _reservedResponseTurnId = 0;
            _reservedResponseRequestedAt = 0;
            _activeResponseTurnId = 0;
            _activeResponseRequestedAt = 0;
            _activeResponseCreatedAt = 0;
            _activeResponseFirstAudioLogged = false;
        }
        _cancellation.Cancel();
        try
        {
            _socket.Abort();
        }
        catch
        {
        }
        _socket.Dispose();

    }
}
