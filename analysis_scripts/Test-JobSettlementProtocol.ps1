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
$job = Get-Content -LiteralPath (Join-Path $ramblersRoot "src\CompanionJob.cs") -Raw
$actions = Get-Content -LiteralPath (Join-Path $ramblersRoot "src\CompanionActions.cs") -Raw
$controller = Get-Content -LiteralPath (Join-Path $ramblersRoot "src\CompanionController.cs") -Raw
$bridge = Get-Content -LiteralPath (Join-Path $ramblersRoot "src\OpenAIRealtimeBridge.cs") -Raw
$inspection = Get-Content -LiteralPath (Join-Path $ramblersRoot "src\CompanionInspectionBehavior.cs") -Raw
$pickup = Get-Content -LiteralPath (Join-Path $ramblersRoot "src\CompanionPickupBehavior.cs") -Raw
$kick = Get-Content -LiteralPath (Join-Path $ramblersRoot "src\CompanionKickBehavior.cs") -Raw
$carry = Get-Content -LiteralPath (Join-Path $ramblersRoot "src\CompanionPlayerCarryBehavior.cs") -Raw

function Assert-Contains {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Needle,
        [Parameter(Mandatory = $true)][string]$Description
    )
    if ($Text.IndexOf($Needle, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Job-settlement check failed: $Description"
    }
}

function Assert-NotContains {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Needle,
        [Parameter(Mandatory = $true)][string]$Description
    )
    if ($Text.IndexOf($Needle, [System.StringComparison]::Ordinal) -ge 0) {
        throw "Job-settlement check failed: $Description"
    }
}

Assert-Contains $job 'bool MayPublishCompletionWhileActive { get; }' `
    "the job contract must make early publication an explicit exception"
Assert-Contains $actions 'CompanionJobSettlementProtocol.CanPublishCompletion(' `
    "the coordinator must gate every completion through the settlement rule"
Assert-Contains $controller 'controller._actions.IsJobSettled(' `
    "cancelled operations must retain their token until the exact job settles"
Assert-Contains $controller 'controller._jobLease.MarkCancellationRequested(now);' `
    "controller cancellation must update the executable token lease without clearing it"
Assert-Contains $controller 'completion = CompanionJobCompletion.Failed("cancelled");' `
    "a settled cancellation without a behavior completion must become terminal"
Assert-Contains $controller 'internal static bool DetachJob(long operationToken)' `
    "client replacement must transfer reconciliation ownership to the controller"
Assert-Contains $controller 'if (controller._jobLease.HasValue)' `
    "a live lease must block replacement even when requested capabilities are disjoint"
Assert-Contains $bridge 'TOOL_BATCH_RECONCILIATION_STARTED' `
    "cancellation settlement must emit a start marker"
Assert-Contains $bridge 'TOOL_BATCH_RECONCILED' `
    "cancellation settlement must emit a terminal marker before model output"
Assert-Contains $bridge 'TOOL_BATCH_RECONCILIATION_BOUNDED' `
    "stalled reconciliation must have an auditable deterministic bound"
Assert-Contains $bridge 'TOOL_BATCH_RECONCILIATION_ABANDONED' `
    "bounded ownership release must not masquerade as observed native reconciliation"
Assert-Contains $controller 'controller._actions.IsJobSettled(jobName)' `
    "bounded ownership release must verify the job actually released its capabilities"
Assert-Contains $controller 'CanReleaseLeaseAfterConclude(' `
    "ordinary conclusion must verify settlement before releasing the lease"
Assert-Contains $controller '_completionAwaitingSettlement' `
    "a completion must remain private if synchronous conclusion does not settle"
Assert-Contains $controller 'RevalidateCompletionForPublication(' `
    "every model-visible completion must revalidate its hands transition at consumption"
Assert-Contains $controller 'JOB_COMPLETION_INVALIDATED' `
    "a changed native hands state must be auditable without publishing stale context"
Assert-Contains $bridge 'RequestPendingJobCancellation(' `
    "timeout and interruption must enter a shared settlement path"
Assert-NotContains $inspection 'ReferenceHoldSeconds' `
    "inspection must not silently release before its explicit presentation conclusion"
foreach ($physicalJob in @($pickup, $kick, $carry)) {
    Assert-NotContains $physicalJob 'Faulted' `
        "an exact-identity mismatch must not strand capability ownership"
    Assert-Contains $physicalJob 'ownership returned to stock' `
        "identity failure must avoid retargeting while releasing Ramblers ownership"
}
Assert-Contains $pickup 'CompanionJobCompletion.Failed(' `
    "pickup release must be able to invalidate an unpublished holding success"
Assert-Contains $pickup '"pickup_not_retained"' `
    "pickup release must not publish a stale holding-item transition"
Assert-Contains $pickup 'RevalidateQueuedHoldCompletion();' `
    "pickup must revalidate its exact held prop at completion consumption"
Assert-Contains $carry '"player_pickup_not_retained"' `
    "player release must not publish a stale holding-player transition"
Assert-Contains $carry 'if (IsFullyHeldByCompanion())' `
    "player carry must revalidate every exact carry link at completion consumption"

$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$probeRoot = Join-Path $tempRoot (
    "RamblersJobSettlementProbe-" + [Guid]::NewGuid().ToString("N"))
$probeRoot = [System.IO.Path]::GetFullPath($probeRoot)
if (-not $probeRoot.StartsWith(
        $tempRoot,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Job settlement probe path escaped the temporary directory."
}

New-Item -ItemType Directory -Path $probeRoot | Out-Null
try {
    $probeOutput = Join-Path $probeRoot "CompanionJobSettlementProtocolProbe.exe"
    & $compiler `
        /nologo `
        /target:exe `
        /langversion:latest `
        /optimize+ `
        "/out:$probeOutput" `
        (Join-Path $ramblersRoot "src\CompanionJobSettlementProtocol.cs") `
        (Join-Path $PSScriptRoot "CompanionJobSettlementProtocolProbe.cs")
    if ($LASTEXITCODE -ne 0) {
        throw "Job settlement probe compilation failed with exit code $LASTEXITCODE."
    }

    & $probeOutput
    if ($LASTEXITCODE -ne 0) {
        throw "Job settlement probe failed with exit code $LASTEXITCODE."
    }
}
finally {
    if (Test-Path -LiteralPath $probeRoot) {
        Remove-Item -LiteralPath $probeRoot -Recurse -Force
    }
}

Write-Host "Job-settlement protocol checks passed."
Write-Host "  Proven: executable publication and token-lease rules are wired into cancellation, detachment, and bounded ownership."
Write-Host "  Not proven: live native reconciliation timing, Unity resource state, or presentation gaze duration."
