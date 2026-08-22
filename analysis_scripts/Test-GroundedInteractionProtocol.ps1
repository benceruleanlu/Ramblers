#requires -Version 5.1

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ramblersRoot = Split-Path -Parent $PSScriptRoot

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

$catalog = Read-Source "src\AgentToolCatalog.cs"
$router = Read-Source "src\AgentToolRouter.cs"
$bridge = Read-Source "src\OpenAIRealtimeBridge.cs"
$jobContract = Read-Source "src\CompanionJob.cs"
$actions = Read-Source "src\CompanionActions.cs"
$controller = Read-Source "src\CompanionController.cs"
$target = Read-Source "src\CompanionPeckTarget.cs"
$behavior = Read-Source "src\CompanionInteractBehavior.cs"
$awareness = Read-Source "src\CompanionAwareness.cs"
$entities = Read-Source "src\CompanionEntityReferences.cs"
$locomotion = Read-Source "src\CompanionLocomotion.cs"

Assert-Contains $catalog 'internal const string InteractWithObject = "interact_with_object";' `
    "the dormant implementation must retain one narrowly named interaction action"
Assert-NotContains $catalog 'name = InteractWithObject,' `
    "the unverified interaction wrapper chain must not be model-callable"
Assert-NotContains $catalog 'inspect_reference,interact_with_object,pick_up_item' `
    "the ready-tool log must not advertise the quarantined interaction"

Assert-NotContains $bridge 'CompanionController.TryCapturePeckCandidates(' `
    "speech completion must not traverse the unverified interaction wrapper chain"
Assert-Contains $bridge 'PeckCandidates = null' `
    "turn state must make the interaction quarantine explicit"
Assert-Contains $bridge 'PeckCaptureError = "interaction_context_quarantined"' `
    "dormant routing must fail closed if somehow invoked"
Assert-NotContains $bridge '[AGENT] TURN_INTERACTION_REFERENCES_CAPTURED' `
    "speech completion must not inspect or log interaction wrapper properties"
Assert-Contains $bridge 'AgentToolCatalog.InteractWithObject' `
    "dormant in-flight interaction jobs must still be recognized as physical actions"

Assert-Contains $router 'turnReference.PeckCandidates.TrySelect(' `
    "routing must select only from turn-bound interaction candidates"
Assert-Contains $router 'turnReference.EntityReferences.TryResolveInteraction(' `
    "context-selected switches must resolve from the exact turn-bound entity map"
Assert-Contains $router 'PeckTarget = peckTarget' `
    "the selected immutable target must cross the typed job boundary"
Assert-NotContains $router 'TryCapturePeckCandidates(' `
    "tool execution must not reinterpret a later gaze"

Assert-Contains $jobContract 'internal CompanionPeckCandidates PeckCandidates;' `
    "the turn contract must carry every exact interaction candidate"
Assert-Contains $jobContract 'internal CompanionPeckTarget PeckTarget;' `
    "the request contract must carry only the model-selected exact target"
Assert-Contains $actions 'new CompanionInteractBehavior(_locomotion, _attention, _jump)' `
    "the interaction must participate in normal job arbitration"
Assert-Contains $controller '[INTERACT] REFERENCE_CAPTURE_FAILED' `
    "an unsupported cast API must degrade without suppressing conversation"

Assert-Contains $target 'var castableTarget = human.caster.castableTarget;' `
    "capture must reuse Big Walk's validated local primary-interaction cast"
Assert-Contains $target 'var peckSwitch = heldProp.useHeldSwitch;' `
    "held props must use Big Walk's distinct primary-click switch"
Assert-Contains $target 'companion_held_item_not_interactable' `
    "a held prop without a use switch must fail instead of being substituted"
Assert-Contains $target 'castableTarget.GetCastableOutcome(body.Character, out outcome)' `
    "outcome conditions must be evaluated for the companion"
Assert-Contains $target 'internal static bool TryCaptureContextEntity(' `
    "bounded game context must freeze exact usable switches"
Assert-Contains $target 'castableTarget == null || !castableTarget.enabled' `
    "disabled interaction components must never enter actionable context"
Assert-Contains $target '_peckSwitch == null || !_peckSwitch.enabled' `
    "disabled switches must be rejected again before authority"
Assert-Contains $target 'outcome.peckSwitch.GetInstanceID() != _switchInstanceId' `
    "the selected switch must be revalidated without retargeting"
Assert-Contains $target '!_peckSwitch.isNotBlocked' `
    "game-owned blockers must be respected"
Assert-Contains $target 'body.Character.caster.CanStillReachSwitch(_peckSwitch)' `
    "world-switch reach must be validated immediately before acting"
Assert-Contains $target 'else if (!IsStillHeldBy(body))' `
    "held-use must revalidate that the exact prop remains in the companion's hands"
Assert-Contains $target '!NetworkServer.active || !_trackedState.isServer' `
    "the state transition must require host authority"
Assert-Contains $target 'PeckManager.SetState(_trackedState, activation.Context);' `
    "the actuator must use the authoritative switch-state path"
Assert-Contains $target '_trackedState.currentPeckContext' `
    "success must be confirmed from the same tracked switch state"
Assert-Contains $target 'TryValidateObservedState(out error)' `
    "postcondition confirmation must not require a one-shot switch to remain interactable"
Assert-Contains $target '_trackedState.GetInstanceID() != _stateInstanceId' `
    "postcondition confirmation must preserve frozen tracked-state identity"
Assert-NotContains $target '.CmdUsePeckSwitch(' `
    "a connectionless companion must not call the client command wrapper"
Assert-NotContains $target 'FindObjects' `
    "interaction must never search for a replacement switch"
Assert-NotContains $target 'nearest' `
    "interaction must never fall back to proximity selection"

Assert-Contains $behavior 'JobResources.Locomotion | JobResources.Gaze' `
    "following must pause while the companion visibly interacts"
Assert-Contains $behavior 'JobResources.Gaze | JobResources.Hands' `
    "primary interaction must reserve the hands capability"
Assert-Contains $behavior '_target?.IsWorldTarget == true' `
    "held-item interaction must not claim or stop locomotion"
Assert-Contains $behavior 'if (_target.IsWorldTarget)' `
    "held-item interaction must not reset follow progress observation"
Assert-Contains $behavior '_attention.IsAimWithin(' `
    "the companion must visibly align before crossing authority"
Assert-Order $behavior '_target.TryPrepare(_body, out _activation, out error)' `
    '_target.TryActivate(_activation, out error)' `
    "final validation must immediately precede mutation"
Assert-Contains $behavior '_target.TryObserveActivation(' `
    "the tool must wait for state confirmation"
Assert-Contains $behavior '[INTERACT] CONFIRMED' `
    "runtime evidence must distinguish a confirmed interaction"
Assert-Contains $behavior 'InteractionState.Approaching' `
    "out-of-reach world interactions must enter an explicit approach phase"
Assert-Contains $behavior '_locomotion.TrySteerToward(' `
    "interaction approach must reuse obstacle-aware locomotion"
Assert-Contains $behavior '_jump.TryRequestActionRecovery(' `
    "stalled interaction travel must have bounded grounded recovery"
Assert-Contains $behavior '_locomotion.HasGroundSupportAhead(direction, committedDistance)' `
    "interaction recovery must not commit across unsupported ground"
Assert-Contains $behavior '(ApproachCommitSeconds + ApproachNavigationInterval)' `
    "interaction recovery proof must cover the first navigation tick after commit expiry"
Assert-Contains $behavior '_approachCommitDirection = direction;' `
    "interaction recovery must freeze the direction that its support proof covered"
Assert-Contains $behavior '_jump.CancelActionRecovery(AgentToolCatalog.InteractWithObject)' `
    "cancelled interaction work must remove its queued recovery jump"
Assert-Contains $behavior '[INTERACT] APPROACH_REACHED' `
    "runtime evidence must separate arrival from activation"
Assert-Contains $locomotion 'internal bool HasGroundSupportAhead(' `
    "ordinary steering must sample forward ground rather than only current slope"
Assert-Contains $locomotion 'layerMask &= ~(1 << bodyCollider.gameObject.layer);' `
    "ground probes must exclude the companion capsule from floor evidence"
Assert-NotContains $locomotion 'layerMask &= ~(1 << _body.GameObject.layer);' `
    "ground probes must retain shared environment layers"
Assert-Contains $actions 'if (posture == CompanionPosture.Sitting && locomotionHolder != null)' `
    "a tool batch must not seat the companion during admitted locomotion work"
Assert-NotContains $awareness 'nearby_interactables = nearbyInteractables' `
    "ambient switch discovery must stay out of the spoken-turn crash surface"
Assert-NotContains $awareness 'Resources.FindObjectsOfTypeAll<CastableTarget>()' `
    "the unverified CastableTarget generic scan must remain quarantined"
Assert-NotContains $awareness 'FindObjectsByType<CastableTarget>(' `
    "an unprobed stripped Unity API must not enter the runtime path"
Assert-Contains $entities '_interactables.TryGetValue(stableId, out target)' `
    "interaction IDs must resolve exactly without proximity fallback"

Write-Host "Grounded-interaction protocol checks passed."
Write-Host "  Proven: the interaction implementation remains exact-targeted and fails closed, while its tool and all speech-time interaction capture are quarantined."
Write-Host "  Not proven: spoken-turn native safety of interaction capture, named ambient discovery, live light wiring, visible on/off result, or compatibility with puzzle interactions."
