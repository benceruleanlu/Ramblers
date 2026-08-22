using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ramblers;

/// <summary>
/// A transport-independent result from a validated agent command. The JSON
/// representation is produced only at the agent boundary.
/// </summary>
internal sealed class AgentToolResult
{
    private AgentToolResult(
        bool ok,
        string action,
        string status,
        string state,
        string error)
    {
        Ok = ok;
        Action = action;
        Status = ok ? status : FailureStatus(error);
        State = state;
        Error = error;
        Guidance = ok ? null : FailureGuidance(Status);
    }

    [JsonPropertyName("ok")]
    public bool Ok { get; }

    [JsonPropertyName("action")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Action { get; }

    [JsonPropertyName("status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Status { get; }

    [JsonPropertyName("state")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string State { get; }

    // Exact failure codes are developer diagnostics. Sending them to the model
    // made otherwise-natural replies leak phrases such as "reference" and
    // "valid target" into the game conversation.
    [JsonIgnore]
    public string Error { get; }

    [JsonPropertyName("guidance")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Guidance { get; }

    internal static AgentToolResult Success(
        string action,
        string status,
        string state = null)
    {
        return new AgentToolResult(true, action, status, state, null);
    }

    internal static AgentToolResult Failure(string error)
    {
        return new AgentToolResult(false, null, null, null, error);
    }

    internal string ToJson()
    {
        return JsonSerializer.Serialize(this);
    }

    private static string FailureStatus(string error)
    {
        if (!string.IsNullOrEmpty(error) &&
            (error.IndexOf("reference", System.StringComparison.Ordinal) >= 0 ||
             error.IndexOf("not_found", System.StringComparison.Ordinal) >= 0 ||
             error.IndexOf("_not_known", System.StringComparison.Ordinal) >= 0 ||
             error.IndexOf("target_lost", System.StringComparison.Ordinal) >= 0))
        {
            return "could_not_identify_object";
        }

        if (!string.IsNullOrEmpty(error) &&
            (error.IndexOf("in_progress", System.StringComparison.Ordinal) >= 0 ||
             error.IndexOf("cooldown", System.StringComparison.Ordinal) >= 0 ||
             error.IndexOf("hands_occupied", System.StringComparison.Ordinal) >= 0))
        {
            return "temporarily_busy";
        }

        return "game_action_unavailable";
    }

    private static string FailureGuidance(string status)
    {
        if (status == "could_not_identify_object")
        {
            return "Briefly ask which thing the player means in natural, in-world language. " +
                   "Do not mention tools, codes, identifiers, diagnostics, or internal mechanics.";
        }

        if (status == "temporarily_busy")
        {
            return "Briefly and naturally say you need a moment or are already doing something. " +
                   "Do not mention tools, codes, diagnostics, or internal mechanics.";
        }

        return "Briefly and naturally say the game would not let you do that right now. " +
               "Do not blame the player or mention tools, codes, diagnostics, or internal mechanics.";
    }
}
