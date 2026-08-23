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
    internal const string GoToLocation = "go_to_location";
    internal const string InteractWithObject = "interact_with_object";
    internal const string PickUpItem = "pick_up_item";
    internal const string KickItem = "kick_item";
    internal const string DropItem = "drop_item";
    internal const string PickUpPlayer = "pick_up_player";
    internal const string DropPlayer = "drop_player";
    internal const string CancelAction = "cancel_action";

    internal const string NamesForLog =
        "set_follow_mode,set_posture,jump,inspect_reference,go_to_location,interact_with_object,pick_up_item,kick_item,drop_item,pick_up_player,drop_player,cancel_action";

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
                "Queue one jump; the companion automatically stands first if needed.",
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
                "Look at one reference the human indicated and capture an image from your own point of view. Infer the target silently: use human_held_item for deictic requests such as 'look at this', 'what am I holding?', or something the human says they are holding or showing you; use human_gaze for a place, direction, scene, or 'over there'. Never ask the human to choose or announce this internal distinction. The visual result arrives after this tool completes, so make this the final tool in the current response and choose any next action only after observing it.",
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
            name = GoToLocation,
            description =
                "Walk to the place the human is currently indicating with their view. Use for natural directions such as 'go there', 'take me over there', or 'carry me to that spot'. If the human asks to be picked up and taken somewhere, call pick_up_player and then go_to_location in the same response. The destination is inferred silently from the current view; never ask for coordinates or mention gaze, references, or targeting modes. This action does not pick up, drop, or change the human's follow/stay preference.",
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
            name = InteractWithObject,
            description =
                "Use the exact object's normal in-game primary action. Call this for requests to press a button, toggle a switch, enter or sit in an indicated seat or pose, place the exact item already in your hands into an indicated holder, activate a control, or use the primary action of the item you are holding. Set intent to sit only when the human explicitly asks to sit in the indicated pose; otherwise omit it. Never imitate a world interaction with set_posture and never kick a control. Silently use an exact interaction ID from nearby_interactables or recently_seen_interactables when context identifies the named object, human_reference when current human gaze is the only grounding, or companion_held_item for the item in your hands. Never substitute another or nearest object. If multiple context entries have the same plausible name and the request does not distinguish them, ask a natural clarification instead of choosing by distance. The companion may walk to a world target before using it. Call the tool directly when the request is clear; never mention internal references or ask the human to choose a targeting mode.",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    target = new
                    {
                        type = "string",
                        description =
                            "Silently inferred exact target: human_reference, companion_held_item, or an interaction:net:<network-id>:castable:<instance-id> or interaction:local:<instance-id> ID from private game context."
                    },
                    intent = new
                    {
                        type = "string",
                        description =
                            "Use sit only for an explicit request to sit in the selected native pose; otherwise omit this field and use the object's primary action.",
                        @enum = new[] { "use", "sit" }
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
                "Go to and kick one exact prop, or continue naturally with the exact prop already in your hands. Call this tool directly; it walks to the prop when needed, grabs it, charges, and kicks, so never call pick_up_item first just to prepare a kick and never narrate those mechanics. The target is always the prop to kick, never the hoop or destination: use companion_held_item when you already hold it, its exact prop ID when game context identifies it, or human_reference only when the human is currently looking at the prop. For toward_reference, human gaze is reserved for the destination, so target must be companion_held_item or an exact prop ID, never human_reference. Infer strength silently: light only for gentle or short wording, hard only for forceful or far wording, otherwise normal. Infer direction silently: use toward_reference when the human is looking or pointing at a destination such as a hoop, goal, or spot; use toward_human when they ask for the prop toward them; otherwise use away_from_companion. Do not offer parameter choices, ask the human to restate an already grounded request, mention internal references, or substitute another or nearest item.",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    target = new
                    {
                        type = "string",
                        description =
                            "The exact prop to kick: companion_held_item for the prop already in your hands, human_reference only when current human gaze selects the prop, or its private prop:net:/prop:local: ID from game context. This is never the hoop or destination. When direction is toward_reference, do not use human_reference here because gaze identifies the destination."
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
                            "Silently inferred aim: toward_reference for the destination under current human gaze, toward_human only when explicitly asked toward the human, otherwise away_from_companion. Never ask the human to select it.",
                        @enum = new[] { "away_from_companion", "toward_human", "toward_reference" }
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
            name = PickUpPlayer,
            description =
                "Go to and pick up the human player so you are carrying them. Use when the human directly asks you to pick them up, lift them, or carry them. This is for a person, never for a prop. Call it directly without discussing implementation details.",
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
            name = DropPlayer,
            description =
                "Set down the exact human player you are currently carrying. Use when they ask to be put down, dropped, or released. This is for a carried person, never for a prop.",
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
