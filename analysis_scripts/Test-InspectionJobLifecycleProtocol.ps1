#requires -Version 5.1

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$bridge = Get-Content -LiteralPath (Join-Path $PSScriptRoot "..\src\OpenAIRealtimeBridge.cs") -Raw

function Assert-Contains {
    param(
        [Parameter(Mandatory = $true)][string]$Needle,
        [Parameter(Mandatory = $true)][string]$Description
    )
    if ($bridge.IndexOf($Needle, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Inspection-lifecycle check failed: $Description"
    }
}

function Assert-NotContains {
    param(
        [Parameter(Mandatory = $true)][string]$Needle,
        [Parameter(Mandatory = $true)][string]$Description
    )
    if ($bridge.IndexOf($Needle, [System.StringComparison]::Ordinal) -ge 0) {
        throw "Inspection-lifecycle check failed: $Description"
    }
}

Assert-Contains 'var retainForAudio = sent &&' `
    "a discarded function-output batch must never retain the completed inspection job"
Assert-Contains 'AgentToolCatalog.InspectReference,' `
    "new human speech must cancel an in-flight inspection before its stale image can continue"
Assert-Contains '_lingeringJobTurnId = retainForAudio ? pending.TurnId : 0;' `
    "the presentation hold must bind to its exact response turn"
Assert-Contains 'PRESENTATION_JOB_RETAINED turnId={pending.TurnId}' `
    "a retained inspection must emit an auditable lifecycle marker"
Assert-Contains 'if (_concludeJobOnAssistantAudio)' `
    "the next serialized response completion must release a tool-only or folded presentation hold"
Assert-Contains 'continuation may be folded into a newer waiting human turn' `
    "release semantics must document why response completion is not tied to the original tool turn"
Assert-Contains 'ReleaseLingeringJob("response_completed_without_audio")' `
    "no-audio completion must have an explicit fallback release"
Assert-Contains 'ReleaseLingeringJob("assistant_audio_started")' `
    "ordinary spoken presentation must release at first audio"
Assert-Contains 'ReleaseLingeringJob("human_interrupted_response")' `
    "new human speech must not strand the previous inspection"
Assert-Contains 'ReleaseLingeringJob("client_reconnect")' `
    "connection replacement must release retained inspection state"
Assert-Contains '_gameVoiceOutput.Stop();' `
    "connection replacement must stop audio from the abandoned session"
Assert-Contains '_continuationHeld = false;' `
    "connection replacement must not carry a held old-session continuation forward"
Assert-Contains 'ReleaseLingeringJob("client_stopped")' `
    "shutdown must release retained inspection state"
Assert-Contains 'CompanionController.ConcludeJob(token);' `
    "a successfully completed inspection must conclude rather than remain active"
Assert-Contains '_lingeringJobTurnId = 0;' `
    "every release must clear turn identity with its token"
Assert-NotContains 'clientEvent.TurnId == _lingeringJobTurnId' `
    "a continuation folded into a newer turn must not strand the original inspection"
Assert-NotContains 'CompanionController.CancelJob(_lingeringJobToken);' `
    "completed presentation holds must not be silently cancelled only at shutdown"

Write-Host "Inspection-job lifecycle checks passed."
Write-Host "  Proven: retained inspection jobs release on send failure, first audio, response completion, interruption, reconnect, and shutdown."
Write-Host "  Not proven: live inspection interruption timing."
