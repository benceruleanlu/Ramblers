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
    void CommitInputAudioAndRespond();
    void CancelActiveResponse();
}

internal sealed class RealtimeFunctionCall
{
    internal string Name;
    internal string CallId;
    internal string Arguments;
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
    InputSpeechStarted
}

internal sealed class RealtimeClientEvent
{
    internal RealtimeClientEventType Type;
    internal RealtimeAudioPacket AudioPacket;
}

internal sealed class RealtimeAudioTruncation
{
    internal string ItemId;
    internal int ContentIndex;
    internal int AudioEndMilliseconds;
}

/// <summary>
/// Pure managed WebSocket client. It exchanges JSON/PCM and queues model
/// decisions for the Unity main thread; it never touches Unity objects.
/// </summary>
internal sealed class OpenAIRealtimeClient : IAgentAudioSink, IDisposable
{
    private readonly string _apiKey;
    private readonly string _model;
    private readonly ClientWebSocket _socket = new ClientWebSocket();
    private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
    private readonly ConcurrentQueue<string> _outbound = new ConcurrentQueue<string>();
    private readonly ConcurrentQueue<string> _logs = new ConcurrentQueue<string>();
    private readonly ConcurrentQueue<RealtimeFunctionCall> _functionCalls =
        new ConcurrentQueue<RealtimeFunctionCall>();
    private readonly ConcurrentQueue<RealtimeClientEvent> _clientEvents =
        new ConcurrentQueue<RealtimeClientEvent>();
    private readonly SemaphoreSlim _outboundSignal = new SemaphoreSlim(0);
    private readonly object _responseSync = new object();

    private Task _runTask;
    private volatile bool _ready;
    private volatile bool _stopped;
    private bool _responseActive;
    private bool _responseCreateQueued;
    private bool _responseRequested;
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

    internal bool TryDequeueFunctionCall(out RealtimeFunctionCall functionCall)
    {
        return _functionCalls.TryDequeue(out functionCall);
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
        QueueJson(new { type = "input_audio_buffer.clear" });
    }

    public void AppendInputAudio(byte[] pcm16)
    {
        QueueJson(new
        {
            type = "input_audio_buffer.append",
            audio = Convert.ToBase64String(pcm16)
        });
    }

    public void CommitInputAudioAndRespond()
    {
        QueueJson(new { type = "input_audio_buffer.commit" });
        RequestResponse();
    }

    public void CancelActiveResponse()
    {
        var shouldCancel = false;
        lock (_responseSync)
        {
            _responseRequested = false;
            shouldCancel = _responseActive || _responseCreateQueued;
        }

        if (shouldCancel)
            QueueJson(new { type = "response.cancel" });
    }

    internal void SendFunctionOutput(string callId, string resultJson)
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
            create_response = true,
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
                _logs.Enqueue(
                    "READY tools=" + AgentToolCatalog.NamesForLog +
                    ", noiseReduction=near_field, " +
                    "turnDetection=semantic_vad_auto");
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
                MarkResponseDone();
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
                    var errorMessage = message.GetString();
                    HandleResponseCreateError(error, errorMessage);
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

    internal void RequestResponse()
    {
        var shouldCreate = false;
        lock (_responseSync)
        {
            _responseRequested = true;
            shouldCreate = TryReserveResponseCreate();
        }

        if (shouldCreate)
            QueueJson(new { type = "response.create" });
    }

    private void MarkResponseCreated()
    {
        lock (_responseSync)
        {
            _responseActive = true;
            _responseCreateQueued = false;
        }
    }

    private void MarkResponseDone()
    {
        var shouldCreate = false;
        lock (_responseSync)
        {
            _responseActive = false;
            _responseCreateQueued = false;
            shouldCreate = TryReserveResponseCreate();
        }

        if (shouldCreate)
            QueueJson(new { type = "response.create" });
    }

    private bool TryReserveResponseCreate()
    {
        if (!_responseRequested || _responseActive || _responseCreateQueued)
            return false;

        _responseRequested = false;
        _responseCreateQueued = true;
        return true;
    }

    private void HandleResponseCreateError(JsonElement error, string message)
    {
        JsonElement codeElement;
        var code = error.TryGetProperty("code", out codeElement)
            ? codeElement.GetString()
            : null;
        var activeResponseConflict =
            string.Equals(code, "conversation_already_has_active_response", StringComparison.Ordinal) ||
            (!string.IsNullOrEmpty(message) &&
             message.IndexOf("active response", StringComparison.OrdinalIgnoreCase) >= 0);

        lock (_responseSync)
        {
            _responseCreateQueued = false;
            if (activeResponseConflict)
            {
                // A server-created semantic-VAD response can race its
                // response.created event. Keep the request pending and retry
                // only after response.done establishes a free response slot.
                _responseActive = true;
                _responseRequested = true;
            }
        }
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

        // Realtime transcripts are valid Unicode, but BepInEx's legacy Windows
        // console can display UTF-8 smart punctuation as multiple garbled
        // characters. Normalize only transcript diagnostics at the log boundary.
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
        // Background loops observe cancellation and finish asynchronously. Their
        // wait handles remain valid until then, avoiding a shutdown race.
    }
}
