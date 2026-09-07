using UnityEngine;

namespace Ramblers;

internal enum CompanionAffordanceSource
{
    HumanReference,
    CompanionHeldItem,
    ContextEntity
}

internal enum CompanionAffordanceKind
{
    WorldSwitch,
    HeldItemSwitch,
    PlayerPose,
    PropHome
}

internal sealed class CompanionAffordanceCandidates
{
    private readonly CompanionActorContext _actor;
    private readonly CastableTarget _humanCastableTarget;
    private readonly int _humanCastableInstanceId;
    private readonly CompanionAffordanceTarget _humanReference;
    private readonly CompanionAffordanceTarget _companionHeldItem;

    private CompanionAffordanceCandidates(
        CompanionActorContext actor,
        CastableTarget humanCastableTarget,
        CompanionAffordanceTarget humanReference,
        string humanReferenceError,
        CompanionAffordanceTarget companionHeldItem,
        string companionHeldItemError)
    {
        _actor = actor;
        _humanCastableTarget = humanCastableTarget;
        _humanCastableInstanceId = humanCastableTarget == null
            ? 0
            : humanCastableTarget.GetInstanceID();
        _humanReference = humanReference;
        HumanReferenceError = humanReferenceError;
        _companionHeldItem = companionHeldItem;
        CompanionHeldItemError = companionHeldItemError;
    }

    internal bool HumanReferenceAvailable => _humanReference != null;
    internal string HumanReferenceError { get; }
    internal string HumanReferenceId =>
        _humanReference == null ? "none" : _humanReference.ReferenceId;
    internal uint HumanReferenceNetworkId =>
        _humanReference == null ? 0u : _humanReference.NetworkId;
    internal string HumanReferenceKind =>
        _humanReference == null
            ? _humanCastableTarget == null
                ? "none"
                : "deferred_world_affordance"
            : _humanReference.KindLabel;
    internal bool CompanionHeldItemAvailable => _companionHeldItem != null;
    internal string CompanionHeldItemError { get; }
    internal string CompanionHeldItemReferenceId =>
        _companionHeldItem == null ? "none" : _companionHeldItem.ReferenceId;
    internal uint CompanionHeldItemNetworkId =>
        _companionHeldItem == null ? 0u : _companionHeldItem.NetworkId;
    internal string CompanionHeldItemKind =>
        _companionHeldItem == null ? "none" : _companionHeldItem.KindLabel;

    internal bool CanResolveContextReferences(CompanionBody body)
    {
        string error;
        return _actor != null && _actor.TryValidateBody(body, out error);
    }

    internal bool TryGetExactHumanWorldReference(
        out CastableTarget castableTarget,
        out int frozenInstanceId)
    {
        castableTarget = _humanCastableTarget;
        frozenInstanceId = _humanCastableInstanceId;
        return castableTarget != null && frozenInstanceId != 0;
    }

    internal static bool TryCapture(
        PlayerCharacter human,
        CompanionBody body,
        out CompanionAffordanceCandidates candidates,
        out string error)
    {
        candidates = null;
        error = null;
        if (human == null)
        {
            error = "human_player_unavailable";
            return false;
        }
        if (body == null || !body.IsAlive)
        {
            error = "bot_not_spawned";
            return false;
        }

        var actor = new CompanionActorContext(body, human);
        CastableTarget humanCastableTarget;
        string humanCastableError;
        CompanionAffordanceTarget.TryCaptureHumanReferenceIdentity(
            human,
            body,
            out humanCastableTarget,
            out humanCastableError);

        CompanionAffordanceTarget humanReference = null;
        var humanReferenceError = humanCastableError;
        if (humanCastableTarget != null)
        {
            CompanionAffordanceTarget.TryCaptureWorldOutcome(
                humanCastableTarget,
                actor,
                CompanionAffordanceSource.HumanReference,
                out humanReference,
                out humanReferenceError);
        }
        CompanionReferenceDiagnostics.LogBinding(
            humanCastableTarget,
            humanReference,
            humanReferenceError);

        CompanionAffordanceTarget companionHeldItem;
        string companionHeldItemError;
        CompanionAffordanceTarget.TryCaptureCompanionHeldItem(
            actor,
            null,
            out companionHeldItem,
            out companionHeldItemError);

        candidates = new CompanionAffordanceCandidates(
            actor,
            humanCastableTarget,
            humanReference,
            humanReferenceError,
            companionHeldItem,
            companionHeldItemError);
        return true;
    }

    internal static bool TryCaptureAmbient(
        PlayerCharacter human,
        CompanionBody body,
        out CompanionAffordanceCandidates candidates,
        out string error)
    {
        candidates = null;
        error = null;
        if (human == null)
        {
            error = "human_player_unavailable";
            return false;
        }
        if (body == null || !body.IsAlive)
        {
            error = "bot_not_spawned";
            return false;
        }

        var actor = new CompanionActorContext(body, human);
        CompanionAffordanceTarget companionHeldItem;
        string companionHeldItemError;
        CompanionAffordanceTarget.TryCaptureCompanionHeldItem(
            actor,
            null,
            out companionHeldItem,
            out companionHeldItemError);
        candidates = new CompanionAffordanceCandidates(
            actor,
            null,
            null,
            "ambient_capture_without_human_reference",
            companionHeldItem,
            companionHeldItemError);
        return true;
    }

    internal bool TrySelect(
        CompanionAffordanceSource source,
        out CompanionAffordanceTarget target,
        out string error)
    {
        if (source == CompanionAffordanceSource.CompanionHeldItem)
        {
            target = _companionHeldItem;
            error = target == null
                ? CompanionHeldItemError ?? "companion_held_item_unavailable"
                : null;
            return target != null;
        }
        if (source != CompanionAffordanceSource.HumanReference)
        {
            target = null;
            error = "interaction_reference_unavailable";
            return false;
        }

        target = null;
        if (_humanCastableTarget == null)
        {
            error = HumanReferenceError ?? "human_reference_not_interactable";
            return false;
        }
        if (!CompanionAffordanceTarget.IsExactWorldReference(
                _humanCastableTarget,
                _humanCastableInstanceId))
        {
            error = "interaction_target_changed";
            return false;
        }

        target = _humanReference;
        error = target == null
            ? HumanReferenceError ?? "human_reference_not_interactable"
            : null;
        return target != null;
    }

    internal bool TrySelectExactWorldReference(
        CompanionAffordanceSource source,
        CastableTarget castableTarget,
        int frozenInstanceId,
        out CompanionAffordanceTarget target,
        out string error)
    {
        target = null;
        if (source != CompanionAffordanceSource.ContextEntity)
        {
            error = "interaction_reference_unavailable";
            return false;
        }
        if (!CompanionAffordanceTarget.IsExactWorldReference(
                castableTarget,
                frozenInstanceId))
        {
            error = "interaction_target_changed";
            return false;
        }
        string dynamicError;
        if (CompanionAffordanceTarget.TryCaptureDynamicWorldOutcome(
                castableTarget,
                _actor,
                source,
                out target,
                out dynamicError))
        {
            error = null;
            return true;
        }

        string structuralError;
        if (CompanionAffordanceTarget.TryCaptureStructuralWorldOutcome(
                castableTarget,
                _actor,
                source,
                out target,
                out structuralError))
        {
            error = null;
            return true;
        }

        target = null;
        error = dynamicError ?? structuralError ??
                "interaction_conditions_unmet";
        return false;
    }

    internal bool TryWithCompanionHeldProp(
        CompanionBody body,
        CompanionPropTarget exactProp,
        out CompanionAffordanceCandidates advanced,
        out string error)
    {
        advanced = null;
        if (!_actor.TryValidateBody(body, out error))
            return false;
        var heldProp = body.Character.hands == null
            ? null
            : body.Character.hands.heldProp;
        if (exactProp == null || heldProp == null ||
            !exactProp.IsStillTheSameProp(heldProp))
        {
            error = "turn_state_transition_not_confirmed";
            return false;
        }

        CompanionAffordanceTarget heldItem;
        string heldError;
        CompanionAffordanceTarget.TryCaptureCompanionHeldItem(
            _actor,
            exactProp,
            out heldItem,
            out heldError);

        CompanionAffordanceTarget humanReference;
        string humanError;
        RebuildHumanReferenceAfterStateTransition(
            out humanReference,
            out humanError);

        advanced = new CompanionAffordanceCandidates(
            _actor,
            _humanCastableTarget,
            humanReference,
            humanError,
            heldItem,
            heldError);
        return true;
    }

    internal CompanionAffordanceCandidates WithoutCompanionHeldProp(
        string heldError)
    {
        CompanionAffordanceTarget humanReference;
        string humanError;
        RebuildHumanReferenceAfterStateTransition(
            out humanReference,
            out humanError);
        return new CompanionAffordanceCandidates(
            _actor,
            _humanCastableTarget,
            humanReference,
            humanError,
            null,
            heldError ?? "companion_held_item_unavailable");
    }

    private void RebuildHumanReferenceAfterStateTransition(
        out CompanionAffordanceTarget humanReference,
        out string humanError)
    {
        humanReference = null;
        humanError = HumanReferenceError;
        if (_humanCastableTarget == null)
            return;
        if (!CompanionAffordanceTarget.IsExactWorldReference(
                _humanCastableTarget,
                _humanCastableInstanceId))
        {
            humanError = "interaction_target_changed";
            return;
        }
        CompanionAffordanceTarget.TryCaptureWorldOutcome(
            _humanCastableTarget,
            _actor,
            CompanionAffordanceSource.HumanReference,
            out humanReference,
            out humanError);
    }
}

internal sealed class CompanionAffordanceTarget
{
    private const float HumanGazeCastDistance =
        CompanionPropTarget.MaximumHumanReferenceDistance;

    private readonly CompanionActorContext _actor;
    private readonly ICompanionAffordanceDriver _driver;

    private CompanionAffordanceTarget(
        CompanionActorContext actor,
        ICompanionAffordanceDriver driver)
    {
        _actor = actor;
        _driver = driver;
    }

    internal string ReferenceId => _driver.ReferenceId;
    internal uint NetworkId => _driver.NetworkId;
    internal CompanionAffordanceKind Kind => _driver.Kind;
    internal string KindLabel => _driver.KindLabel;
    internal string SourceLabel =>
        _driver.Source == CompanionAffordanceSource.CompanionHeldItem
            ? "companion_held_item"
            : _driver.Source == CompanionAffordanceSource.ContextEntity
                ? "context_entity"
                : "human_reference";
    internal bool IsWorldTarget => _driver.IsWorldTarget;

    internal CompanionAffordanceReadiness GetReadiness(
        CompanionBody body,
        CompanionInteractionIntent intent)
    {
        string error;
        return !_actor.TryValidateBody(body, out error)
            ? CompanionAffordanceReadiness.Unavailable(error)
            : _driver.GetReadiness(_actor, intent);
    }

    internal bool TryGetCurrentPoint(out Vector3 point, out string error)
    {
        return _driver.TryGetCurrentPoint(_actor, out point, out error);
    }

    internal bool TryActivate(
        CompanionBody body,
        CompanionAffordanceActivation activation,
        float now,
        out string error)
    {
        if (!_actor.TryValidateBody(body, out error))
            return false;
        return _driver.TryActivate(_actor, activation, now, out error);
    }

    internal bool TryProgressActivation(
        CompanionBody body,
        CompanionAffordanceActivation activation,
        float now,
        out bool observed,
        out string observation,
        out string error)
    {
        if (!_actor.TryValidateBody(body, out error))
        {
            observed = false;
            observation = "none";
            return false;
        }
        return _driver.TryProgressActivation(
            _actor,
            activation,
            now,
            out observed,
            out observation,
            out error);
    }

    internal string SuccessState(CompanionAffordanceActivation activation)
    {
        return _driver.SuccessState(activation);
    }

    internal string DescribeActivation(CompanionAffordanceActivation activation)
    {
        return _driver.DescribeActivation(activation);
    }

    internal static bool TryCaptureHumanReference(
        PlayerCharacter human,
        CompanionBody body,
        out CompanionAffordanceTarget target,
        out string error)
    {
        target = null;
        CastableTarget castableTarget;
        if (!TryCaptureHumanReferenceIdentity(
                human,
                body,
                out castableTarget,
                out error))
        {
            return false;
        }
        return TryCaptureWorldOutcome(
            castableTarget,
            new CompanionActorContext(body, human),
            CompanionAffordanceSource.HumanReference,
            out target,
            out error);
    }

    internal static bool TryCaptureHumanReferenceIdentity(
        PlayerCharacter human,
        CompanionBody body,
        out CastableTarget castableTarget,
        out string error)
    {
        castableTarget = null;
        error = null;
        if (human == null || human.caster == null)
        {
            error = "human_interaction_reference_unavailable";
            return false;
        }
        if (body == null || !body.IsAlive)
        {
            error = "bot_not_spawned";
            return false;
        }

        castableTarget = human.caster.castableTarget;
        if (CompanionAffordanceDriverUtility.IsAvailable(castableTarget))
        {
            CompanionReferenceDiagnostics.LogGaze(
                human, body, "stock_castable", null, castableTarget, null);
            Plugin.Logger.LogInfo(
                $"[INTERACT] WORLD_REFERENCE_CAPTURED source=stock_castable," +
                $" instanceId={castableTarget.GetInstanceID()}.");
            return true;
        }

        var view = ResolveHumanViewTransform(human);
        if (view == null || view.forward.sqrMagnitude < 0.0001f)
        {
            error = "human_view_unavailable";
            CompanionReferenceDiagnostics.LogGaze(
                human, body, "extended_gaze", null, null, error);
            return false;
        }

        GameObject hitObject = null;
        try
        {
            hitObject = human.caster.CastThroughHands(
                new Ray(view.position, view.forward.normalized),
                HumanGazeCastDistance);
            castableTarget = hitObject == null
                ? null
                : hitObject.GetComponentInParent<CastableTarget>();
        }
        catch (System.Exception exception)
        {
            Plugin.Logger.LogWarning(
                $"[INTERACT] WORLD_REFERENCE_CAPTURE_FAILED " +
                $"source=extended_gaze, error={exception.Message}");
            error = "human_interaction_reference_unavailable";
            CompanionReferenceDiagnostics.LogGaze(
                human, body, "extended_gaze", hitObject, castableTarget, error);
            return false;
        }

        if (!CompanionAffordanceDriverUtility.IsAvailable(castableTarget))
        {
            castableTarget = null;
            error = "human_reference_not_interactable";
            CompanionReferenceDiagnostics.LogGaze(
                human, body, "extended_gaze", hitObject, null, error);
            return false;
        }

        CompanionReferenceDiagnostics.LogGaze(
            human, body, "extended_gaze", hitObject, castableTarget, null);
        Plugin.Logger.LogInfo(
            $"[INTERACT] WORLD_REFERENCE_CAPTURED source=extended_gaze," +
            $" instanceId={castableTarget.GetInstanceID()}," +
            $" maxDistance={HumanGazeCastDistance:F1}.");
        return true;
    }

    internal static bool TryCaptureCompanionHeldItem(
        CompanionActorContext actor,
        CompanionPropTarget exactProp,
        out CompanionAffordanceTarget target,
        out string error)
    {
        target = null;
        ICompanionAffordanceDriver driver;
        if (!CompanionHeldItemSwitchAffordanceDriver.TryCreate(
                actor,
                exactProp,
                out driver,
                out error))
        {
            return false;
        }
        target = new CompanionAffordanceTarget(actor, driver);
        return true;
    }

    internal static bool TryCaptureWorldOutcome(
        CastableTarget castableTarget,
        CompanionActorContext actor,
        CompanionAffordanceSource source,
        out CompanionAffordanceTarget target,
        out string error)
    {
        return TryCaptureWorldOutcome(
            castableTarget,
            actor,
            source,
            true,
            out target,
            out error);
    }

    internal static bool TryCaptureDynamicWorldOutcome(
        CastableTarget castableTarget,
        CompanionActorContext actor,
        CompanionAffordanceSource source,
        out CompanionAffordanceTarget target,
        out string error)
    {
        return TryCaptureWorldOutcome(
            castableTarget,
            actor,
            source,
            false,
            out target,
            out error);
    }

    private static bool TryCaptureWorldOutcome(
        CastableTarget castableTarget,
        CompanionActorContext actor,
        CompanionAffordanceSource source,
        bool allowStructuralFallback,
        out CompanionAffordanceTarget target,
        out string error)
    {
        target = null;
        error = null;
        if (castableTarget == null || actor?.Body == null ||
            !actor.Body.IsAlive)
        {
            error = "interaction_target_unavailable";
            return false;
        }

        CastableOutcome outcome;
        if (!castableTarget.GetCastableOutcome(
                actor.Body.Character,
                out outcome) || outcome == null)
        {
            ICompanionAffordanceDriver fallbackDriver;
            if (CompanionWorldSwitchAffordanceDriver.TryCreateCarriedFallback(
                    castableTarget,
                    actor,
                    source,
                    out fallbackDriver,
                    out error))
            {
                target = new CompanionAffordanceTarget(actor, fallbackDriver);
                return true;
            }
            var carriedInteraction =
                actor.HumanCarryingCompanionAtCapture ||
                actor.IsHumanCarryingCompanion;
            if (allowStructuralFallback && !carriedInteraction &&
                (source == CompanionAffordanceSource.ContextEntity ||
                 source == CompanionAffordanceSource.HumanReference) &&
                TryCaptureStructuralWorldOutcome(
                    castableTarget,
                    actor,
                    source,
                    out target,
                    out error))
            {
                return true;
            }
            if (allowStructuralFallback && carriedInteraction)
            {
                Plugin.Logger.LogInfo(
                    $"[INTERACT] CARRIED_STRUCTURAL_APPROACH_BLOCKED " +
                    $"castableInstanceId={castableTarget.GetInstanceID()}, " +
                    $"source={source}.");
            }
            error = "interaction_conditions_unmet";
            return false;
        }

        ICompanionAffordanceDriver driver;
        if (!TryCreateDriverFromOutcome(
                castableTarget,
                outcome,
                actor,
                source,
                true,
                out driver,
                out error))
        {
            return false;
        }
        target = new CompanionAffordanceTarget(actor, driver);
        return true;
    }

    internal static bool TryCaptureStructuralWorldOutcome(
        CastableTarget castableTarget,
        CompanionActorContext actor,
        CompanionAffordanceSource source,
        out CompanionAffordanceTarget target,
        out string error)
    {
        target = null;
        error = "interaction_conditions_unmet";
        if (castableTarget == null || actor?.Body == null ||
            !actor.Body.IsAlive ||
            source == CompanionAffordanceSource.CompanionHeldItem)
        {
            return false;
        }

        var scanCompleted = false;
        var constructibleDriverCount = 0;
        var uncertainSupportedCount = 0;
        var outcomeCount = 0;
        ICompanionAffordanceDriver selectedDriver = null;
        try
        {
            var outcomes = castableTarget.outcomes;
            outcomeCount = outcomes == null ? 0 : outcomes.Length;
            for (var index = 0; index < outcomeCount; index++)
            {
                var candidate = outcomes[index];
                var hasSupportedPrimitive = candidate != null &&
                    (candidate.playerPose != null ||
                     candidate.propHome != null ||
                     candidate.peckSwitch != null);
                if (!hasSupportedPrimitive)
                    continue;
                ICompanionAffordanceDriver candidateDriver;
                string candidateError;
                if (!TryCreateDriverFromOutcome(
                        castableTarget,
                        candidate,
                        actor,
                        source,
                        false,
                        out candidateDriver,
                        out candidateError))
                {
                    uncertainSupportedCount++;
                    continue;
                }

                constructibleDriverCount++;
                selectedDriver = candidateDriver;
            }
            scanCompleted = true;
        }
        catch (System.Exception exception)
        {
            Plugin.Logger.LogWarning(
                $"[INTERACT] STRUCTURAL_AFFORDANCE_SCAN_FAILED " +
                $"castableInstanceId={castableTarget.GetInstanceID()}, " +
                $"source={source}, error={exception.GetType().Name}.");
        }

        Plugin.Logger.LogInfo(
            $"[INTERACT] STRUCTURAL_AFFORDANCE_SCANNED " +
            $"castableInstanceId={castableTarget.GetInstanceID()}, " +
            $"source={source}, outcomes={outcomeCount}, " +
            $"constructible={constructibleDriverCount}, " +
            $"uncertainSupported={uncertainSupportedCount}.");
        if (!CompanionAffordanceProtocol.CanSelectStructuralWorldAffordance(
                scanCompleted,
                constructibleDriverCount))
        {
            return false;
        }

        target = new CompanionAffordanceTarget(actor, selectedDriver);
        error = null;
        return true;
    }

    private static bool TryCreateDriverFromOutcome(
        CastableTarget castableTarget,
        CastableOutcome outcome,
        CompanionActorContext actor,
        CompanionAffordanceSource source,
        bool requireDynamicAdmissionAtCapture,
        out ICompanionAffordanceDriver driver,
        out string error)
    {
        driver = null;
        error = null;
        if (outcome == null)
        {
            error = "interaction_kind_unsupported";
            return false;
        }

        var selectedPrimitiveIsSwitch = outcome.playerPose == null &&
                                        outcome.propHome == null &&
                                        outcome.peckSwitch != null;
        if (!CompanionAffordanceProtocol.HasSupportedPrerequisites(
                outcome.needsKey,
                outcome.needsPocketProp,
                selectedPrimitiveIsSwitch))
        {
            error = outcome.needsPocketProp
                ? "interaction_requires_pocket_item"
                : "interaction_key_prerequisite_unsupported";
            return false;
        }

        if (outcome.playerPose != null)
        {
            if (!CompanionPlayerPoseAffordanceDriver.TryCreate(
                    castableTarget,
                    outcome.playerPose,
                    source,
                    out driver,
                    out error))
            {
                return false;
            }
        }
        else if (outcome.propHome != null)
        {
            if (!CompanionPropHomeAffordanceDriver.TryCreate(
                    castableTarget,
                    outcome.propHome,
                    actor,
                    source,
                    requireDynamicAdmissionAtCapture,
                    out driver,
                    out error))
            {
                return false;
            }
        }
        else if (outcome.peckSwitch != null)
        {
            if (!CompanionWorldSwitchAffordanceDriver.TryCreate(
                    castableTarget,
                    outcome,
                    actor,
                    source,
                    out driver,
                    out error))
            {
                return false;
            }
        }
        else
        {
            error = "interaction_kind_unsupported";
            return false;
        }

        return true;
    }

    internal static bool TryCaptureWorldOutcome(
        CastableTarget castableTarget,
        CompanionBody body,
        CompanionAffordanceSource source,
        out CompanionAffordanceTarget target,
        out string error)
    {
        if (source != CompanionAffordanceSource.ContextEntity)
        {
            target = null;
            error = "interaction_actor_context_unavailable";
            return false;
        }
        return TryCaptureStructuralWorldOutcome(
            castableTarget,
            new CompanionActorContext(body, null),
            source,
            out target,
            out error);
    }

    internal static bool IsExactWorldReference(
        CastableTarget castableTarget,
        int instanceId)
    {
        return CompanionAffordanceDriverUtility.IsAvailable(castableTarget) &&
               castableTarget.GetInstanceID() == instanceId;
    }

    internal static Transform ResolveHumanViewTransform(PlayerCharacter human)
    {
        if (human != null && human.cameraMinder != null)
        {
            var references = human.cameraMinder.playerCameraReferences;
            if (references != null && references.playerCamera != null)
                return references.playerCamera.transform;
        }
        return human == null ? null : human.cameraTransform;
    }
}
