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
$client = Read-Source "src\OpenAIRealtimeClient.cs"
$router = Read-Source "src\AgentToolRouter.cs"
$catalog = Read-Source "src\AgentToolCatalog.cs"
$target = Read-Source "src\CompanionInteractionTarget.cs"
$pickup = Read-Source "src\CompanionPickupBehavior.cs"
$jobContract = Read-Source "src\CompanionJob.cs"
$controller = Read-Source "src\CompanionController.cs"
$inspection = Read-Source "src\CompanionInspectionBehavior.cs"
$bridge = Read-Source "src\OpenAIRealtimeBridge.cs"

Assert-Contains $catalog 'internal const string PickUpItem = "pick_up_item";' `
    "the allowlist must expose the bounded pickup tool"
Assert-Contains $catalog '@enum = new[] { "human_reference" }' `
    "the model may select only the frozen human reference"
Assert-Contains $router 'CompanionTurnReference turnReference' `
    "dispatch must receive response-scoped reference context"
Assert-Contains $router 'InteractionTarget = turnReference.Target' `
    "the job request must receive the frozen target"

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
Assert-Contains $pickup '[ACTION] PICKUP_JOB_CONCLUDED' `
    "a completed pickup must log release of its job lifecycle"
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
