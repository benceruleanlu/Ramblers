#requires -Version 5.1

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ramblersRoot = Split-Path -Parent $PSScriptRoot

function Read-Source {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RelativePath
    )

    return Get-Content -LiteralPath (Join-Path $ramblersRoot $RelativePath) -Raw
}

function Assert-Contains {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text,

        [Parameter(Mandatory = $true)]
        [string]$Needle,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    if ($Text.IndexOf($Needle, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Grounded-kick protocol check failed: $Description"
    }
}

function Assert-NotContains {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text,

        [Parameter(Mandatory = $true)]
        [string]$Needle,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    if ($Text.IndexOf($Needle, [System.StringComparison]::Ordinal) -ge 0) {
        throw "Grounded-kick protocol check failed: $Description"
    }
}

function Assert-Order {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text,

        [Parameter(Mandatory = $true)]
        [string]$Earlier,

        [Parameter(Mandatory = $true)]
        [string]$Later,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    $earlierIndex = $Text.IndexOf($Earlier, [System.StringComparison]::Ordinal)
    $laterIndex = $Text.IndexOf($Later, [System.StringComparison]::Ordinal)
    if ($earlierIndex -lt 0 -or $laterIndex -lt 0 -or $earlierIndex -ge $laterIndex) {
        throw "Grounded-kick protocol check failed: $Description"
    }
}

$catalog = Read-Source "src\AgentToolCatalog.cs"
$prompt = Read-Source "src\AgentPrompt.cs"
$router = Read-Source "src\AgentToolRouter.cs"
$bridge = Read-Source "src\OpenAIRealtimeBridge.cs"
$actions = Read-Source "src\CompanionActions.cs"
$jobContract = Read-Source "src\CompanionJob.cs"
$target = Read-Source "src\CompanionPropTarget.cs"
$playerTarget = Read-Source "src\CompanionPlayerTarget.cs"
$kick = Read-Source "src\CompanionKickBehavior.cs"
$approach = Read-Source "src\CompanionApproachController.cs"
$kickRouter = $router.Substring(
    $router.IndexOf(
        'private static AgentToolDispatch ExecuteKickItemJob',
        [System.StringComparison]::Ordinal))

Assert-Contains $catalog 'internal const string KickItem = "kick_item";' `
    "the allowlist must expose kick_item"
Assert-Contains $catalog 'name = KickItem' `
    "the Realtime schema must publish kick_item"
Assert-Contains $prompt 'Do not advertise optional tool parameters' `
    "clear physical requests must not become a spoken option menu"
Assert-Contains $catalog 'Do not offer parameter choices' `
    "kick must be invoked directly without offering optional variants"
Assert-Contains $catalog 'Infer strength silently' `
    "strength modifiers must be inferred rather than discussed"
Assert-Contains $catalog 'Infer direction silently' `
    "direction modifiers must be inferred rather than discussed"
Assert-Contains $catalog 'never call pick_up_item first just to prepare a kick' `
    "one kick request must own approach, pickup, charge, and release"
Assert-Contains $catalog 'The target is always the prop to kick, never the hoop or destination' `
    "the model contract must separate the kicked prop from its destination"
Assert-Contains $catalog 'For toward_reference, human gaze is reserved for the destination' `
    "goal-directed kick must reserve the one gaze ray for the destination"
Assert-Contains $catalog 'target must be companion_held_item or an exact prop ID, never human_reference' `
    "goal-directed kick must not identify both objects with one gaze token"
Assert-Contains $catalog 'companion_held_item for the prop already in your hands' `
    "the tool must expose an already-held exact target"
Assert-Contains $catalog '@enum = new[] { "light", "normal", "hard" }' `
    "the schema must bound the requested kick strength"
Assert-Contains $catalog '@enum = new[] { "away_from_companion", "toward_human", "toward_reference" }' `
    "the schema must bound the requested kick direction"
Assert-Contains $router 'case AgentToolCatalog.KickItem:' `
    "the router must dispatch kick_item"
Assert-Contains $router 'propTarget = turnReference.CompanionHeldTarget;' `
    "kick must resolve the exact companion-held turn target"
Assert-Contains $router 'string.Equals(target, "human_reference", StringComparison.Ordinal) &&' `
    "the router must recognize an ambiguous one-gaze goal-directed request"
Assert-Contains $router '"kick_target_destination_ambiguous"' `
    "goal-directed kick without a separately captured ball must fail explicitly"
Assert-NotContains $router '[ENTITY] KICK_TARGET_DISAMBIGUATED' `
    "the router must never replace a requested gaze target with a held prop"
Assert-Order $kickRouter '"kick_target_destination_ambiguous"' `
    'CompanionPropTarget propTarget;' `
    "ambiguous target/destination pairs must be rejected before target resolution"
Assert-Contains $router 'turnReference.EntityReferences.TryResolve(' `
    "kick must resolve exact prop IDs from bounded turn context"
Assert-Contains $router 'CompanionInspectionSource.HumanGaze' `
    "destination aim must select the frozen utterance-boundary gaze"
Assert-Contains $router 'PropTarget = propTarget' `
    "kick must receive the immutable response-turn target"
Assert-Contains $router 'KickDestination = destination' `
    "kick must receive its destination separately from the prop target"
Assert-Contains $router 'KickStrength = strength' `
    "the validated strength must cross the typed job boundary"
Assert-Contains $router 'KickDirection = direction' `
    "the validated direction must cross the typed job boundary"
Assert-Contains $kickRouter 'playerTarget = turnReference.HumanPlayerTarget;' `
    "toward-human routing must bind the utterance-frozen player"
Assert-Contains $kickRouter 'direction == CompanionKickDirection.TowardHuman' `
    "the frozen player must be required for a toward-human kick"
Assert-Contains $kickRouter 'AgentToolResult.Failure("human_player_unavailable")' `
    "a missing frozen human must fail instead of resolving a live replacement"
Assert-Contains $kickRouter 'humanTarget={(playerTarget == null ? "none" : playerTarget.StableId)}' `
    "kick target-resolution telemetry must identify the frozen human"
Assert-Contains $kickRouter 'humanReferenceId={(playerTarget == null ? 0 : playerTarget.ReferenceId)}' `
    "kick target-resolution telemetry must retain managed player identity"
Assert-Contains $kickRouter 'humanNetId={(playerTarget == null ? 0u : playerTarget.NetworkId)}' `
    "kick target-resolution telemetry must retain network player identity"
Assert-Contains $kickRouter 'PlayerTarget = playerTarget' `
    "the exact human must cross the typed kick-job boundary"
Assert-Contains $router 'direction={direction.ToWireValue()}' `
    "target resolution must log the canonical direction spelling"
Assert-Contains $jobContract 'internal static class CompanionKickDirectionProtocol' `
    "kick direction serialization must have one shared protocol boundary"
Assert-Contains $jobContract '"toward_reference"' `
    "the shared direction protocol must retain the model wire value"
Assert-Contains $jobContract 'internal CompanionPlayerTarget PlayerTarget;' `
    "the typed job request must carry an exact player target"
Assert-Contains $kickRouter 'CallId = callId' `
    "the model call identity must cross the typed kick-job boundary"
Assert-Contains $actions 'new CompanionKickBehavior(_locomotion, _attention, _jump)' `
    "the coordinator must register the kick job"
Assert-Contains $kick 'ICompanionJob, ICompanionStandingJob' `
    "a kick must declare that the stock action needs a standing pose"
Assert-Contains $actions 'var requiresStanding = job is ICompanionStandingJob;' `
    "the coordinator must auto-stand a crouched kick before its first tick"
Assert-Contains $actions 'locomotionHolder is ICompanionStandingJob' `
    "a later crouch call in the same batch must not undo a standing-only kick"
Assert-Contains $actions 'posture != CompanionPosture.Standing &&' `
    "standing-only jobs must reject every non-standing posture transition"
Assert-Contains $kick 'TryValidateAdmission(' `
    "kick admission must defer current-pose validation until after auto-stand"
Assert-Contains $kick 'requireReach: false' `
    "kick admission must permit a bounded approach before reach validation"
Assert-Contains $kick 'bool validateCurrentPose)' `
    "post-admission kick validation must restore the stock pose check"
Assert-Contains $kick 'if (!TryValidateKickPose(validateCurrentPose, out error))' `
    "kick admission must always cross the shared lifecycle validation"
Assert-Contains $kick 'if (validateCurrentPose && pose != null && !pose.allowKicking)' `
    "only current-pose compatibility may wait for auto-stand"
Assert-Contains $kick 'if (PlayerArms.LegIsBusyKicking(_body.Character))' `
    "kick admission must retain the stock leg-busy lifecycle guard"
Assert-Contains $bridge 'AgentToolCatalog.KickItem,' `
    "new human speech must cancel a pending kick through reconciliation"
Assert-Contains $bridge 'TryCaptureCompanionHeldTarget(' `
    "the response turn must freeze the item already in companion hands"
Assert-Contains $bridge 'CompanionController.TryCaptureHumanPlayerTarget(' `
    "the utterance boundary must freeze the human player"
Assert-Contains $bridge 'HumanPlayerTarget = humanPlayerTarget' `
    "the response turn must retain the frozen human player"

Assert-Contains $target 'candidate == null || candidate != Prop ||' `
    "the referent must retain managed-object identity"
Assert-Contains $target 'candidateIdentity.netId == _networkId' `
    "the referent must retain network identity when present"
Assert-Contains $playerTarget 'internal bool IsStillTheSamePlayer(PlayerCharacter candidate)' `
    "the human referent must expose exact managed and network validation"
Assert-Contains $playerTarget 'candidate == null || candidate != Player ||' `
    "the human referent must retain managed-object identity"
Assert-Contains $playerTarget 'candidateIdentity.netId == _networkId' `
    "the human referent must retain network identity when present"
Assert-Contains $playerTarget 'IsStillTheSamePlayer(Player)' `
    "current player positions must validate the frozen identity"

Assert-Contains $kick 'ServerPickUpPropAutomatic(_target.Prop)' `
    "the authoritative pickup transition must receive the frozen prop"
Assert-Contains $kick 'KickState.ApproachingTarget' `
    "a kick request must be able to approach an exact distant prop"
Assert-Contains $kick '_approach.Advance(' `
    "kick must use the shared approach state machine"
Assert-Contains $approach '_locomotion.TrySteerToward(' `
    "shared approaches must use obstacle-aware locomotion"
Assert-Contains $approach '_jump.TryRequestActionRecovery(' `
    "shared approaches must use bounded traversal recovery"
Assert-NotContains $kick '_locomotion.TrySteerToward(' `
    "kick must not duplicate shared steering mechanics"
Assert-NotContains $kick '_locomotion.ObserveProgress(' `
    "kick must not duplicate shared progress observation"
Assert-NotContains $kick '_jump.TryRequestActionRecovery(' `
    "kick must not duplicate shared recovery mechanics"
Assert-Contains $kick '_approach.CancelRecovery();' `
    "kick completion and cancellation must clear any queued approach jump"
Assert-Contains $approach '_recoveryUntil = 0f;' `
    "entering alignment must clear its obstacle-bypassing direction commitment"
Assert-Contains $kick '[ACTION] KICK_APPROACH_RECOVERY' `
    "kick recovery must retain action-specific runtime evidence"
Assert-Contains $kick 'var ownsLocomotion = (Held & JobResources.Locomotion) != 0;' `
    "kick cleanup must respect whether follow already reacquired locomotion"
Assert-Contains $kick 'if (ownsLocomotion)' `
    "hands-only reconciliation must not stop concurrent follow movement"
Assert-Contains $kick '_target.IsStillTheSameProp(hands.heldProp)' `
    "an already-held kick must preserve exact prop identity"
Assert-Contains $kick '_target.TryGetCurrentInspectionPoint(out point)' `
    "held inventory state must remain a valid exact kick target"
Assert-Contains $kick '_state = KickState.Charging;' `
    "the exact held prop must enter a separate visible charge phase"
Assert-Contains $kick 'duration = tunings.maxWindUpDuration * windUp;' `
    "charge delay must use Big Walk's runtime tuning"
Assert-Contains $kick 'now - _chargeStartedAt < _chargeDuration' `
    "launch must wait until the selected charge duration has elapsed"
Assert-Contains $kick 'private CompanionPlayerTarget _humanTarget;' `
    "kick execution must retain the request-bound human target"
Assert-Contains $kick '_humanTarget = request == null ? null : request.PlayerTarget;' `
    "kick admission must receive the exact player from the typed request"
Assert-Contains $kick '_humanTarget.TryGetCurrentPosition(out humanPosition)' `
    "toward-human launch must validate and use the frozen player"
Assert-Contains $kick 'launchDirection = humanPosition - launchPosition;' `
    "toward-human launch must aim at the validated frozen player position"
Assert-Contains $kick '_humanTarget.TryGetCurrentLookPoint(out point)' `
    "toward-human charge gaze must track the same frozen player"
Assert-NotContains $kick 'WorldManager.localPlayerCharacter' `
    "kick execution must never resolve a live local player"
Assert-NotContains $kick '_humanAtSpawn' `
    "kick execution must not fall back to a spawn-time player"
Assert-NotContains $kick 'GetHumanPlayer()' `
    "kick execution must not retain a live-player resolver"
Assert-Contains $kick '_destination.TryGetCurrentPoint(out destinationPoint)' `
    "toward-reference requests must resolve the frozen destination"
Assert-Contains $kick 'launchDirection = destinationPoint - launchPosition;' `
    "the launch heading must aim from the ball toward the frozen destination"
Assert-Contains $kick 'launchDirection.y = 0f;' `
    "the launch record must not double-apply destination elevation"
Assert-Contains $kick 'launchDirection = _body.Transform.forward;' `
    "a directly vertical destination must retain a defined body yaw"
Assert-Contains $kick 'tunings.kickSettings.angleCurve.Evaluate(headPitch)' `
    "runtime telemetry must expose the stock pitch actually applied to the kick"
Assert-Contains $kick 'stockMaxForce={tunings.kickSettings.maxForce:0.00}' `
    "runtime telemetry must expose the stock force selected for the kick"
Assert-Contains $kick 'Mathf.Abs(Vector3.Dot(launchForward, Vector3.up)) > 0.98f' `
    "all launch headings must retain a non-collinear rotation basis"
Assert-Contains $kick 'Quaternion.LookRotation(launchForward, launchUp)' `
    "kick launch must use the stable aim basis"
Assert-Contains $kick 'PlayerHeldInformation.ThrowInfo(' `
    "kick must build Big Walk's stock launch record"
Assert-Order $kick '[ACTION] KICK_PREPARE_FAILED' `
    'UserCode_CmdPickUp__PlayerHeldInformation(' `
    "launch-record preparation failures must remain before host authority"
Assert-Order $kick 'FailBeforeLaunch("kick_execution_failed", now);' `
    'UserCode_CmdPickUp__PlayerHeldInformation(' `
    "a preparation failure must preserve a prop that was already held"
Assert-Contains $kick 'UserCode_CmdPickUp__PlayerHeldInformation(' `
    "kick must cross the stock server-side held-prop command path"
Assert-Contains $kick '_callId = request == null ? null : request.CallId;' `
    "kick execution must retain its originating model call"
Assert-Contains $kick 'callId={CallIdForLog}, turnId={_turnId}' `
    "kick launch telemetry must be call- and turn-scoped"
Assert-Contains $kick '_direction.ToWireValue()' `
    "kick execution must use the same direction spelling as target resolution"
Assert-Order $kick 'UserCode_CmdPickUp__PlayerHeldInformation(' `
    '[ACTION] KICK_LAUNCH_FAILED' `
    "only a host-command exception may enter uncertain-authority reconciliation"
Assert-Order $kick '_target.IsStillTheSameProp(hands.heldProp)' `
    'UserCode_CmdPickUp__PlayerHeldInformation(' `
    "exact held identity must be checked before the launch call"
Assert-Order $kick 'ServerPickUpPropAutomatic(_target.Prop)' `
    '_state = KickState.Charging;' `
    "kick must confirm pickup before starting its charge"
Assert-Order $kick '_state = KickState.Charging;' `
    'UserCode_CmdPickUp__PlayerHeldInformation(' `
    "kick must finish a distinct charge phase before launch"
Assert-Contains $kick 'ServerDropPropAutomatic(false)' `
    "post-authority cancellation must recover with a plain exact-item drop"
Assert-Contains $kick '_target.Prop.rb.linearVelocity.magnitude' `
    "success must observe target motion"
Assert-Contains $kick 'displacement >= MinimumMotionDistance' `
    "success must also accept visible target displacement"
Assert-Contains $kick '"item_moving"' `
    "the terminal state must report confirmed motion"
Assert-Contains $kick '[ACTION] KICK_ALIGNMENT_TIMEOUT' `
    "custom gaze timeout must be telemetry rather than an action veto"
Assert-NotContains $kick 'CompleteFailure("target_alignment_failed")' `
    "custom kick alignment must not reject an exact stock action"

Assert-NotContains $kick 'AddForce' `
    "kick must not bypass stock replication with a raw Rigidbody shove"
Assert-NotContains $kick 'FindObjects' `
    "kick must never search for a replacement prop"
Assert-NotContains $kick 'castProp' `
    "kick must never reinterpret live gaze"
Assert-NotContains $kick 'nearest' `
    "kick must never use nearest-item fallback"

Write-Host "Grounded-kick protocol checks passed."
Write-Host "  Proven: direct silent prompt routing, exact utterance-frozen ball/human/destination bindings, held-item continuation, obstacle-aware approach, staged game-tuned charge, stock server launch, no fallback selector."
Write-Host "  Not proven: live model compliance, visible approach/charge timing, hoop accuracy, or deployed behavior."
