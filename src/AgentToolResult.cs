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
        Status = status;
        State = state;
        Error = error;
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

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Error { get; }

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
}
