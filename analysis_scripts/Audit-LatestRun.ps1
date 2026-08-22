#requires -Version 5.1

[CmdletBinding()]
param(
    [string]$GamePath,
    [switch]$RequireTurn,
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ramblersRoot = Split-Path -Parent $PSScriptRoot
$gamePathResolver = Join-Path $ramblersRoot "scripts\Resolve-BigWalkGamePath.ps1"
$sharedLogReader = Join-Path $ramblersRoot "scripts\Read-SharedLogLines.ps1"
if (-not (Test-Path -LiteralPath $gamePathResolver -PathType Leaf)) {
    throw "Big Walk path resolver is missing: $gamePathResolver"
}
if (-not (Test-Path -LiteralPath $sharedLogReader -PathType Leaf)) {
    throw "Shared log reader is missing: $sharedLogReader"
}
. $gamePathResolver
. $sharedLogReader

if ([string]::IsNullOrWhiteSpace($GamePath)) {
    $GamePath = $env:RAMBLERS_GAME_PATH
}
if ([string]::IsNullOrWhiteSpace($GamePath)) {
    $GamePath = Find-BigWalkPath
}
if ([string]::IsNullOrWhiteSpace($GamePath)) {
    throw "Big Walk was not found in the registered Steam libraries. Pass -GamePath or set RAMBLERS_GAME_PATH."
}
$GamePath = [System.IO.Path]::GetFullPath($GamePath)

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $auditDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "Ramblers"
    $OutputPath = Join-Path $auditDirectory "latest-runtime-audit.txt"
}
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)

$sourcePath = Join-Path $ramblersRoot "src\RamblersPlugin.cs"
$buildPath = Join-Path $ramblersRoot "build.ps1"
$distPath = Join-Path $ramblersRoot "dist\Ramblers.dll"
$distCodecPath = Join-Path $ramblersRoot "dist\StbImageWriteSharp.dll"
$pluginPath = Join-Path $GamePath "BepInEx\plugins\Ramblers\Ramblers.dll"
$pluginCodecPath = Join-Path $GamePath "BepInEx\plugins\Ramblers\StbImageWriteSharp.dll"
$logPath = Join-Path $GamePath "BepInEx\LogOutput.log"

$failures = New-Object System.Collections.Generic.List[string]
$warnings = New-Object System.Collections.Generic.List[string]
$report = New-Object System.Collections.Generic.List[string]

function Add-Failure {
    param([Parameter(Mandatory = $true)][string]$Message)
    $script:failures.Add($Message)
}

function Add-Warning {
    param([Parameter(Mandatory = $true)][string]$Message)
    $script:warnings.Add($Message)
}

function File-HashOrMissing {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return "missing"
    }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
    throw "Ramblers source version file is missing: $sourcePath"
}
$source = Get-Content -LiteralPath $sourcePath -Raw
$versionMatch = [regex]::Match(
    $source,
    'public const string Version = "(?<version>[^"]+)";')
if (-not $versionMatch.Success) {
    throw "Could not read Ramblers source version from $sourcePath"
}
$sourceVersion = $versionMatch.Groups["version"].Value
$buildSucceeded = $false
try {
    & $buildPath -NoRestore -GamePath $GamePath
    if ($LASTEXITCODE -ne 0) {
        throw "build.ps1 exited with code $LASTEXITCODE"
    }
    $buildSucceeded = $true
}
catch {
    Add-Failure "Fresh build failed: $($_.Exception.Message)"
}
$distHash = File-HashOrMissing $distPath
$distCodecHash = File-HashOrMissing $distCodecPath
$deployedHash = File-HashOrMissing $pluginPath
$deployedCodecHash = File-HashOrMissing $pluginCodecPath

if ($distHash -eq "missing") {
    Add-Failure "Build proof missing: dist/Ramblers.dll does not exist."
}
if ($deployedHash -eq "missing") {
    Add-Failure "Deployment proof missing: the installed Ramblers.dll does not exist."
}
if ($distCodecHash -eq "missing") {
    Add-Failure "Build proof missing: dist/StbImageWriteSharp.dll does not exist."
}
if ($deployedCodecHash -eq "missing") {
    Add-Failure "Deployment proof missing: the installed StbImageWriteSharp.dll does not exist."
}
if ($distHash -ne "missing" -and $deployedHash -ne "missing" -and
    $distHash -ne $deployedHash) {
    Add-Failure "Deployment mismatch: built and installed DLL hashes differ."
}
if ($distCodecHash -ne "missing" -and $deployedCodecHash -ne "missing" -and
    $distCodecHash -ne $deployedCodecHash) {
    Add-Failure "Deployment mismatch: built and installed JPEG codec hashes differ."
}

$sessionLines = @()
$loadedVersion = "missing"
$loadedHash = "missing"
$ready = $false
$logTimestamp = "missing"
if (-not (Test-Path -LiteralPath $logPath -PathType Leaf)) {
    Add-Failure "Runtime proof missing: BepInEx/LogOutput.log does not exist."
}
else {
    $logItem = Get-Item -LiteralPath $logPath
    $logTimestamp = $logItem.LastWriteTime.ToString("o")
    $allLines = @(Read-SharedLogLines $logPath)
    $sessionStart = -1
    for ($index = 0; $index -lt $allLines.Length; $index++) {
        if ($allLines[$index] -match '\[RAMBLERS\] Loaded version (?<version>\d+\.\d+\.\d+)(?:, assemblySha256=(?<hash>[A-Fa-f0-9]+|unavailable))?\.') {
            $sessionStart = $index
            $loadedVersion = $Matches["version"]
            $loadedHash = if ($Matches.ContainsKey("hash") -and
                -not [string]::IsNullOrWhiteSpace($Matches["hash"])) {
                $Matches["hash"].ToUpperInvariant()
            }
            else {
                "missing"
            }
        }
    }
    if ($sessionStart -lt 0) {
        Add-Failure "Runtime proof missing: no Ramblers startup marker exists in the latest log."
    }
    else {
        $sessionLines = $allLines[$sessionStart..($allLines.Length - 1)]
        $ready = @($sessionLines -match '\[AGENT\] READY ').Count -gt 0
        if (-not $ready) {
            Add-Failure "Runtime proof incomplete: OpenAI Realtime did not reach READY."
        }
        if ($loadedVersion -ne $sourceVersion) {
            Add-Failure "Startup mismatch: source is $sourceVersion but the latest run loaded $loadedVersion."
        }
        if ($loadedHash -eq "missing" -or $loadedHash -eq "unavailable") {
            Add-Failure "Runtime identity missing: the latest startup did not log its loaded assembly hash."
        }
        elseif ($deployedHash -ne "missing" -and $loadedHash -ne $deployedHash) {
            Add-Failure "Runtime mismatch: the latest run loaded a different DLL than the one currently deployed."
        }
        $ramblersErrors = @($sessionLines | Where-Object {
            $_ -match '^\[(Error|Fatal)\s*:\s*Ramblers\]'
        })
        if ($ramblersErrors.Count -gt 0) {
            Add-Failure "The latest run contains $($ramblersErrors.Count) Ramblers error log(s)."
        }

        $lastReadyIndex = -1
        for ($index = 0; $index -lt $sessionLines.Count; $index++) {
            if ($sessionLines[$index] -match '\[AGENT\] READY ') {
                $lastReadyIndex = $index
            }
        }
        if ($lastReadyIndex -ge 0 -and $lastReadyIndex -lt $sessionLines.Count - 1) {
            $clientFailures = @($sessionLines[($lastReadyIndex + 1)..($sessionLines.Count - 1)] |
                Where-Object {
                    $_ -match '\[AGENT\] (CONNECTION_ERROR|CONNECTION_STOPPED|API_ERROR|INVALID_EVENT_JSON)'
                })
            if ($clientFailures.Count -gt 0) {
                Add-Failure "The Realtime client reported $($clientFailures.Count) stop or protocol error(s) after its latest READY."
            }
        }
    }
}

$turns = @{}
$activeJobs = @{}
$deferredResponses = @{}
$possibleStaleBlockers = New-Object System.Collections.Generic.List[object]
$completedResponses = @{}
$successfulPhysicalCalls = @{}
$activePresentationJobs = @{}
$discardedResponses = New-Object System.Collections.Generic.List[string]
$resolvedIdentities = @{}
$physicalActions = @(
    "inspect_reference",
    "interact_with_object",
    "pick_up_item",
    "kick_item",
    "drop_item"
)

foreach ($line in $sessionLines) {
    if ($line -match 'TURN_LATENCY turnId=(?<turn>\d+), stage=(?<stage>[^,]+)(?<details>.*)$') {
        $turnId = [long]$Matches["turn"]
        if (-not $turns.ContainsKey($turnId)) {
            $turns[$turnId] = New-Object System.Collections.Generic.List[string]
        }
        $turns[$turnId].Add($Matches["stage"] + $Matches["details"])
    }

    if ($line -match '\[AGENT\] TURN_REFERENCE_CAPTURED .*turnId=(?<turn>\d+), (?<details>.*)$') {
        $turnId = [long]$Matches["turn"]
        if (-not $turns.ContainsKey($turnId)) {
            $turns[$turnId] = New-Object System.Collections.Generic.List[string]
        }
        $turns[$turnId].Add("reference " + $Matches["details"])
    }

    if ($line -match '\[AWARENESS\] TURN_CONTEXT_CAPTURED .*turnId=(?<turn>\d+), (?<details>.*)$') {
        $turnId = [long]$Matches["turn"]
        if (-not $turns.ContainsKey($turnId)) {
            $turns[$turnId] = New-Object System.Collections.Generic.List[string]
        }
        $turns[$turnId].Add("context " + $Matches["details"])
    }

    if ($line -match '\[ENTITY\] TARGET_RESOLVED action=(?<action>[^,]+), .*referenceId=(?<reference>[^,]+), .*turnId=(?<turn>\d+)') {
        $identityKey = $Matches["turn"] + "|" + $Matches["action"]
        if (-not $resolvedIdentities.ContainsKey($identityKey)) {
            $resolvedIdentities[$identityKey] = New-Object System.Collections.Generic.HashSet[string]
        }
        [void]$resolvedIdentities[$identityKey].Add($Matches["reference"])
        $turnId = [long]$Matches["turn"]
        if (-not $turns.ContainsKey($turnId)) {
            $turns[$turnId] = New-Object System.Collections.Generic.List[string]
        }
        $turns[$turnId].Add(
            "target action=$($Matches["action"]), referenceId=$($Matches["reference"])")
    }

    if ($line -match 'TOOL_BATCH_DEFERRED responseId=(?<response>[^,]+), turnId=(?<turn>\d+)') {
        $response = $Matches["response"]
        $activeJobs[$response] = $Matches["turn"]
        $deferredResponses[$response] = $true
    }

    if ($line -match '\[AGENT\] CALL name=(?<action>[^,]+), .*turnId=(?<turn>\d+), responseId=(?<response>[^,]+), result=(?<result>\{.*\}), diagnosticError=(?<error>[^,\s]+)') {
        $action = $Matches["action"]
        $turn = $Matches["turn"]
        $response = $Matches["response"]
        $resultJson = $Matches["result"]
        $diagnosticError = $Matches["error"]
        if ($physicalActions -contains $action -and $resultJson -match '"ok":true') {
            $successfulPhysicalCalls[$response] = [pscustomobject]@{
                Turn = $turn
                Action = $action
            }
        }
        if ($diagnosticError -match '_in_progress$') {
            $possibleStaleBlockers.Add([pscustomobject]@{
                Turn = $turn
                Response = $response
                Error = $diagnosticError
            })
        }
        $turnId = [long]$turn
        if (-not $turns.ContainsKey($turnId)) {
            $turns[$turnId] = New-Object System.Collections.Generic.List[string]
        }
        $turns[$turnId].Add(
            "action name=$action, ok=$($resultJson -match '"ok":true'), diagnostic=$diagnosticError")
    }

    if ($line -match 'TOOL_BATCH_(COMPLETED|TIMEOUT|CANCELLED) responseId=(?<response>[^,]+), turnId=(?<turn>\d+)') {
        $response = $Matches["response"]
        $completedResponses[$response] = $true
        [void]$activeJobs.Remove($response)
    }

    if ($line -match 'TOOL_BATCH_DISCARDED responseId=(?<response>[^,]+), turnId=(?<turn>\d+)') {
        $response = $Matches["response"]
        $discardedResponses.Add($response)
        [void]$activeJobs.Remove($response)
        $turnId = [long]$Matches["turn"]
        if (-not $turns.ContainsKey($turnId)) {
            $turns[$turnId] = New-Object System.Collections.Generic.List[string]
        }
        $turns[$turnId].Add("tool batch discarded responseId=$response")
    }

    if ($line -match 'PRESENTATION_JOB_RETAINED turnId=(?<turn>\d+)') {
        $turn = $Matches["turn"]
        $activePresentationJobs[$turn] = $true
    }

    if ($line -match 'PRESENTATION_JOB_RELEASED turnId=(?<turn>\d+)') {
        [void]$activePresentationJobs.Remove($Matches["turn"])
    }
}

foreach ($identityKey in $resolvedIdentities.Keys) {
    if ($resolvedIdentities[$identityKey].Count -gt 1) {
        Add-Failure "Identity mismatch: $identityKey resolved to more than one immutable reference."
    }
}
foreach ($candidate in $possibleStaleBlockers) {
    if (-not $deferredResponses.ContainsKey([string]$candidate.Response)) {
        Add-Failure "Turn $($candidate.Turn) response $($candidate.Response) reported $($candidate.Error) without deferring that tool batch; this may be a stale blocker."
    }
}
foreach ($response in $successfulPhysicalCalls.Keys) {
    $call = $successfulPhysicalCalls[$response]
    if (-not $completedResponses.ContainsKey($response)) {
        Add-Failure "Turn $($call.Turn) response $response succeeded as $($call.Action) without a completed tool batch."
    }
}
foreach ($response in $activeJobs.Keys) {
    Add-Failure "Turn $($activeJobs[$response]) response $response still has an unresolved deferred tool batch at the end of the log."
}
foreach ($turn in $activePresentationJobs.Keys) {
    Add-Failure "Turn $turn still has an unreleased presentation job at the end of the log."
}
foreach ($response in $discardedResponses) {
    Add-Failure "Response $response discarded its tool output batch before the model continuation was accepted."
}
$completeSpokenTurns = New-Object System.Collections.Generic.List[long]
foreach ($turnId in @($turns.Keys | Sort-Object)) {
    $entries = @($turns[$turnId])
    $latencyEntries = @($entries | Where-Object {
        $_ -match '^(response_requested|continuation_requested|response_created|first_audio|response_done|cancel_requested)'
    })
    foreach ($entry in $latencyEntries) {
        if ($entry -match 'Ms=-\d+') {
            Add-Failure "Turn $turnId contains an invalid negative latency: $entry"
        }
    }

    $firstAudioIndex = -1
    for ($index = 0; $index -lt $latencyEntries.Count; $index++) {
        if ($latencyEntries[$index] -match '^first_audio') {
            $firstAudioIndex = $index
            break
        }
    }
    if ($firstAudioIndex -ge 0) {
        $requestIndex = -1
        $createdIndex = -1
        for ($index = 0; $index -lt $firstAudioIndex; $index++) {
            if ($latencyEntries[$index] -match '^(response_requested|continuation_requested)') {
                $requestIndex = $index
            }
            elseif ($latencyEntries[$index] -match '^response_created') {
                $createdIndex = $index
            }
        }
        $doneIndex = -1
        for ($index = $firstAudioIndex + 1; $index -lt $latencyEntries.Count; $index++) {
            if ($latencyEntries[$index] -match '^response_done.*status=completed.*firstAudioSeen=True') {
                $doneIndex = $index
                break
            }
        }
        if ($requestIndex -ge 0 -and $createdIndex -gt $requestIndex -and
            $doneIndex -gt $firstAudioIndex) {
            $completeSpokenTurns.Add([long]$turnId)
        }
    }

    $lastRequestIndex = -1
    $lastTerminalIndex = -1
    for ($index = 0; $index -lt $latencyEntries.Count; $index++) {
        if ($latencyEntries[$index] -match '^(response_requested|continuation_requested)') {
            $lastRequestIndex = $index
        }
        if ($latencyEntries[$index] -match '^response_done') {
            $lastTerminalIndex = $index
        }
    }
    if ($lastRequestIndex -gt $lastTerminalIndex) {
        Add-Failure "Turn $turnId has a response request with no later response.done acknowledgement."
    }
}

if ($RequireTurn -and $completeSpokenTurns.Count -eq 0) {
    Add-Failure "Runtime turn proof required, but no turn completed request, creation, first audio, and response.done in order."
}
elseif ($completeSpokenTurns.Count -eq 0) {
    Add-Warning "No latency-correlated turn exists yet; startup is proven but spoken runtime is not."
}

$gitHead = "unavailable"
$gitDirty = "unknown"
try {
    $gitHead = (& git -C $ramblersRoot rev-parse --short=12 HEAD 2>$null).Trim()
    $gitDirty = if (@(& git -C $ramblersRoot status --porcelain 2>$null).Count -gt 0) {
        "true"
    }
    else {
        "false"
    }
}
catch {
    Add-Warning "Git source identity was unavailable."
}

$resultLabel = if ($failures.Count -eq 0) { "PASS" } else { "FAIL" }
$report.Add("RAMBLERS LATEST-RUN AUDIT: $resultLabel")
$report.Add("Source proof: version=$sourceVersion commit=$gitHead dirty=$gitDirty")
$report.Add("Build proof: fresh=$buildSucceeded hash=$distHash")
$report.Add("Codec proof: builtHash=$distCodecHash deployedHash=$deployedCodecHash")
$report.Add("Deployment proof: hash=$deployedHash game=$GamePath")
$report.Add("Runtime proof: loadedVersion=$loadedVersion loadedHash=$loadedHash ready=$ready logUpdated=$logTimestamp")
$report.Add("Visual QA: not assessed by this command")
$report.Add("")
$report.Add("Turns: $($turns.Count)")
foreach ($turnId in @($turns.Keys | Sort-Object)) {
    $report.Add("  turn $turnId")
    foreach ($stage in $turns[$turnId]) {
        $report.Add("    $stage")
    }
}

if ($warnings.Count -gt 0) {
    $report.Add("")
    $report.Add("Warnings:")
    foreach ($warning in $warnings) {
        $report.Add("  - $warning")
    }
}
if ($failures.Count -gt 0) {
    $report.Add("")
    $report.Add("Failures:")
    foreach ($failure in $failures) {
        $report.Add("  - $failure")
    }
}

$outputDirectory = Split-Path -Parent $OutputPath
if (-not (Test-Path -LiteralPath $outputDirectory)) {
    [void](New-Item -ItemType Directory -Path $outputDirectory -Force)
}
$reportText = $report -join [Environment]::NewLine
Set-Content -LiteralPath $OutputPath -Value $reportText -Encoding UTF8
Write-Host $reportText
Write-Host ""
Write-Host "Audit artifact: $OutputPath"

if ($failures.Count -gt 0) {
    exit 1
}
