using Mirror;

namespace Ramblers;

internal sealed class CompanionKeyAffordance
{
    private readonly Prop _prop;
    private readonly PeckSwitch _onUseAsKey;
    private readonly TrackedPeckState _trackedState;
    private readonly PropGroup _keyType;
    private readonly int _propInstanceId;
    private readonly int _switchInstanceId;
    private readonly int _stateInstanceId;
    private readonly NetworkIdentity _networkIdentity;
    private readonly uint _networkId;

    internal bool HasNativeEffect => _onUseAsKey != null;

    private CompanionKeyAffordance(
        Prop prop,
        PeckSwitch onUseAsKey,
        PropGroup keyType)
    {
        _prop = prop;
        _onUseAsKey = onUseAsKey;
        _trackedState = onUseAsKey?.trackedStateSystem;
        _keyType = keyType;
        _propInstanceId = prop.GetInstanceID();
        _switchInstanceId = onUseAsKey == null
            ? 0
            : onUseAsKey.GetInstanceID();
        _stateInstanceId = _trackedState == null
            ? 0
            : _trackedState.GetInstanceID();
        _networkIdentity = prop.GetComponentInParent<NetworkIdentity>();
        _networkId = _networkIdentity == null ? 0u : _networkIdentity.netId;
    }

    internal static bool TryCapture(
        CastableOutcome outcome,
        CompanionBody body,
        out CompanionKeyAffordance key,
        out string error)
    {
        key = null;
        error = null;
        if (outcome == null || !outcome.needsKey)
            return true;

        var prop = body?.Character?.hands?.heldProp;
        if (!IsAvailable(prop) || !prop.MatchesGroup(outcome.keyType))
        {
            error = "interaction_requires_key";
            return false;
        }

        var onUseAsKey = prop.onUseAsKey;
        if (onUseAsKey != null &&
            (!IsAvailable(onUseAsKey) ||
             onUseAsKey.trackedStateSystem == null))
        {
            error = "interaction_key_unavailable";
            return false;
        }

        key = new CompanionKeyAffordance(prop, onUseAsKey, outcome.keyType);
        return true;
    }

    internal bool MatchesOutcome(CastableOutcome outcome)
    {
        return outcome != null && outcome.needsKey &&
               outcome.keyType == _keyType;
    }

    internal bool TryValidate(CompanionBody body, out string error)
    {
        error = null;
        if (body == null || !body.IsAlive || body.Character.hands == null)
        {
            error = "interaction_system_unavailable";
            return false;
        }

        var current = body.Character.hands.heldProp;
        if (!IsAvailable(current) || current != _prop ||
            current.GetInstanceID() != _propInstanceId ||
            !current.MatchesGroup(_keyType))
        {
            error = "interaction_key_changed";
            return false;
        }

        var identity = current.GetComponentInParent<NetworkIdentity>();
        if (identity != _networkIdentity ||
            (identity != null && identity.netId != _networkId) ||
            (identity == null && _networkId != 0u))
        {
            error = "interaction_key_changed";
            return false;
        }

        if (current.onUseAsKey != _onUseAsKey)
        {
            error = "interaction_key_changed";
            return false;
        }
        if (_onUseAsKey == null)
            return true;

        if (!IsAvailable(_onUseAsKey) || _trackedState == null ||
            _onUseAsKey.GetInstanceID() != _switchInstanceId ||
            _onUseAsKey.trackedStateSystem != _trackedState ||
            _trackedState.GetInstanceID() != _stateInstanceId ||
            !_trackedState.isServer)
        {
            error = "interaction_key_changed";
            return false;
        }
        return true;
    }

    internal bool TryDispatch(
        CompanionBody body,
        CompanionSwitchAffordanceActivation activation,
        out string error)
    {
        if (activation == null)
        {
            error = "interaction_plan_unavailable";
            return false;
        }

        activation.HasNativeKeyEffect = HasNativeEffect;
        if (!TryValidate(body, out error))
            return false;
        if (_onUseAsKey == null)
            return true;

        var actions = body.Character.actions;
        if (actions == null)
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
        var actionNumber = CompanionAffordanceProtocol.NextDistinctActionNumber(
            actions.heldSwitchActionNumber,
            previousActionNumber);
        actions.heldSwitchActionNumber = actionNumber;
        var context = new PeckContext(body.Character, _prop)
        {
            state = _onUseAsKey.GetNextState(previousState),
            actionNumber = actionNumber
        };

        activation.KeyContext = context;
        activation.PreviousKeyState = previousState;
        activation.PreviousKeyActionNumber = previousActionNumber;
        activation.ExpectedKeyState = context.state;
        activation.ExpectedKeyActionNumber = context.actionNumber;
        activation.KeyEffectDispatched = true;
        activation.MarkAuthorityCrossed();
        body.Networking.UserCode_CmdUseHeldAsKey__PeckContext(context);
        activation.KeyEffectCommandReturned = true;
        activation.KeyEffectWasNativeNoOp =
            CompanionAffordanceProtocol.IsIgnoredNativeRepeat(
                _trackedState.ignoreStateRepeats,
                previousContext != null,
                previousState,
                context.state);
        activation.KeyEffectWasNativeRetrigger =
            CompanionAffordanceProtocol.IsNativeRetrigger(
                _trackedState.ignoreStateRepeats,
                previousContext != null,
                previousState,
                context.state);
        error = null;
        return true;
    }

    internal bool TryProgress(
        CompanionSwitchAffordanceActivation activation,
        out bool observed,
        out string observation,
        out string error)
    {
        observed = false;
        observation = "keyState=none,keyActionNumber=none";
        error = null;
        if (activation == null)
        {
            error = "interaction_plan_unavailable";
            return false;
        }
        if (!HasNativeEffect)
        {
            observed = true;
            observation = "keyEffect=none";
            return true;
        }
        if (activation.KeyEffectObserved)
        {
            observed = true;
            observation = "keyEffect=confirmed";
            return true;
        }
        if (!activation.KeyEffectDispatched || activation.KeyContext == null)
        {
            observation = "keyEffect=not_dispatched";
            return true;
        }
        var nativeCompletion = activation.KeyEffectCommandReturned &&
                               (activation.KeyEffectWasNativeNoOp ||
                                activation.KeyEffectWasNativeRetrigger);
        if (nativeCompletion)
        {
            activation.KeyEffectObserved = true;
            observed = true;
            observation =
                $"keyEffect=native_completion," +
                $"nativeNoOp={activation.KeyEffectWasNativeNoOp}," +
                $"nativeRetrigger={activation.KeyEffectWasNativeRetrigger}";
            return true;
        }
        if (_trackedState == null ||
            _trackedState.GetInstanceID() != _stateInstanceId)
        {
            error = "interaction_key_changed";
            return false;
        }

        var currentContext = _trackedState.currentPeckContext;
        var currentState = currentContext == null
            ? _trackedState.initialState
            : currentContext.state;
        var currentActionNumber = currentContext == null
            ? -1
            : currentContext.actionNumber;
        var identityMatches = currentContext != null &&
                              currentContext.playerIdentity ==
                              activation.KeyContext.playerIdentity &&
                              currentContext.propIdentity ==
                              activation.KeyContext.propIdentity;
        var receiptObserved =
            CompanionAffordanceProtocol.MatchesSwitchReceipt(
                currentContext != null,
                identityMatches,
                currentState,
                currentActionNumber,
                activation.ExpectedKeyState,
                activation.ExpectedKeyActionNumber,
                activation.PreviousKeyState,
                activation.PreviousKeyActionNumber);
        observed = receiptObserved || nativeCompletion;
        if (observed)
            activation.KeyEffectObserved = true;
        observation =
            $"keyState={currentState},keyActionNumber={currentActionNumber}," +
            $"expectedKeyState={activation.ExpectedKeyState}," +
            $"expectedKeyActionNumber={activation.ExpectedKeyActionNumber}," +
            $"nativeNoOp={activation.KeyEffectWasNativeNoOp}," +
            $"nativeRetrigger={activation.KeyEffectWasNativeRetrigger}";
        return true;
    }

    private static bool IsAvailable(UnityEngine.Behaviour behaviour)
    {
        return behaviour != null && behaviour.enabled &&
               behaviour.gameObject != null &&
               behaviour.gameObject.activeInHierarchy;
    }
}
