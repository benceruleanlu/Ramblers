using Mirror;
using UnityEngine;

namespace Ramblers;

/// <summary>
/// Typed admission result for one exact affordance. Only NeedsApproach grants
/// the interaction job permission to reserve locomotion and retry.
/// </summary>
internal sealed class CompanionAffordanceReadiness
{
    private CompanionAffordanceReadiness(
        CompanionAffordanceReadinessState state,
        Vector3 point,
        CompanionAffordanceActivation activation,
        string error)
    {
        State = state;
        Point = point;
        Activation = activation;
        Error = error;
    }

    internal CompanionAffordanceReadinessState State { get; }
    internal Vector3 Point { get; }
    internal CompanionAffordanceActivation Activation { get; }
    internal string Error { get; }

    internal static CompanionAffordanceReadiness Ready(
        Vector3 point,
        CompanionAffordanceActivation activation)
    {
        return new CompanionAffordanceReadiness(
            CompanionAffordanceReadinessState.Ready,
            point,
            activation,
            null);
    }

    internal static CompanionAffordanceReadiness NeedsApproach(Vector3 point)
    {
        return new CompanionAffordanceReadiness(
            CompanionAffordanceReadinessState.NeedsApproach,
            point,
            null,
            "interaction_out_of_reach");
    }

    internal static CompanionAffordanceReadiness Unavailable(string error)
    {
        return new CompanionAffordanceReadiness(
            CompanionAffordanceReadinessState.Unavailable,
            Vector3.zero,
            null,
            error ?? "interaction_unavailable");
    }
}

/// <summary>
/// Exact actors bound at turn capture. Relationship-sensitive affordances use
/// these references directly instead of rediscovering a local player through
/// global world state or borrowing follow-behaviour policy.
/// </summary>
internal sealed class CompanionActorContext
{
    private readonly CompanionBody _body;
    private readonly PlayerCharacter _human;
    private readonly int _humanInstanceId;

    internal CompanionActorContext(CompanionBody body, PlayerCharacter human)
    {
        _body = body;
        _human = human;
        _humanInstanceId = human == null ? 0 : human.GetInstanceID();
        HumanCarryingCompanionAtCapture = IsHumanCarryingCompanion;
    }

    internal CompanionBody Body => _body;
    internal PlayerCharacter Human => _human;
    internal bool HumanCarryingCompanionAtCapture { get; }

    internal bool TryValidateBody(CompanionBody body, out string error)
    {
        if (body == null || !body.IsAlive)
        {
            error = "bot_not_spawned";
            return false;
        }
        if (!ReferenceEquals(body, _body))
        {
            error = "interaction_actor_changed";
            return false;
        }

        error = null;
        return true;
    }

    internal bool IsBoundHumanAvailable =>
        _human != null && _human.GetInstanceID() == _humanInstanceId;

    /// <summary>
    /// Exact stock relationship when the controller-bound human carries this
    /// companion. All native links participate so partial pose teardown does
    /// not silently change carried-affordance admission.
    /// </summary>
    internal bool IsHumanCarryingCompanion
    {
        get
        {
            if (_body == null || !_body.IsAlive || !IsBoundHumanAvailable ||
                _human.gameObject == _body.GameObject)
            {
                return false;
            }

            var grabPose = _human.registry?.grabPose;
            var poser = _body.Character?.poser;
            return _human.hands?.heldCharacter == _body.Character ||
                   (poser != null && poser.playerHoldingMe == _human) ||
                   (grabPose != null && poser != null &&
                    poser.currentPose == grabPose) ||
                   (grabPose != null && grabPose.occupant == _body.Character);
        }
    }
}

internal interface ICompanionAffordanceDriver
{
    CompanionAffordanceKind Kind { get; }
    string KindLabel { get; }
    CompanionAffordanceSource Source { get; }
    string ReferenceId { get; }
    uint NetworkId { get; }
    bool IsWorldTarget { get; }

    bool TryGetCurrentPoint(
        CompanionActorContext actor,
        out Vector3 point,
        out string error);

    CompanionAffordanceReadiness GetReadiness(
        CompanionActorContext actor,
        CompanionInteractionIntent intent);

    bool TryActivate(
        CompanionActorContext actor,
        CompanionAffordanceActivation activation,
        float now,
        out string error);

    bool TryProgressActivation(
        CompanionActorContext actor,
        CompanionAffordanceActivation activation,
        float now,
        out bool observed,
        out string observation,
        out string error);

    string SuccessState(CompanionAffordanceActivation activation);
    string DescribeActivation(CompanionAffordanceActivation activation);
}

internal static class CompanionAffordanceDriverUtility
{
    private const float ReachTolerance = 0.15f;

    internal static bool HasCompanionAuthority(CompanionActorContext actor)
    {
        var body = actor?.Body;
        return body != null && body.IsAlive && NetworkServer.active &&
               body.Networking.isServer && !body.Networking.isLocalPlayer;
    }

    internal static bool IsWithinNativeReach(
        CompanionActorContext actor,
        Vector3 point)
    {
        var body = actor?.Body;
        if (body == null || !body.IsAlive || body.Character.caster == null)
            return false;
        var toTarget = point - body.HeadPosition;
        var distance = toTarget.magnitude;
        var direction = distance <= 0.001f
            ? body.Transform.forward
            : toTarget / distance;
        var maxDistance = body.Character.caster.GetMaxDistanceForDirection(
            direction);
        return distance <= maxDistance + ReachTolerance;
    }

    internal static bool IsAvailable(Behaviour behaviour)
    {
        return behaviour != null && behaviour.enabled &&
               behaviour.gameObject != null &&
               behaviour.gameObject.activeInHierarchy;
    }

    internal static bool HasValidReleaseSwitch(PeckSwitch releaseSwitch)
    {
        return releaseSwitch == null ||
               (releaseSwitch.gameObject != null &&
                releaseSwitch.trackedStateSystem != null);
    }

    internal static bool HasValidWorldSwitchReference(PeckSwitch peckSwitch)
    {
        if (peckSwitch == null)
            return false;
        var reference = peckSwitch.shellReference;
        return !reference.isEmpty && reference.GetPeckSwitch() == peckSwitch;
    }

    internal static bool MatchesWorldSwitchReference(
        PeckSwitch peckSwitch,
        ushort ticket,
        uint networkId,
        int index)
    {
        if (peckSwitch == null)
            return false;
        var reference = peckSwitch.shellReference;
        return !reference.isEmpty && reference.ticket == ticket &&
               reference.netId == networkId && reference.index == index &&
               reference.GetPeckSwitch() == peckSwitch;
    }
}

internal abstract class CompanionAffordanceActivation
{
    internal abstract CompanionAffordanceKind Kind { get; }

    /// <summary>
    /// Sticky transaction boundary. Drivers set this immediately before every
    /// native command so the owning job can reconcile a command that throws or
    /// reports failure after the game may already have accepted it.
    /// </summary>
    internal bool AuthorityCrossed { get; private set; }

    internal void MarkAuthorityCrossed()
    {
        AuthorityCrossed = true;
    }
}

internal sealed class CompanionSwitchAffordanceActivation :
    CompanionAffordanceActivation
{
    private readonly CompanionAffordanceKind _kind;

    internal CompanionSwitchAffordanceActivation(
        CompanionAffordanceKind kind)
    {
        _kind = kind;
    }

    internal override CompanionAffordanceKind Kind => _kind;
    internal CompanionInteractionIntent Intent = CompanionInteractionIntent.Use;
    internal PeckContext SwitchContext;
    internal PeckContext KeyContext;
    internal PeckContext ReleaseContext;
    internal Prop SwitchContextProp;
    internal int PreviousKeyState;
    internal int PreviousKeyActionNumber;
    internal int ExpectedKeyState;
    internal int ExpectedKeyActionNumber;
    internal int PreviousSwitchState;
    internal int PreviousActionNumber;
    internal int ExpectedSwitchState;
    internal int ExpectedActionNumber;
    internal int PreviousReleaseState;
    internal int PreviousReleaseActionNumber;
    internal int ExpectedReleaseState;
    internal int ExpectedReleaseActionNumber;
    internal bool HasReleaseSwitch;
    internal bool HasNativeKeyEffect;
    internal bool KeyEffectDispatched;
    internal bool KeyEffectObserved;
    internal bool KeyEffectCommandReturned;
    internal bool DownDispatched;
    internal bool DownObserved;
    internal bool DownCommandReturned;
    internal bool ReleaseDispatched;
    internal bool ReleaseCommandReturned;
    internal float DownDispatchedAt;
    internal float ReleaseDispatchedAt;
    internal bool KeyEffectWasNativeNoOp;
    internal bool KeyEffectWasNativeRetrigger;
    internal bool DownWasNativeNoOp;
    internal bool ReleaseWasNativeNoOp;
    internal bool DownWasNativeRetrigger;
    internal bool ReleaseWasNativeRetrigger;

    internal bool CanDispatchMainSwitch =>
        CanDispatchAfterPrerequisite(
            HasNativeKeyEffect,
            KeyEffectDispatched,
            KeyEffectObserved);

    internal static bool CanDispatchAfterPrerequisite(
        bool hasNativePrerequisite,
        bool prerequisiteDispatched,
        bool prerequisiteObserved)
    {
        return !hasNativePrerequisite ||
               (prerequisiteDispatched && prerequisiteObserved);
    }
}

internal sealed class CompanionPlayerPoseAffordanceActivation :
    CompanionAffordanceActivation
{
    internal override CompanionAffordanceKind Kind =>
        CompanionAffordanceKind.PlayerPose;
    internal bool ExpectedSitting;
    internal bool AlreadyApplied;
    internal bool EntryDispatched;
    internal bool EntryObserved;
    internal bool EntryCommandReturned;
    internal bool SittingDispatched;
    internal bool SittingCommandReturned;
}

internal sealed class CompanionPropHomeAffordanceActivation :
    CompanionAffordanceActivation
{
    internal override CompanionAffordanceKind Kind =>
        CompanionAffordanceKind.PropHome;
}
