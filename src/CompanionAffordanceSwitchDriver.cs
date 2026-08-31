using Mirror;
using UnityEngine;

namespace Ramblers;

internal abstract class CompanionSwitchAffordanceDriver :
    ICompanionAffordanceDriver
{
    protected readonly PeckSwitch PeckSwitch;
    protected readonly TrackedPeckState TrackedState;
    protected readonly PeckSwitch ReleaseSwitch;
    protected readonly TrackedPeckState ReleaseTrackedState;

    private readonly int _switchInstanceId;
    private readonly int _stateInstanceId;
    private readonly int _releaseSwitchInstanceId;
    private readonly int _releaseStateInstanceId;

    protected CompanionSwitchAffordanceDriver(
        PeckSwitch peckSwitch,
        TrackedPeckState trackedState,
        PeckSwitch releaseSwitch,
        CompanionAffordanceSource source)
    {
        PeckSwitch = peckSwitch;
        TrackedState = trackedState;
        ReleaseSwitch = releaseSwitch;
        ReleaseTrackedState = releaseSwitch?.trackedStateSystem;
        _switchInstanceId = peckSwitch.GetInstanceID();
        _stateInstanceId = trackedState.GetInstanceID();
        _releaseSwitchInstanceId = releaseSwitch == null
            ? 0
            : releaseSwitch.GetInstanceID();
        _releaseStateInstanceId = ReleaseTrackedState == null
            ? 0
            : ReleaseTrackedState.GetInstanceID();
        Source = source;
    }

    public abstract CompanionAffordanceKind Kind { get; }
    public abstract string KindLabel { get; }
    public CompanionAffordanceSource Source { get; }
    public abstract string ReferenceId { get; }
    public abstract uint NetworkId { get; }
    public abstract bool IsWorldTarget { get; }

    protected int SwitchInstanceId => _switchInstanceId;

    public abstract bool TryGetCurrentPoint(
        CompanionActorContext actor,
        out Vector3 point,
        out string error);

    public CompanionAffordanceReadiness GetReadiness(
        CompanionActorContext actor,
        CompanionInteractionIntent intent)
    {
        string error = null;
        var body = actor?.Body;
        if (actor == null || !actor.TryValidateBody(body, out error) ||
            !TryValidateExactComponents(actor, false, out error) ||
            !TryValidateBoundRelationship(actor, out error))
        {
            return CompanionAffordanceReadiness.Unavailable(error);
        }
        if (intent == CompanionInteractionIntent.Sit)
        {
            return CompanionAffordanceReadiness.Unavailable(
                "interaction_intent_unsupported");
        }

        Vector3 point;
        if (!TryGetCurrentPoint(actor, out point, out error))
            return CompanionAffordanceReadiness.Unavailable(error);
        if (IsWorldTarget)
        {
            var carriedByHuman = actor.IsHumanCarryingCompanion;
            var reachState = CompanionAffordanceProtocol.ClassifyWorldReadiness(
                true,
                true,
                CompanionAffordanceDriverUtility.IsWithinNativeReach(
                    actor,
                    point),
                !carriedByHuman);
            if (reachState == CompanionAffordanceReadinessState.NeedsApproach)
                return CompanionAffordanceReadiness.NeedsApproach(point);
            if (reachState == CompanionAffordanceReadinessState.Unavailable)
            {
                return CompanionAffordanceReadiness.Unavailable(
                    carriedByHuman
                        ? "interaction_carrier_position_required"
                        : "interaction_out_of_reach");
            }
        }
        if (!TryValidateExactComponents(actor, true, out error) ||
            !TryValidateSwitchPreconditions(actor, false, true, out error))
        {
            return CompanionAffordanceReadiness.Unavailable(error);
        }

        return CompanionAffordanceReadiness.Ready(
            point,
            new CompanionSwitchAffordanceActivation(Kind)
            {
                Intent = CompanionInteractionIntent.Use,
                HasReleaseSwitch = ReleaseSwitch != null,
                HasNativeKeyEffect = HasNativePrerequisite
            });
    }

    public bool TryActivate(
        CompanionActorContext actor,
        CompanionAffordanceActivation activation,
        float now,
        out string error)
    {
        error = null;
        var switchActivation =
            activation as CompanionSwitchAffordanceActivation;
        if (switchActivation == null || switchActivation.Kind != Kind)
        {
            error = "interaction_plan_unavailable";
            return false;
        }
        var finalReadiness = GetReadiness(
            actor,
            CompanionInteractionIntent.Use);
        if (finalReadiness.State != CompanionAffordanceReadinessState.Ready)
        {
            error = finalReadiness.Error;
            return false;
        }
        if (IsWorldTarget &&
            !TryValidateSwitchPreconditions(
                actor,
                true,
                true,
                out error))
        {
            return false;
        }

        var body = actor.Body;
        try
        {

            switchActivation.SwitchContextProp = GetSwitchContextProp(actor);
            switchActivation.SwitchContext = new PeckContext(
                actor.Body.Character,
                switchActivation.SwitchContextProp);
            switchActivation.HasNativeKeyEffect = HasNativePrerequisite;
            if (!TryDispatchPrerequisite(
                    actor,
                    switchActivation,
                    out error))
            {
                return false;
            }
            if (!switchActivation.CanDispatchMainSwitch)
                return true;
            DispatchSwitch(
                PeckSwitch,
                TrackedState,
                actor,
                switchActivation,
                false,
                switchActivation.SwitchContextProp,
                now);
            return true;
        }
        catch (System.Exception exception)
        {
            error = "interaction_authority_failed";
            Plugin.Logger.LogWarning(
                $"[INTERACT] AUTHORITY_EXCEPTION kind={KindLabel}, " +
                $"referenceId={ReferenceId}, error={exception.Message}");
            return false;
        }
    }

    public bool TryProgressActivation(
        CompanionActorContext actor,
        CompanionAffordanceActivation activation,
        float now,
        out bool complete,
        out string observation,
        out string error)
    {
        complete = false;
        observation = "none";
        error = null;
        var switchActivation =
            activation as CompanionSwitchAffordanceActivation;
        if (switchActivation == null || switchActivation.Kind != Kind)
        {
            error = "interaction_plan_unavailable";
            return false;
        }

        if (!switchActivation.DownDispatched)
        {
            bool prerequisiteObserved;
            string prerequisiteObservation;
            if (!TryProgressPrerequisite(
                    switchActivation,
                    out prerequisiteObserved,
                    out prerequisiteObservation,
                    out error))
            {
                observation = "phase=prerequisite_unavailable," +
                              prerequisiteObservation;
                return false;
            }

            observation = "phase=prerequisite," + prerequisiteObservation;
            if (prerequisiteObserved)
                switchActivation.KeyEffectObserved = true;
            if (!switchActivation.CanDispatchMainSwitch)
                return true;

            if (!TryValidatePostPrerequisiteCommit(actor, out error))
                return false;
            try
            {
                DispatchSwitch(
                    PeckSwitch,
                    TrackedState,
                    actor,
                    switchActivation,
                    false,
                    switchActivation.SwitchContextProp,
                    now);
                observation = "phase=down_dispatched";
                return true;
            }
            catch (System.Exception exception)
            {
                error = "interaction_authority_failed";
                Plugin.Logger.LogWarning(
                    $"[INTERACT] AUTHORITY_EXCEPTION kind={KindLabel}, " +
                    $"referenceId={ReferenceId}, error={exception.Message}");
                return false;
            }
        }

        bool receiptObserved;
        string receipt;
        if (!switchActivation.DownObserved)
        {
            if (!TryReadSwitchReceipt(
                    TrackedState,
                    _stateInstanceId,
                    switchActivation.ExpectedSwitchState,
                    switchActivation.ExpectedActionNumber,
                    switchActivation.PreviousSwitchState,
                    switchActivation.PreviousActionNumber,
                    switchActivation.SwitchContext,
                    out receiptObserved,
                    out receipt,
                    out error))
            {
                observation = "phase=down_unavailable";
                return false;
            }

            observation = "phase=down," + receipt;
            if (receiptObserved ||
                (switchActivation.DownCommandReturned &&
                 (switchActivation.DownWasNativeNoOp ||
                  switchActivation.DownWasNativeRetrigger)))
            {
                switchActivation.DownObserved = true;
            }
            observation += $",nativeNoOp={switchActivation.DownWasNativeNoOp}," +
                           $"nativeRetrigger={switchActivation.DownWasNativeRetrigger}," +
                           $"commandReturned={switchActivation.DownCommandReturned}";
            if (!switchActivation.HasReleaseSwitch)
            {
                complete = switchActivation.DownObserved;
                return true;
            }
        }

        if (!switchActivation.ReleaseDispatched)
        {
            var elapsed = now - switchActivation.DownDispatchedAt;
            observation = $"phase=release_wait,elapsed={elapsed:F2}";
            if (!CompanionAffordanceProtocol.IsReleaseDue(
                    switchActivation.DownDispatched,
                    switchActivation.HasReleaseSwitch,
                    elapsed))
            {
                return true;
            }
            if (!TryValidateReleaseSwitchIdentity(out error))
                return false;
            if (!CompanionAffordanceDriverUtility.HasCompanionAuthority(actor) ||
                !ReleaseTrackedState.isServer)
            {
                error = "interaction_authority_unavailable";
                return false;
            }
            if (!TryValidateReleaseActor(actor, out error))
                return false;
            if (actor.Body.Character.actions == null ||
                PeckManager.Instance == null ||
                !PeckManager.Instance.isReadyForEffects)
            {
                error = "interaction_system_unavailable";
                return false;
            }

            try
            {
                DispatchSwitch(
                    ReleaseSwitch,
                    ReleaseTrackedState,
                    actor,
                    switchActivation,
                    true,
                    GetReleaseContextProp(actor),
                    now);
                observation = "phase=release_dispatched";
                return true;
            }
            catch (System.Exception exception)
            {
                error = "interaction_authority_failed";
                Plugin.Logger.LogWarning(
                    $"[INTERACT] RELEASE_EXCEPTION kind={KindLabel}, " +
                    $"referenceId={ReferenceId}, error={exception.Message}");
                return false;
            }
        }

        if (!TryReadSwitchReceipt(
                ReleaseTrackedState,
                _releaseStateInstanceId,
                switchActivation.ExpectedReleaseState,
                switchActivation.ExpectedReleaseActionNumber,
                switchActivation.PreviousReleaseState,
                switchActivation.PreviousReleaseActionNumber,
                switchActivation.ReleaseContext,
                out receiptObserved,
                out receipt,
                out error))
        {
            observation = "phase=release_unavailable";
            return false;
        }
        complete = CompanionAffordanceProtocol.IsMomentaryComplete(
            switchActivation.DownObserved,
            receiptObserved ||
            (switchActivation.ReleaseCommandReturned &&
             (switchActivation.ReleaseWasNativeNoOp ||
              switchActivation.ReleaseWasNativeRetrigger)));
        observation = "phase=release," + receipt +
                      $",nativeNoOp={switchActivation.ReleaseWasNativeNoOp}," +
                      $"nativeRetrigger={switchActivation.ReleaseWasNativeRetrigger}," +
                      $"commandReturned={switchActivation.ReleaseCommandReturned}";
        return true;
    }

    public string SuccessState(CompanionAffordanceActivation activation)
    {
        var switchActivation =
            activation as CompanionSwitchAffordanceActivation;
        return switchActivation?.HasReleaseSwitch == true
            ? "switch_press_released"
            : "switch_action_completed";
    }

    public abstract string DescribeActivation(
        CompanionAffordanceActivation activation);

    protected bool TryValidateExactComponents(
        CompanionActorContext actor,
        bool validateOutcome,
        out string error)
    {
        if (!TryValidateSwitchIdentity(out error) ||
            !TryValidateSourceIdentity(out error))
        {
            return false;
        }
        return !validateOutcome || TryValidateOutcome(actor, out error);
    }

    protected abstract bool TryValidateSourceIdentity(out string error);
    protected abstract bool TryValidateOutcome(
        CompanionActorContext actor,
        out string error);
    protected abstract bool TryValidateBoundRelationship(
        CompanionActorContext actor,
        out string error);
    protected abstract bool TryValidateUsePreconditions(
        CompanionActorContext actor,
        bool requireWorldReach,
        bool validatePrerequisite,
        out string error);
    protected abstract bool HasNativePrerequisite { get; }
    protected abstract bool TryDispatchPrerequisite(
        CompanionActorContext actor,
        CompanionSwitchAffordanceActivation activation,
        out string error);
    protected abstract bool TryProgressPrerequisite(
        CompanionSwitchAffordanceActivation activation,
        out bool observed,
        out string observation,
        out string error);
    protected abstract bool TryValidateReleaseActor(
        CompanionActorContext actor,
        out string error);
    protected abstract Prop GetSwitchContextProp(CompanionActorContext actor);
    protected abstract Prop GetReleaseContextProp(CompanionActorContext actor);
    protected abstract PeckSwitch GetCurrentReleaseSwitch();
    protected abstract void ReadAndAdvanceActionNumber(
        CompanionActorContext actor,
        int previousReceiptActionNumber,
        out int actionNumber);
    protected abstract void DispatchNativeSwitch(
        CompanionActorContext actor,
        PeckSwitch peckSwitch,
        PeckContext context,
        int actionNumber,
        bool isRelease);

    private bool TryValidateSwitchPreconditions(
        CompanionActorContext actor,
        bool requireWorldReach,
        bool validatePrerequisite,
        out string error)
    {
        error = null;
        if (!CompanionAffordanceDriverUtility.HasCompanionAuthority(actor) ||
            !TrackedState.isServer ||
            (ReleaseTrackedState != null && !ReleaseTrackedState.isServer))
        {
            error = "interaction_authority_unavailable";
            return false;
        }
        if (actor.Body.Character.actions == null)
        {
            error = "interaction_system_unavailable";
            return false;
        }
        if (!TryValidateUsePreconditions(
                actor,
                requireWorldReach,
                validatePrerequisite,
                out error))
        {
            return false;
        }
        if (PeckManager.Instance == null ||
            !PeckManager.Instance.isReadyForEffects)
        {
            error = "interaction_system_unavailable";
            return false;
        }
        return true;
    }

    private bool TryValidatePostPrerequisiteCommit(
        CompanionActorContext actor,
        out string error)
    {
        error = null;
        if (actor == null || !actor.TryValidateBody(actor.Body, out error) ||
            !TryValidateExactComponents(actor, false, out error) ||
            !TryValidateBoundRelationship(actor, out error))
        {
            return false;
        }
        return TryValidateSwitchPreconditions(
            actor,
            IsWorldTarget,
            false,
            out error);
    }

    private bool TryValidateSwitchIdentity(out string error)
    {
        error = null;
        if (!CompanionAffordanceDriverUtility.IsAvailable(PeckSwitch) ||
            TrackedState == null || TrackedState.gameObject == null ||
            !TrackedState.gameObject.activeInHierarchy)
        {
            error = "interaction_target_unavailable";
            return false;
        }
        if (PeckSwitch.GetInstanceID() != _switchInstanceId ||
            TrackedState.GetInstanceID() != _stateInstanceId ||
            PeckSwitch.trackedStateSystem == null ||
            PeckSwitch.trackedStateSystem.GetInstanceID() != _stateInstanceId)
        {
            error = "interaction_target_changed";
            return false;
        }
        return TryValidateReleaseSwitchIdentity(out error);
    }

    private bool TryValidateReleaseSwitchIdentity(out string error)
    {
        error = null;
        var currentReleaseSwitch = GetCurrentReleaseSwitch();
        if (ReleaseSwitch == null)
        {
            if (currentReleaseSwitch == null)
                return true;
            error = "interaction_target_changed";
            return false;
        }
        if (currentReleaseSwitch == null ||
            currentReleaseSwitch.GetInstanceID() != _releaseSwitchInstanceId ||
            currentReleaseSwitch != ReleaseSwitch ||
            ReleaseTrackedState == null ||
            ReleaseTrackedState.GetInstanceID() != _releaseStateInstanceId ||
            currentReleaseSwitch.trackedStateSystem != ReleaseTrackedState)
        {
            error = "interaction_target_changed";
            return false;
        }
        return TryValidateWorldReleaseReference(out error);
    }

    protected virtual bool TryValidateWorldReleaseReference(out string error)
    {
        error = null;
        return true;
    }

    private void DispatchSwitch(
        PeckSwitch peckSwitch,
        TrackedPeckState trackedState,
        CompanionActorContext actor,
        CompanionSwitchAffordanceActivation activation,
        bool isRelease,
        Prop contextProp,
        float now)
    {
        var previousContext = trackedState.currentPeckContext;
        var previousState = previousContext == null
            ? trackedState.initialState
            : previousContext.state;
        var previousActionNumber = previousContext == null
            ? -1
            : previousContext.actionNumber;
        var expectedState = peckSwitch.GetNextState(previousState);
        int actionNumber;
        ReadAndAdvanceActionNumber(
            actor,
            previousActionNumber,
            out actionNumber);
        var context = !isRelease && activation.SwitchContext != null
            ? activation.SwitchContext
            : new PeckContext(actor.Body.Character, contextProp);
        context.state = expectedState;
        context.actionNumber = actionNumber;

        if (isRelease)
        {
            activation.ReleaseContext = context;
            activation.PreviousReleaseState = previousState;
            activation.PreviousReleaseActionNumber = previousActionNumber;
            activation.ExpectedReleaseState = expectedState;
            activation.ExpectedReleaseActionNumber = context.actionNumber;
            activation.ReleaseDispatched = true;
            activation.ReleaseDispatchedAt = now;
        }
        else
        {
            activation.SwitchContext = context;
            activation.PreviousSwitchState = previousState;
            activation.PreviousActionNumber = previousActionNumber;
            activation.ExpectedSwitchState = expectedState;
            activation.ExpectedActionNumber = context.actionNumber;
            activation.DownDispatched = true;
            activation.DownDispatchedAt = now;
        }

        activation.MarkAuthorityCrossed();
        DispatchNativeSwitch(
            actor,
            peckSwitch,
            context,
            actionNumber,
            isRelease);
        if (isRelease)
        {
            activation.ReleaseCommandReturned = true;
            activation.ReleaseWasNativeNoOp =
                CompanionAffordanceProtocol.IsIgnoredNativeRepeat(
                    trackedState.ignoreStateRepeats,
                    previousContext != null,
                    previousState,
                    expectedState);
            activation.ReleaseWasNativeRetrigger =
                CompanionAffordanceProtocol.IsNativeRetrigger(
                    trackedState.ignoreStateRepeats,
                    previousContext != null,
                    previousState,
                    expectedState);
            return;
        }

        activation.DownCommandReturned = true;
        activation.DownWasNativeNoOp =
            CompanionAffordanceProtocol.IsIgnoredNativeRepeat(
                trackedState.ignoreStateRepeats,
                previousContext != null,
                previousState,
                expectedState);
        activation.DownWasNativeRetrigger =
            CompanionAffordanceProtocol.IsNativeRetrigger(
                trackedState.ignoreStateRepeats,
                previousContext != null,
                previousState,
                expectedState);
    }

    private static bool TryReadSwitchReceipt(
        TrackedPeckState trackedState,
        int expectedStateInstanceId,
        int expectedState,
        int expectedActionNumber,
        int previousState,
        int previousActionNumber,
        PeckContext expectedContext,
        out bool observed,
        out string observation,
        out string error)
    {
        observed = false;
        observation = "state=none,actionNumber=none";
        error = null;
        if (trackedState == null ||
            trackedState.GetInstanceID() != expectedStateInstanceId)
        {
            error = "interaction_target_changed";
            return false;
        }

        var currentContext = trackedState.currentPeckContext;
        var currentState = currentContext == null
            ? trackedState.initialState
            : currentContext.state;
        var currentActionNumber = currentContext == null
            ? -1
            : currentContext.actionNumber;
        var identityMatches = currentContext != null && expectedContext != null &&
                              currentContext.playerIdentity == expectedContext.playerIdentity &&
                              currentContext.propIdentity == expectedContext.propIdentity;
        observed = CompanionAffordanceProtocol.MatchesSwitchReceipt(
            currentContext != null,
            identityMatches,
            currentState,
            currentActionNumber,
            expectedState,
            expectedActionNumber,
            previousState,
            previousActionNumber);
        observation = $"state={currentState},actionNumber={currentActionNumber}," +
                      $"expectedState={expectedState}," +
                      $"expectedActionNumber={expectedActionNumber}";
        return true;
    }
}

internal sealed class CompanionWorldSwitchAffordanceDriver :
    CompanionSwitchAffordanceDriver
{
    private readonly CastableTarget _castableTarget;
    private readonly int _castableInstanceId;
    private readonly CompanionKeyAffordance _keyAffordance;
    private readonly NetworkIdentity _networkIdentity;
    private readonly uint _networkId;
    private readonly ushort _switchTicket;
    private readonly uint _switchNetworkId;
    private readonly int _switchIndex;
    private readonly ushort _releaseSwitchTicket;
    private readonly uint _releaseSwitchNetworkId;
    private readonly int _releaseSwitchIndex;
    private readonly bool _requiresCompanionCarriedByHuman;

    private CompanionWorldSwitchAffordanceDriver(
        CastableTarget castableTarget,
        PeckSwitch peckSwitch,
        TrackedPeckState trackedState,
        CompanionKeyAffordance keyAffordance,
        CompanionAffordanceSource source,
        bool requiresCompanionCarriedByHuman)
        : base(peckSwitch, trackedState, peckSwitch.upSwitch, source)
    {
        _castableTarget = castableTarget;
        _castableInstanceId = castableTarget.GetInstanceID();
        _keyAffordance = keyAffordance;
        _requiresCompanionCarriedByHuman =
            requiresCompanionCarriedByHuman;

        var switchReference = peckSwitch.shellReference;
        _switchTicket = switchReference.ticket;
        _switchNetworkId = switchReference.netId;
        _switchIndex = switchReference.index;
        if (ReleaseSwitch != null)
        {
            var releaseReference = ReleaseSwitch.shellReference;
            _releaseSwitchTicket = releaseReference.ticket;
            _releaseSwitchNetworkId = releaseReference.netId;
            _releaseSwitchIndex = releaseReference.index;
        }
        _networkIdentity = peckSwitch.GetComponentInParent<NetworkIdentity>();
        _networkId = _networkIdentity == null ? 0u : _networkIdentity.netId;
        ReferenceId = _networkId == 0u
            ? $"switch:local:{SwitchInstanceId}"
            : $"switch:net:{_networkId}:instance:{SwitchInstanceId}";
    }

    internal static bool TryCreate(
        CastableTarget castableTarget,
        CastableOutcome outcome,
        CompanionActorContext actor,
        CompanionAffordanceSource source,
        out ICompanionAffordanceDriver driver,
        out string error)
    {
        return TryCreateWithRelationship(
            castableTarget,
            outcome,
            actor,
            source,
            false,
            out driver,
            out error);
    }

    private static bool TryCreateWithRelationship(
        CastableTarget castableTarget,
        CastableOutcome outcome,
        CompanionActorContext actor,
        CompanionAffordanceSource source,
        bool requiresCompanionCarriedByHuman,
        out ICompanionAffordanceDriver driver,
        out string error)
    {
        driver = null;
        var peckSwitch = outcome == null ? null : outcome.peckSwitch;
        var trackedState = peckSwitch == null
            ? null
            : peckSwitch.trackedStateSystem;
        if (!CompanionAffordanceDriverUtility.IsAvailable(peckSwitch) ||
            trackedState == null ||
            !CompanionAffordanceDriverUtility.HasValidWorldSwitchReference(
                peckSwitch) ||
            !CompanionAffordanceDriverUtility.HasValidReleaseSwitch(
                peckSwitch.upSwitch) ||
            (peckSwitch.upSwitch != null &&
             !CompanionAffordanceDriverUtility.HasValidWorldSwitchReference(
                 peckSwitch.upSwitch)))
        {
            error = "interaction_target_unavailable";
            return false;
        }

        CompanionKeyAffordance keyAffordance;
        if (!CompanionKeyAffordance.TryCapture(
                outcome,
                actor.Body,
                out keyAffordance,
                out error))
        {
            return false;
        }

        driver = new CompanionWorldSwitchAffordanceDriver(
            castableTarget,
            peckSwitch,
            trackedState,
            keyAffordance,
            source,
            requiresCompanionCarriedByHuman);
        error = null;
        return true;
    }

    internal static bool TryCreateCarriedFallback(
        CastableTarget castableTarget,
        CompanionActorContext actor,
        CompanionAffordanceSource source,
        out ICompanionAffordanceDriver driver,
        out string error)
    {
        driver = null;
        error = "interaction_conditions_unmet";
        var carriedByHuman = actor?.IsHumanCarryingCompanion == true;
        var body = actor?.Body;
        if (!CompanionAffordanceProtocol.IsGroundedCarriedSwitchSource(
                source == CompanionAffordanceSource.HumanReference,
                source == CompanionAffordanceSource.ContextEntity) ||
            !carriedByHuman || castableTarget == null ||
            body?.Character?.caster == null ||
            body.Character.decisions == null)
        {
            return false;
        }

        var structuralCount = 0;
        var constructibleCount = 0;
        var outcomeCount = 0;
        CastableOutcome selected = null;
        try
        {
            var outcomes = castableTarget.outcomes;
            outcomeCount = outcomes == null ? 0 : outcomes.Length;
            for (var index = 0; index < outcomeCount; index++)
            {
                var candidate = outcomes[index];
                if (candidate == null ||
                    !CompanionAffordanceProtocol.IsUnconditionedWorldSwitchOutcome(
                        candidate.peckSwitch != null,
                        candidate.playerPose != null,
                        candidate.propHome != null,
                        candidate.needsKey,
                        candidate.needsPocketProp))
                {
                    continue;
                }

                structuralCount++;
                var peckSwitch = candidate.peckSwitch;
                if (!CompanionAffordanceDriverUtility.IsAvailable(peckSwitch) ||
                    peckSwitch.trackedStateSystem == null ||
                    !CompanionAffordanceDriverUtility.HasValidWorldSwitchReference(
                        peckSwitch) ||
                    !CompanionAffordanceDriverUtility.HasValidReleaseSwitch(
                        peckSwitch.upSwitch) ||
                    (peckSwitch.upSwitch != null &&
                     !CompanionAffordanceDriverUtility.HasValidWorldSwitchReference(
                         peckSwitch.upSwitch)))
                {
                    continue;
                }

                constructibleCount++;
                selected = candidate;
            }
        }
        catch (System.Exception exception)
        {
            Plugin.Logger.LogWarning(
                $"[INTERACT] CARRIED_SWITCH_FALLBACK_SCAN_FAILED " +
                $"castableInstanceId={castableTarget.GetInstanceID()}, " +
                $"error={exception.GetType().Name}.");
            return false;
        }

        Plugin.Logger.LogInfo(
            $"[INTERACT] CARRIED_SWITCH_FALLBACK_SCANNED " +
            $"castableInstanceId={castableTarget.GetInstanceID()}, " +
            $"outcomes={outcomeCount}, structural={structuralCount}, " +
            $"constructible={constructibleCount}, " +
            $"carriedByBoundHuman={carriedByHuman}.");
        if (!CompanionAffordanceProtocol.CanSelectCarriedSwitchFallback(
                carriedByHuman,
                structuralCount,
                constructibleCount))
        {
            return false;
        }

        if (!TryCreateWithRelationship(
                castableTarget,
                selected,
                actor,
                source,
                true,
                out driver,
                out error))
        {
            return false;
        }

        Plugin.Logger.LogInfo(
            $"[INTERACT] CARRIED_SWITCH_FALLBACK_CAPTURED " +
            $"referenceId={driver.ReferenceId}, " +
            $"castableInstanceId={castableTarget.GetInstanceID()}.");
        try
        {
            var selectedSwitch = selected.peckSwitch;
            var crosshair = selectedSwitch.GetCrosshairTransform();
            if (crosshair == null)
                crosshair = castableTarget.GetCrosshairTransform();
            if (crosshair == null)
                crosshair = selectedSwitch.transform;
            var directionalReach = crosshair != null &&
                CompanionAffordanceDriverUtility.IsWithinNativeReach(
                    actor,
                    crosshair.position);
            var legacyReach = body.Character.caster.CanStillReachSwitch(
                selectedSwitch);
            var safe = body.Character.decisions.IsSafeToUseSwitch(
                selectedSwitch);
            Plugin.Logger.LogInfo(
                $"[INTERACT] CARRIED_SWITCH_REACH_SNAPSHOT " +
                $"referenceId={driver.ReferenceId}, " +
                $"directionalReach={directionalReach}, " +
                $"currentCasterReach={legacyReach}, safe={safe}.");
        }
        catch (System.Exception exception)
        {
            Plugin.Logger.LogInfo(
                $"[INTERACT] CARRIED_SWITCH_REACH_SNAPSHOT " +
                $"referenceId={driver.ReferenceId}, unavailable=" +
                $"{exception.GetType().Name}.");
        }
        return true;
    }

    public override CompanionAffordanceKind Kind =>
        CompanionAffordanceKind.WorldSwitch;
    public override string KindLabel => "world_switch";
    public override string ReferenceId { get; }
    public override uint NetworkId => _networkId;
    public override bool IsWorldTarget => true;

    public override bool TryGetCurrentPoint(
        CompanionActorContext actor,
        out Vector3 point,
        out string error)
    {
        point = Vector3.zero;
        if (!TryValidateExactComponents(actor, false, out error))
            return false;

        var crosshair = PeckSwitch.GetCrosshairTransform();
        if (crosshair == null)
            crosshair = _castableTarget.GetCrosshairTransform();
        if (crosshair == null)
            crosshair = PeckSwitch.transform;
        if (crosshair == null)
        {
            error = "interaction_point_unavailable";
            return false;
        }
        point = crosshair.position;
        return true;
    }

    public override string DescribeActivation(
        CompanionAffordanceActivation activation)
    {
        var switchActivation =
            activation as CompanionSwitchAffordanceActivation;
        return $"intent={switchActivation?.Intent.ToString().ToLowerInvariant() ?? "use"}," +
               $"releaseConfigured={switchActivation?.HasReleaseSwitch == true}," +
               $"keyRequired={_keyAffordance != null}," +
               $"keyEffect={_keyAffordance?.HasNativeEffect == true}";
    }

    protected override bool TryValidateSourceIdentity(out string error)
    {
        if (!CompanionAffordanceTarget.IsExactWorldReference(
                _castableTarget,
                _castableInstanceId) ||
            !CompanionAffordanceDriverUtility.MatchesWorldSwitchReference(
                PeckSwitch,
                _switchTicket,
                _switchNetworkId,
                _switchIndex))
        {
            error = "interaction_target_changed";
            return false;
        }
        if (_networkId != 0u)
        {
            var identity = PeckSwitch.GetComponentInParent<NetworkIdentity>();
            if (identity == null || identity != _networkIdentity ||
                identity.netId != _networkId)
            {
                error = "interaction_target_changed";
                return false;
            }
        }
        error = null;
        return true;
    }

    protected override bool TryValidateOutcome(
        CompanionActorContext actor,
        out string error)
    {
        if (_requiresCompanionCarriedByHuman)
            return TryValidateCarriedSwitchFallback(actor, out error);

        error = null;
        return true;
    }

    protected override bool TryValidateBoundRelationship(
        CompanionActorContext actor,
        out string error)
    {
        if (_requiresCompanionCarriedByHuman &&
            actor?.IsHumanCarryingCompanion != true)
        {
            error = "interaction_conditions_changed";
            return false;
        }
        error = null;
        return true;
    }

    protected override bool TryValidateUsePreconditions(
        CompanionActorContext actor,
        bool requireWorldReach,
        bool validatePrerequisite,
        out string error)
    {
        var body = actor.Body;
        if (body.Character.caster == null ||
            (requireWorldReach &&
             !body.Character.caster.CanStillReachSwitch(PeckSwitch)))
        {
            error = "interaction_out_of_reach";
            return false;
        }
        if (body.Character.decisions == null)
        {
            error = "interaction_system_unavailable";
            return false;
        }
        if (!body.Character.decisions.IsSafeToUseSwitch(PeckSwitch))
        {
            error = "interaction_blocked";
            return false;
        }
        if (validatePrerequisite && _keyAffordance != null &&
            !_keyAffordance.TryValidate(body, out error))
        {
            return false;
        }
        error = null;
        return true;
    }

    protected override bool HasNativePrerequisite =>
        _keyAffordance?.HasNativeEffect == true;

    protected override bool TryDispatchPrerequisite(
        CompanionActorContext actor,
        CompanionSwitchAffordanceActivation activation,
        out string error)
    {
        if (_keyAffordance == null)
        {
            error = null;
            return true;
        }
        return _keyAffordance.TryDispatch(
            actor.Body,
            activation,
            out error);
    }

    protected override bool TryProgressPrerequisite(
        CompanionSwitchAffordanceActivation activation,
        out bool observed,
        out string observation,
        out string error)
    {
        if (_keyAffordance == null)
        {
            observed = true;
            observation = "keyEffect=none";
            error = null;
            return true;
        }
        return _keyAffordance.TryProgress(
            activation,
            out observed,
            out observation,
            out error);
    }

    protected override bool TryValidateReleaseActor(
        CompanionActorContext actor,
        out string error)
    {
        error = null;
        return true;
    }

    protected override Prop GetSwitchContextProp(CompanionActorContext actor)
    {
        return actor.Body.Character.hands?.heldProp;
    }

    protected override Prop GetReleaseContextProp(CompanionActorContext actor)
    {
        return actor.Body.Character.hands?.heldProp;
    }

    protected override PeckSwitch GetCurrentReleaseSwitch()
    {
        return PeckSwitch?.upSwitch;
    }

    protected override bool TryValidateWorldReleaseReference(out string error)
    {
        if (ReleaseSwitch != null &&
            !CompanionAffordanceDriverUtility.MatchesWorldSwitchReference(
                ReleaseSwitch,
                _releaseSwitchTicket,
                _releaseSwitchNetworkId,
                _releaseSwitchIndex))
        {
            error = "interaction_target_changed";
            return false;
        }
        error = null;
        return true;
    }

    protected override void ReadAndAdvanceActionNumber(
        CompanionActorContext actor,
        int previousReceiptActionNumber,
        out int actionNumber)
    {
        var actions = actor.Body.Character.actions;
        actionNumber = CompanionAffordanceProtocol.NextDistinctActionNumber(
            actions.switchActionNumber,
            previousReceiptActionNumber);
        actions.switchActionNumber = actionNumber;
    }

    protected override void DispatchNativeSwitch(
        CompanionActorContext actor,
        PeckSwitch peckSwitch,
        PeckContext context,
        int actionNumber,
        bool isRelease)
    {
        if (isRelease)
        {
            actor.Body.Networking
                .UserCode_CmdReleaseHeldSwitch__ShellReference__PeckContext__Int32(
                    peckSwitch.shellReference,
                    context,
                    actionNumber);
            return;
        }
        actor.Body.Networking
            .UserCode_CmdUsePeckSwitch__ShellReference__PeckContext__Int32(
                peckSwitch.shellReference,
                context,
                actionNumber);
    }

    private bool TryValidateCarriedSwitchFallback(
        CompanionActorContext actor,
        out string error)
    {
        error = null;
        var carriedByHuman = actor?.IsHumanCarryingCompanion == true;
        var scanCompleted = false;
        var structuralCount = 0;
        var exactSwitchRetained = false;
        try
        {
            var outcomes = _castableTarget == null
                ? null
                : _castableTarget.outcomes;
            var outcomeCount = outcomes == null ? 0 : outcomes.Length;
            for (var index = 0; index < outcomeCount; index++)
            {
                var candidate = outcomes[index];
                if (candidate == null ||
                    !CompanionAffordanceProtocol.IsUnconditionedWorldSwitchOutcome(
                        candidate.peckSwitch != null,
                        candidate.playerPose != null,
                        candidate.propHome != null,
                        candidate.needsKey,
                        candidate.needsPocketProp))
                {
                    continue;
                }

                structuralCount++;
                exactSwitchRetained |= candidate.peckSwitch == PeckSwitch &&
                                       candidate.peckSwitch.GetInstanceID() ==
                                       SwitchInstanceId;
            }
            scanCompleted = true;
        }
        catch (System.Exception exception)
        {
            Plugin.Logger.LogWarning(
                $"[INTERACT] CARRIED_SWITCH_FALLBACK_REVALIDATION_FAILED " +
                $"referenceId={ReferenceId}, " +
                $"error={exception.GetType().Name}.");
        }

        if (!CompanionAffordanceProtocol.CanRetainCarriedSwitchFallback(
                carriedByHuman,
                scanCompleted,
                structuralCount,
                exactSwitchRetained))
        {
            error = "interaction_conditions_changed";
            return false;
        }
        return true;
    }
}

internal sealed class CompanionHeldItemSwitchAffordanceDriver :
    CompanionSwitchAffordanceDriver
{
    private readonly Prop _heldProp;
    private readonly int _heldPropInstanceId;
    private readonly NetworkIdentity _networkIdentity;
    private readonly uint _networkId;

    private CompanionHeldItemSwitchAffordanceDriver(
        Prop heldProp,
        PeckSwitch peckSwitch,
        TrackedPeckState trackedState)
        : base(
            peckSwitch,
            trackedState,
            heldProp.useHeldUpSwitch,
            CompanionAffordanceSource.CompanionHeldItem)
    {
        _heldProp = heldProp;
        _heldPropInstanceId = heldProp.GetInstanceID();
        _networkIdentity = heldProp.GetComponentInParent<NetworkIdentity>();
        _networkId = _networkIdentity == null ? 0u : _networkIdentity.netId;
        ReferenceId = _networkId == 0u
            ? $"held_prop:local:{_heldPropInstanceId}"
            : $"held_prop:net:{_networkId}:instance:{_heldPropInstanceId}";
    }

    internal static bool TryCreate(
        CompanionActorContext actor,
        CompanionPropTarget exactProp,
        out ICompanionAffordanceDriver driver,
        out string error)
    {
        driver = null;
        var body = actor?.Body;
        if (body == null || !body.IsAlive || body.Character.hands == null)
        {
            error = "bot_not_spawned";
            return false;
        }

        var heldProp = body.Character.hands.heldProp;
        if (heldProp == null || heldProp.gameObject == null ||
            !heldProp.gameObject.activeInHierarchy)
        {
            error = "companion_held_item_unavailable";
            return false;
        }
        if (exactProp != null && !exactProp.IsStillTheSameProp(heldProp))
        {
            error = "companion_held_item_changed";
            return false;
        }

        var peckSwitch = heldProp.useHeldSwitch;
        if (peckSwitch == null)
        {
            error = "companion_held_item_not_interactable";
            return false;
        }
        var trackedState = peckSwitch.trackedStateSystem;
        if (!CompanionAffordanceDriverUtility.IsAvailable(peckSwitch) ||
            trackedState == null ||
            !CompanionAffordanceDriverUtility.HasValidReleaseSwitch(
                heldProp.useHeldUpSwitch))
        {
            error = "interaction_target_unavailable";
            return false;
        }

        driver = new CompanionHeldItemSwitchAffordanceDriver(
            heldProp,
            peckSwitch,
            trackedState);
        error = null;
        return true;
    }

    public override CompanionAffordanceKind Kind =>
        CompanionAffordanceKind.HeldItemSwitch;
    public override string KindLabel => "held_item_switch";
    public override string ReferenceId { get; }
    public override uint NetworkId => _networkId;
    public override bool IsWorldTarget => false;

    public override bool TryGetCurrentPoint(
        CompanionActorContext actor,
        out Vector3 point,
        out string error)
    {
        point = Vector3.zero;
        if (!TryValidateExactComponents(actor, false, out error))
            return false;
        var crosshair = _heldProp.GetCrosshairTransform();
        if (crosshair == null)
            crosshair = PeckSwitch.GetCrosshairTransform();
        if (crosshair == null)
            crosshair = PeckSwitch.transform;
        if (crosshair == null)
        {
            error = "interaction_point_unavailable";
            return false;
        }
        point = crosshair.position;
        return true;
    }

    public override string DescribeActivation(
        CompanionAffordanceActivation activation)
    {
        var switchActivation =
            activation as CompanionSwitchAffordanceActivation;
        return $"intent={switchActivation?.Intent.ToString().ToLowerInvariant() ?? "use"}," +
               $"releaseConfigured={switchActivation?.HasReleaseSwitch == true}," +
               "keyRequired=False,keyEffect=False";
    }

    protected override bool TryValidateSourceIdentity(out string error)
    {
        if (_heldProp == null || _heldProp.gameObject == null ||
            !_heldProp.gameObject.activeInHierarchy ||
            _heldProp.GetInstanceID() != _heldPropInstanceId ||
            _heldProp.useHeldSwitch == null ||
            _heldProp.useHeldSwitch.GetInstanceID() != SwitchInstanceId ||
            _heldProp.useHeldSwitch != PeckSwitch)
        {
            error = "interaction_target_changed";
            return false;
        }
        if (_networkIdentity == null)
        {
            var identity = _heldProp.GetComponentInParent<NetworkIdentity>();
            if (identity != null || _networkId != 0u)
            {
                error = "interaction_target_changed";
                return false;
            }
        }
        else
        {
            var identity = _heldProp.GetComponentInParent<NetworkIdentity>();
            if (identity != _networkIdentity || identity == null ||
                identity.netId != _networkId)
            {
                error = "interaction_target_changed";
                return false;
            }
        }
        error = null;
        return true;
    }

    protected override bool TryValidateOutcome(
        CompanionActorContext actor,
        out string error)
    {
        error = null;
        return true;
    }

    protected override bool TryValidateBoundRelationship(
        CompanionActorContext actor,
        out string error)
    {
        error = null;
        return true;
    }

    protected override bool TryValidateUsePreconditions(
        CompanionActorContext actor,
        bool requireWorldReach,
        bool validatePrerequisite,
        out string error)
    {
        if (!IsStillHeldBy(actor))
        {
            error = "companion_held_item_changed";
            return false;
        }
        if (!PeckSwitch.isNotBlocked)
        {
            error = "interaction_blocked";
            return false;
        }
        error = null;
        return true;
    }

    protected override bool HasNativePrerequisite => false;

    protected override bool TryDispatchPrerequisite(
        CompanionActorContext actor,
        CompanionSwitchAffordanceActivation activation,
        out string error)
    {
        error = null;
        return true;
    }

    protected override bool TryProgressPrerequisite(
        CompanionSwitchAffordanceActivation activation,
        out bool observed,
        out string observation,
        out string error)
    {
        observed = true;
        observation = "keyEffect=none";
        error = null;
        return true;
    }

    protected override bool TryValidateReleaseActor(
        CompanionActorContext actor,
        out string error)
    {
        if (!IsStillHeldBy(actor))
        {
            error = "companion_held_item_changed";
            return false;
        }
        error = null;
        return true;
    }

    protected override Prop GetSwitchContextProp(CompanionActorContext actor)
    {
        return _heldProp;
    }

    protected override Prop GetReleaseContextProp(CompanionActorContext actor)
    {
        return _heldProp;
    }

    protected override PeckSwitch GetCurrentReleaseSwitch()
    {
        return _heldProp?.useHeldUpSwitch;
    }

    protected override void ReadAndAdvanceActionNumber(
        CompanionActorContext actor,
        int previousReceiptActionNumber,
        out int actionNumber)
    {
        var actions = actor.Body.Character.actions;
        actionNumber = CompanionAffordanceProtocol.NextDistinctActionNumber(
            actions.heldSwitchActionNumber,
            previousReceiptActionNumber);
        actions.heldSwitchActionNumber = actionNumber;
    }

    protected override void DispatchNativeSwitch(
        CompanionActorContext actor,
        PeckSwitch peckSwitch,
        PeckContext context,
        int actionNumber,
        bool isRelease)
    {
        if (isRelease)
            actor.Body.Networking.UserCode_CmdUseHeldUp__PeckContext(context);
        else
            actor.Body.Networking.UserCode_CmdUseHeld__PeckContext(context);
    }

    private bool IsStillHeldBy(CompanionActorContext actor)
    {
        var body = actor?.Body;
        if (_heldProp == null || body == null || !body.IsAlive ||
            body.Character.hands == null)
        {
            return false;
        }
        var current = body.Character.hands.heldProp;
        if (current == null || current != _heldProp ||
            current.GetInstanceID() != _heldPropInstanceId)
        {
            return false;
        }
        if (_networkIdentity == null)
            return _networkId == 0u;
        var identity = current.GetComponentInParent<NetworkIdentity>();
        return identity == _networkIdentity && identity != null &&
               identity.netId == _networkId;
    }
}
