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

Assert-Contains $catalog 'internal const string InteractWithObject = "interact_with_object";' `
    "the model must receive one narrowly named primary-interaction tool"
Assert-Contains $catalog 'turn that on or off, press that, activate that, or use that' `
    "natural switch requests must route directly to the tool"
Assert-Contains $catalog '@enum = new[] { "human_reference", "companion_held_item" }' `
    "the model may resolve a world switch or the exact prop it already holds"
Assert-Contains $catalog 'Never ask the human to choose or announce this distinction.' `
    "internal interaction targeting must remain invisible in conversation"
Assert-Contains $catalog 'never substitute another object' `
    "the schema must prohibit fallback targeting"

Assert-Contains $bridge 'CompanionController.TryCapturePeckCandidates(' `
    "all game-owned usable references must be frozen at the utterance boundary"
Assert-Order $bridge 'CompanionController.TryCapturePeckCandidates(' `
    '_client.RequestResponse(turnId);' `
    "interaction capture must precede the model response"
Assert-Contains $bridge 'PeckCandidates = peckCandidates' `
    "the frozen interaction candidates must stay bound to their response turn"
Assert-Contains $bridge '[AGENT] TURN_INTERACTION_REFERENCES_CAPTURED' `
    "capture success and boundary failures must be observable"
Assert-Contains $bridge 'heldItemReason={peckCandidates.CompanionHeldItemError' `
    "held-use capture failures must remain diagnosable"
Assert-Contains $bridge 'AgentToolCatalog.InteractWithObject' `
    "a new utterance must recognize the job as a physical action"

Assert-Contains $router 'turnReference.PeckCandidates.TrySelect(' `
    "routing must select only from turn-bound interaction candidates"
Assert-Contains $router 'PeckTarget = peckTarget' `
    "the selected immutable target must cross the typed job boundary"
Assert-NotContains $router 'TryCapturePeckCandidates(' `
    "tool execution must not reinterpret a later gaze"

Assert-Contains $jobContract 'internal CompanionPeckCandidates PeckCandidates;' `
    "the turn contract must carry every exact interaction candidate"
Assert-Contains $jobContract 'internal CompanionPeckTarget PeckTarget;' `
    "the request contract must carry only the model-selected exact target"
Assert-Contains $actions 'new CompanionInteractBehavior(_attention)' `
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
Assert-Contains $behavior '_attention.IsAimWithin(' `
    "the companion must visibly align before crossing authority"
Assert-Order $behavior '_target.TryPrepare(_body, out _activation, out error)' `
    '_target.TryActivate(_activation, out error)' `
    "final validation must immediately precede mutation"
Assert-Contains $behavior '_target.TryObserveActivation(' `
    "the tool must wait for state confirmation"
Assert-Contains $behavior '[INTERACT] CONFIRMED' `
    "runtime evidence must distinguish a confirmed interaction"
Assert-NotContains $behavior '_locomotion.' `
    "this first slice must not quietly expand into remote travel or fetch"

Write-Host "Grounded-interaction protocol checks passed."
Write-Host "  Proven: boundary-time world/held candidate capture, exact identity, companion conditions, reach/held checks, host authority, visible aim, state confirmation, and no fallback target."
Write-Host "  Not proven: the light's live switch wiring, visible on/off result, or compatibility with every puzzle interaction."
