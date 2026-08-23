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
        throw "Grounded-pickup protocol check failed: $Description"
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
        throw "Grounded-pickup protocol check failed: $Description"
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
        throw "Grounded-pickup protocol check failed: $Description"
    }
}

$bridge = Read-Source "src\OpenAIRealtimeBridge.cs"
$entities = Read-Source "src\CompanionEntityReferences.cs"
$actions = Read-Source "src\CompanionActions.cs"
$follow = Read-Source "src\CompanionFollowBehavior.cs"
$client = Read-Source "src\OpenAIRealtimeClient.cs"
$router = Read-Source "src\AgentToolRouter.cs"
$catalog = Read-Source "src\AgentToolCatalog.cs"
$target = Read-Source "src\CompanionPropTarget.cs"
$pickup = Read-Source "src\CompanionPickupBehavior.cs"
$approach = Read-Source "src\CompanionApproachController.cs"
$jobContract = Read-Source "src\CompanionJob.cs"
$controller = Read-Source "src\CompanionController.cs"
$inspection = Read-Source "src\CompanionInspectionBehavior.cs"
$bridge = Read-Source "src\OpenAIRealtimeBridge.cs"

Assert-Contains $catalog 'internal const string PickUpItem = "pick_up_item";' `
    "the allowlist must expose the bounded pickup tool"
Assert-Contains $catalog 'the exact prop:net:/prop:local: ID from private game context' `
    "the model may ground named objects through private stable IDs"
Assert-Contains $router 'CompanionTurnReference turnReference' `
    "dispatch must receive response-scoped reference context"
Assert-Contains $router 'turnReference.EntityReferences.TryResolve(' `
    "dispatch must resolve a model-selected context ID"
Assert-Contains $router 'PropTarget = propTarget' `
    "the job request must receive the frozen target"
Assert-Contains $router '[ENTITY] TARGET_RESOLVED' `
    "runtime evidence must report exact target grounding"
Assert-Contains $entities '_props.TryGetValue(stableId, out target)' `
    "entity resolution must be exact rather than proximity based"

Assert-Order $bridge 'DrainClientEvents();' '_gameVoice.Tick(_client);' `
    "semantic speech edges must be handled before local voice sampling"
Assert-Order $bridge '_gameVoice.Tick(_client);' 'DrainFunctionCallBatches();' `
    "manual speech edges must invalidate references before tool dispatch"
Assert-Contains $bridge '_turnReferences.Clear();' `
    "new speech must invalidate undispatched references"
Assert-Contains $bridge '_client.RequestResponse(turnId);' `
    "the captured turn id must reserve the model response"
Assert-Contains $client 'TurnId = turnId' `
    "the response turn id must reach the function-call batch"

Assert-Contains $target 'candidate == null || candidate != Prop ||' `
    "target revalidation must retain managed object identity"
Assert-Contains $target 'candidateIdentity.netId == _networkId' `
    "target revalidation must retain network identity when present"
Assert-NotContains $target 'FindObjects' `
    "target capture must not scan for replacement props"
Assert-NotContains $target 'castProp' `
    "target capture must not fall back to a later caster selection"

Assert-Contains $pickup 'ServerPickUpPropAutomatic(_target.Prop)' `
    "host pickup must receive the exact frozen prop"
Assert-Contains $actions 'new CompanionPickupBehavior(_locomotion, _attention, _jump)' `
    "pickup must share the companion's native locomotion and recovery actuators"
Assert-Contains $pickup 'PickupState.ApproachingTarget' `
    "out-of-reach pickup must enter a bounded approach phase"
Assert-Contains $pickup '_approach.Advance(' `
    "pickup must use the shared approach state machine"
Assert-Contains $pickup '[ACTION] PICKUP_APPROACH_RESUMED' `
    "the same exact prop must resume approach if it moves during alignment"
Assert-Contains $approach '_locomotion.TrySteerToward(' `
    "shared approaches must reuse obstacle-aware steering"
Assert-Contains $approach 'private const float NavigationInterval = 0.1f;' `
    "shared physical navigation must run at navigation cadence rather than every frame"
Assert-NotContains $pickup '_locomotion.TrySteerToward(' `
    "pickup must not duplicate shared steering mechanics"
Assert-NotContains $pickup '_locomotion.ObserveProgress(' `
    "pickup must not duplicate shared progress observation"
Assert-NotContains $pickup '_jump.TryRequestActionRecovery(' `
    "pickup must not duplicate shared recovery mechanics"
Assert-Contains $follow 'any live movement intent belongs to the action holding that' `
    "suspended follow must not clear pickup's locomotion intent"
Assert-Contains $follow 'Yield before idle-follow cleanup can clear that job' `
    "stay mode must also yield locomotion to pickup"
Assert-Contains $approach '_jump.TryRequestActionRecovery(' `
    "a stalled physical approach must have bounded grounded jump recovery"
Assert-Contains $approach 'CompanionJumpActuator.IsDeferredRecoveryError(jumpError)' `
    "temporary jump contention must defer pickup instead of failing the job"
Assert-Contains $pickup '[ACTION] PICKUP_APPROACH_DEFERRED' `
    "deferred recovery must be visible in runtime evidence"
Assert-Contains $pickup '[ACTION] PICKUP_APPROACH_RECOVERY' `
    "committed recovery must retain pickup-specific runtime evidence"
Assert-NotContains $pickup 'MaximumApproachRecoveries' `
    "the existing job timeout must be the single recovery bound"
Assert-NotContains $pickup 'directGroundLimited ||' `
    "the stock slope signal must not veto pickup recovery"
Assert-NotContains $pickup 'HasGroundSupportAhead' `
    "an unreliable floor heuristic must not veto pickup recovery"
Assert-Contains $approach '_recoveryDirection = direction;' `
    "pickup recovery must keep one direction through its bounded commitment"
Assert-Contains $approach '_recoveryDirection,' `
    "a moving prop must not redirect an active recovery commitment"
Assert-Contains $pickup '_approach.CancelRecovery();' `
    "cancelled pickup work must remove its queued recovery jump"
Assert-Contains $pickup '[ACTION] PICKUP_APPROACH_REACHED' `
    "runtime evidence must distinguish successful navigation from pickup"
Assert-Contains $pickup '[ACTION] PICKUP_ALIGNMENT_TIMEOUT' `
    "custom gaze timeout must be telemetry rather than an action veto"
Assert-NotContains $pickup 'CompleteFailure("target_alignment_failed")' `
    "custom pickup alignment must not reject an exact stock action"
Assert-Contains $pickup '[ACTION] PICKUP_JOB_CONCLUDED' `
    "a completed pickup must log release of its job lifecycle"
Assert-Contains $pickup 'CompanionTurnHandsTransition.HoldingExactProp' `
    "confirmed pickup must publish an exact hands-state transition"
Assert-Contains $pickup 'ExactProp = _target' `
    "the transition must carry the same frozen pickup identity"
Assert-Contains $bridge 'turnReference.TryApply(' `
    "the exact pickup transition must be applied before model continuation"
Assert-Contains $controller 'current.TryWithCompanionHeldProp(' `
    "turn composition must validate the exact completed prop rather than recapture hands"
Assert-Order $pickup 'public void Conclude(float now)' `
    '[ACTION] PICKUP_JOB_CONCLUDED' `
    "pickup conclusion must release the completed job while leaving possession in game state"
Assert-Contains $jobContract 'internal bool RetainUntilAssistantAudio;' `
    "completion retention must be explicit rather than inferred from success"
Assert-Contains $inspection 'RetainUntilAssistantAudio = true' `
    "only the visual presentation hold should opt into assistant-audio retention"
Assert-Contains $controller '!retainUntilAssistantAudio' `
    "ordinary successful jobs must conclude as soon as their result is consumed"
Assert-Contains $controller 'controller._actions.ConcludeJob(' `
    "completion consumption must release the job before a tool-only continuation"
Assert-Contains $bridge 'pending.RetainJobUntilAssistantAudio &&' `
    "the agent bridge must retain only jobs that explicitly requested it"
Assert-NotContains $pickup 'RetainUntilAssistantAudio = true' `
    "pickup must never wait for assistant audio before releasing its job"
Assert-Contains $pickup '_target.IsStillTheSameProp(hands.heldProp)' `
    "compensating drop must be gated by exact held-prop identity"
Assert-Contains $pickup 'ServerDropPropAutomatic(false)' `
    "post-authority cancellation must use the verified host drop path"
Assert-NotContains $pickup 'FindObjects' `
    "pickup must never search for another prop"
Assert-NotContains $pickup 'castProp' `
    "pickup must never reinterpret the live gaze target"
Assert-NotContains $pickup 'nearest' `
    "pickup must never use nearest-item fallback"

Write-Host "Grounded-pickup protocol checks passed."
Write-Host "  Proven: source routing, response-turn binding, exact target identity, no fallback selector."
Write-Host "  Not proven: Unity runtime pickup, cancellation settlement, or deployed behavior."
