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
$bridge = Get-Content -LiteralPath (Join-Path $ramblersRoot "src\OpenAIRealtimeBridge.cs") -Raw
$client = Get-Content -LiteralPath (Join-Path $ramblersRoot "src\OpenAIRealtimeClient.cs") -Raw

function Assert-Contains {
    param(
        [Parameter(Mandatory = $true)][string]$Needle,
        [Parameter(Mandatory = $true)][string]$Description
    )
    if ($bridge.IndexOf($Needle, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Sequential tool-batch check failed: $Description"
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
        throw "Sequential tool-batch check failed: $Description"
    }
}

function Assert-ClientContains {
    param(
        [Parameter(Mandatory = $true)][string]$Needle,
        [Parameter(Mandatory = $true)][string]$Description
    )
    if ($client.IndexOf($Needle, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Sequential tool-batch check failed: $Description"
    }
}

Assert-Contains 'SequentialToolBatchCursor Cursor;' `
    "every function-call batch must own a serialized cursor"
Assert-Contains 'DispatchPendingToolCalls(pending);' `
    "batch execution must use one sequential dispatcher"
Assert-Contains 'TryApplyTurnTransition(' `
    "verified physical state must advance the turn between calls"
Assert-Contains 'CompleteCurrentJobBeforeNextCall(' `
    "a finished job must release its capability reservations before the next call"
Assert-Contains 'FailUndispatchedCalls(' `
    "a broken causal transition must fail closed instead of dispatching stale work"
Assert-Contains 'ShouldAbortRemainderAfterFailure(slot.Call, result)' `
    "a deferred player-pickup failure must stop its dependent movement"
Assert-Contains 'ShouldAbortRemainderAfterFailure(functionCall, result)' `
    "an immediate player-pickup failure must stop its dependent movement"
Assert-Contains 'FailUndispatchedCalls(pending, "previous_action_failed")' `
    "dependent calls must receive an explicit causal failure"
Assert-Order $bridge 'PollPendingToolBatches();' 'DrainFunctionCallBatches();' `
    "job completions must be consumed before new calls can dispatch"
Assert-Contains ': "action_interrupted");' `
    "an interrupted job must not expose later calls from its old response"
Assert-Contains 'RequestPendingJobCancellation(' `
    "interruption and timeout must wait through the same cancellation lifecycle"
Assert-Contains 'FinishPendingJob(pending, completion);' `
    "a deferred slot must finish only after the controller publishes settlement"
Assert-Contains 'PresentationRetentionPolicy.MustEndBatchBeforeNextCall(' `
    "a presentation must end its batch before any call that has not observed it"
Assert-Contains 'PRESENTATION_SEQUENCE_TERMINATED' `
    "a multi-call presentation boundary must be auditable"
Assert-ClientContains 'HasFunctionCallBatch = hasFunctionCallBatch' `
    "response completion must carry causal tool-batch metadata across queues"
Assert-Contains 'clientEvent.HasFunctionCallBatch' `
    "turn lifetime must use response metadata rather than frame timing"
$finishStart = $bridge.IndexOf(
    'private void FinishPendingJob(',
    [System.StringComparison]::Ordinal)
$finishEnd = $bridge.IndexOf(
    'private static void RequestPendingJobCancellation(',
    $finishStart,
    [System.StringComparison]::Ordinal)
if ($finishStart -lt 0 -or $finishEnd -le $finishStart) {
    throw "Sequential tool-batch check failed: completion dispatcher boundaries were unavailable"
}
$finishPendingJob = $bridge.Substring($finishStart, $finishEnd - $finishStart)
Assert-Order $finishPendingJob 'TryApplyTurnTransition(' 'DispatchPendingToolCalls(pending);' `
    "verified turn state must advance before a later call is routed"

$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$probeRoot = Join-Path $tempRoot (
    "RamblersSequentialBatchProbe-" + [Guid]::NewGuid().ToString("N"))
$probeRoot = [System.IO.Path]::GetFullPath($probeRoot)
if (-not $probeRoot.StartsWith(
        $tempRoot,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Sequential batch probe path escaped the temporary directory."
}

New-Item -ItemType Directory -Path $probeRoot | Out-Null
try {
    $probeOutput = Join-Path $probeRoot "SequentialToolBatchCursorProbe.exe"
    & $compiler `
        /nologo `
        /target:exe `
        /langversion:latest `
        /optimize+ `
        "/out:$probeOutput" `
        (Join-Path $ramblersRoot "src\SequentialToolBatchCursor.cs") `
        (Join-Path $ramblersRoot "src\TurnReferenceRetentionPolicy.cs") `
        (Join-Path $PSScriptRoot "SequentialToolBatchCursorProbe.cs")
    if ($LASTEXITCODE -ne 0) {
        throw "Sequential tool-batch cursor probe compilation failed with exit code $LASTEXITCODE."
    }

    & $probeOutput
    if ($LASTEXITCODE -ne 0) {
        throw "Sequential tool-batch cursor probe failed with exit code $LASTEXITCODE."
    }
}
finally {
    if (Test-Path -LiteralPath $probeRoot) {
        Remove-Item -LiteralPath $probeRoot -Recurse -Force
    }
}

Write-Host "Sequential tool-batch protocol checks passed."
Write-Host "  Proven: calls cannot overlap or reorder; verified state advancement precedes later dispatch."
Write-Host "  Proven: deferred and immediate tool responses retain their frozen turn reference independent of client-queue frame timing."
Write-Host "  Not proven: live model emission of multi-call responses or deployed Unity behavior."
