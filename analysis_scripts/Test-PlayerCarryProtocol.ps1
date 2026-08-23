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
        throw "Player-carry protocol check failed: $Description"
    }
}

function Assert-NotContains {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Needle,
        [Parameter(Mandatory = $true)][string]$Description
    )

    if ($Text.IndexOf($Needle, [System.StringComparison]::Ordinal) -ge 0) {
        throw "Player-carry protocol check failed: $Description"
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
        throw "Player-carry protocol check failed: $Description"
    }
}

$catalog = Read-Source "src\AgentToolCatalog.cs"
$router = Read-Source "src\AgentToolRouter.cs"
$bridge = Read-Source "src\OpenAIRealtimeBridge.cs"
$jobContract = Read-Source "src\CompanionJob.cs"
$actions = Read-Source "src\CompanionActions.cs"
$controller = Read-Source "src\CompanionController.cs"
$target = Read-Source "src\CompanionPlayerTarget.cs"
$behavior = Read-Source "src\CompanionPlayerCarryBehavior.cs"
$approach = Read-Source "src\CompanionApproachController.cs"
$follow = Read-Source "src\CompanionFollowBehavior.cs"

Assert-Contains $catalog 'internal const string PickUpPlayer = "pick_up_player";' `
    "the allowlist must expose human pickup"
Assert-Contains $catalog 'internal const string DropPlayer = "drop_player";' `
    "the allowlist must expose human release"
Assert-Contains $catalog 'Call it directly without discussing implementation details.' `
    "the model should act naturally instead of narrating internals"
Assert-Contains $catalog 'properties = new { }' `
    "player carry must not ask the model to identify an internal player reference"

Assert-Contains $bridge 'CompanionController.TryCaptureHumanPlayerTarget(' `
    "the local human must be frozen at the utterance boundary"
Assert-Contains $bridge 'HumanPlayerTarget = humanPlayerTarget' `
    "turn state must retain the exact human"
Assert-Contains $bridge 'AgentToolCatalog.PickUpPlayer' `
    "a new utterance must be able to interrupt pending human pickup"
Assert-Contains $bridge 'AgentToolCatalog.DropPlayer' `
    "a new utterance must be able to interrupt pending human release"
Assert-Contains $bridge 'AgentToolCatalog.GoToLocation' `
    "a new utterance must be able to interrupt directed travel"

Assert-Contains $router 'turnReference?.HumanPlayerTarget == null' `
    "routing must fail closed without an utterance-bound human"
Assert-Contains $router 'PlayerTarget = turnReference.HumanPlayerTarget' `
    "both carry commands must receive the frozen player object"
Assert-Contains $router '[ENTITY] TARGET_RESOLVED action={AgentToolCatalog.PickUpPlayer}' `
    "runtime evidence must identify the resolved human pickup target"
Assert-Contains $router '[ENTITY] TARGET_RESOLVED action={AgentToolCatalog.DropPlayer}' `
    "runtime evidence must identify the exact human release target"
Assert-NotContains $router 'FindObjectsOfType' `
    "tool execution must not rescan for another player"

Assert-Contains $jobContract 'internal CompanionPlayerTarget PlayerTarget;' `
    "the typed request must carry player identity"
Assert-Contains $jobContract 'HoldingExactPlayer' `
    "confirmed player pickup must advance turn hands state"
Assert-Contains $jobContract 'completion.ExactPlayer.IsStillTheSamePlayer(' `
    "the turn transition must validate the completed human against the frozen human"
Assert-Contains $actions 'new CompanionPlayerCarryBehavior(_locomotion, _attention, _jump)' `
    "player carry must use the shared job and movement architecture"
Assert-Contains $actions 'internal bool IsCarryingHuman =>' `
    "the action boundary must expose exact passenger state to follow and context"
Assert-Contains $actions '!IsCarryingHuman;' `
    "follow movement must stay gated while the companion holds the human"
Assert-Contains $actions '? "carrying_player"' `
    "the movement gate must expose the exact native carry blocker"
Assert-Contains $controller 'var human = WorldManager.localPlayerCharacter;' `
    "capture must select the controller-bound local human rather than proximity"
Assert-Contains $controller 'CompanionPlayerTarget.TryCapture(' `
    "the local human must cross the exact player-target boundary"

Assert-Contains $target 'candidate != Player' `
    "managed player identity must be exact"
Assert-Contains $target 'candidateIdentity.netId == _networkId' `
    "network identity must remain exact when present"
Assert-Contains $target 'Player.poser.PoseIsSafe(grabPose, null)' `
    "pickup must retain the game-owned pose admission predicate"
Assert-Contains $target 'body.Character.registry.grabPose' `
    "pickup must use the companion carrier's native grab pose"
Assert-NotContains $target 'FindObjects' `
    "player targeting must never search for a replacement"
Assert-NotContains $target 'Vector3.Distance' `
    "player target capture must not rank candidates by proximity"

Assert-Contains $behavior 'ICompanionStandingJob' `
    "approaching a player must use the standing movement contract"
Assert-Contains $behavior '_body.Character.caster.GetMaxDistanceForDirection(' `
    "player pickup reach must come from Big Walk's directional interaction reach"
Assert-NotContains $behavior 'PickupReachDistance' `
    "player pickup must not impose a fixed guessed reach gate"
Assert-Contains $behavior 'JobResources.Locomotion | JobResources.Gaze | JobResources.Hands' `
    "pickup must reserve all physical capabilities while approaching"
Assert-Contains $behavior '? JobResources.Hands' `
    "release must not reserve locomotion or eject a carried companion"
Assert-Contains $behavior '_approach.Advance(' `
    "player pickup must use the shared approach state machine"
Assert-Contains $approach '_locomotion.TrySteerToward(' `
    "shared player approach must retain obstacle-aware locomotion"
Assert-Contains $approach '_jump.TryRequestActionRecovery(' `
    "shared player approach must retain stock jump recovery"
Assert-NotContains $behavior '_locomotion.TrySteerToward(' `
    "player pickup must not duplicate shared steering mechanics"
Assert-NotContains $behavior '_locomotion.ObserveProgress(' `
    "player pickup must not duplicate shared progress observation"
Assert-NotContains $behavior '_jump.TryRequestActionRecovery(' `
    "player pickup must not duplicate shared recovery mechanics"
Assert-Contains $behavior '[ACTION] PLAYER_PICKUP_APPROACH_DEFERRED' `
    "player-specific recovery telemetry must stay at the job boundary"
Assert-Order $behavior '_target.IsPickupAdmittedByGame(_body, out validationError)' `
    '_body.Networking.UserCode_CmdPickUpPlayer__PlayerCharacter(' `
    "native admission must immediately precede player pickup authority"
Assert-Contains $behavior '_body.Networking.UserCode_CmdDropHeldPlayer();' `
    "release must use the connectionless server command body"
Assert-Contains $behavior '_target.IsStillTheSamePlayer(hands.heldCharacter)' `
    "hands confirmation must match the exact player"
Assert-Contains $behavior 'poser.playerHoldingMe == _body.Character' `
    "pickup confirmation must match the native carrier link"
Assert-Contains $behavior 'poser.currentPose == grabPose' `
    "pickup confirmation must match the native carry pose"
Assert-Contains $behavior '_target.IsStillTheSamePlayer(grabPose.occupant)' `
    "pickup confirmation must match the pose occupant"
Assert-Contains $behavior 'CompanionTurnHandsTransition.HoldingExactPlayer' `
    "success must publish an exact player-held transition"
Assert-Contains $behavior 'CompanionTurnHandsTransition.HandsEmpty' `
    "release must publish stable empty hands"
Assert-Contains $behavior 'HasHeldPlayerMismatch(out mismatchError)' `
    "the parameterless native drop must be guarded against identity drift"
Assert-Contains $behavior 'GetCarrierGrabPoseForLinkCheck()' `
    "carry teardown must retain exact native links after pose deactivation"
Assert-Contains $behavior 'PLAYER_CARRY_RECONCILIATION_STARTED' `
    "post-authority cancellation must reconcile native carry state"
Assert-NotContains $behavior '.CmdPickUpPlayer(' `
    "a connectionless companion must not call the client Command wrapper"
Assert-NotContains $behavior '.CmdDropHeldPlayer(' `
    "a connectionless companion must not call the client Command wrapper"
Assert-NotContains $behavior 'FindObjects' `
    "carry execution must never search for a replacement target"
Assert-NotContains $behavior 'CowBell' `
    "player carry must contain no puzzle-specific object branch"
Assert-NotContains $behavior 'Buoy' `
    "player carry must contain no puzzle-specific object branch"

Assert-Contains $follow 'body.Character?.hands?.heldCharacter == human' `
    "inverse carry detection must use the exact companion hands relationship"
Assert-Contains $follow 'poser.playerHoldingMe == body.Character' `
    "inverse carry must remain suspended while the native carrier link settles"
Assert-Contains $follow 'poser.currentPose == grabPose' `
    "inverse carry must remain suspended through native pose teardown"
Assert-Contains $follow 'grabPose.occupant == human' `
    "inverse carry must validate the exact pose occupant"
Assert-Contains $follow 'return isCarried || isCarryingHuman;' `
    "both carry directions must suppress breadcrumb observation"
Assert-Contains $follow '[FOLLOW] PLAYER_CARRY_STARTED carrier=companion' `
    "runtime evidence must distinguish the companion carrying the human"
Assert-Contains $follow '_state = FollowState.Suspended;' `
    "follow intent must remain suspended through carry release arbitration"
Assert-Contains $follow '_trail.Clear();' `
    "carry transitions must discard attached-player breadcrumbs"

Write-Host "Player-carry protocol checks passed."
Write-Host "  Proven: frozen human identity, native admission/authority, exact carry confirmation, cancellation guard."
Write-Host "  Not proven: Unity runtime pickup animation, carried movement, or deployed behavior."
