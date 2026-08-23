using UnityEngine;

namespace Ramblers;

internal sealed class CompanionPlayerPoseAffordanceDriver :
    ICompanionAffordanceDriver
{
    private readonly CastableTarget _castableTarget;
    private readonly int _castableInstanceId;
    private readonly PlayerPose _playerPose;
    private readonly int _poseInstanceId;
    private readonly ushort _poseTicket;
    private readonly uint _poseNetworkId;
    private readonly int _poseIndex;

    private CompanionPlayerPoseAffordanceDriver(
        CastableTarget castableTarget,
        PlayerPose playerPose,
        CompanionAffordanceSource source)
    {
        _castableTarget = castableTarget;
        _castableInstanceId = castableTarget.GetInstanceID();
        _playerPose = playerPose;
        _poseInstanceId = playerPose.GetInstanceID();
        var reference = playerPose.shellReference;
        _poseTicket = reference.ticket;
        _poseNetworkId = reference.netId;
        _poseIndex = reference.index;
        Source = source;
        ReferenceId = _poseNetworkId == 0u
            ? $"pose:local:{_poseInstanceId}"
            : $"pose:net:{_poseNetworkId}:instance:{_poseInstanceId}";
    }

    internal static bool TryCreate(
        CastableTarget castableTarget,
        PlayerPose playerPose,
        CompanionAffordanceSource source,
        out ICompanionAffordanceDriver driver,
        out string error)
    {
        driver = null;
        error = null;
        if (!CompanionAffordanceDriverUtility.IsAvailable(playerPose))
        {
            error = "interaction_target_unavailable";
            return false;
        }

        var reference = playerPose.shellReference;
        if (reference.isEmpty || reference.GetPose() != playerPose)
        {
            error = "interaction_target_unavailable";
            return false;
        }

        driver = new CompanionPlayerPoseAffordanceDriver(
            castableTarget,
            playerPose,
            source);
        return true;
    }

    public CompanionAffordanceKind Kind => CompanionAffordanceKind.PlayerPose;
    public string KindLabel => "player_pose";
    public CompanionAffordanceSource Source { get; }
    public string ReferenceId { get; }
    public uint NetworkId => _poseNetworkId;
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
            crosshair = _playerPose.transform;
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
        var body = actor?.Body;
        string error = null;
        if (actor == null || !actor.TryValidateBody(body, out error) ||
            !TryValidateExactComponents(actor, false, out error))
        {
            return CompanionAffordanceReadiness.Unavailable(error);
        }

        Vector3 point;
        if (!TryGetCurrentPoint(actor, out point, out error))
            return CompanionAffordanceReadiness.Unavailable(error);

        var poser = body.Character.poser;
        if (poser == null || body.Character.caster == null)
        {
            return CompanionAffordanceReadiness.Unavailable(
                "interaction_system_unavailable");
        }

        if (poser.currentPose != null && poser.currentPose != _playerPose)
        {
            return actor.IsHumanCarryingCompanion
                ? CompanionAffordanceReadiness.Unavailable(
                    "interaction_carrier_position_required")
                : CompanionAffordanceReadiness.NeedsApproach(point);
        }

        var alreadyInExactPose = poser.currentPose == _playerPose;
        if (!alreadyInExactPose)
        {
            var reachState = CompanionAffordanceProtocol.ClassifyWorldReadiness(
                true,
                true,
                CompanionAffordanceDriverUtility.IsWithinNativeReach(
                    actor,
                    point),
                !actor.IsHumanCarryingCompanion);
            if (reachState == CompanionAffordanceReadinessState.NeedsApproach)
            {
                return CompanionAffordanceReadiness.NeedsApproach(point);
            }
            if (reachState == CompanionAffordanceReadinessState.Unavailable)
            {
                return CompanionAffordanceReadiness.Unavailable(
                    "interaction_carrier_position_required");
            }
        }

        if (!TryValidateExactComponents(actor, true, out error))
            return CompanionAffordanceReadiness.Unavailable(error);
        if (!alreadyInExactPose)
        {
            if (_playerPose.entryIsBlocked ||
                !poser.PoseIsSafe(_playerPose, body.Character))
            {
                return CompanionAffordanceReadiness.Unavailable(
                    "interaction_blocked");
            }
        }

        if (intent == CompanionInteractionIntent.Sit &&
            !_playerPose.allowSitting)
        {
            return CompanionAffordanceReadiness.Unavailable(
                "interaction_posture_unavailable");
        }
        if (!CompanionAffordanceDriverUtility.HasCompanionAuthority(actor))
        {
            return CompanionAffordanceReadiness.Unavailable(
                "interaction_authority_unavailable");
        }

        return CompanionAffordanceReadiness.Ready(
            point,
            new CompanionPlayerPoseAffordanceActivation
            {
                ExpectedSitting = intent == CompanionInteractionIntent.Sit,
                AlreadyApplied = alreadyInExactPose
            });
    }

    public bool TryActivate(
        CompanionActorContext actor,
        CompanionAffordanceActivation activation,
        float now,
        out string error)
    {
        error = null;
        var poseActivation =
            activation as CompanionPlayerPoseAffordanceActivation;
        if (poseActivation == null)
        {
            error = "interaction_plan_unavailable";
            return false;
        }
        var finalIntent = poseActivation.ExpectedSitting
            ? CompanionInteractionIntent.Sit
            : CompanionInteractionIntent.Use;
        var finalReadiness = GetReadiness(actor, finalIntent);
        if (finalReadiness.State != CompanionAffordanceReadinessState.Ready)
        {
            error = finalReadiness.Error;
            return false;
        }

        var body = actor.Body;
        try
        {
            var poser = body.Character.poser;
            poseActivation.EntryObserved =
                poser != null && poser.currentPose == _playerPose;
            if (!poseActivation.EntryObserved)
            {
                poseActivation.EntryDispatched = true;
                poseActivation.MarkAuthorityCrossed();
                body.Networking.ServerEnterPoseAuto(_playerPose.shellReference);
                poseActivation.EntryCommandReturned = true;
                return true;
            }
            if (poseActivation.ExpectedSitting)
                DispatchSitting(body, poseActivation);
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
        var poseActivation =
            activation as CompanionPlayerPoseAffordanceActivation;
        if (poseActivation == null)
        {
            error = "interaction_plan_unavailable";
            return false;
        }
        if (!TryValidatePoseIdentity(out error) ||
            !actor.TryValidateBody(actor.Body, out error) ||
            actor.Body.Character.poser == null)
        {
            error = error ?? "interaction_confirmation_unavailable";
            return false;
        }

        var currentPose = actor.Body.Character.poser.currentPose;
        var poseMatches = currentPose == _playerPose;
        if (poseMatches)
            poseActivation.EntryObserved = true;
        if (!poseActivation.EntryObserved)
        {
            observation = $"phase=pose_entry,poseMatch={poseMatches}," +
                          $"entryDispatched={poseActivation.EntryDispatched}," +
                          $"commandReturned={poseActivation.EntryCommandReturned}";
            return true;
        }

        if (poseActivation.ExpectedSitting &&
            !poseActivation.SittingDispatched)
        {
            if (!TryValidateSittingCommit(actor, out error))
            {
                observation = "phase=sitting_unavailable,poseMatch=true";
                return false;
            }
            try
            {
                DispatchSitting(actor.Body, poseActivation);
                observation = "phase=sitting_dispatched,poseMatch=true";
                return true;
            }
            catch (System.Exception exception)
            {
                error = "interaction_authority_failed";
                observation = "phase=sitting_dispatch_failed,poseMatch=true";
                Plugin.Logger.LogWarning(
                    $"[INTERACT] AUTHORITY_EXCEPTION kind={KindLabel}, " +
                    $"referenceId={ReferenceId}, phase=sitting, " +
                    $"error={exception.Message}");
                return false;
            }
        }

        var sitting = actor.Body.Networking.NetworkisSitting;
        observed = poseMatches &&
                   (!poseActivation.ExpectedSitting || sitting);
        observation = $"phase=confirm,poseMatch={poseMatches},sitting={sitting}";
        observation +=
            $",sittingCommandReturned={poseActivation.SittingCommandReturned}";
        return true;
    }

    public string SuccessState(CompanionAffordanceActivation activation)
    {
        var poseActivation =
            activation as CompanionPlayerPoseAffordanceActivation;
        return poseActivation?.ExpectedSitting == true
            ? "native_pose_entered_sitting"
            : "native_pose_entered";
    }

    public string DescribeActivation(CompanionAffordanceActivation activation)
    {
        var poseActivation =
            activation as CompanionPlayerPoseAffordanceActivation;
        return $"expectedSitting={poseActivation?.ExpectedSitting == true}," +
               $"alreadyApplied={poseActivation?.AlreadyApplied == true}";
    }

    private bool TryValidateExactComponents(
        CompanionActorContext actor,
        bool validateOutcome,
        out string error)
    {
        if (!TryValidatePoseIdentity(out error) ||
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
            outcome == null || outcome.playerPose == null ||
            outcome.playerPose.GetInstanceID() != _poseInstanceId ||
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

    private bool TryValidatePoseIdentity(out string error)
    {
        error = null;
        if (!CompanionAffordanceDriverUtility.IsAvailable(_playerPose) ||
            _playerPose.GetInstanceID() != _poseInstanceId)
        {
            error = "interaction_target_unavailable";
            return false;
        }

        var reference = _playerPose.shellReference;
        if (reference.isEmpty || reference.ticket != _poseTicket ||
            reference.netId != _poseNetworkId || reference.index != _poseIndex ||
            reference.GetPose() != _playerPose)
        {
            error = "interaction_target_changed";
            return false;
        }
        return true;
    }

    private bool TryValidateSittingCommit(
        CompanionActorContext actor,
        out string error)
    {
        error = null;
        if (actor == null ||
            !TryValidateExactComponents(actor, false, out error) ||
            !actor.TryValidateBody(actor.Body, out error) ||
            actor.Body.Character.poser == null ||
            actor.Body.Character.poser.currentPose != _playerPose)
        {
            error = error ?? "interaction_target_changed";
            return false;
        }
        if (!_playerPose.allowSitting)
        {
            error = "interaction_posture_unavailable";
            return false;
        }
        if (!CompanionAffordanceDriverUtility.HasCompanionAuthority(actor))
        {
            error = "interaction_authority_unavailable";
            return false;
        }
        return true;
    }

    private static void DispatchSitting(
        CompanionBody body,
        CompanionPlayerPoseAffordanceActivation activation)
    {
        activation.SittingDispatched = true;
        activation.MarkAuthorityCrossed();
        body.Networking.UserCode_CmdSetSitting__Boolean(true);
        activation.SittingCommandReturned = true;
    }
}
