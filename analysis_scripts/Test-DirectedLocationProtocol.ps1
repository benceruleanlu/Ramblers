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
        throw "Directed-location protocol check failed: $Description"
    }
}

function Assert-NotContains {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Needle,
        [Parameter(Mandatory = $true)][string]$Description
    )
    if ($Text.IndexOf($Needle, [System.StringComparison]::Ordinal) -ge 0) {
        throw "Directed-location protocol check failed: $Description"
    }
}

$catalog = Read-Source "src\AgentToolCatalog.cs"
$router = Read-Source "src\AgentToolRouter.cs"
$job = Read-Source "src\CompanionJob.cs"
$actions = Read-Source "src\CompanionActions.cs"
$behavior = Read-Source "src\CompanionMoveToLocationBehavior.cs"
$approach = Read-Source "src\CompanionApproachController.cs"
$inspection = Read-Source "src\CompanionInspectionReferent.cs"

Assert-Contains $catalog 'internal const string GoToLocation = "go_to_location";' `
    "the general destination action must be model-callable"
Assert-Contains $catalog "'take me over there'" `
    "the tool prompt must cover natural carried-player direction"
Assert-Contains $catalog 'call pick_up_player and then go_to_location in the same response' `
    "pickup and travel must compose through ordered tool calls"
Assert-Contains $catalog 'never ask for coordinates or mention gaze, references, or targeting modes' `
    "the model must not expose internal destination mechanics"
Assert-Contains $catalog 'properties = new { }' `
    "direction must not require raw WASD or model-generated coordinates"

Assert-Contains $router 'CompanionInspectionSource.HumanGaze' `
    "routing must select the utterance-frozen indicated location"
Assert-Contains $router 'destination == null || !destination.GazeRayHit' `
    "movement must fail closed when the human did not indicate world geometry"
Assert-Contains $router 'MoveDestination = destination' `
    "the frozen destination must cross the typed job boundary"
Assert-Contains $router 'referenceId={destinationReferenceId}' `
    "target resolution must expose a call-scoped frozen destination identity"
Assert-Contains $router 'destinationPoint={destinationPoint}' `
    "target resolution must expose the exact frozen point for runtime evidence"
Assert-NotContains $router 'Camera.main.transform.position' `
    "tool execution must not recast a later camera direction"

Assert-Contains $job 'internal CompanionInspectionReferent MoveDestination;' `
    "the typed request must carry the exact frozen point"
Assert-Contains $actions 'new CompanionMoveToLocationBehavior(_locomotion, _attention, _jump)' `
    "destination travel must use ordinary resource arbitration"
Assert-Contains $behavior 'JobResources.Locomotion | JobResources.Gaze' `
    "directed travel must own motion and visible navigation attention"
Assert-Contains $behavior 'destination.Source != CompanionInspectionSource.HumanGaze' `
    "the job must accept only the intended gaze-point referent"
Assert-Contains $behavior '!destination.GazeRayHit' `
    "the job must not walk forty metres toward a no-hit fallback"
Assert-Contains $behavior '_approach.Advance(' `
    "directed travel must use the shared approach state machine"
Assert-Contains $approach '_locomotion.TrySteerToward(' `
    "the shared approach must reuse obstacle-aware locomotion"
Assert-Contains $approach '_jump.TryRequestActionRecovery(' `
    "blocked shared approaches must retain native jump recovery"
Assert-NotContains $behavior '_locomotion.TrySteerToward(' `
    "directed travel must not duplicate shared steering mechanics"
Assert-NotContains $behavior '_locomotion.ObserveProgress(' `
    "directed travel must not duplicate shared progress observation"
Assert-NotContains $behavior '_jump.TryRequestActionRecovery(' `
    "directed travel must not duplicate shared recovery mechanics"
Assert-NotContains $approach 'Plugin.Logger' `
    "the shared approach must return typed state instead of owning action telemetry"
Assert-NotContains $behavior 'MaximumRecoveries' `
    "an unobserved retry count must not block travel before the job timeout"
Assert-Contains $behavior 'verticalDistance <= ArrivalVerticalTolerance' `
    "arrival must not succeed directly above or below the frozen destination"
Assert-Contains $behavior 'if (horizontalDistance <= 0.0001f)' `
    "vertical-only separation must not create a NaN steering vector"
Assert-Contains $behavior 'TimeoutSecondsValue = 30f' `
    "the ordinary job lifecycle must remain the travel bound"
Assert-Contains $behavior 'IsBodyCarryingHuman(_body, human)' `
    "completion telemetry must derive passenger state from exact native hands"
Assert-Contains $behavior 'if (_requiresCarriedHuman && !IsCarryingHuman)' `
    "carried travel must not continue alone after losing the exact passenger"
Assert-Contains $behavior 'callId={CallIdForLog}, turnId={_turnId}' `
    "start and terminal movement evidence must remain correlated to its tool call"
Assert-Contains $behavior 'GO_TO_LOCATION_ARRIVED ' `
    "successful directed movement must emit an auditable terminal receipt"
Assert-Contains $behavior 'GO_TO_LOCATION_RECOVERY reason={approachStep.Reason}' `
    "directed movement must retain action-specific recovery telemetry"
Assert-NotContains $behavior 'SetFollowMode' `
    "directed travel must not mutate persistent follow intent"
Assert-NotContains $behavior 'UserCode_CmdPickUpPlayer' `
    "directed travel must not secretly pick up a player"
Assert-NotContains $behavior 'UserCode_CmdDropHeldPlayer' `
    "directed travel must not secretly drop a player"
Assert-NotContains $behavior 'FindObjects' `
    "directed travel must never search for a replacement destination"
Assert-Contains $inspection 'point = _frozenPoint;' `
    "gaze destinations must remain frozen after the utterance boundary"

Write-Host "Directed-location protocol checks passed."
Write-Host "  Proven: empty-argument natural tool, utterance-frozen hit point, shared locomotion, lifecycle-bounded recovery, and carry/follow composition boundaries."
Write-Host "  Not proven: in-game arrival path or model tool choice."
