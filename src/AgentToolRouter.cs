using System;
using System.Text.Json;

namespace Ramblers;

/// <summary>
/// Validates model-selected tools and arguments before any Unity-side action.
/// </summary>
internal static class AgentToolRouter
{
    internal static string Execute(RealtimeFunctionCall functionCall)
    {
        if (functionCall == null ||
            !string.Equals(functionCall.Name, "set_follow_mode", StringComparison.Ordinal))
        {
            return "{\"ok\":false,\"error\":\"unknown_tool\"}";
        }

        string mode;
        if (!TryReadFollowMode(functionCall.Arguments, out mode))
            return "{\"ok\":false,\"error\":\"invalid_arguments\"}";

        return CompanionController.ExecuteAgentTool(functionCall.Name, mode);
    }

    private static bool TryReadFollowMode(string arguments, out string mode)
    {
        mode = null;
        if (string.IsNullOrWhiteSpace(arguments))
            return false;

        try
        {
            using var document = JsonDocument.Parse(arguments);
            JsonElement value;
            if (!document.RootElement.TryGetProperty("mode", out value) ||
                value.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            mode = value.GetString();
            return string.Equals(mode, "follow", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(mode, "stay", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
