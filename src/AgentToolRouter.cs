using System;
using System.Text.Json;

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
            case AgentToolCatalog.InteractWithObject:
                return ExecuteInteractionJob(
                    functionCall.Arguments,
                    turnReference);
            case AgentToolCatalog.PickUpItem:
                return ExecuteReferencedItemJob(
                    AgentToolCatalog.PickUpItem,
                    functionCall.Arguments,
                    turnReference);
            case AgentToolCatalog.KickItem:
                return ExecuteKickItemJob(
                    functionCall.Arguments,
                    turnReference);
            case AgentToolCatalog.DropItem:
                return ExecuteJob(
                    AgentToolCatalog.DropItem,
                    functionCall.Arguments,
                    null);
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
        CompanionTurnReference turnReference)
    {
        string target;
        if (!TryReadOnlyStringArgument(arguments, "target", out target) ||
            !string.Equals(
                target,
                "human_reference",
                StringComparison.Ordinal))
        {
            return AgentToolDispatch.Immediate(
                AgentToolResult.Failure("invalid_arguments"));
        }

        if (turnReference == null)
        {
            return AgentToolDispatch.Immediate(
                AgentToolResult.Failure("human_reference_not_captured"));
        }

        if (turnReference.Target == null)
        {
            return AgentToolDispatch.Immediate(
                AgentToolResult.Failure(
                    string.IsNullOrEmpty(turnReference.CaptureError)
                        ? "human_reference_not_captured"
                        : turnReference.CaptureError));
        }

        return ExecuteJob(
            jobName,
            "{}",
            new CompanionJobRequest
            {
                TurnId = turnReference.TurnId,
                InteractionTarget = turnReference.Target
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
        CompanionTurnReference turnReference)
    {
        string target;
        if (!TryReadOnlyStringArgument(arguments, "target", out target))
        {
            return AgentToolDispatch.Immediate(
                AgentToolResult.Failure("invalid_arguments"));
        }

        CompanionPeckSource source;
        if (string.Equals(target, "human_reference", StringComparison.Ordinal))
        {
            source = CompanionPeckSource.HumanReference;
        }
        else if (string.Equals(
                     target,
                     "companion_held_item",
                     StringComparison.Ordinal))
        {
            source = CompanionPeckSource.CompanionHeldItem;
        }
        else
        {
            return AgentToolDispatch.Immediate(
                AgentToolResult.Failure("invalid_arguments"));
        }

        if (turnReference == null || turnReference.PeckCandidates == null)
        {
            return AgentToolDispatch.Immediate(
                AgentToolResult.Failure(
                    turnReference?.PeckCaptureError ??
                    "interaction_reference_not_captured"));
        }

        CompanionPeckTarget peckTarget;
        string selectionError;
        if (!turnReference.PeckCandidates.TrySelect(
                source,
                out peckTarget,
                out selectionError))
        {
            return AgentToolDispatch.Immediate(
                AgentToolResult.Failure(
                    selectionError ?? "interaction_reference_unavailable"));
        }

        return ExecuteJob(
            AgentToolCatalog.InteractWithObject,
            "{}",
            new CompanionJobRequest
            {
                TurnId = turnReference.TurnId,
                PeckTarget = peckTarget
            });
    }

    private static AgentToolDispatch ExecuteKickItemJob(
        string arguments,
        CompanionTurnReference turnReference)
    {
        string target;
        CompanionKickStrength strength;
        CompanionKickDirection direction;
        if (!TryReadKickArguments(
                arguments,
                out target,
                out strength,
                out direction) ||
            !string.Equals(
                target,
                "human_reference",
                StringComparison.Ordinal))
        {
            return AgentToolDispatch.Immediate(
                AgentToolResult.Failure("invalid_arguments"));
        }

        if (turnReference == null)
        {
            return AgentToolDispatch.Immediate(
                AgentToolResult.Failure("human_reference_not_captured"));
        }

        if (turnReference.Target == null)
        {
            return AgentToolDispatch.Immediate(
                AgentToolResult.Failure(
                    string.IsNullOrEmpty(turnReference.CaptureError)
                        ? "human_reference_not_captured"
                        : turnReference.CaptureError));
        }

        return ExecuteJob(
            AgentToolCatalog.KickItem,
            "{}",
            new CompanionJobRequest
            {
                TurnId = turnReference.TurnId,
                InteractionTarget = turnReference.Target,
                KickStrength = strength,
                KickDirection = direction
            });
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
