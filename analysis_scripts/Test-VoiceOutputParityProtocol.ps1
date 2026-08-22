#requires -Version 5.1

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ramblersRoot = Split-Path -Parent $PSScriptRoot
$voice = Get-Content -LiteralPath (Join-Path $ramblersRoot "src\GameVoiceOutput.cs") -Raw

function Assert-Contains {
    param([string]$Needle, [string]$Description)
    if ($voice.IndexOf($Needle, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Voice-output parity check failed: $Description"
    }
}

function Assert-NotContains {
    param([string]$Needle, [string]$Description)
    if ($voice.IndexOf($Needle, [System.StringComparison]::Ordinal) -ge 0) {
        throw "Voice-output parity check failed: $Description"
    }
}

Assert-NotContains '.SourceController' `
    "synthetic speech must not cross the crash-prone Dissonance SourceController wrapper"
Assert-NotContains 'FindObjectsOfTypeAll<PlayerVoicePlaybackControl>' `
    "synthetic speech must not enumerate and dereference unrelated live voice controls"
Assert-NotContains 'CopyStockVoiceRoute' `
    "synthetic speech must not clone an unproven live AudioSource route"
Assert-NotContains 'outputAudioMixerGroup' `
    "mixer-route parity must remain deferred until it has an isolated safe integration"
Assert-Contains 'playback == null ? null : playback.AttenuationCurve' `
    "attenuation must use the already-exercised companion playback field path"
Assert-Contains 'ConfigureMetreAttenuation();' `
    "the game curve must select direct metre-domain attenuation"
Assert-Contains '_attenuationCurve.Evaluate(Mathf.Max(0f, distance))' `
    "Big Walk attenuation must be evaluated in the game's metre domain"
Assert-Contains 'AnimationCurve.Linear(0f, 1f, 1f, 1f)' `
    "Unity rolloff must stay flat when game attenuation is applied manually"
$rescaledCurvePattern = 'AudioSourceCurveType.CustomRolloff,' + "`r`n" +
    '                    attenuationCurve'
Assert-NotContains $rescaledCurvePattern `
    "the metre-keyed game curve must never be rescaled as a Unity rolloff curve"
Assert-Contains '[AGENT] VOICE_ROUTE_READY' `
    "the selected route and reference curve must be logged"
Assert-Contains '[AGENT] VOICE_ROUTE_LEVEL' `
    "live distance and applied level must be observable"
Assert-Contains 'route=local_3d_safe' `
    "runtime evidence must identify the crash-contained local route"

Write-Host "Voice-output safety protocol checks passed."
Write-Host "  Proven: crash-prone source introspection is absent; metre-domain attenuation, flat Unity rolloff, and live level telemetry remain."
Write-Host "  Not proven: spoken runtime stability, perceived loudness, or mixer-route parity with a real remote player."
