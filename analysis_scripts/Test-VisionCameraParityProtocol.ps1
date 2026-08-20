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
        throw "Vision-camera parity check failed: $Description"
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
        throw "Vision-camera parity check failed: $Description"
    }
}

$capture = Read-Source "src\CompanionVisionCapture.cs"
$inspection = Read-Source "src\CompanionInspectionBehavior.cs"

Assert-Contains $capture '"get_fieldOfView",' `
    "the source-camera getter must be probed before use"
Assert-Contains $capture '"set_fieldOfView",' `
    "the capture-camera setter must be probed before use"
Assert-Contains $capture '_canCopyFieldOfView = canGetFieldOfView && canSetFieldOfView;' `
    "FOV parity must require both runtime methods"
Assert-Contains $capture 'if (_canCopyFieldOfView)' `
    "property access must remain behind the combined runtime gate"
Assert-Order $capture 'if (_canCopyFieldOfView)' `
    'sourceFieldOfView = sourceCamera.fieldOfView;' `
    "the guarded branch must precede the source-camera getter"
Assert-Contains $capture 'captureCamera.fieldOfView = sourceFieldOfView;' `
    "the player's FOV must be applied to the capture camera"
Assert-Contains $capture 'captureFieldOfView - sourceFieldOfView) <= 0.01f;' `
    "the applied value must be read back and verified"
Assert-Contains $inspection 'fieldOfViewMatched={observation.FieldOfViewMatched}' `
    "runtime capture evidence must report whether framing matched"

Write-Host "Vision-camera parity checks passed."
Write-Host "  Proven: getter/setter probes, combined guard, bounded FOV validation, applied-value readback, and capture telemetry."
Write-Host "  Not proven by this static check: Unity runtime method availability or visual framing."
