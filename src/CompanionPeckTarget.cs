using Mirror;
using UnityEngine;

namespace Ramblers;

internal enum CompanionPeckSource
{
    HumanReference,
    CompanionHeldItem
}

/// <summary>
/// Every exact primary-interaction referent available when an utterance ends.
/// The model may resolve whether "it" means what the human indicated or the
/// prop already in the companion's hands, but execution never reacquires a
/// newer object.
/// </summary>
internal sealed class CompanionPeckCandidates
{
    private readonly CompanionPeckTarget _humanReference;
    private readonly CompanionPeckTarget _companionHeldItem;

    private CompanionPeckCandidates(
        CompanionPeckTarget humanReference,
        string humanReferenceError,
        CompanionPeckTarget companionHeldItem,
        string companionHeldItemError)
    {
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
    internal bool CompanionHeldItemAvailable => _companionHeldItem != null;
    internal string CompanionHeldItemError { get; }
    internal string CompanionHeldItemReferenceId =>
        _companionHeldItem == null ? "none" : _companionHeldItem.ReferenceId;
    internal uint CompanionHeldItemNetworkId =>
        _companionHeldItem == null ? 0u : _companionHeldItem.NetworkId;

    internal static bool TryCapture(
        PlayerCharacter human,
        CompanionBody body,
        out CompanionPeckCandidates candidates,
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

        CompanionPeckTarget humanReference;
        string humanReferenceError;
        CompanionPeckTarget.TryCaptureHumanReference(
            human,
            body,
            out humanReference,
            out humanReferenceError);

        CompanionPeckTarget companionHeldItem;
        string companionHeldItemError;
        CompanionPeckTarget.TryCaptureCompanionHeldItem(
            body,
            out companionHeldItem,
            out companionHeldItemError);

        candidates = new CompanionPeckCandidates(
            humanReference,
            humanReferenceError,
            companionHeldItem,
            companionHeldItemError);
        return true;
    }

    internal bool TrySelect(
        CompanionPeckSource source,
        out CompanionPeckTarget target,
        out string error)
    {
        if (source == CompanionPeckSource.CompanionHeldItem)
        {
            target = _companionHeldItem;
            error = target == null
                ? CompanionHeldItemError ?? "companion_held_item_unavailable"
                : null;
            return target != null;
        }

        target = _humanReference;
        error = target == null
            ? HumanReferenceError ?? "human_reference_not_interactable"
            : null;
        return target != null;
    }
}

/// <summary>
/// One exact primary-interaction switch frozen at an utterance boundary. The
/// game calls this interaction a peck; Ramblers keeps that implementation
/// detail below the model-facing "interact" tool.
/// </summary>
internal sealed class CompanionPeckTarget
{
    private readonly CastableTarget _castableTarget;
    private readonly Prop _heldProp;
    private readonly PeckSwitch _peckSwitch;
    private readonly TrackedPeckState _trackedState;
    private readonly CompanionPeckSource _source;
    private readonly int _castableInstanceId;
    private readonly int _heldPropInstanceId;
    private readonly int _switchInstanceId;
    private readonly int _stateInstanceId;
    private readonly NetworkIdentity _networkIdentity;
    private readonly uint _networkId;

    private CompanionPeckTarget(
        CastableTarget castableTarget,
        Prop heldProp,
        PeckSwitch peckSwitch,
        TrackedPeckState trackedState,
        CompanionPeckSource source)
    {
        _castableTarget = castableTarget;
        _heldProp = heldProp;
        _peckSwitch = peckSwitch;
        _trackedState = trackedState;
        _source = source;
        _castableInstanceId = castableTarget == null
            ? 0
            : castableTarget.GetInstanceID();
        _heldPropInstanceId = heldProp == null
            ? 0
            : heldProp.GetInstanceID();
        _switchInstanceId = peckSwitch.GetInstanceID();
        _stateInstanceId = trackedState.GetInstanceID();
        _networkIdentity = heldProp == null
            ? peckSwitch.GetComponentInParent<NetworkIdentity>()
            : heldProp.GetComponentInParent<NetworkIdentity>();
        _networkId = _networkIdentity == null ? 0u : _networkIdentity.netId;
        var prefix = source == CompanionPeckSource.CompanionHeldItem
            ? "held_prop"
            : "switch";
        ReferenceId = _networkId == 0u
            ? $"{prefix}:local:{TargetInstanceId}"
            : $"{prefix}:net:{_networkId}:instance:{TargetInstanceId}";
    }

    internal string ReferenceId { get; }

    internal uint NetworkId => _networkId;

    internal string SourceLabel =>
        _source == CompanionPeckSource.CompanionHeldItem
            ? "companion_held_item"
            : "human_reference";

    private int TargetInstanceId => _heldProp == null
        ? _switchInstanceId
        : _heldPropInstanceId;

    /// <summary>
    /// Freezes only the switch selected by Big Walk's own local cast. Outcome
    /// selection is evaluated for the companion, not borrowed from the human,
    /// so key/pose requirements cannot be bypassed at capture time.
    /// </summary>
    internal static bool TryCaptureHumanReference(
        PlayerCharacter human,
        CompanionBody body,
        out CompanionPeckTarget target,
        out string error)
    {
        target = null;
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

        var castableTarget = human.caster.castableTarget;
        if (castableTarget == null || castableTarget.gameObject == null ||
            !castableTarget.gameObject.activeInHierarchy)
        {
            error = "human_reference_not_interactable";
            return false;
        }

        CastableOutcome outcome;
        if (!castableTarget.GetCastableOutcome(body.Character, out outcome) ||
            outcome == null || outcome.peckSwitch == null)
        {
            error = "interaction_conditions_unmet";
            return false;
        }

        var peckSwitch = outcome.peckSwitch;
        var trackedState = peckSwitch.trackedStateSystem;
        if (peckSwitch.gameObject == null ||
            !peckSwitch.gameObject.activeInHierarchy || trackedState == null)
        {
            error = "interaction_target_unavailable";
            return false;
        }

        target = new CompanionPeckTarget(
            castableTarget,
            null,
            peckSwitch,
            trackedState,
            CompanionPeckSource.HumanReference);
        return true;
    }

    /// <summary>
    /// Freezes the prop and use switch already in the companion's hands. Big
    /// Walk routes primary click on held props through Prop.useHeldSwitch, not
    /// through PlayerCaster.castableTarget.
    /// </summary>
    internal static bool TryCaptureCompanionHeldItem(
        CompanionBody body,
        out CompanionPeckTarget target,
        out string error)
    {
        target = null;
        error = null;
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

        var peckSwitch = heldProp.useHeldSwitch;
        if (peckSwitch == null)
        {
            error = "companion_held_item_not_interactable";
            return false;
        }

        var trackedState = peckSwitch.trackedStateSystem;
        if (peckSwitch.gameObject == null ||
            !peckSwitch.gameObject.activeInHierarchy || trackedState == null)
        {
            error = "interaction_target_unavailable";
            return false;
        }

        target = new CompanionPeckTarget(
            null,
            heldProp,
            peckSwitch,
            trackedState,
            CompanionPeckSource.CompanionHeldItem);
        return true;
    }

    internal bool TryGetCurrentPoint(out Vector3 point, out string error)
    {
        point = Vector3.zero;
        error = null;
        if (!TryValidateExactComponents(out error))
            return false;

        var crosshair = _heldProp == null
            ? null
            : _heldProp.GetCrosshairTransform();
        if (crosshair == null)
            crosshair = _peckSwitch.GetCrosshairTransform();
        if (crosshair == null && _castableTarget != null)
            crosshair = _castableTarget.GetCrosshairTransform();
        if (crosshair == null)
            crosshair = _peckSwitch.transform;
        if (crosshair == null)
        {
            error = "interaction_point_unavailable";
            return false;
        }

        point = crosshair.position;
        return true;
    }

    /// <summary>
    /// Revalidates the frozen components, the companion-specific cast outcome,
    /// reach, blockers, and server authority immediately before mutation.
    /// </summary>
    internal bool TryPrepare(
        CompanionBody body,
        out CompanionPeckActivation activation,
        out string error)
    {
        activation = null;
        error = null;
        if (body == null || !body.IsAlive)
        {
            error = "bot_not_spawned";
            return false;
        }
        if (!TryValidateExactComponents(out error))
            return false;

        if (_source == CompanionPeckSource.HumanReference)
        {
            CastableOutcome outcome;
            if (!_castableTarget.GetCastableOutcome(body.Character, out outcome) ||
                outcome == null || outcome.peckSwitch == null ||
                outcome.peckSwitch.GetInstanceID() != _switchInstanceId)
            {
                error = "interaction_conditions_changed";
                return false;
            }
            if (body.Character.caster == null ||
                !body.Character.caster.CanStillReachSwitch(_peckSwitch))
            {
                error = "interaction_out_of_reach";
                return false;
            }
        }
        else if (!IsStillHeldBy(body))
        {
            error = "companion_held_item_changed";
            return false;
        }
        if (!_peckSwitch.isNotBlocked)
        {
            error = "interaction_blocked";
            return false;
        }
        if (!NetworkServer.active || !_trackedState.isServer)
        {
            error = "interaction_authority_unavailable";
            return false;
        }
        if (PeckManager.Instance == null ||
            !PeckManager.Instance.isReadyForEffects)
        {
            error = "interaction_system_unavailable";
            return false;
        }

        var previousContext = _trackedState.currentPeckContext;
        var previousState = previousContext == null
            ? _trackedState.initialState
            : previousContext.state;
        var previousActionNumber = previousContext == null
            ? -1
            : previousContext.actionNumber;
        var expectedState = _peckSwitch.GetNextState(previousState);
        var heldProp = _heldProp ?? (body.Character.hands == null
            ? null
            : body.Character.hands.heldProp);
        var context = new PeckContext(body.Character, heldProp)
        {
            state = expectedState
        };
        activation = new CompanionPeckActivation
        {
            Context = context,
            PreviousState = previousState,
            PreviousActionNumber = previousActionNumber,
            ExpectedState = expectedState
        };
        return true;
    }

    internal bool TryActivate(
        CompanionPeckActivation activation,
        out string error)
    {
        error = null;
        if (activation == null || activation.Context == null)
        {
            error = "interaction_plan_unavailable";
            return false;
        }
        if (!TryValidateExactComponents(out error))
            return false;

        try
        {
            // This is the authoritative server state path. Do not call the
            // client CmdUsePeckSwitch wrapper on a connectionless companion.
            PeckManager.SetState(_trackedState, activation.Context);
            return true;
        }
        catch (System.Exception exception)
        {
            error = "interaction_authority_failed";
            Plugin.Logger.LogWarning(
                $"[INTERACT] AUTHORITY_EXCEPTION referenceId={ReferenceId}, " +
                $"error={exception.Message}");
            return false;
        }
    }

    internal bool TryObserveActivation(
        CompanionPeckActivation activation,
        out bool observed,
        out int currentState,
        out int currentActionNumber,
        out string error)
    {
        observed = false;
        currentState = 0;
        currentActionNumber = -1;
        error = null;
        if (activation == null || !TryValidateExactComponents(out error))
            return false;

        var currentContext = _trackedState.currentPeckContext;
        if (currentContext == null)
        {
            currentState = _trackedState.initialState;
            return true;
        }

        currentState = currentContext.state;
        currentActionNumber = currentContext.actionNumber;
        observed = currentState == activation.ExpectedState &&
                   (activation.ExpectedState != activation.PreviousState ||
                    currentActionNumber != activation.PreviousActionNumber);
        return true;
    }

    private bool TryValidateExactComponents(out string error)
    {
        error = null;
        if (_peckSwitch == null || _trackedState == null ||
            _peckSwitch.gameObject == null || _trackedState.gameObject == null ||
            !_peckSwitch.gameObject.activeInHierarchy ||
            !_trackedState.gameObject.activeInHierarchy)
        {
            error = "interaction_target_unavailable";
            return false;
        }
        if (_peckSwitch.GetInstanceID() != _switchInstanceId ||
            _trackedState.GetInstanceID() != _stateInstanceId ||
            _peckSwitch.trackedStateSystem == null ||
            _peckSwitch.trackedStateSystem.GetInstanceID() != _stateInstanceId)
        {
            error = "interaction_target_changed";
            return false;
        }

        if (_source == CompanionPeckSource.HumanReference)
        {
            if (_castableTarget == null || _castableTarget.gameObject == null ||
                !_castableTarget.gameObject.activeInHierarchy ||
                _castableTarget.GetInstanceID() != _castableInstanceId)
            {
                error = "interaction_target_changed";
                return false;
            }
        }
        else if (_heldProp == null || _heldProp.gameObject == null ||
                 !_heldProp.gameObject.activeInHierarchy ||
                 _heldProp.GetInstanceID() != _heldPropInstanceId ||
                 _heldProp.useHeldSwitch == null ||
                 _heldProp.useHeldSwitch.GetInstanceID() != _switchInstanceId)
        {
            error = "interaction_target_changed";
            return false;
        }

        if (_networkId != 0u)
        {
            var identity = _heldProp == null
                ? _peckSwitch.GetComponentInParent<NetworkIdentity>()
                : _heldProp.GetComponentInParent<NetworkIdentity>();
            if (identity == null || identity != _networkIdentity ||
                identity.netId != _networkId)
            {
                error = "interaction_target_changed";
                return false;
            }
        }
        return true;
    }

    private bool IsStillHeldBy(CompanionBody body)
    {
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

internal sealed class CompanionPeckActivation
{
    internal PeckContext Context;
    internal int PreviousState;
    internal int PreviousActionNumber;
    internal int ExpectedState;
}
