#requires -Version 5.1

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$audit = Get-Content -LiteralPath (Join-Path $PSScriptRoot "Audit-LatestRun.ps1") -Raw
$bridge = Get-Content -LiteralPath (Join-Path $PSScriptRoot "..\src\OpenAIRealtimeBridge.cs") -Raw
$client = Get-Content -LiteralPath (Join-Path $PSScriptRoot "..\src\OpenAIRealtimeClient.cs") -Raw
$plugin = Get-Content -LiteralPath (Join-Path $PSScriptRoot "..\src\RamblersPlugin.cs") -Raw
$build = Get-Content -LiteralPath (Join-Path $PSScriptRoot "..\build.ps1") -Raw

function Assert-Contains {
    param(
        [Parameter(Mandatory = $true)][string]$Needle,
        [Parameter(Mandatory = $true)][string]$Description
    )
    if ($audit.IndexOf($Needle, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Runtime-audit check failed: $Description"
    }
}

Assert-Contains 'Source proof: version=$sourceVersion commit=$gitHead' `
    "the report must identify source version and commit"
Assert-Contains '& $buildPath -NoRestore -GamePath $GamePath' `
    "the gate must freshly compile current source before accepting build proof"
Assert-Contains 'Build proof: fresh=$buildSucceeded hash=$distHash' `
    "the report must distinguish fresh build identity"
Assert-Contains 'Deployment proof: hash=$deployedHash' `
    "the report must distinguish installed identity"
Assert-Contains 'Codec proof: builtHash=$distCodecHash deployedHash=$deployedCodecHash' `
    "the report must bind the deployed JPEG codec to the built dependency"
Assert-Contains 'Runtime proof: loadedVersion=$loadedVersion loadedHash=$loadedHash ready=$ready' `
    "the report must distinguish startup/runtime identity"
Assert-Contains 'Runtime mismatch: the latest run loaded a different DLL than the one currently deployed.' `
    "runtime evidence must bind to the exact deployed assembly"
Assert-Contains '@($sessionLines -match ''\[AGENT\] READY '').Count -gt 0' `
    "one unmatched scalar log line must not masquerade as READY"
Assert-Contains 'Visual QA: not assessed by this command' `
    "the report must not present structured evidence as visual proof"
Assert-Contains 'Deployment mismatch: built and installed DLL hashes differ.' `
    "a stale deployment must fail the gate"
Assert-Contains 'Identity mismatch:' `
    "multiple immutable identities for one turn/action must fail the gate"
Assert-Contains 'without a completed tool batch' `
    "successful physical actions must have a terminal batch"
Assert-Contains 'this may be a stale blocker' `
    "an in-progress result without an active job must fail the gate"
Assert-Contains 'still has an unresolved deferred tool batch' `
    "unfinished jobs at log end must fail the gate"
Assert-Contains 'still has an unreleased presentation job' `
    "inspection presentation holds must be released before the run passes"
Assert-Contains 'discarded its tool output batch' `
    "rejected function output must fail the gate"
Assert-Contains 'without deferring that tool batch' `
    "stale blockers must be correlated by exact response rather than historical turn"
Assert-Contains 'stop or protocol error(s) after its latest READY' `
    "a dead or protocol-erroring Realtime client must not pass from historical readiness"
Assert-Contains 'no turn completed request, creation, first audio, and response.done in order' `
    "partial or crashed turns must not satisfy required spoken proof"
Assert-Contains 'status=completed.*firstAudioSeen=True' `
    "cancelled responses with partial audio must not satisfy spoken-turn proof"
Assert-Contains 'has a response request with no later response.done acknowledgement' `
    "a cancellation request without server completion must remain unfinished"
Assert-Contains 'stage=(?<stage>[^,]+)' `
    "turn-latency stages must be included in the compact report"
Assert-Contains 'Set-Content -LiteralPath $OutputPath' `
    "the audit must leave a diagnostic artifact"
Assert-Contains 'exit 1' `
    "invariant violations must produce a non-zero exit"
if ($plugin.IndexOf('assemblySha256={assemblySha256}', [System.StringComparison]::Ordinal) -lt 0) {
    throw "Runtime-audit check failed: startup must log the exact loaded assembly hash"
}
if ($build.IndexOf('System.Security.Cryptography.Algorithms.dll', [System.StringComparison]::Ordinal) -lt 0 -or
    $build.IndexOf('System.Security.Cryptography.Primitives.dll', [System.StringComparison]::Ordinal) -lt 0) {
    throw "Runtime-audit check failed: the build must include the managed assembly-hash dependencies"
}
if ($client.IndexOf('_logs.Enqueue("CONNECTION_STOPPED")', [System.StringComparison]::Ordinal) -lt 0) {
    throw "Runtime-audit check failed: a clean client disconnect must emit terminal evidence"
}
$stoppedMarkerIndex = $client.IndexOf('_logs.Enqueue("CONNECTION_STOPPED")', [System.StringComparison]::Ordinal)
$stoppedPublishIndex = $client.IndexOf('_stopped = true;', $stoppedMarkerIndex, [System.StringComparison]::Ordinal)
if ($stoppedPublishIndex -lt $stoppedMarkerIndex) {
    throw "Runtime-audit check failed: terminal evidence must queue before stopped-client publication"
}
if ($client.IndexOf('if (!_initialSessionConfigured)', [System.StringComparison]::Ordinal) -lt 0 -or
    $client.IndexOf('_logs.Enqueue("SESSION_UPDATED")', [System.StringComparison]::Ordinal) -lt 0) {
    throw "Runtime-audit check failed: later mode updates must not masquerade as a new connection READY"
}
$drainIndex = $bridge.IndexOf('DrainClientEvents();', [System.StringComparison]::Ordinal)
$ensureIndex = $bridge.IndexOf('EnsureClient();', [System.StringComparison]::Ordinal)
if ($drainIndex -lt 0 -or $ensureIndex -lt 0 -or $drainIndex -gt $ensureIndex) {
    throw "Runtime-audit check failed: stopped-client logs must drain before client replacement"
}

Write-Host "Runtime-audit protocol checks passed."
Write-Host "  Proven: the local gate separates evidence layers and detects deployment, identity, lifecycle, stale-blocker, and unfinished-job failures."
Write-Host "  Not proven: live log coverage until the new DLL records a spoken/action turn."
