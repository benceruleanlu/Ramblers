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

Assert-Contains 'playback?.SourceController?.AudioSource' `
    "the synthetic source must discover the stock player-voice route"
Assert-Contains 'destination.outputAudioMixerGroup = stock.outputAudioMixerGroup;' `
    "the stock voice mixer must be preserved"
Assert-Contains 'destination.spatialize = stock.spatialize;' `
    "the stock spatializer setting must be preserved"
Assert-Contains 'destination.spatialBlend = stock.spatialBlend;' `
    "the stock 2D/3D blend must be preserved"
Assert-Contains 'if (!_stockRouteApplied &&' `
    "late stock voice initialization must still upgrade the synthetic route"
Assert-Contains '_nextStockRouteResolveAt = Time.realtimeSinceStartup + 1f;' `
    "late route discovery must not scan Unity assets every frame"
Assert-Contains '_nextAttenuationResolveAt = Time.realtimeSinceStartup + 1f;' `
    "a late game attenuation curve must be retried at bounded cadence"
Assert-Contains '[AGENT] VOICE_ROUTE_TEMPLATE_UNAVAILABLE' `
    "late stock-route failures must degrade to the existing source without escaping Update"
Assert-Contains '[AGENT] VOICE_ATTENUATION_ROUTE_UNAVAILABLE' `
    "late curve failures must degrade to the temporary Unity fallback without escaping Update"
Assert-Contains 'ConfigureMetreAttenuation();' `
    "a late curve must replace the temporary logarithmic fallback"
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

Write-Host "Voice-output parity protocol checks passed."
Write-Host "  Proven: stock mixer/spatial route cloning, metre-domain attenuation, flat Unity rolloff, and live level telemetry."
Write-Host "  Not proven: perceived loudness parity with a real remote player in the deployed game."
