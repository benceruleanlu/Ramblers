using System;
using System.Text.Json;
using UnityEngine;

namespace Ramblers;

/// <summary>
/// Validates model-selected tools and arguments before any Unity-side action.
/// The router translates untrusted JSON into typed companion commands.
/// </summary>
internal static class AgentToolRouter
{
    internal static AgentToolDispatch Execute(
        RealtimeFunctionCall functionCall,
        CompanionTurnReference turnReference)
    {
        if (functionCall == null || string.IsNullOrEmpty(functionCall.Name))
            return AgentToolDispatch.Immediate(AgentToolResult.Failure("unknown_tool"));

        AgentToolResult result;
        switch (functionCall.Name)
        {
            case AgentToolCatalog.SetFollowMode:
                result = ExecuteFollowMode(functionCall.Arguments);
                break;
            case AgentToolCatalog.SetPosture:
                result = ExecutePosture(functionCall.Arguments);
                break;
            case AgentToolCatalog.Jump:
                result = ExecuteJump(functionCall.Arguments);
                break;
            case AgentToolCatalog.InspectReference:
                return ExecuteInspectionJob(
                    functionCall.Arguments,
                    turnReference);
            case AgentToolCatalog.GoToLocation:
                return ExecuteMoveToLocationJob(
                    functionCall.Arguments,
                    turnReference,
                    functionCall.CallId);
            case AgentToolCatalog.InteractWithObject:
                return ExecuteInteractionJob(
                    functionCall.Arguments,
                    turnReference,
                    functionCall.CallId);
            case AgentToolCatalog.PickUpItem:
                return ExecuteReferencedItemJob(
                    AgentToolCatalog.PickUpItem,
                    functionCall.Arguments,
                    turnReference,
                    functionCall.CallId);
            case AgentToolCatalog.KickItem:
                return ExecuteKickItemJob(
                    functionCall.Arguments,
                    turnReference,
                    functionCall.CallId);
            case AgentToolCatalog.DropItem:
                return ExecuteDropItemJob(
                    functionCall.Arguments,
                    turnReference,
                    functionCall.CallId);
            case AgentToolCatalog.PickUpPlayer:
                return ExecutePickUpPlayerJob(
                    functionCall.Arguments,
                    turnReference,
                    functionCall.CallId);
            case AgentToolCatalog.DropPlayer:
                return ExecuteDropPlayerJob(
                    functionCall.Arguments,
                    turnReference,
                    functionCall.CallId);
            case AgentToolCatalog.CancelAction:
                result = ExecuteCancelAction(functionCall.Arguments);
                break;
            default:
                result = AgentToolResult.Failure("unknown_tool");
                break;
        }

        return AgentToolDispatch.Immediate(result);
    }

    private static AgentToolResult ExecuteFollowMode(string arguments)
    {
        string mode;
        if (!TryReadOnlyStringArgument(arguments, "mode", out mode))
            return AgentToolResult.Failure("invalid_arguments");

        if (string.Equals(mode, "follow", StringComparison.OrdinalIgnoreCase))
            return CompanionController.SetFollowMode(FollowMode.Follow);
        if (string.Equals(mode, "stay", StringComparison.OrdinalIgnoreCase))
            return CompanionController.SetFollowMode(FollowMode.Stay);
        return AgentToolResult.Failure("invalid_arguments");
    }

    private static AgentToolResult ExecutePosture(string arguments)
    {
        string posture;
        if (!TryReadOnlyStringArgument(arguments, "posture", out posture))
            return AgentToolResult.Failure("invalid_arguments");

        if (string.Equals(posture, "standing", StringComparison.OrdinalIgnoreCase))
            return CompanionController.SetPosture(CompanionPosture.Standing);
        if (string.Equals(posture, "crouching", StringComparison.OrdinalIgnoreCase))
            return CompanionController.SetPosture(CompanionPosture.Crouching);
        if (string.Equals(posture, "sitting", StringComparison.OrdinalIgnoreCase))
            return CompanionController.SetPosture(CompanionPosture.Sitting);
        return AgentToolResult.Failure("invalid_arguments");
    }

    private static AgentToolResult ExecuteJump(string arguments)
    {
        if (!IsEmptyObject(arguments))
            return AgentToolResult.Failure("invalid_arguments");
        return CompanionController.RequestJump();
    }

    /// <summary>
    /// Starts a multi-frame companion job. The model's turn stays open until the
    /// job reports a terminal result, so no branch here is specific to what the
    /// job actually does.
    /// </summary>
    private static AgentToolDispatch ExecuteJob(
        string jobName,
        string arguments,
        CompanionJobRequest request)
    {
        if (!IsEmptyObject(arguments))
            return AgentToolDispatch.Immediate(AgentToolResult.Failure("invalid_arguments"));

        if (request == null)
            request = new CompanionJobRequest();
        request.ActionName = jobName;

        AgentToolResult failure;
        CompanionJobHandle handle;
        if (!CompanionController.TryBeginJob(
                jobName,
                request,
                out handle,
                out failure))
            return AgentToolDispatch.Immediate(failure);
        return AgentToolDispatch.Pending(handle.Token, handle.TimeoutSeconds);
    }

    private static AgentToolDispatch ExecuteReferencedItemJob(
        string jobName,
        string arguments,
        CompanionTurnReference turnReference,
        string callId)
    {
        string target;
        if (!TryReadOnlyStringArgument(arguments, "target", out target))
        {
            return AgentToolDispatch.Immediate(
                AgentToolResult.Failure("invalid_arguments"));
        }

        if (turnReference == null)
        {
            return AgentToolDispatch.Immediate(
                AgentToolResult.Failure("human_reference_not_captured"));
        }

        CompanionPropTarget propTarget;
        if (string.Equals(target, "human_reference", StringComparison.Ordinal))
        {
            propTarget = turnReference.Target;
            if (propTarget == null)
            {
                return AgentToolDispatch.Immediate(
                    AgentToolResult.Failure("item_not_found"));
            }
        }
        else
        {
            string resolveError = null;
            if (turnReference.EntityReferences == null ||
                !turnReference.EntityReferences.TryResolve(
                    target,
                    out propTarget,
                    out resolveError))
            {
                return AgentToolDispatch.Immediate(
                    AgentToolResult.Failure(resolveError ?? "item_not_found"));
            }
        }

        Plugin.Logger.LogInfo(
            $"[ENTITY] TARGET_RESOLVED action={jobName}, target={target}, " +
            $"referenceId={propTarget.ReferenceId}, " +
            $"netId={propTarget.NetworkId}, callId={callId ?? "none"}, " +
            $"turnId={turnReference.TurnId}.");

        return ExecuteJob(
            jobName,
            "{}",
            new CompanionJobRequest
            {
                CallId = callId,
                TurnId = turnReference.TurnId,
                PropTarget = propTarget
            });
    }

    private static AgentToolDispatch ExecutePickUpPlayerJob(
        string arguments,
        CompanionTurnReference turnReference,
        string callId)
    {
        if (!IsEmptyObject(arguments))
        {
            return AgentToolDispatch.Immediate(
                AgentToolResult.Failure("invalid_arguments"));
        }
        if (turnReference?.HumanPlayerTarget == null)
        {
            return AgentToolDispatch.Immediate(
                AgentToolResult.Failure("human_player_unavailable"));
        }

        Plugin.Logger.LogInfo(
            $"[ENTITY] TARGET_RESOLVED action={AgentToolCatalog.PickUpPlayer}, " +
            $"target=human, referenceId={turnReference.HumanPlayerTarget.StableId}, " +
            $"netId={turnReference.HumanPlayerTarget.NetworkId}, " +
            $"callId={callId ?? "none"}, turnId={turnReference.TurnId}.");
        return ExecuteJob(
            AgentToolCatalog.PickUpPlayer,
            arguments,
            new CompanionJobRequest
            {
                CallId = callId,
                TurnId = turnReference.TurnId,
                PlayerTarget = turnReference.HumanPlayerTarget
            });
    }

    private static AgentToolDispatch ExecuteDropItemJob(
        string arguments,
        CompanionTurnReference turnReference,
        string callId)
    {
        if (!IsEmptyObject(arguments))
        {
            return AgentToolDispatch.Immediate(
                AgentToolResult.Failure("invalid_arguments"));
        }

        var propTarget = turnReference?.CompanionHeldTarget;
        if (propTarget == null)
        {
            return AgentToolDispatch.Immediate(
                AgentToolResult.Failure(
                    turnReference?.CompanionHeldCaptureError ??
                    "companion_held_item_unavailable"));
        }

        Plugin.Logger.LogInfo(
            $"[ENTITY] TARGET_RESOLVED action={AgentToolCatalog.DropItem}, " +
            $"target=companion_held_item, referenceId={propTarget.ReferenceId}, " +
            $"netId={propTarget.NetworkId}, callId={callId ?? "none"}, " +
            $"turnId={turnReference.TurnId}.");
        return ExecuteJob(
            AgentToolCatalog.DropItem,
            arguments,
            new CompanionJobRequest
            {
                CallId = callId,
                TurnId = turnReference.TurnId,
                PropTarget = propTarget
            });
    }

    private static AgentToolDispatch ExecuteDropPlayerJob(
        string arguments,
        CompanionTurnReference turnReference,
        string callId)
    {
        if (!IsEmptyObject(arguments))
        {
            return AgentToolDispatch.Immediate(
                AgentToolResult.Failure("invalid_arguments"));
        }
        if (turnReference?.HumanPlayerTarget == null)
        {
            return AgentToolDispatch.Immediate(
                AgentToolResult.Failure("human_player_unavailable"));
        }

        Plugin.Logger.LogInfo(
            $"[ENTITY] TARGET_RESOLVED action={AgentToolCatalog.DropPlayer}, " +
            $"target=companion_held_player, " +
            $"referenceId={turnReference.HumanPlayerTarget.StableId}, " +
            $"netId={turnReference.HumanPlayerTarget.NetworkId}, " +
            $"callId={callId ?? "none"}, turnId={turnReference.TurnId}.");
        return ExecuteJob(
            AgentToolCatalog.DropPlayer,
            arguments,
            new CompanionJobRequest
            {
                CallId = callId,
                TurnId = turnReference.TurnId,
                PlayerTarget = turnReference.HumanPlayerTarget
            });
    }

    private static AgentToolDispatch ExecuteInspectionJob(
        string arguments,
        CompanionTurnReference turnReference)
    {
        string target;
        if (!TryReadOnlyStringArgument(arguments, "target", out target))
        {
            return AgentToolDispatch.Immediate(
                AgentToolResult.Failure("invalid_arguments"));
        }

        CompanionInspectionSource source;
        if (string.Equals(target, "human_held_item", StringComparison.Ordinal))
        {
            source = CompanionInspectionSource.HumanHeldItem;
        }
        else if (string.Equals(target, "human_gaze", StringComparison.Ordinal))
        {
            source = CompanionInspectionSource.HumanGaze;
        }
        else
        {
            return AgentToolDispatch.Immediate(
                AgentToolResult.Failure("invalid_arguments"));
        }

        if (turnReference == null || turnReference.InspectionCandidates == null)
        {
            return AgentToolDispatch.Immediate(
                AgentToolResult.Failure(
                    turnReference?.InspectionCaptureError ??
                    "inspection_reference_not_captured"));
        }

        CompanionInspectionReferent referent;
        string selectionError;
        if (!turnReference.InspectionCandidates.TrySelect(
                source,
                out referent,
                out selectionError))
        {
            return AgentToolDispatch.Immediate(
                AgentToolResult.Failure(
                    selectionError ?? "inspection_reference_unavailable"));
        }

        return ExecuteJob(
            AgentToolCatalog.InspectReference,
            "{}",
            new CompanionJobRequest
            {
                TurnId = turnReference.TurnId,
                InspectionReferent = referent
            });
    }

    private static AgentToolDispatch ExecuteInteractionJob(
        string arguments,
        CompanionTurnReference turnReference,
        string callId)
    {
        string target;
        CompanionInteractionIntent intent;
        if (!TryReadInteractionArguments(arguments, out target, out intent))
        {
            return AgentToolDispatch.Immediate(
                AgentToolResult.Failure("invalid_arguments"));
        }

        if (turnReference == null)
        {
            return AgentToolDispatch.Immediate(
                AgentToolResult.Failure("object_not_found"));
        }

        CompanionAffordanceTarget affordanceTarget;
        string selectionError = null;
        if (string.Equals(target, "human_reference", StringComparison.Ordinal) ||
            string.Equals(
                target,
                "companion_held_item",
                StringComparison.Ordinal))
        {
            var source = string.Equals(
                target,
                "companion_held_item",
                StringComparison.Ordinal)
                ? CompanionAffordanceSource.CompanionHeldItem
                : CompanionAffordanceSource.HumanReference;
            if (turnReference.AffordanceCandidates == null ||
                !turnReference.AffordanceCandidates.TrySelect(
                    source,
                    out affordanceTarget,
                    out selectionError))
            {
                return AgentToolDispatch.Immediate(
                    AgentToolResult.Failure(
                        selectionError ??
                        turnReference.AffordanceCaptureError ??
                        "object_not_found"));
            }
        }
        else if (turnReference.EntityReferences == null ||
                 !turnReference.EntityReferences.TryResolveInteraction(
                     target,
                     turnReference.AffordanceCandidates,
                     out affordanceTarget,
                     out selectionError))
        {
            return AgentToolDispatch.Immediate(
                AgentToolResult.Failure(
                    selectionError ??
                    "object_not_found"));
        }

        Plugin.Logger.LogInfo(
            $"[ENTITY] TARGET_RESOLVED action={AgentToolCatalog.InteractWithObject}, " +
            $"target={target}, referenceId={affordanceTarget.ReferenceId}, " +
            $"kind={affordanceTarget.KindLabel}, netId={affordanceTarget.NetworkId}, " +
            $"intent={intent.ToString().ToLowerInvariant()}, callId={callId ?? "none"}, " +
            $"turnId={turnReference.TurnId}.");

        return ExecuteJob(
            AgentToolCatalog.InteractWithObject,
            "{}",
            new CompanionJobRequest
            {
                CallId = callId,
                TurnId = turnReference.TurnId,
                AffordanceTarget = affordanceTarget,
                InteractionIntent = intent
            });
    }

    private static AgentToolDispatch ExecuteMoveToLocationJob(
        string arguments,
        CompanionTurnReference turnReference,
        string callId)
    {
        if (!IsEmptyObject(arguments))
        {
            return AgentToolDispatch.Immediate(
                AgentToolResult.Failure("invalid_arguments"));
        }

        CompanionInspectionReferent destination;
        string selectionError = null;
        if (turnReference?.InspectionCandidates == null ||
            !turnReference.InspectionCandidates.TrySelect(
                CompanionInspectionSource.HumanGaze,
                out destination,
                out selectionError) ||
            destination == null || !destination.GazeRayHit)
        {
            return AgentToolDispatch.Immediate(
                AgentToolResult.Failure(
                    selectionError ??
                    turnReference?.InspectionCaptureError ??
                    "location_not_found"));
        }

        Vector3 destinationPoint;
        if (!destination.TryGetCurrentPoint(out destinationPoint))
        {
            return AgentToolDispatch.Immediate(
                AgentToolResult.Failure("location_not_found"));
        }
        var destinationReferenceId =
            CompanionInspectionReferent.GetFrozenPointReferenceId(
                destinationPoint);
        Plugin.Logger.LogInfo(
            $"[ENTITY] TARGET_RESOLVED action={AgentToolCatalog.GoToLocation}, " +
            $"target=human_indicated_location, source={destination.SourceLabel}, " +
            $"referenceId={destinationReferenceId}, " +
            $"destinationPoint={destinationPoint}, " +
            $"callId={callId ?? "none"}, turnId={turnReference.TurnId}.");
        return ExecuteJob(
            AgentToolCatalog.GoToLocation,
            arguments,
            new CompanionJobRequest
            {
                CallId = callId,
                TurnId = turnReference.TurnId,
                MoveDestination = destination
            });
    }

    private static AgentToolDispatch ExecuteKickItemJob(
        string arguments,
        CompanionTurnReference turnReference,
        string callId)
    {
        string target;
        CompanionKickStrength strength;
        CompanionKickDirection direction;
        if (!TryReadKickArguments(
                arguments,
                out target,
                out strength,
                out direction))
        {
            return AgentToolDispatch.Immediate(
                AgentToolResult.Failure("invalid_arguments"));
        }

        if (turnReference == null)
        {
            return AgentToolDispatch.Immediate(
                AgentToolResult.Failure("human_reference_not_captured"));
        }

        // One utterance-boundary gaze cannot freeze both the prop and its
        // destination. Require the model to name the held/context prop
        // independently; never reinterpret a requested gaze target.
        if (string.Equals(target, "human_reference", StringComparison.Ordinal) &&
            direction == CompanionKickDirection.TowardReference)
        {
            return AgentToolDispatch.Immediate(
                AgentToolResult.Failure(
                    "kick_target_destination_ambiguous"));
        }

        CompanionPropTarget propTarget;
        string targetError = null;
        if (string.Equals(target, "human_reference", StringComparison.Ordinal))
        {
            propTarget = turnReference.Target;
            targetError = turnReference.CaptureError;
        }
        else if (string.Equals(
                     target,
                     "companion_held_item",
                     StringComparison.Ordinal))
        {
            propTarget = turnReference.CompanionHeldTarget;
            targetError = turnReference.CompanionHeldCaptureError;
        }
        else if (turnReference.EntityReferences == null ||
                 !turnReference.EntityReferences.TryResolve(
                     target,
                     out propTarget,
                     out targetError))
        {
            propTarget = null;
        }

        if (propTarget == null)
        {
            return AgentToolDispatch.Immediate(
                AgentToolResult.Failure(targetError ?? "item_not_found"));
        }

        CompanionPlayerTarget playerTarget = null;
        if (direction == CompanionKickDirection.TowardHuman)
        {
            playerTarget = turnReference.HumanPlayerTarget;
            if (playerTarget == null)
            {
                return AgentToolDispatch.Immediate(
                    AgentToolResult.Failure("human_player_unavailable"));
            }
        }

        CompanionInspectionReferent destination = null;
        if (direction == CompanionKickDirection.TowardReference)
        {
            string destinationError = null;
            if (turnReference.InspectionCandidates == null ||
                !turnReference.InspectionCandidates.TrySelect(
                    CompanionInspectionSource.HumanGaze,
                    out destination,
                    out destinationError))
            {
                return AgentToolDispatch.Immediate(
                    AgentToolResult.Failure(
                        destinationError ??
                        turnReference.InspectionCaptureError ??
                        "kick_destination_not_captured"));
            }
        }

        Plugin.Logger.LogInfo(
            $"[ENTITY] TARGET_RESOLVED action={AgentToolCatalog.KickItem}, " +
            $"target={target}, referenceId={propTarget.ReferenceId}, " +
            $"netId={propTarget.NetworkId}, direction={direction.ToWireValue()}, " +
            $"humanTarget={(playerTarget == null ? "none" : playerTarget.StableId)}, " +
            $"humanReferenceId={(playerTarget == null ? 0 : playerTarget.ReferenceId)}, " +
            $"humanNetId={(playerTarget == null ? 0u : playerTarget.NetworkId)}, " +
            $"destination={(destination == null ? "none" : destination.SourceLabel)}, " +
            $"callId={callId ?? "none"}, " +
            $"turnId={turnReference.TurnId}.");

        return ExecuteJob(
            AgentToolCatalog.KickItem,
            "{}",
            new CompanionJobRequest
            {
                CallId = callId,
                TurnId = turnReference.TurnId,
                PropTarget = propTarget,
                PlayerTarget = playerTarget,
                KickDestination = destination,
                KickStrength = strength,
                KickDirection = direction
            });
    }

    private static bool TryReadInteractionArguments(
        string arguments,
        out string target,
        out CompanionInteractionIntent intent)
    {
        target = null;
        intent = CompanionInteractionIntent.Use;
        if (string.IsNullOrWhiteSpace(arguments))
            return false;

        try
        {
            using var document = JsonDocument.Parse(arguments);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            var sawTarget = false;
            var sawIntent = false;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                    return false;

                var value = property.Value.GetString();
                if (string.Equals(property.Name, "target", StringComparison.Ordinal))
                {
                    if (sawTarget || string.IsNullOrWhiteSpace(value))
                        return false;
                    sawTarget = true;
                    target = value;
                    continue;
                }

                if (string.Equals(property.Name, "intent", StringComparison.Ordinal))
                {
                    if (sawIntent)
                        return false;
                    sawIntent = true;
                    if (string.Equals(value, "use", StringComparison.Ordinal))
                    {
                        intent = CompanionInteractionIntent.Use;
                        continue;
                    }
                    if (string.Equals(value, "sit", StringComparison.Ordinal))
                    {
                        intent = CompanionInteractionIntent.Sit;
                        continue;
                    }
                    return false;
                }

                return false;
            }

            return sawTarget;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadKickArguments(
        string arguments,
        out string target,
        out CompanionKickStrength strength,
        out CompanionKickDirection direction)
    {
        target = null;
        strength = CompanionKickStrength.Normal;
        direction = CompanionKickDirection.AwayFromCompanion;
        if (string.IsNullOrWhiteSpace(arguments))
            return false;

        try
        {
            using var document = JsonDocument.Parse(arguments);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            var sawTarget = false;
            var sawStrength = false;
            var sawDirection = false;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                    return false;

                var value = property.Value.GetString();
                if (string.Equals(
                        property.Name,
                        "target",
                        StringComparison.Ordinal))
                {
                    if (sawTarget || string.IsNullOrWhiteSpace(value))
                        return false;
                    sawTarget = true;
                    target = value;
                    continue;
                }

                if (string.Equals(
                        property.Name,
                        "strength",
                        StringComparison.Ordinal))
                {
                    if (sawStrength || !TryParseKickStrength(value, out strength))
                        return false;
                    sawStrength = true;
                    continue;
                }

                if (string.Equals(
                        property.Name,
                        "direction",
                        StringComparison.Ordinal))
                {
                    if (sawDirection || !TryParseKickDirection(value, out direction))
                        return false;
                    sawDirection = true;
                    continue;
                }

                return false;
            }

            return sawTarget;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseKickStrength(
        string value,
        out CompanionKickStrength strength)
    {
        strength = CompanionKickStrength.Normal;
        if (string.Equals(value, "light", StringComparison.Ordinal))
        {
            strength = CompanionKickStrength.Light;
            return true;
        }
        if (string.Equals(value, "normal", StringComparison.Ordinal))
            return true;
        if (string.Equals(value, "hard", StringComparison.Ordinal))
        {
            strength = CompanionKickStrength.Hard;
            return true;
        }
        return false;
    }

    private static bool TryParseKickDirection(
        string value,
        out CompanionKickDirection direction)
    {
        direction = CompanionKickDirection.AwayFromCompanion;
        if (string.Equals(
                value,
                "away_from_companion",
                StringComparison.Ordinal))
        {
            return true;
        }
        if (string.Equals(value, "toward_human", StringComparison.Ordinal))
        {
            direction = CompanionKickDirection.TowardHuman;
            return true;
        }
        if (string.Equals(value, "toward_reference", StringComparison.Ordinal))
        {
            direction = CompanionKickDirection.TowardReference;
            return true;
        }
        return false;
    }

    private static AgentToolResult ExecuteCancelAction(string arguments)
    {
        if (!IsEmptyObject(arguments))
            return AgentToolResult.Failure("invalid_arguments");
        return CompanionController.CancelActiveWork();
    }

    private static bool TryReadOnlyStringArgument(
        string arguments,
        string propertyName,
        out string value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(arguments))
            return false;

        try
        {
            using var document = JsonDocument.Parse(arguments);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            var propertyCount = 0;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                propertyCount++;
                if (!string.Equals(property.Name, propertyName, StringComparison.Ordinal) ||
                    property.Value.ValueKind != JsonValueKind.String)
                {
                    return false;
                }
                value = property.Value.GetString();
            }

            return propertyCount == 1 && !string.IsNullOrWhiteSpace(value);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsEmptyObject(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return false;

        try
        {
            using var document = JsonDocument.Parse(arguments);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            foreach (var ignored in document.RootElement.EnumerateObject())
                return false;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
