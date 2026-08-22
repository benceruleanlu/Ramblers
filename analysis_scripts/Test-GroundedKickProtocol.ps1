#requires -Version 5.1

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ramblersRoot = Split-Path -Parent $PSScriptRoot

function Read-Source {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RelativePath
    )

    return Get-Content -LiteralPath (Join-Path $ramblersRoot $RelativePath) -Raw
}

function Assert-Contains {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text,

        [Parameter(Mandatory = $true)]
        [string]$Needle,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    if ($Text.IndexOf($Needle, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Grounded-kick protocol check failed: $Description"
    }
}

function Assert-NotContains {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text,

        [Parameter(Mandatory = $true)]
        [string]$Needle,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    if ($Text.IndexOf($Needle, [System.StringComparison]::Ordinal) -ge 0) {
        throw "Grounded-kick protocol check failed: $Description"
    }
}

function Assert-Order {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text,

        [Parameter(Mandatory = $true)]
        [string]$Earlier,

        [Parameter(Mandatory = $true)]
        [string]$Later,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    $earlierIndex = $Text.IndexOf($Earlier, [System.StringComparison]::Ordinal)
    $laterIndex = $Text.IndexOf($Later, [System.StringComparison]::Ordinal)
    if ($earlierIndex -lt 0 -or $laterIndex -lt 0 -or $earlierIndex -ge $laterIndex) {
        throw "Grounded-kick protocol check failed: $Description"
    }
}

$catalog = Read-Source "src\AgentToolCatalog.cs"
$prompt = Read-Source "src\AgentPrompt.cs"
$router = Read-Source "src\AgentToolRouter.cs"
$bridge = Read-Source "src\OpenAIRealtimeBridge.cs"
$actions = Read-Source "src\CompanionActions.cs"
$target = Read-Source "src\CompanionInteractionTarget.cs"
$kick = Read-Source "src\CompanionKickBehavior.cs"

Assert-Contains $catalog 'internal const string KickItem = "kick_item";' `
    "the allowlist must expose kick_item"
Assert-Contains $catalog 'name = KickItem' `
    "the Realtime schema must publish kick_item"
Assert-Contains $prompt 'Do not advertise optional tool parameters' `
    "clear physical requests must not become a spoken option menu"
Assert-Contains $catalog 'do not offer strength or direction choices' `
    "kick must be invoked directly without offering optional variants"
Assert-Contains $catalog 'Infer strength silently' `
    "strength modifiers must be inferred rather than discussed"
Assert-Contains $catalog 'Infer direction silently' `
    "direction modifiers must be inferred rather than discussed"
Assert-Contains $catalog '@enum = new[] { "light", "normal", "hard" }' `
    "the schema must bound the requested kick strength"
Assert-Contains $catalog '@enum = new[] { "away_from_companion", "toward_human" }' `
    "the schema must bound the requested kick direction"
Assert-Contains $router 'case AgentToolCatalog.KickItem:' `
    "the router must dispatch kick_item"
Assert-Contains $router 'InteractionTarget = turnReference.Target' `
    "kick must receive the immutable response-turn target"
Assert-Contains $router 'KickStrength = strength' `
    "the validated strength must cross the typed job boundary"
Assert-Contains $router 'KickDirection = direction' `
    "the validated direction must cross the typed job boundary"
Assert-Contains $actions 'new CompanionKickBehavior(_attention)' `
    "the coordinator must register the kick job"
Assert-Contains $kick 'TryValidateAdmission(out targetPoint, out validationError)' `
    "kick admission must defer current-pose validation until after auto-stand"
Assert-Contains $kick 'return TryValidateBeforeAuthority(out point, out error, false);' `
    "kick admission must retain every exact validation except the pre-stand pose"
Assert-Contains $kick 'bool validateCurrentPose = true' `
    "post-admission kick validation must restore the stock pose check"
Assert-Contains $kick 'if (!TryValidateKickPose(validateCurrentPose, out error))' `
    "kick admission must always cross the shared lifecycle validation"
Assert-Contains $kick 'if (validateCurrentPose && pose != null && !pose.allowKicking)' `
    "only current-pose compatibility may wait for auto-stand"
Assert-Contains $kick 'if (PlayerArms.LegIsBusyKicking(_body.Character))' `
    "kick admission must retain the stock leg-busy lifecycle guard"
Assert-Contains $bridge 'AgentToolCatalog.KickItem,' `
    "new human speech must cancel a pending kick through reconciliation"

Assert-Contains $target 'candidate == null || candidate != Prop ||' `
    "the referent must retain managed-object identity"
Assert-Contains $target 'candidateIdentity.netId == _networkId' `
    "the referent must retain network identity when present"

Assert-Contains $kick 'ServerPickUpPropAutomatic(_target.Prop)' `
    "the authoritative pickup transition must receive the frozen prop"
Assert-Contains $kick '_state = KickState.Charging;' `
    "the exact held prop must enter a separate visible charge phase"
Assert-Contains $kick 'duration = tunings.maxWindUpDuration * windUp;' `
    "charge delay must use Big Walk's runtime tuning"
Assert-Contains $kick 'now - _chargeStartedAt < _chargeDuration' `
    "launch must wait until the selected charge duration has elapsed"
Assert-Contains $kick 'human.transform.position - launchPosition' `
    "toward-human requests must resolve an explicit human-directed launch"
Assert-Contains $kick 'PlayerHeldInformation.ThrowInfo(' `
    "kick must build Big Walk's stock launch record"
Assert-Contains $kick 'UserCode_CmdPickUp__PlayerHeldInformation(' `
    "kick must cross the stock server-side held-prop command path"
Assert-Order $kick '_target.IsStillTheSameProp(hands.heldProp)' `
    'UserCode_CmdPickUp__PlayerHeldInformation(' `
    "exact held identity must be checked before the launch call"
Assert-Order $kick 'ServerPickUpPropAutomatic(_target.Prop)' `
    '_state = KickState.Charging;' `
    "kick must confirm pickup before starting its charge"
Assert-Order $kick '_state = KickState.Charging;' `
    'UserCode_CmdPickUp__PlayerHeldInformation(' `
    "kick must finish a distinct charge phase before launch"
Assert-Contains $kick 'ServerDropPropAutomatic(false)' `
    "post-authority cancellation must recover with a plain exact-item drop"
Assert-Contains $kick '_target.Prop.rb.linearVelocity.magnitude' `
    "success must observe target motion"
Assert-Contains $kick 'displacement >= MinimumMotionDistance' `
    "success must also accept visible target displacement"
Assert-Contains $kick '"item_moving"' `
    "the terminal state must report confirmed motion"
Assert-Contains $kick '[ACTION] KICK_ALIGNMENT_TIMEOUT' `
    "custom gaze timeout must be telemetry rather than an action veto"
Assert-NotContains $kick 'CompleteFailure("target_alignment_failed")' `
    "custom kick alignment must not reject an exact stock action"

Assert-NotContains $kick 'AddForce' `
    "kick must not bypass stock replication with a raw Rigidbody shove"
Assert-NotContains $kick 'FindObjects' `
    "kick must never search for a replacement prop"
Assert-NotContains $kick 'castProp' `
    "kick must never reinterpret live gaze"
Assert-NotContains $kick 'nearest' `
    "kick must never use nearest-item fallback"

Write-Host "Grounded-kick protocol checks passed."
Write-Host "  Proven: direct silent prompt routing, exact identity, staged game-tuned charge, bounded strength/direction, stock server launch, no fallback selector."
Write-Host "  Not proven: live model compliance, visible charge timing, directional accuracy, or deployed behavior."
