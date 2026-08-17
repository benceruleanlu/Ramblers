namespace Ramblers;

/// <summary>
/// Single source of truth for model-visible tool names and JSON schemas.
/// Unity dispatch and argument validation remain in <see cref="AgentToolRouter"/>.
/// </summary>
internal static class AgentToolCatalog
{
    internal const string SetFollowMode = "set_follow_mode";
    internal const string SetPosture = "set_posture";
    internal const string Jump = "jump";

    internal const string NamesForLog = "set_follow_mode,set_posture,jump";

    internal static readonly object[] RealtimeDefinitions =
    {
        new
        {
            type = "function",
            name = SetFollowMode,
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
        },
        new
        {
            type = "function",
            name = SetPosture,
            description =
                "Set the companion's persistent body posture. Sitting suspends movement; standing resumes a previously requested follow.",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    posture = new
                    {
                        type = "string",
                        description = "The posture the companion should hold.",
                        @enum = new[] { "standing", "crouching", "sitting" }
                    }
                },
                required = new[] { "posture" },
                additionalProperties = false
            }
        },
        new
        {
            type = "function",
            name = Jump,
            description =
                "Queue one jump when the companion is standing on jumpable ground.",
            parameters = new
            {
                type = "object",
                properties = new { },
                required = new string[0],
                additionalProperties = false
            }
        }
    };
}
