#requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$CompilerPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ramblersRoot = Split-Path -Parent $PSScriptRoot
$compiler = (Resolve-Path -LiteralPath $CompilerPath).Path

function Read-Source {
    param([Parameter(Mandatory = $true)][string]$RelativePath)
    return Get-Content -LiteralPath (Join-Path $ramblersRoot $RelativePath) -Raw
}

function Assert-Contains {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Needle,
        [Parameter(Mandatory = $true)][string]$Description
    )
    if ($Text.IndexOf($Needle, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Grounded-interaction check failed: $Description"
    }
}

function Assert-NotContains {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Needle,
        [Parameter(Mandatory = $true)][string]$Description
    )
    if ($Text.IndexOf($Needle, [System.StringComparison]::Ordinal) -ge 0) {
        throw "Grounded-interaction check failed: $Description"
    }
}

function Assert-Order {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Earlier,
        [Parameter(Mandatory = $true)][string]$Later,
        [Parameter(Mandatory = $true)][string]$Description
    )
    $earlierIndex = $Text.IndexOf($Earlier, [System.StringComparison]::Ordinal)
    $laterIndex = $Text.IndexOf($Later, [System.StringComparison]::Ordinal)
    if ($earlierIndex -lt 0 -or $laterIndex -lt 0 -or $earlierIndex -ge $laterIndex) {
        throw "Grounded-interaction check failed: $Description"
    }
}

function Assert-Sequence {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string[]]$Needles,
        [Parameter(Mandatory = $true)][string]$Description
    )
    $searchStart = 0
    foreach ($needle in $Needles) {
        $index = $Text.IndexOf(
            $needle,
            $searchStart,
            [System.StringComparison]::Ordinal)
        if ($index -lt 0) {
            throw "Grounded-interaction check failed: $Description"
        }
        $searchStart = $index + $needle.Length
    }
}

$catalog = Read-Source "src\AgentToolCatalog.cs"
$router = Read-Source "src\AgentToolRouter.cs"
$bridge = Read-Source "src\OpenAIRealtimeBridge.cs"
$jobContract = Read-Source "src\CompanionJob.cs"
$actions = Read-Source "src\CompanionActions.cs"
$posture = Read-Source "src\CompanionPostureActuator.cs"
$controller = Read-Source "src\CompanionController.cs"
$targetWrapper = Read-Source "src\CompanionAffordanceTarget.cs"
$driverContract = Read-Source "src\CompanionAffordanceDriver.cs"
$switchDrivers = Read-Source "src\CompanionAffordanceSwitchDriver.cs"
$poseDriver = Read-Source "src\CompanionAffordancePlayerPoseDriver.cs"
$homeDriver = Read-Source "src\CompanionAffordancePropHomeDriver.cs"
$target = $targetWrapper + $driverContract + $switchDrivers +
    $poseDriver + $homeDriver
$keyAffordance = Read-Source "src\CompanionKeyAffordance.cs"
$behavior = Read-Source "src\CompanionInteractBehavior.cs"
$approach = Read-Source "src\CompanionApproachController.cs"
$protocol = Read-Source "src\CompanionAffordanceProtocol.cs"
$kick = Read-Source "src\CompanionKickBehavior.cs"
$awareness = Read-Source "src\CompanionAwareness.cs"
$entities = Read-Source "src\CompanionEntityReferences.cs"
$locomotion = Read-Source "src\CompanionLocomotion.cs"
$interactionReference = Read-Source "src\CompanionInteractionReference.cs"
$interactableDiscovery = Read-Source "src\CompanionInteractableDiscovery.cs"

Assert-Contains $catalog 'internal const string InteractWithObject = "interact_with_object";' `
    "the implementation must retain one native primary-action tool"
Assert-Contains $catalog 'name = InteractWithObject,' `
    "the exact interaction must be model-callable"
Assert-Contains $catalog 'inspect_reference,go_to_location,interact_with_object,pick_up_item' `
    "the ready-tool log must advertise interaction"
Assert-Contains $catalog 'enter or sit in an indicated seat or pose' `
    "seat requests must route through the native interaction tool"
Assert-Contains $catalog 'place the exact item already in your hands into an indicated holder' `
    "native item-home outcomes must be model-routable without a puzzle verb"
Assert-Contains $catalog 'Never imitate a world interaction with set_posture' `
    "the model must not replace a seat affordance with a free-standing animation"
Assert-Contains $catalog 'never kick a control' `
    "the model must not improvise a physical kick for a button"
Assert-Contains $catalog 'interaction:net:<network-id>:castable:<instance-id>' `
    "interaction targeting must accept exact context IDs"
Assert-NotContains $catalog '@enum = new[] { "human_reference", "companion_held_item" }' `
    "the schema must not reject exact context IDs"
Assert-Contains $catalog '@enum = new[] { "use", "sit" }' `
    "explicit sitting intent must be distinct from generic pose entry"

Assert-Contains $bridge 'CompanionController.TryCaptureAffordanceCandidates(' `
    "speech completion must freeze native affordance referents"
Assert-Order $bridge '[AGENT] TURN_INTERACTION_REFERENCE_CAPTURE_STARTED' `
    'CompanionController.TryCaptureAffordanceCandidates(' `
    "runtime evidence must precede native affordance capture"
Assert-Contains $bridge 'AffordanceCandidates = affordanceCandidates' `
    "turn state must carry frozen affordances"
Assert-Contains $bridge 'AffordanceCaptureError = affordanceCaptureError' `
    "turn state must retain the exact capture failure"
Assert-Contains $bridge 'humanKind={affordanceCandidates.HumanReferenceKind}' `
    "runtime capture evidence must identify the selected native affordance kind"
Assert-Contains $bridge '[AGENT] TURN_INTERACTION_REFERENCES_CAPTURED' `
    "runtime evidence must report interaction reference availability"
Assert-Contains $bridge 'turnReference.TryApply(' `
    "a verified physical result must advance transient turn capability before continuation"
Assert-Contains $bridge '[AGENT] TURN_STATE_ADVANCED' `
    "causal turn-state composition must be visible in runtime evidence"

Assert-Contains $router 'turnReference.AffordanceCandidates.TrySelect(' `
    "routing must select only from turn-bound affordance candidates"
Assert-Contains $router 'selectionError ??' `
    "routing must preserve why the frozen native affordance was unavailable"
Assert-Contains $router 'turnReference.AffordanceCaptureError ??' `
    "routing must retain the utterance-bound capture failure classification"
Assert-Contains $router 'TryResolveInteraction(' `
    "interaction routing must accept only an exact ID populated in turn context"
Assert-Contains $router 'AffordanceTarget = affordanceTarget' `
    "the selected immutable affordance must cross the typed job boundary"
Assert-Contains $router 'InteractionIntent = intent' `
    "the inferred action intent must cross the typed job boundary"
Assert-Contains $router 'CallId = callId' `
    "the exact model call must cross the typed job boundary for runtime correlation"
Assert-NotContains $router 'TryCaptureAffordanceCandidates(' `
    "tool execution must not reinterpret later gaze"

Assert-Contains $jobContract 'internal CompanionAffordanceCandidates AffordanceCandidates;' `
    "the turn contract must carry exact affordance candidates"
Assert-Contains $jobContract 'internal CompanionAffordanceTarget AffordanceTarget;' `
    "the job request must carry only the selected exact affordance"
Assert-Contains $jobContract 'internal CompanionInteractionIntent InteractionIntent;' `
    "the job request must keep pose intent separate from target identity"
Assert-Contains $jobContract 'internal string CallId;' `
    "multi-response turns must retain distinct physical-call identities"
Assert-Contains $jobContract 'internal enum CompanionTurnHandsTransition' `
    "physical continuations must communicate verified hands-state transitions"
Assert-Contains $jobContract 'CompanionController.TryAdvanceHeldPropCandidates(' `
    "pickup composition must materialize only the exact confirmed prop"
Assert-Contains $actions 'new CompanionInteractBehavior(_locomotion, _attention, _jump)' `
    "interaction must participate in normal resource arbitration"
Assert-Contains $controller '[INTERACT] REFERENCE_CAPTURE_FAILED' `
    "a native capture failure must degrade without suppressing conversation"

Assert-Contains $target 'internal enum CompanionAffordanceKind' `
    "native outcomes must be explicit data rather than puzzle-specific branches"
Assert-Contains $target 'WorldSwitch,' `
    "world switches must be one supported native kind"
Assert-Contains $target 'HeldItemSwitch,' `
    "held-item use must remain a distinct native kind"
Assert-Contains $target 'PlayerPose' `
    "game-owned poses must be a supported native kind"
Assert-Contains $target 'PropHome' `
    "game-owned item homes must be a supported native kind"
Assert-Contains $target 'castableTarget = human.caster.castableTarget;' `
    "capture must prefer Big Walk's local primary-interaction cast"
Assert-Contains $target 'human.caster.CastThroughHands(' `
    "an exact visible control outside human reach must still be capturable"
Assert-Contains $target 'source=extended_gaze' `
    "extended-gaze selection must be distinguishable in runtime evidence"
Assert-Contains $target 'IsExactWorldReference(' `
    "deferred affordance resolution must retain the same CastableTarget identity"
Assert-Contains $target 'TryWithCompanionHeldProp(' `
    "same-turn pickup must update capability without retargeting"
Assert-Contains $switchDrivers 'var peckSwitch = heldProp.useHeldSwitch;' `
    "held props must use Big Walk's separate held-use affordance"
Assert-Contains $driverContract 'internal interface ICompanionAffordanceDriver' `
    "per-kind native lifecycles must share one internal driver contract"
Assert-Contains $switchDrivers 'internal sealed class CompanionWorldSwitchAffordanceDriver' `
    "world switches must have a dedicated driver"
Assert-Contains $switchDrivers 'internal sealed class CompanionHeldItemSwitchAffordanceDriver' `
    "held use must have a dedicated driver"
Assert-Contains $poseDriver 'internal sealed class CompanionPlayerPoseAffordanceDriver' `
    "native poses must have a dedicated driver"
Assert-Contains $homeDriver 'internal sealed class CompanionPropHomeAffordanceDriver' `
    "native prop homes must have a dedicated driver"
Assert-Contains $driverContract 'internal abstract class CompanionAffordanceActivation' `
    "the generic lifecycle must keep activation state opaque"
Assert-Contains $driverContract 'internal bool AuthorityCrossed { get; private set; }' `
    "each activation must expose a sticky native-authority boundary"
Assert-Contains $driverContract 'internal void MarkAuthorityCrossed()' `
    "drivers must mark authority on the transaction before native commands"
Assert-Contains $driverContract 'internal sealed class CompanionSwitchAffordanceActivation' `
    "switch receipt state must remain switch-owned"
Assert-Contains $driverContract 'internal sealed class CompanionPlayerPoseAffordanceActivation' `
    "pose flags must remain pose-owned"
Assert-Contains $driverContract 'internal sealed class CompanionPropHomeAffordanceActivation' `
    "home activation must not inherit switch or pose fields"
Assert-Contains $driverContract 'internal sealed class CompanionActorContext' `
    "affordances must receive an exact bound actor context"
Assert-Contains $driverContract 'HumanCarryingCompanionAtCapture' `
    "carried admission must retain the utterance-bound relationship state"
Assert-Contains $driverContract '_human.hands?.heldCharacter == _body.Character' `
    "carried state must be derived from the exact bound human/body pair"
Assert-NotContains $target 'WorldManager' `
    "interaction drivers must not rediscover actors through world globals"
Assert-NotContains $target 'CompanionFollowBehavior' `
    "interaction drivers must not borrow follow-behaviour relationship policy"
Assert-Contains $targetWrapper 'TryGetExactHumanWorldReference(' `
    "context discovery must reuse the same frozen human CastableTarget"
Assert-Contains $targetWrapper 'TrySelectExactWorldReference(' `
    "context ID selection must revalidate the frozen CastableTarget instance"
Assert-Contains $targetWrapper 'TryCaptureStructuralWorldOutcome(' `
    "far exact references must have a structural capture path"
Assert-Contains $targetWrapper 'var outcomes = castableTarget.outcomes;' `
    "structural capture must inspect only the exact target's native outcomes"
Assert-Contains $targetWrapper 'outcomeCount = outcomes == null ? 0 : outcomes.Length;' `
    "IL2CPP outcome traversal must use indexed collection access"
Assert-Contains $targetWrapper 'CompanionAffordanceProtocol.HasSupportedPrerequisites(' `
    "every captured primitive must share centralized prerequisite admission"
Assert-Contains $targetWrapper 'outcome.needsPocketProp' `
    "unsupported pocket prerequisites must remain explicit"
Assert-Contains $targetWrapper 'CanSelectStructuralWorldAffordance(' `
    "structural capture must require one exact constructible driver"
Assert-Contains $targetWrapper 'TryCreateDriverFromOutcome(' `
    "structural capture must bind exact per-kind prerequisites before selection"
Assert-Contains $targetWrapper 'if (outcome.peckSwitch != null)' `
    "switch selection must come from native CastableOutcome data"
Assert-Contains $targetWrapper 'if (outcome.playerPose != null)' `
    "pose selection must come from the same native CastableOutcome data"
Assert-Contains $targetWrapper 'if (outcome.propHome != null)' `
    "item placement must come from the same native CastableOutcome data"
Assert-Order $targetWrapper 'if (outcome.playerPose != null)' `
    'if (outcome.propHome != null)' `
    "native pose outcomes must retain PlayerDecisions precedence over item homes"
Assert-Order $targetWrapper 'if (outcome.propHome != null)' `
    'if (outcome.peckSwitch != null)' `
    "native item homes must retain PlayerDecisions precedence over world switches"
Assert-Contains $targetWrapper 'ContextEntity' `
    "the affordance abstraction must distinguish bounded context identities"
Assert-Contains $poseDriver 'outcome.playerPose.GetInstanceID() != _poseInstanceId' `
    "pose outcome revalidation must preserve the frozen identity"
Assert-Contains $poseDriver 'CompanionAffordanceProtocol.HasSupportedPrerequisites(' `
    "pose commit must reject unsupported key and pocket prerequisites"
Assert-Contains $homeDriver 'CompanionAffordanceProtocol.HasSupportedPrerequisites(' `
    "home commit must reject unsupported key and pocket prerequisites"
Assert-Contains $switchDrivers 'outcome.peckSwitch.GetInstanceID() != SwitchInstanceId' `
    "switch outcome revalidation must preserve the frozen identity"
Assert-Contains $switchDrivers 'outcome.needsPocketProp,' `
    "world-switch commit must reject unsupported pocket prerequisites"
Assert-Sequence $switchDrivers @(
    'TryValidateExactComponents(actor, false, out error)',
    'CompanionAffordanceDriverUtility.IsWithinNativeReach(',
    'TryValidateExactComponents(actor, true, out error)') `
    "switch identity, geometric reach, and dynamic admission must remain ordered"
Assert-Order $poseDriver 'TryValidateExactComponents(actor, false, out error)' `
    'CompanionAffordanceProtocol.ClassifyWorldReadiness(' `
    "pose identity must be checked before approach classification"
Assert-Order $poseDriver 'CompanionAffordanceProtocol.ClassifyWorldReadiness(' `
    'TryValidateExactComponents(actor, true, out error)' `
    "pose dynamic outcome admission must wait until native reach"
Assert-Order $homeDriver 'TryValidateExactComponents(actor, false, out error)' `
    'CompanionAffordanceProtocol.ClassifyWorldReadiness(' `
    "home identity must be checked before approach classification"
Assert-Order $homeDriver 'CompanionAffordanceProtocol.ClassifyWorldReadiness(' `
    'TryValidateExactComponents(actor, true, out error)' `
    "home dynamic outcome admission must wait until native reach"
Assert-Contains $protocol 'CompanionAffordanceReadinessState.NeedsApproach' `
    "typed readiness must classify far exact targets without error-string parsing"
Assert-Contains $protocol 'bool canSelfApproach)' `
    "world readiness must distinguish owned locomotion from a human-carried pose"
Assert-Contains $switchDrivers 'interaction_carrier_position_required' `
    "a carried out-of-range interaction must fail without claiming locomotion"
Assert-Contains $switchDrivers 'body.Character.caster.CanStillReachSwitch(PeckSwitch)' `
    "aligned world-switch commit must retain the game-owned raycast check"
Assert-Contains $switchDrivers 'body.Character.decisions.IsSafeToUseSwitch(PeckSwitch)' `
    "world switches must reuse stock blocker and keyed-switch admission"
Assert-Sequence $switchDrivers @(
    'var finalReadiness = GetReadiness(',
    '!TryValidateSwitchPreconditions(',
    'true,',
    'true,',
    'switchActivation.SwitchContext = new PeckContext(') `
    "current caster reach and native safety must be revalidated after alignment and before authority"
Assert-Contains $switchDrivers 'TryCreateCarriedFallback(' `
    "a carried companion must have one exact switch-only admission path"
Assert-Contains $switchDrivers 'IsGroundedCarriedSwitchSource(' `
    "direct and exact context references must share carried switch admission"
Assert-Contains $behavior '[INTERACT] APPROACH_RESUMED' `
    "an exact initially-far interaction must resume approach if it moves during alignment"
Assert-Contains $switchDrivers 'candidate.needsPocketProp' `
    "carried fallback must preserve pocket-prop requirements"
Assert-NotContains $switchDrivers '!body.Character.caster.CanStillReachSwitch(peckSwitch)' `
    "carried capture must not confuse a pre-alignment raycast with geometric reach"
Assert-NotContains $switchDrivers '!body.Character.decisions.IsSafeToUseSwitch(peckSwitch)' `
    "carried capture must defer dynamic native safety to readiness and commit"
Assert-Contains $switchDrivers 'CARRIED_SWITCH_REACH_SNAPSHOT' `
    "carried capture must expose geometric, caster, and safety disagreement"
Assert-Contains $targetWrapper 'CARRIED_STRUCTURAL_APPROACH_BLOCKED' `
    "failed carried capture must never fall through to an ordinary locomoting driver"
Assert-Contains $protocol 'structuralOutcomeCount == 1' `
    "carried fallback must reject ambiguous native outcomes"
Assert-Contains $switchDrivers '_requiresCastableOutcomeValidation' `
    "ordinary and structurally captured switches must rematerialize their outcome"
Assert-Contains $switchDrivers '_requiresCompanionCarriedByHuman' `
    "carried-only admission must be revalidated before native authority"
Assert-Contains $switchDrivers 'actor?.IsHumanCarryingCompanion == true' `
    "a released companion must not retain the carried interaction bypass"
Assert-Contains $switchDrivers 'CanRetainCarriedSwitchFallback(' `
    "the same unique raw outcome must survive through commit"
Assert-Contains $switchDrivers 'scanCompleted = true;' `
    "an IL2CPP outcome collection failure must invalidate commit revalidation"
Assert-Contains $protocol 'outcomeScanCompleted' `
    "the executable carried-switch protocol must represent scan failure"
Assert-Contains $driverContract 'body.Character.caster.GetMaxDistanceForDirection(' `
    "pose approach must use the game's directional interaction reach"
Assert-Contains $poseDriver 'poser.PoseIsSafe(_playerPose, body.Character)' `
    "pose admission must use the game-owned occupancy check"
Assert-Contains $switchDrivers 'UserCode_CmdUsePeckSwitch__ShellReference__PeckContext__Int32(' `
    "world-switch down must use the stock server command body"
Assert-Contains $switchDrivers 'UserCode_CmdReleaseHeldSwitch__ShellReference__PeckContext__Int32(' `
    "world-switch release must use the stock server command body"
Assert-Contains $switchDrivers 'UserCode_CmdUseHeld__PeckContext(context);' `
    "held-item down must use the stock server command body"
Assert-Contains $switchDrivers 'UserCode_CmdUseHeldUp__PeckContext(context);' `
    "held-item release must use the stock server command body"
Assert-Contains $switchDrivers 'heldProp.useHeldUpSwitch' `
    "held primary actions must freeze their configured release action"
Assert-Contains $switchDrivers 'peckSwitch.upSwitch' `
    "world primary actions must freeze their configured release action"
Assert-Contains $switchDrivers 'CompanionAffordanceProtocol.NextDistinctActionNumber(' `
    "each native switch dispatch must carry a receipt distinct from shared tracked state"
Assert-Contains $keyAffordance 'CompanionAffordanceProtocol.NextDistinctActionNumber(' `
    "key effects must not collide with a shared tracked-state receipt"
Assert-Contains $switchDrivers 'CompanionAffordanceProtocol.IsReleaseDue(' `
    "momentary controls must complete a bounded down/up pulse"
Assert-Contains $switchDrivers 'switchActivation.DownDispatched' `
    "release timing must follow authoritative down dispatch rather than receipt arrival"
Assert-Contains $switchDrivers 'CompanionAffordanceProtocol.IsMomentaryComplete(' `
    "terminal success must confirm both press and release phases"
Assert-Contains $switchDrivers 'currentContext.playerIdentity == expectedContext.playerIdentity' `
    "switch receipts must belong to the exact companion actor"
Assert-Contains $switchDrivers 'currentContext.propIdentity == expectedContext.propIdentity' `
    "switch receipts must preserve the exact prop context"
Assert-Contains $switchDrivers 'CompanionAffordanceProtocol.IsIgnoredNativeRepeat(' `
    "only previously fired native same-state actions may complete without a new receipt"
Assert-Contains $switchDrivers 'CompanionAffordanceProtocol.IsNativeRetrigger(' `
    "repeat-enabled same-state actions must honor TrackedPeckState retrigger semantics"
Assert-Contains $driverContract 'CanDispatchAfterPrerequisite(' `
    "main-switch admission must be an explicit prerequisite phase policy"
Assert-Contains $switchDrivers 'TryProgressPrerequisite(' `
    "a keyed switch must observe its prerequisite before main dispatch"
Assert-Contains $keyAffordance 'CompanionAffordanceProtocol.MatchesSwitchReceipt(' `
    "the exact key effect must produce its own tracked receipt"
Assert-Contains $keyAffordance 'activation.KeyEffectObserved = true;' `
    "the key receipt must be retained across later transaction phases"
Assert-Sequence $keyAffordance @(
    'activation.KeyEffectDispatched = true;',
    'activation.MarkAuthorityCrossed();',
    'UserCode_CmdUseHeldAsKey__PeckContext(context);') `
    "key-effect authority must be recorded before the stock native command"
Assert-Sequence $switchDrivers @(
    'if (!TryProgressPrerequisite(',
    'if (!switchActivation.CanDispatchMainSwitch)',
    'TryValidatePostPrerequisiteCommit(actor, out error)',
    'DispatchSwitch(') `
    "the exact key receipt must gate later main-switch authority"
Assert-Sequence $switchDrivers @(
    'activation.MarkAuthorityCrossed();',
    'DispatchNativeSwitch(') `
    "each switch phase must record authority before its stock native command"
Assert-Contains $driverContract 'HasCompanionAuthority(CompanionActorContext actor)' `
    "switch commits must revalidate authority on the acting companion"
Assert-Contains $poseDriver 'body.Networking.ServerEnterPoseAuto(_playerPose.shellReference);' `
    "pose activation must use the connectionless server-native path"
Assert-Contains $poseDriver 'body.Networking.UserCode_CmdSetSitting__Boolean(true);' `
    "explicit native sitting must use the stock server command body"
Assert-Contains $homeDriver 'UserCode_CmdPlaceInHome__Prop__ShellReference(' `
    "item placement must reuse the stock server command body"
Assert-Contains $poseDriver 'currentPose == _playerPose' `
    "pose success must be confirmed from the exact live pose"
Assert-Contains $poseDriver 'actor.Body.Networking.NetworkisSitting' `
    "sittable pose completion must confirm replicated sitting state"
Assert-Contains $poseDriver 'poseActivation.EntryObserved' `
    "pose entry must be an observed phase before sitting authority"
Assert-Contains $poseDriver '!poseActivation.SittingDispatched' `
    "sitting must dispatch only once after exact pose entry"
Assert-Sequence $poseDriver @(
    'poseActivation.EntryDispatched = true;',
    'poseActivation.MarkAuthorityCrossed();',
    'ServerEnterPoseAuto(_playerPose.shellReference);') `
    "pose-entry authority must be recorded before the stock native command"
Assert-Sequence $poseDriver @(
    'phase=pose_entry',
    '!poseActivation.SittingDispatched',
    'DispatchSitting(actor.Body, poseActivation)') `
    "exact pose entry must be observed before a later sitting phase"
Assert-Sequence $poseDriver @(
    'activation.SittingDispatched = true;',
    'activation.MarkAuthorityCrossed();',
    'UserCode_CmdSetSitting__Boolean(true);') `
    "sitting authority must be recorded before the stock native command"
Assert-Contains $homeDriver '_propHome.pinnedProp == _placementProp' `
    "item placement must confirm the exact prop in the exact home"
Assert-Contains $homeDriver 'if (_placementNetworkIdentity == null)' `
    "placement identity must distinguish a missing component from a zero network id"
Assert-Contains $homeDriver 'identity != _placementNetworkIdentity' `
    "a zero-id placement prop must retain its exact NetworkIdentity component"
Assert-Sequence $homeDriver @(
    'homeActivation.MarkAuthorityCrossed();',
    'UserCode_CmdPlaceInHome__Prop__ShellReference(') `
    "home-placement authority must be recorded before the stock native command"
Assert-Contains $switchDrivers 'TryReadSwitchReceipt(' `
    "switch confirmation must tolerate a one-shot target becoming hidden"
Assert-NotContains $switchDrivers '.CmdUsePeckSwitch(' `
    "a connectionless companion must not call a local-player switch Command"
Assert-NotContains $poseDriver '.CmdEnterPose(' `
    "a connectionless companion must not call a local-player pose Command"
Assert-NotContains $homeDriver 'body.Networking.CmdPlaceInHome(' `
    "a connectionless companion must not call a local-player placement Command"
Assert-NotContains $target 'FindObjects' `
    "interaction must never search for a replacement target"
Assert-NotContains $target 'nearest' `
    "interaction must never fall back to proximity selection"

Assert-Contains $switchDrivers 'CompanionKeyAffordance.TryCapture(' `
    "native keyed outcomes must freeze their held prerequisite"
Assert-Contains $switchDrivers '_keyAffordance.TryValidate(body, out error)' `
    "the exact held key must be revalidated immediately before authority"
Assert-Contains $switchDrivers '_keyAffordance.TryDispatch(' `
    "the native key effect must run before the world switch"
Assert-Order $switchDrivers 'switchActivation.SwitchContextProp = GetSwitchContextProp(actor);' `
    'switchActivation.SwitchContext = new PeckContext(' `
    "world context must snapshot the key identity before synchronous key consumption"
Assert-Order $switchDrivers 'switchActivation.SwitchContext = new PeckContext(' `
    '_keyAffordance.TryDispatch(' `
    "world dispatch must freeze exact context identities before the key effect"
Assert-Contains $switchDrivers 'activation.SwitchContext != null' `
    "main dispatch must reuse the pre-key context identities"
Assert-Contains $keyAffordance 'outcome.needsKey' `
    "key behavior must be driven by generic CastableOutcome data"
Assert-Contains $keyAffordance 'current.onUseAsKey != _onUseAsKey' `
    "key-effect identity must remain frozen"
Assert-Contains $keyAffordance 'UserCode_CmdUseHeldAsKey__PeckContext(context);' `
    "key consumption and unlock effects must use the stock server command body"
Assert-NotContains $keyAffordance 'FindObjects' `
    "key resolution must never search for a replacement prop"

Assert-Contains $behavior 'JobResources.Locomotion | JobResources.Gaze' `
    "following must pause while a world affordance is approached"
Assert-Contains $behavior 'JobResources.Gaze | JobResources.Hands' `
    "all primary interaction must reserve hands"
Assert-Contains $behavior 'CompanionAffordanceReadinessState.NeedsApproach' `
    "resource admission must distinguish approach readiness from in-place native use"
Assert-Contains $behavior '_requiresLocomotion' `
    "held resources must latch the readiness decision made at admission"
Assert-Contains $behavior 'request.InteractionIntent' `
    "resource admission must evaluate the exact requested native intent"
Assert-Contains $behavior '_requiresLocomotion = readiness.State ==' `
    "a currently reachable carried interaction must not reserve locomotion and auto-stand"
Assert-Contains $poseDriver 'poser != null && poser.currentPose == _playerPose' `
    "reusing the exact occupied pose must not force an auto-stand"
Assert-Contains $behavior '_attention.IsAimWithin(' `
    "the companion must visibly align before authority"
Assert-Contains $behavior '[INTERACT] ALIGNMENT_TIMEOUT' `
    "custom gaze timeout must remain telemetry rather than a veto"
Assert-Order $behavior '_activation = readiness.Activation;' `
    '_target.TryActivate(_body, _activation, now, out error)' `
    "exact final validation must immediately precede authority"
Assert-Contains $behavior '_target.TryProgressActivation(' `
    "the job must advance and confirm each native affordance transaction"
Assert-Contains $behavior 'RefreshAuthorityCrossing();' `
    "the job must read the activation-owned authority boundary after every driver call"
Assert-Contains $behavior '[INTERACT] RECONCILIATION_PENDING' `
    "post-authority driver failures must remain in confirmation"
Assert-Contains $behavior 'kind={_target.KindLabel}' `
    "runtime telemetry must identify kind without object names"
Assert-Contains $behavior 'callId={CallIdForLog}' `
    "interaction postconditions must identify the exact model call"
Assert-Order $behavior '[INTERACT] CANCEL_RECONCILED' `
    '_completion = new CompanionJobCompletion' `
    "post-authority cancellation must reconcile release before any normal completion path"
Assert-Contains $behavior '[INTERACT] CANCEL_RECONCILIATION_FAILED' `
    "failed cancellation reconciliation must terminate without latching an orphaned completion"
Assert-Contains $behavior 'InteractionState.Approaching' `
    "out-of-reach world affordances must enter one generic approach phase"
Assert-Contains $behavior '_approach.Advance(' `
    "affordance travel must use the shared approach state machine"
Assert-Contains $approach '_locomotion.TrySteerToward(' `
    "shared affordance approach must reuse obstacle-aware locomotion"
Assert-Contains $approach '_jump.TryRequestActionRecovery(' `
    "stalled shared approaches must retain bounded recovery"
Assert-Contains $approach 'CompanionJumpActuator.IsDeferredRecoveryError(jumpError)' `
    "temporary jump contention must defer instead of failing"
Assert-NotContains $behavior '_locomotion.TrySteerToward(' `
    "interaction must not duplicate shared steering mechanics"
Assert-NotContains $behavior '_locomotion.ObserveProgress(' `
    "interaction must not duplicate shared progress observation"
Assert-NotContains $behavior '_jump.TryRequestActionRecovery(' `
    "interaction must not duplicate shared recovery mechanics"
Assert-NotContains $behavior 'MaximumApproachRecoveries' `
    "the existing job timeout must remain the single recovery bound"
Assert-Contains $behavior '_approach.CancelRecovery();' `
    "cancelled interaction work must remove its recovery jump"

Assert-Contains $posture 'internal bool NativePoseActive =>' `
    "native pose occupancy must participate in persistent posture state"
Assert-Contains $posture 'Current == CompanionPosture.Sitting || NativePoseActive' `
    "follow must remain suspended in every native pose"
Assert-Contains $posture '_body.Networking.ServerExitPoseAuto();' `
    "standing or moving away must leave a native pose through server authority"
Assert-Contains $actions '_posture.SynchronizeFromGame()' `
    "native pose changes must reconcile locomotion and gaze centrally"
Assert-Contains $actions '!_posture.NativePoseActive' `
    "auto-stand must not mistake a standing native pose for free locomotion"
Assert-NotContains $locomotion 'HasGroundSupportAhead' `
    "the disproven forward-floor subsystem must stay removed"
Assert-NotContains $awareness 'Resources.FindObjectsOfTypeAll<CastableTarget>()' `
    "the unverified global CastableTarget scan must stay outside spoken capture"
Assert-Contains $entities 'CompanionAffordanceTarget' `
    "the turn map must carry exact context-selected native affordances"
Assert-Contains $interactionReference 'IsExactCastableIdentity()' `
    "context selection must revalidate the frozen CastableTarget"
Assert-Contains $interactableDiscovery 'var spawned = NetworkServer.spawned;' `
    "ambient discovery must stay on a game-owned registry"
Assert-NotContains $interactableDiscovery 'Resources.' `
    "bounded discovery must never restore a global resource scan"

$productionExecutors = $target + $keyAffordance + $behavior + $kick + $protocol
foreach ($puzzleName in @(
    'TeachingArea',
    'StumpSeats',
    'PushButton',
    'ObjectsTeaching',
    'SpawnCourtyard',
    'hoop')) {
    Assert-NotContains $productionExecutors $puzzleName `
        "production action code must not contain puzzle or scene-name exceptions"
}

$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$probeRoot = Join-Path $tempRoot (
    "RamblersAffordanceProtocolProbe-" + [Guid]::NewGuid().ToString("N"))
$probeRoot = [System.IO.Path]::GetFullPath($probeRoot)
if (-not $probeRoot.StartsWith(
        $tempRoot,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Affordance protocol probe path escaped the temporary directory."
}

New-Item -ItemType Directory -Path $probeRoot | Out-Null
try {
    $probeOutput = Join-Path $probeRoot "CompanionAffordanceProtocolProbe.exe"
    & $compiler `
        /nologo `
        /target:exe `
        /langversion:latest `
        /optimize+ `
        "/out:$probeOutput" `
        (Join-Path $ramblersRoot "src\CompanionAffordanceProtocol.cs") `
        (Join-Path $PSScriptRoot "CompanionAffordanceProtocolProbe.cs")
    if ($LASTEXITCODE -ne 0) {
        throw "Affordance protocol probe compilation failed with exit code $LASTEXITCODE."
    }
    & $probeOutput
    if ($LASTEXITCODE -ne 0) {
        throw "Affordance protocol probe failed with exit code $LASTEXITCODE."
    }

    $activationProbeOutput = Join-Path $probeRoot `
        "CompanionAffordanceActivationProbe.exe"
    & $compiler `
        /nologo `
        /target:exe `
        /langversion:latest `
        /optimize+ `
        /nowarn:0649 `
        "/out:$activationProbeOutput" `
        (Join-Path $ramblersRoot "src\CompanionAffordanceProtocol.cs") `
        (Join-Path $ramblersRoot "src\CompanionAffordanceDriver.cs") `
        (Join-Path $PSScriptRoot "CompanionAffordanceActivationProbe.cs")
    if ($LASTEXITCODE -ne 0) {
        throw "Affordance activation probe compilation failed with exit code $LASTEXITCODE."
    }

    & $activationProbeOutput
    if ($LASTEXITCODE -ne 0) {
        throw "Affordance activation probe failed with exit code $LASTEXITCODE."
    }
}
finally {
    if (Test-Path -LiteralPath $probeRoot) {
        Remove-Item -LiteralPath $probeRoot -Recurse -Force
    }
}

Write-Host "Grounded-affordance protocol checks passed."
Write-Host "  Proven: exact turn-bound switches, keyed prerequisites, held-use actions, PlayerPose seats, and PropHome placement share one puzzle-agnostic lifecycle; actor-bound receipts, same-state completion, and down/up timing are executable."
Write-Host "  Not proven: live button effects, visible native seat entry, item placement, or model tool choice in the deployed game."
