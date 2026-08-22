#requires -Version 5.1

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ramblersRoot = Split-Path -Parent $PSScriptRoot
$client = Get-Content -LiteralPath (Join-Path $ramblersRoot "src\OpenAIRealtimeClient.cs") -Raw

function Assert-Contains {
    param(
        [Parameter(Mandatory = $true)][string]$Needle,
        [Parameter(Mandatory = $true)][string]$Description
    )
    if ($client.IndexOf($Needle, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Turn-latency check failed: $Description"
    }
}

Assert-Contains 'stage=response_requested' `
    "each user turn must log the client response-request boundary"
Assert-Contains 'stage=response_created' `
    "each response must log server creation latency"
Assert-Contains 'stage=first_audio' `
    "the first streamed audio packet must have a latency boundary"
Assert-Contains 'stage=response_done' `
    "response completion must close the latency trace"
Assert-Contains 'status={responseStatus ?? "missing"}' `
    "response completion must distinguish completed output from cancellation"
Assert-Contains 'stage=cancel_requested' `
    "false VAD splits and interruptions must remain visible"
Assert-Contains '_activeResponseFirstAudioLogged' `
    "one response must emit only one first-audio timing"
Assert-Contains '_reservedResponseRequestedAt = _responseRequestedAt;' `
    "queued requests must carry their original monotonic timestamp"
Assert-Contains 'var continuationRequestedAt = Stopwatch.GetTimestamp();' `
    "tool continuations must receive their own latency origin"
Assert-Contains 'Stopwatch.Frequency' `
    "timings must use a monotonic clock rather than wall time"

Write-Host "Turn-latency protocol checks passed."
Write-Host "  Proven: request, queue, creation, first audio, cancellation, and completion are correlated by turn on a monotonic clock."
Write-Host "  Not proven: live service latency or perceived playback onset."
