using System;
using UnityEngine;

namespace BigWalkBotProbe;

/// <summary>
/// Owns the agent lifecycle and keeps Unity work on the main thread. Game voice,
/// tool validation, and the WebSocket protocol live behind separate boundaries.
/// </summary>
internal sealed class RealtimeAgentBridge : MonoBehaviour
{
    private const float ReconnectDelay = 5f;

    private readonly GameVoiceInput _gameVoice = new GameVoiceInput();
    private OpenAIRealtimeClient _client;
    private float _nextConnectAt;
    private bool _missingKeyLogged;

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
        _gameVoice.Tick(_client);
    }

    private void EnsureClient()
    {
        if (_client != null && !_client.IsStopped)
            return;

        if (_client != null)
        {
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
            if (!_missingKeyLogged)
            {
                _missingKeyLogged = true;
                Plugin.Logger.LogWarning(
                    "[BOT-AGENT] OpenAI disabled for this run: OPENAI_API_KEY is not present " +
                    "in the process or Windows user environment. No microphone audio will be sent.");
            }
            return;
        }

        _missingKeyLogged = false;
        var configuredModel = Plugin.OpenAIRealtimeModel == null
            ? null
            : Plugin.OpenAIRealtimeModel.Value;
        var model = string.IsNullOrWhiteSpace(configuredModel)
            ? "gpt-realtime-2.1"
            : configuredModel.Trim();
        _client = new OpenAIRealtimeClient(apiKey.Trim(), model);
        _client.Start();
        Plugin.Logger.LogInfo(
            $"[BOT-AGENT] Connecting to OpenAI Realtime model {model}. " +
            "Listening follows Big Walk voice controls and direct proximity.");
    }

    private void DrainClientEvents()
    {
        if (_client == null)
            return;

        string message;
        while (_client.TryDequeueLog(out message))
            Plugin.Logger.LogInfo($"[BOT-AGENT] {message}");

        RealtimeFunctionCall functionCall;
        while (_client.TryDequeueFunctionCall(out functionCall))
        {
            var result = AgentToolRouter.Execute(functionCall);
            Plugin.Logger.LogInfo(
                $"[BOT-AGENT] CALL name={functionCall.Name}, " +
                $"arguments={functionCall.Arguments}, result={result}");
            _client.SendFunctionResult(functionCall.CallId, result);
        }
    }

    private void StopClient()
    {
        _gameVoice.Stop(_client);
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
