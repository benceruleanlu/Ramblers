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
                return ExecuteJob(
                    AgentToolCatalog.InspectReference,
                    functionCall.Arguments,
                    null);
            case AgentToolCatalog.PickUpItem:
                return ExecutePickup(
                    functionCall.Arguments,
                    turnReference);
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

    private static AgentToolDispatch ExecutePickup(
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
            AgentToolCatalog.PickUpItem,
            "{}",
            new CompanionJobRequest
            {
                TurnId = turnReference.TurnId,
                InteractionTarget = turnReference.Target
            });
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
