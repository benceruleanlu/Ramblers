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
        throw "Held-item drop protocol check failed: $Description"
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
        throw "Held-item drop protocol check failed: $Description"
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
        throw "Held-item drop protocol check failed: $Description"
    }
}

$catalog = Read-Source "src\AgentToolCatalog.cs"
$router = Read-Source "src\AgentToolRouter.cs"
$bridge = Read-Source "src\OpenAIRealtimeBridge.cs"
$jobs = Read-Source "src\CompanionJob.cs"
$actions = Read-Source "src\CompanionActions.cs"
$target = Read-Source "src\CompanionInteractionTarget.cs"
$pickup = Read-Source "src\CompanionPickupBehavior.cs"

Assert-Contains $catalog 'internal const string DropItem = "drop_item";' `
    "the allowlist must expose drop_item"
Assert-Contains $router 'case AgentToolCatalog.DropItem:' `
    "the router must dispatch drop_item"
Assert-Contains $router 'request.ActionName = jobName;' `
    "the shared held-item job must receive the selected operation"
Assert-Contains $jobs 'JobResources RequiredFor(CompanionJobRequest request);' `
    "resource arbitration must be operation-specific"
Assert-Contains $actions 'var wanted = job.RequiredFor(request);' `
    "the coordinator must reserve the requested operation's resources"
Assert-Contains $pickup '? JobResources.Hands' `
    "drop must claim hands without unnecessarily blocking locomotion"

Assert-Contains $target 'TryCaptureHeldProp(' `
    "drop must freeze the prop already in the companion's hands"
Assert-Contains $pickup 'CompanionInteractionTarget.TryCaptureHeldProp(' `
    "drop must carry the frozen held prop into its job"
Assert-Contains $pickup '_target.IsStillTheSameProp(hands.heldProp)' `
    "the host drop command must be gated by exact held-prop identity"
Assert-Contains $pickup 'ServerDropPropAutomatic(false)' `
    "drop must use the verified host authority entrypoint"
Assert-Order $pickup '_target.IsStillTheSameProp(hands.heldProp)' `
    'ServerDropPropAutomatic(false)' `
    "identity validation must appear before the parameterless host drop call"

Assert-Contains $pickup 'if (heldProp != null)' `
    "drop confirmation must continue while any prop remains held"
Assert-Contains $pickup 'now - _dropAbsentSince < DropAbsentSettlementSeconds' `
    "drop success must wait for a stable empty-hands observation"
Assert-Contains $pickup 'AgentToolCatalog.DropItem,' `
    "the terminal result must report the drop action"
Assert-Contains $pickup '"hands_empty"' `
    "empty hands must be explicit in the terminal state"
Assert-Contains $bridge 'AgentToolCatalog.DropItem,' `
    "new speech must interrupt a pending drop through reconciliation"

Assert-NotContains $pickup 'FindObjects' `
    "drop must never search for a replacement prop"
Assert-NotContains $pickup 'castProp' `
    "drop must never consult live gaze"
Assert-NotContains $pickup 'nearest' `
    "drop must never use nearest-item fallback"

Write-Host "Held-item drop protocol checks passed."
Write-Host "  Proven: tool routing, hands-only arbitration, exact held identity, host drop path, stable empty-hands confirmation."
Write-Host "  Not proven: Unity runtime release or visible held-item state."
