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
        throw "Inspection-reference grounding check failed: $Description"
    }
}

function Assert-NotContains {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Needle,
        [Parameter(Mandatory = $true)][string]$Description
    )
    if ($Text.IndexOf($Needle, [System.StringComparison]::Ordinal) -ge 0) {
        throw "Inspection-reference grounding check failed: $Description"
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
        throw "Inspection-reference grounding check failed: $Description"
    }
}

$catalog = Read-Source "src\AgentToolCatalog.cs"
$router = Read-Source "src\AgentToolRouter.cs"
$bridge = Read-Source "src\OpenAIRealtimeBridge.cs"
$candidates = Read-Source "src\CompanionInspectionReferent.cs"
$interactionTarget = Read-Source "src\CompanionInteractionTarget.cs"
$inspection = Read-Source "src\CompanionInspectionBehavior.cs"

Assert-Contains $catalog '@enum = new[] { "human_held_item", "human_gaze" }' `
    "the model must choose only between the two frozen visual meanings"
Assert-Contains $catalog 'Infer the target silently' `
    "referent resolution must not become a spoken option menu"
Assert-Contains $catalog "'look at this'" `
    "deictic held-item language must be explicitly grounded"
Assert-Contains $catalog 'Never ask the human to choose' `
    "the companion must act directly on a clear request"

Assert-Contains $bridge 'CompanionController.TryCaptureInspectionCandidates(' `
    "both inspection candidates must be frozen at the utterance boundary"
Assert-Order $bridge 'CompanionController.TryCaptureInspectionCandidates(' `
    '_client.RequestResponse(turnId);' `
    "candidate capture must precede the model response"
Assert-Contains $bridge 'InspectionCandidates = inspectionCandidates' `
    "the captured candidates must stay bound to the same turn id"
Assert-Contains $bridge 'gazeReason={inspectionCandidates.GazeCaptureError' `
    "runtime evidence must retain a failed gaze candidate's boundary reason"
Assert-Contains $bridge 'heldItemReason={inspectionCandidates.HeldItemCaptureError' `
    "runtime evidence must retain a failed held-item candidate's boundary reason"

Assert-Contains $router 'CompanionInspectionSource.HumanHeldItem' `
    "the hidden tool choice must route held-item language"
Assert-Contains $router 'CompanionInspectionSource.HumanGaze' `
    "the hidden tool choice must preserve directional gaze requests"
Assert-Contains $router 'turnReference.InspectionCandidates.TrySelect(' `
    "routing must select only from the frozen turn candidates"
Assert-Contains $router 'InspectionReferent = referent' `
    "the selected immutable referent must cross the typed job boundary"

Assert-Contains $candidates 'human.hands.heldProp' `
    "the human-held prop must be captured at the boundary"
Assert-Contains $candidates 'CompanionInteractionTarget.TryCaptureHeldProp(' `
    "held-item capture must reuse exact managed and network identity"
Assert-Contains $candidates 'CompanionInspectionReferent.FromGaze(' `
    "gaze selection must use the previously captured point"
Assert-NotContains $candidates 'FindObjects' `
    "inspection must never search for a replacement object"
Assert-NotContains $candidates 'nearest' `
    "inspection must never substitute the nearest object"

Assert-Contains $interactionTarget 'TryGetCurrentInspectionPoint' `
    "a moving held item must expose its current point without changing identity"
Assert-Contains $interactionTarget 'IsStillTheSameProp(Prop)' `
    "held-item tracking must revalidate the exact frozen object"

Assert-Contains $inspection 'request.InspectionReferent' `
    "the job must consume the turn-bound referent"
Assert-Contains $inspection '_inspectionReferent.TryGetCurrentPoint' `
    "the job must track only the selected held object as it moves"
Assert-Contains $inspection '_inspectionReferent.UnavailableError' `
    "a vanished exact target must fail with its selected source rather than retarget"
Assert-Contains $inspection 'referenceSource={_inspectionReferent.SourceLabel}' `
    "runtime evidence must report which visual meaning was used"
Assert-NotContains $inspection 'Physics.Raycast' `
    "the multi-frame job must not cast a newer gaze ray"
Assert-NotContains $inspection 'TryResolveReference' `
    "the old late reference resolution must remain removed"

Write-Host "Inspection-reference grounding checks passed."
Write-Host "  Proven: boundary-time dual capture, silent semantic selection, exact held identity, moving-target tracking, frozen gaze, no late raycast, and source telemetry."
Write-Host "  Not proven: live model target choice, visible gaze direction, or captured image composition."
