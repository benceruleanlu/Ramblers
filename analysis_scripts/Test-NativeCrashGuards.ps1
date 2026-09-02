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

function Assert-Absent {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Needle,
        [Parameter(Mandatory = $true)][string]$File,
        [Parameter(Mandatory = $true)][string]$Evidence
    )
    if ($Text.IndexOf($Needle, [System.StringComparison]::Ordinal) -ge 0) {
        throw "Native-crash guard failed: '$Needle' is back in ${File}. $Evidence"
    }
}

function Assert-Present {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Needle,
        [Parameter(Mandatory = $true)][string]$File,
        [Parameter(Mandatory = $true)][string]$Evidence
    )
    if ($Text.IndexOf($Needle, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Native-crash guard failed: '$Needle' is missing from ${File}. $Evidence"
    }
}

function Assert-Order {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Earlier,
        [Parameter(Mandatory = $true)][string]$Later,
        [Parameter(Mandatory = $true)][string]$File,
        [Parameter(Mandatory = $true)][string]$Evidence
    )
    $earlierIndex = $Text.IndexOf($Earlier, [System.StringComparison]::Ordinal)
    $laterIndex = $Text.IndexOf($Later, [System.StringComparison]::Ordinal)
    if ($earlierIndex -lt 0 -or $laterIndex -lt 0 -or $earlierIndex -ge $laterIndex) {
        throw "Native-crash guard failed: '$Later' is no longer behind '$Earlier' in ${File}. $Evidence"
    }
}

$globalScanEvidence =
    "The ambient global CastableTarget scan was on the 0.13.0 spoken turn that " +
    "ended in a native CoreCLR access violation; discovery uses game-owned registries only."
$awareness = Read-Source "src\CompanionAwareness.cs"
$discovery = Read-Source "src\CompanionInteractableDiscovery.cs"
$router = Read-Source "src\AgentToolRouter.cs"
foreach ($scan in @('Resources.', 'FindObjectsOfType')) {
    Assert-Absent $awareness $scan 'CompanionAwareness.cs' $globalScanEvidence
    Assert-Absent $discovery $scan 'CompanionInteractableDiscovery.cs' $globalScanEvidence
}
Assert-Absent $router 'FindObjectsOfType' 'AgentToolRouter.cs' $globalScanEvidence
Assert-Absent $discovery 'GetComponentsInChildren<CastableTarget>' `
    'CompanionInteractableDiscovery.cs' `
    "Discovery stays on the runtime-proven non-generic component lookup; this generic instantiation was never probed on the crash-quarantined path."

$voiceEvidence =
    "Dereferencing Dissonance's live SourceController and copying its AudioSource " +
    "route were in the IL2CPP wrapper chain of the 0.13.0 native access violation."
$voiceOutput = Read-Source "src\GameVoiceOutput.cs"
foreach ($route in @(
        '.SourceController',
        'FindObjectsOfTypeAll<PlayerVoicePlaybackControl>',
        'CopyStockVoiceRoute',
        'outputAudioMixerGroup')) {
    Assert-Absent $voiceOutput $route 'GameVoiceOutput.cs' $voiceEvidence
}

$locomotion = Read-Source "src\CompanionLocomotion.cs"
Assert-Absent $locomotion 'SweepTestAll(' 'CompanionLocomotion.cs' `
    "Rigidbody.SweepTestAll is absent from Big Walk's IL2CPP build; calling a stripped method kills the process."

$vision = Read-Source "src\CompanionVisionCapture.cs"
$fieldOfViewEvidence =
    "Camera.fieldOfView accessors are probed at startup because a stripped " +
    "accessor kills the process; the copy must stay behind the combined probe."
Assert-Present $vision '_canCopyFieldOfView = canGetFieldOfView && canSetFieldOfView;' `
    'CompanionVisionCapture.cs' $fieldOfViewEvidence
Assert-Order $vision 'if (_canCopyFieldOfView)' 'sourceCamera.fieldOfView' `
    'CompanionVisionCapture.cs' $fieldOfViewEvidence

Write-Host "Native-crash guards passed."
