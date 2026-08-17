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
    void ClearInputAudio();
    void AppendInputAudio(byte[] pcm16);
    void CommitInputAudioAndRespond();
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
    internal bool EndsResponse;
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
    private readonly ConcurrentQueue<RealtimeAudioPacket> _audioPackets =
        new ConcurrentQueue<RealtimeAudioPacket>();
    private readonly SemaphoreSlim _outboundSignal = new SemaphoreSlim(0);

    private Task _runTask;
    private volatile bool _ready;
    private volatile bool _stopped;
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

    internal bool TryDequeueAudioPacket(out RealtimeAudioPacket packet)
    {
        return _audioPackets.TryDequeue(out packet);
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
        QueueJson(new { type = "response.create" });
    }

    internal void SendFunctionResult(string callId, string resultJson)
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
        QueueJson(new { type = "response.create" });
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
                        turn_detection = (object)null
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
                tools = new[]
                {
                    new
                    {
                        type = "function",
                        name = "set_follow_mode",
                        description =
                            "Start or stop the companion's verified breadcrumb-follow behavior.",
                        parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                mode = new
                                {
                                    type = "string",
                                    description =
                                        "Use follow to walk behind the human; use stay to stop and hold position.",
                                    @enum = new[] { "follow", "stay" }
                                }
                            },
                            required = new[] { "mode" },
                            additionalProperties = false
                        }
                    }
                },
                tool_choice = "auto"
            }
        };
        return JsonSerializer.Serialize(payload);
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
                _logs.Enqueue("READY tools=set_follow_mode");
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
                        _audioPackets.Enqueue(new RealtimeAudioPacket
                        {
                            Pcm16 = Convert.FromBase64String(delta),
                            EndsResponse = false
                        });
                    }
                }
                return;
            }

            if (type == "response.output_audio.done")
            {
                _audioPackets.Enqueue(new RealtimeAudioPacket
                {
                    Pcm16 = null,
                    EndsResponse = true
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
                    _logs.Enqueue("API_ERROR " + message.GetString());
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
