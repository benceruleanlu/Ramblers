using System;

namespace Ramblers;

/// <summary>
/// The companion capabilities a job needs exclusive use of. Every action
/// declares what it claims, so mutual exclusion is stated once per action
/// instead of being written out by hand for each pair of actions.
/// </summary>
[Flags]
internal enum JobResources
{
    None = 0,
    Locomotion = 1 << 0,
    Gaze = 1 << 1,
    Hands = 1 << 2
}

/// <summary>
/// Bounded points on Big Walk's native normalized kick-charge curve. The
/// corresponding hold time is resolved from PlayerTunings at runtime.
/// </summary>
internal enum CompanionKickStrength
{
    Normal,
    Light,
    Hard
}

/// <summary>
/// The deliberate launch intent selected for a kick. The target prop remains
/// the immutable response-turn referent regardless of direction; a referenced
/// destination is frozen independently at the same turn boundary.
/// </summary>
internal enum CompanionKickDirection
{
    AwayFromCompanion,
    TowardHuman,
    TowardReference
}

/// <summary>
/// One canonical representation for model arguments and structured telemetry.
/// Keeping this at the typed boundary prevents routers, jobs, and audits from
/// inventing different spellings for the same physical intent.
/// </summary>
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

/// <summary>
/// A confirmed hands-state transition produced by a physical job. The turn
/// reference applies this only after the job has verified the exact native
/// postcondition, allowing a continuation to compose actions without scanning
/// the live world for a replacement target.
/// </summary>
internal enum CompanionTurnHandsTransition
{
    None,
    HoldingExactProp,
    HandsEmpty,
    HoldingExactPlayer
}

/// <summary>
/// A terminal job outcome: the tool result the model receives, plus any extra
/// conversation items the job wants delivered alongside it.
/// </summary>
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

/// <summary>
/// What the agent boundary needs to keep tracking a job it has just started.
/// </summary>
internal sealed class CompanionJobHandle
{
    internal long Token;
    internal float TimeoutSeconds;
}

/// <summary>
/// Immutable context captured before a model-selected job is dispatched. A
/// physical job receives the exact turn-scoped target through this boundary;
/// it never asks the live world to reinterpret the human's reference later.
/// </summary>
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

/// <summary>
/// The result of freezing the human's physical and visual references for one
/// utterance. Failed captures are retained too, so a later tool reports the
/// original boundary error instead of consulting a newer camera direction or
/// held object.
/// </summary>
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

    /// <summary>
    /// Advances only the transient hands capability after an exact physical
    /// postcondition. The spoken world reference remains frozen; no camera or
    /// nearest-entity query is performed here.
    /// </summary>
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

/// <summary>
/// A companion action that runs across frames rather than finishing inside the
/// tool call. The coordinator arbitrates jobs purely through this interface, so
/// adding an action does not add branches to the coordinator.
/// </summary>
internal interface ICompanionJob
{
    /// <summary>The primary model-facing tool name for this job.</summary>
    string Name { get; }

    /// <summary>The model-facing name of the operation currently in progress.</summary>
    string ActiveName { get; }

    /// <summary>Whether this job owns the implementation of a tool name.</summary>
    bool Handles(string actionName);

    /// <summary>What this specific operation must claim to start.</summary>
    JobResources RequiredFor(CompanionJobRequest request);

    /// <summary>
    /// What the job currently holds. This narrows as a job winds down: an
    /// inspection stops holding locomotion once it has captured, keeping only
    /// gaze for its settle hold.
    /// </summary>
    JobResources Held { get; }

    bool IsActive { get; }

    /// <summary>
    /// Whether this job's current completion may be consumed while it still
    /// owns capabilities. This is a narrow state-dependent exception for a
    /// successful pickup/carry hold or inspection presentation; reconciliation
    /// failures and cancellation never use it.
    /// </summary>
    bool MayPublishCompletionWhileActive { get; }

    /// <summary>How long the agent boundary should wait before giving up.</summary>
    float TimeoutSeconds { get; }

    void Bind(CompanionBody body, PlayerCharacter human);

    bool TryBegin(
        float now,
        CompanionJobRequest request,
        out AgentToolResult failure);

    void Tick(float now);

    bool TryTakeCompletion(out CompanionJobCompletion completion);

    /// <summary>
    /// The model has acted on this job's report. Any hold the job kept past
    /// completion can be dropped now.
    /// </summary>
    void Conclude(float now);

    void Cancel(float now);

    void Fail(string error, float now);

    void Release();
}

/// <summary>
/// Marker for jobs whose stock Big Walk action requires a standing pose even
/// though the companion could otherwise locomote while crouched.
/// </summary>
internal interface ICompanionStandingJob
{
}
