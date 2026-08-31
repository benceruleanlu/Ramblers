using System;

namespace Ramblers;

[Flags]
internal enum JobResources
{
    None = 0,
    Locomotion = 1 << 0,
    Gaze = 1 << 1,
    Hands = 1 << 2
}

internal enum CompanionKickStrength
{
    Normal,
    Light,
    Hard
}

internal enum CompanionKickDirection
{
    AwayFromCompanion,
    TowardHuman,
    TowardReference
}

internal static class CompanionKickDirectionProtocol
{
    internal static string ToWireValue(
        this CompanionKickDirection direction)
    {
        return direction == CompanionKickDirection.TowardReference
            ? "toward_reference"
            : direction == CompanionKickDirection.TowardHuman
                ? "toward_human"
                : "away_from_companion";
    }
}

internal enum CompanionTurnHandsTransition
{
    None,
    HoldingExactProp,
    HandsEmpty,
    HoldingExactPlayer
}

internal sealed class CompanionJobCompletion
{
    internal AgentToolResult Result;
    internal AgentContinuationItem[] Continuation;
    internal bool RetainUntilAssistantAudio;
    internal CompanionTurnHandsTransition HandsTransition;
    internal CompanionPropTarget ExactProp;
    internal CompanionPlayerTarget ExactPlayer;

    internal static CompanionJobCompletion Failed(string error)
    {
        return new CompanionJobCompletion
        {
            Result = AgentToolResult.Failure(error),
            Continuation = null,
            RetainUntilAssistantAudio = false,
            HandsTransition = CompanionTurnHandsTransition.None
        };
    }
}

internal sealed class CompanionJobHandle
{
    internal long Token;
    internal float TimeoutSeconds;
}

internal sealed class CompanionJobRequest
{
    internal string ActionName;
    internal string CallId;
    internal long TurnId;
    internal CompanionPropTarget PropTarget;
    internal CompanionInspectionReferent InspectionReferent;
    internal CompanionInspectionReferent MoveDestination;
    internal CompanionInspectionReferent KickDestination;
    internal CompanionAffordanceTarget AffordanceTarget;
    internal CompanionPlayerTarget PlayerTarget;
    internal CompanionInteractionIntent InteractionIntent;
    internal CompanionKickStrength KickStrength;
    internal CompanionKickDirection KickDirection;
}

internal sealed class CompanionTurnReference
{
    internal long TurnId;
    internal CompanionPropTarget Target;
    internal string CaptureError;
    internal CompanionPropTarget CompanionHeldTarget;
    internal string CompanionHeldCaptureError;
    internal CompanionInspectionCandidates InspectionCandidates;
    internal string InspectionCaptureError;
    internal CompanionAffordanceCandidates AffordanceCandidates;
    internal string AffordanceCaptureError;
    internal CompanionPlayerTarget HumanPlayerTarget;
    internal CompanionEntityReferenceSet EntityReferences;

    internal bool TryApply(
        CompanionJobCompletion completion,
        out string error)
    {
        error = null;
        if (completion == null || completion.Result == null ||
            !completion.Result.Ok ||
            completion.HandsTransition == CompanionTurnHandsTransition.None)
        {
            return true;
        }

        if (completion.HandsTransition ==
            CompanionTurnHandsTransition.HoldingExactProp)
        {
            if (completion.ExactProp == null)
            {
                error = "turn_state_transition_invalid";
                return false;
            }

            CompanionAffordanceCandidates advanced;
            if (!CompanionController.TryAdvanceHeldPropCandidates(
                    AffordanceCandidates,
                    completion.ExactProp,
                    out advanced,
                    out error))
            {
                return false;
            }

            CompanionHeldTarget = completion.ExactProp;
            CompanionHeldCaptureError = null;
            AffordanceCandidates = advanced;
            AffordanceCaptureError = null;
            return true;
        }

        if (completion.HandsTransition ==
            CompanionTurnHandsTransition.HoldingExactPlayer)
        {
            if (completion.ExactPlayer == null || HumanPlayerTarget == null ||
                !completion.ExactPlayer.IsStillTheSamePlayer(
                    HumanPlayerTarget.Player))
            {
                error = "turn_state_transition_invalid";
                return false;
            }
        }

        CompanionHeldTarget = null;
        CompanionHeldCaptureError =
            completion.HandsTransition ==
            CompanionTurnHandsTransition.HoldingExactPlayer
                ? "companion_held_item_not_prop"
                : "companion_held_item_unavailable";
        AffordanceCandidates = AffordanceCandidates?.WithoutCompanionHeldProp(
            CompanionHeldCaptureError);
        return true;
    }
}

internal interface ICompanionJob
{

    string Name { get; }

    string ActiveName { get; }

    bool Handles(string actionName);

    JobResources RequiredFor(CompanionJobRequest request);

    JobResources Held { get; }

    bool IsActive { get; }

    bool MayPublishCompletionWhileActive { get; }

    float TimeoutSeconds { get; }

    void Bind(CompanionBody body, PlayerCharacter human);

    bool TryBegin(
        float now,
        CompanionJobRequest request,
        out AgentToolResult failure);

    void Tick(float now);

    bool TryTakeCompletion(out CompanionJobCompletion completion);

    void Conclude(float now);

    void Cancel(float now);

    void Fail(string error, float now);

    void Release();
}

internal interface ICompanionStandingJob
{
}
