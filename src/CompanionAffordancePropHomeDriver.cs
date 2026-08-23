using Mirror;
using UnityEngine;

namespace Ramblers;

internal sealed class CompanionPropHomeAffordanceDriver :
    ICompanionAffordanceDriver
{
    private readonly CastableTarget _castableTarget;
    private readonly int _castableInstanceId;
    private readonly PropHome _propHome;
    private readonly int _homeInstanceId;
    private readonly ushort _homeTicket;
    private readonly uint _homeNetworkId;
    private readonly int _homeIndex;
    private readonly Prop _placementProp;
    private readonly int _placementPropInstanceId;
    private readonly NetworkIdentity _placementNetworkIdentity;
    private readonly uint _placementNetworkId;

    private CompanionPropHomeAffordanceDriver(
        CastableTarget castableTarget,
        PropHome propHome,
        Prop placementProp,
        CompanionAffordanceSource source)
    {
        _castableTarget = castableTarget;
        _castableInstanceId = castableTarget.GetInstanceID();
        _propHome = propHome;
        _homeInstanceId = propHome.GetInstanceID();
        var reference = propHome.shellReference;
        _homeTicket = reference.ticket;
        _homeNetworkId = reference.netId;
        _homeIndex = reference.index;
        _placementProp = placementProp;
        _placementPropInstanceId = placementProp.GetInstanceID();
        _placementNetworkIdentity =
            placementProp.GetComponentInParent<NetworkIdentity>();
        _placementNetworkId = _placementNetworkIdentity == null
            ? 0u
            : _placementNetworkIdentity.netId;
        Source = source;
        ReferenceId = _homeNetworkId == 0u
            ? $"prop_home:local:{_homeInstanceId}"
            : $"prop_home:net:{_homeNetworkId}:instance:{_homeInstanceId}";
    }

    internal static bool TryCreate(
        CastableTarget castableTarget,
        PropHome propHome,
        CompanionActorContext actor,
        CompanionAffordanceSource source,
        bool requireSafePlacementAtCapture,
        out ICompanionAffordanceDriver driver,
        out string error)
    {
        driver = null;
        var body = actor?.Body;
        var heldProp = body == null || !body.IsAlive ||
                       body.Character.hands == null
            ? null
            : body.Character.hands.heldProp;
        if (!CompanionAffordanceDriverUtility.IsAvailable(propHome) ||
            heldProp == null)
        {
            error = "interaction_requires_item_placement";
            return false;
        }

        var reference = propHome.shellReference;
        if (reference.isEmpty || reference.GetPropHome() != propHome ||
            (requireSafePlacementAtCapture &&
             !propHome.IsSafeToPlace(heldProp)))
        {
            error = "interaction_target_unavailable";
            return false;
        }

        driver = new CompanionPropHomeAffordanceDriver(
            castableTarget,
            propHome,
            heldProp,
            source);
        error = null;
        return true;
    }

    public CompanionAffordanceKind Kind => CompanionAffordanceKind.PropHome;
    public string KindLabel => "prop_home";
    public CompanionAffordanceSource Source { get; }
    public string ReferenceId { get; }
    public uint NetworkId => _homeNetworkId;
    public bool IsWorldTarget => true;

    public bool TryGetCurrentPoint(
        CompanionActorContext actor,
        out Vector3 point,
        out string error)
    {
        point = Vector3.zero;
        if (!TryValidateExactComponents(actor, false, out error))
            return false;

        var crosshair = _castableTarget.GetCrosshairTransform();
        if (crosshair == null)
            crosshair = _propHome.transform;
        if (crosshair == null)
        {
            error = "interaction_point_unavailable";
            return false;
        }

        point = crosshair.position;
        return true;
    }

    public CompanionAffordanceReadiness GetReadiness(
        CompanionActorContext actor,
        CompanionInteractionIntent intent)
    {
        string error = null;
        var body = actor?.Body;
        if (actor == null || !actor.TryValidateBody(body, out error) ||
            !TryValidateExactComponents(actor, false, out error))
        {
            return CompanionAffordanceReadiness.Unavailable(error);
        }
        if (intent == CompanionInteractionIntent.Sit)
        {
            return CompanionAffordanceReadiness.Unavailable(
                "interaction_intent_unsupported");
        }
        if (body.Character.caster == null)
        {
            return CompanionAffordanceReadiness.Unavailable(
                "interaction_system_unavailable");
        }
        Vector3 point;
        if (!TryGetCurrentPoint(actor, out point, out error))
            return CompanionAffordanceReadiness.Unavailable(error);
        var reachState = CompanionAffordanceProtocol.ClassifyWorldReadiness(
            true,
            true,
            CompanionAffordanceDriverUtility.IsWithinNativeReach(actor, point),
            !actor.IsHumanCarryingCompanion);
        if (reachState == CompanionAffordanceReadinessState.NeedsApproach)
            return CompanionAffordanceReadiness.NeedsApproach(point);
        if (reachState == CompanionAffordanceReadinessState.Unavailable)
        {
            return CompanionAffordanceReadiness.Unavailable(
                "interaction_carrier_position_required");
        }
        if (!TryValidateExactComponents(actor, true, out error))
            return CompanionAffordanceReadiness.Unavailable(error);
        if (!IsPlacementPropStillHeldBy(actor))
        {
            return CompanionAffordanceReadiness.Unavailable(
                "companion_held_item_changed");
        }
        if (_propHome.blockPlacing || !_propHome.IsSafeToPlace(_placementProp))
        {
            return CompanionAffordanceReadiness.Unavailable(
                "interaction_blocked");
        }
        if (!CompanionAffordanceDriverUtility.HasCompanionAuthority(actor))
        {
            return CompanionAffordanceReadiness.Unavailable(
                "interaction_authority_unavailable");
        }

        return CompanionAffordanceReadiness.Ready(
            point,
            new CompanionPropHomeAffordanceActivation());
    }

    public bool TryActivate(
        CompanionActorContext actor,
        CompanionAffordanceActivation activation,
        float now,
        out string error)
    {
        error = null;
        var homeActivation =
            activation as CompanionPropHomeAffordanceActivation;
        if (homeActivation == null)
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

        try
        {
            homeActivation.MarkAuthorityCrossed();
            actor.Body.Networking
                .UserCode_CmdPlaceInHome__Prop__ShellReference(
                    _placementProp,
                    _propHome.shellReference);
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
        out bool observed,
        out string observation,
        out string error)
    {
        observed = false;
        observation = "none";
        if (!(activation is CompanionPropHomeAffordanceActivation))
        {
            error = "interaction_plan_unavailable";
            return false;
        }
        if (!TryValidateHomeIdentity(out error) ||
            !TryValidatePlacementPropIdentity(out error) ||
            actor == null || !actor.TryValidateBody(actor.Body, out error) ||
            actor.Body.Character.hands == null)
        {
            error = error ?? "interaction_confirmation_unavailable";
            return false;
        }

        var pinned = _propHome.pinnedProp == _placementProp;
        var currentHome = _placementProp.currentHome == _propHome;
        var released = actor.Body.Character.hands.heldProp != _placementProp;
        observed = pinned && currentHome && released;
        observation = $"pinned={pinned},currentHome={currentHome},released={released}";
        return true;
    }

    public string SuccessState(CompanionAffordanceActivation activation)
    {
        return "item_placed_in_home";
    }

    public string DescribeActivation(CompanionAffordanceActivation activation)
    {
        return $"placementPropNetId={_placementNetworkId}";
    }

    private bool TryValidateExactComponents(
        CompanionActorContext actor,
        bool validateOutcome,
        out string error)
    {
        if (!TryValidateHomeIdentity(out error) ||
            !TryValidatePlacementPropIdentity(out error) ||
            !CompanionAffordanceTarget.IsExactWorldReference(
                _castableTarget,
                _castableInstanceId))
        {
            error = error ?? "interaction_target_changed";
            return false;
        }
        if (!validateOutcome)
            return true;

        CastableOutcome outcome;
        if (actor?.Body == null || !actor.Body.IsAlive ||
            !_castableTarget.GetCastableOutcome(
                actor.Body.Character,
            out outcome) ||
            outcome == null || outcome.propHome == null ||
            outcome.propHome.GetInstanceID() != _homeInstanceId ||
            !CompanionAffordanceProtocol.HasSupportedPrerequisites(
                outcome.needsKey,
                outcome.needsPocketProp,
                false))
        {
            error = "interaction_conditions_changed";
            return false;
        }

        error = null;
        return true;
    }

    private bool TryValidateHomeIdentity(out string error)
    {
        error = null;
        if (!CompanionAffordanceDriverUtility.IsAvailable(_propHome) ||
            _propHome.GetInstanceID() != _homeInstanceId)
        {
            error = "interaction_target_unavailable";
            return false;
        }

        var reference = _propHome.shellReference;
        if (reference.isEmpty || reference.ticket != _homeTicket ||
            reference.netId != _homeNetworkId || reference.index != _homeIndex ||
            reference.GetPropHome() != _propHome)
        {
            error = "interaction_target_changed";
            return false;
        }
        return true;
    }

    private bool TryValidatePlacementPropIdentity(out string error)
    {
        error = null;
        if (_placementProp == null || _placementProp.gameObject == null ||
            !_placementProp.gameObject.activeInHierarchy ||
            _placementProp.GetInstanceID() != _placementPropInstanceId)
        {
            error = "interaction_target_changed";
            return false;
        }
        var identity = _placementProp.GetComponentInParent<NetworkIdentity>();
        if (_placementNetworkIdentity == null)
        {
            if (identity == null && _placementNetworkId == 0u)
                return true;
            error = "interaction_target_changed";
            return false;
        }
        if (identity != _placementNetworkIdentity ||
            identity.netId != _placementNetworkId)
        {
            error = "interaction_target_changed";
            return false;
        }
        return true;
    }

    private bool IsPlacementPropStillHeldBy(CompanionActorContext actor)
    {
        var body = actor?.Body;
        if (body == null || !body.IsAlive || body.Character.hands == null ||
            !TryValidatePlacementPropIdentity(out _))
        {
            return false;
        }
        return body.Character.hands.heldProp == _placementProp;
    }
}
