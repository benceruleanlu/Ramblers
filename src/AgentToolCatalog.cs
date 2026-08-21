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
    internal const string InspectReference = "inspect_reference";
    internal const string InteractWithObject = "interact_with_object";
    internal const string PickUpItem = "pick_up_item";
    internal const string KickItem = "kick_item";
    internal const string DropItem = "drop_item";
    internal const string CancelAction = "cancel_action";

    internal const string NamesForLog =
        "set_follow_mode,set_posture,jump,inspect_reference,interact_with_object,pick_up_item,kick_item,drop_item,cancel_action";

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
        },
        new
        {
            type = "function",
            name = InspectReference,
            description =
                "Look at one reference the human indicated and capture an image from your own point of view. Infer the target silently: use human_held_item for deictic requests such as 'look at this', 'what am I holding?', or something the human says they are holding or showing you; use human_gaze for a place, direction, scene, or 'over there'. Never ask the human to choose or announce this internal distinction. The visual result arrives after this tool completes.",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    target = new
                    {
                        type = "string",
                        description =
                            "Silently inferred visual referent for this utterance.",
                        @enum = new[] { "human_held_item", "human_gaze" }
                    }
                },
                required = new[] { "target" },
                additionalProperties = false
            }
        },
        new
        {
            type = "function",
            name = InteractWithObject,
            description =
                "Perform one primary interaction on an exact usable object frozen when the current utterance ended. Infer the target silently: use companion_held_item when the request refers to the prop you are already holding, such as 'turn it on' after you grabbed it; use human_reference for a world switch the human is indicating. Never ask the human to choose or announce this distinction. Use directly for requests such as turn that on or off, press that, activate that, or use that. This is the same kind of interaction as a player's primary click; do not use it for pickup, kick, or drop, and never substitute another object.",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    target = new
                    {
                        type = "string",
                        description =
                            "Silently inferred frozen usable reference for this utterance.",
                        @enum = new[] { "human_reference", "companion_held_item" }
                    }
                },
                required = new[] { "target" },
                additionalProperties = false
            }
        },
        new
        {
            type = "function",
            name = PickUpItem,
            description =
                "Go to and pick up one exact prop. Silently use its prop ID from nearby_props or recently_seen_props when the human names it or refers to an object already in context; use human_reference when their current gaze is the only grounding. Reuse a recent prop ID for natural follow-ups such as 'fetch it'. Never say IDs aloud, ask the human to choose an internal reference type, or substitute another or nearest item.",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    target = new
                    {
                        type = "string",
                        description =
                            "Either human_reference for the current pointed/gazed-at prop, or the exact prop:net:/prop:local: ID from private game context."
                    }
                },
                required = new[] { "target" },
                additionalProperties = false
            }
        },
        new
        {
            type = "function",
            name = KickItem,
            description =
                "Execute a requested kick on the single nearby prop the human was looking at when their current utterance ended. Call this tool immediately when the request is clear; do not offer strength or direction choices, ask about optional modifiers, or narrate the automatic grab and charge. Infer strength silently: light only for gentle or short wording, hard only for forceful or far wording, otherwise normal. Infer direction silently: toward_human only when the human asks for the item toward them, otherwise away_from_companion. The tool automatically grabs, charges, and kicks that exact prop. Never substitute another or nearest item.",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    target = new
                    {
                        type = "string",
                        description =
                            "The frozen physical referent from this human utterance.",
                        @enum = new[] { "human_reference" }
                    },
                    strength = new
                    {
                        type = "string",
                        description =
                            "Silently inferred charge: light for explicit gentle or short wording, hard for explicit forceful or far wording, otherwise normal. Never ask the human to select it.",
                        @enum = new[] { "light", "normal", "hard" }
                    },
                    direction = new
                    {
                        type = "string",
                        description =
                            "Silently inferred direction: toward_human only when the human explicitly asks for the item toward them; otherwise away_from_companion. Never ask the human to select it.",
                        @enum = new[] { "away_from_companion", "toward_human" }
                    }
                },
                required = new[] { "target" },
                additionalProperties = false
            }
        },
        new
        {
            type = "function",
            name = DropItem,
            description =
                "Release the exact prop currently held by the companion. Use when the human asks you to drop, put down, or release what you are holding, or when continuing to hold it no longer makes sense. This captures and validates the held prop before issuing the host drop command; it never acts on another object.",
            parameters = new
            {
                type = "object",
                properties = new { },
                required = new string[0],
                additionalProperties = false
            }
        },
        new
        {
            type = "function",
            name = CancelAction,
            description =
                "Stop whatever the companion is doing: any in-progress action, a queued jump, and any active following. A physical action that already crossed host authority is reconciled against its exact target before cancellation is complete. Use when the human says stop, never mind, forget it, cancel that, or wait. This does not change posture.",
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
