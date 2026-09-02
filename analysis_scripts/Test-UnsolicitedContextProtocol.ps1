#requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$CompilerPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ramblersRoot = Split-Path -Parent $PSScriptRoot
$compiler = (Resolve-Path -LiteralPath $CompilerPath).Path

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
        throw "Unsolicited-context check failed: $Description"
    }
}

function Assert-NotContains {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Needle,
        [Parameter(Mandatory = $true)][string]$Description
    )
    if ($Text.IndexOf($Needle, [System.StringComparison]::Ordinal) -ge 0) {
        throw "Unsolicited-context check failed: $Description"
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
        throw "Unsolicited-context check failed: $Description"
    }
}

function Extract-Span {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Start,
        [Parameter(Mandatory = $true)][string]$End,
        [Parameter(Mandatory = $true)][string]$Description
    )
    $startIndex = $Text.IndexOf($Start, [System.StringComparison]::Ordinal)
    if ($startIndex -lt 0) {
        throw "Unsolicited-context check failed: $Description start was not found"
    }
    $endIndex = $Text.IndexOf($End, $startIndex, [System.StringComparison]::Ordinal)
    if ($endIndex -le $startIndex) {
        throw "Unsolicited-context check failed: $Description end was not found"
    }
    return $Text.Substring($startIndex, $endIndex - $startIndex)
}

$prompt = Read-Source "src\AgentPrompt.cs"
$bridge = Read-Source "src\OpenAIRealtimeBridge.cs"
$client = Read-Source "src\OpenAIRealtimeClient.cs"
$awareness = Read-Source "src\CompanionAwareness.cs"
$controller = Read-Source "src\CompanionController.cs"
$affordance = Read-Source "src\CompanionAffordanceTarget.cs"
$policy = Read-Source "src\CompanionAwarenessInjectionPolicy.cs"
$audit = Read-Source "analysis_scripts\Audit-LatestRun.ps1"

Assert-Contains $prompt 'when the packet says it arrived unsolicited' `
    "the prompt must recognize an unsolicited perception packet"
Assert-Contains $prompt 'never announce that an update arrived' `
    "the model must fold injected facts in silently"
Assert-Contains $prompt 'commentary merely because context arrived.' `
    "injection alone must not license unsolicited speech"

$queueMethod = Extract-Span $client 'internal bool QueueUnsolicitedContext' `
    'internal void TruncateAudio' "the unsolicited queue method"
Assert-Contains $queueMethod 'type = "conversation.item.create"' `
    "unsolicited context must be a normal Realtime conversation item"
Assert-Contains $queueMethod 'role = "user"' `
    "the injected item must be visible to the next model response"
Assert-Contains $queueMethod 'NextEventId("game_event")' `
    "unsolicited items must carry their own event category"
Assert-NotContains $queueMethod 'QueueResponseCreate' `
    "injection must never create a response on its own"
Assert-NotContains $queueMethod 'RequestResponse' `
    "injection must never request a response"
Assert-NotContains $queueMethod 'RequestContinuation' `
    "injection must never request a continuation"

$updateMethod = Extract-Span $bridge 'private void Update()' `
    'private bool IsHumanSpeaking()' "the bridge update loop"
Assert-Order $updateMethod 'DrainClientEvents();' 'InjectUnsolicitedContext();' `
    "speech state must be current before an injection decision"
Assert-Order $updateMethod 'DrainFunctionCallBatches();' 'InjectUnsolicitedContext();' `
    "tool outputs must precede an injection in the same frame"
$injectMethod = Extract-Span $bridge 'private void InjectUnsolicitedContext()' `
    'private void ReleaseHeldContinuation()' "the bridge injection method"
Assert-Order $injectMethod 'IsHumanSpeaking())' `
    'CompanionController.TryTakeUnsolicitedAwarenessContext(' `
    "a speaking human must suppress injection before any capture"
Assert-Order $injectMethod '_client.QueueUnsolicitedContext(context.Message)' `
    'CompanionController.ConfirmUnsolicitedAwarenessContextDelivered(context)' `
    "delivery must be committed only after queue admission"
Assert-NotContains $injectMethod 'RequestResponse(' `
    "the injection path must not request a response"
Assert-NotContains $injectMethod 'RequestContinuation(' `
    "the injection path must not request a continuation"
Assert-NotContains $injectMethod 'QueueTurnContext(' `
    "unsolicited items must not masquerade as utterance-bound context"
Assert-Contains $injectMethod '[AWARENESS] EVENT_CONTEXT_INJECTED' `
    "runtime evidence must report every injection"
Assert-Contains $injectMethod 'humanSpeaking={IsHumanSpeaking()}' `
    "runtime evidence must prove the human was silent"
Assert-Contains $injectMethod 'sinceLastPacketSeconds={context.SecondsSinceLastPacket:F1}' `
    "runtime evidence must expose the minimum-interval gap"
Assert-Contains $injectMethod 'packetsLastMinute={context.PacketsLastMinute + 1}' `
    "runtime evidence must expose the per-minute count including this packet"
Assert-Contains $injectMethod '[AWARENESS] EVENT_CONTEXT_QUEUE_FAILED' `
    "queue failure must remain observable"

Assert-Contains $awareness 'private void RecordEvent(float now, string description, bool salient)' `
    "every journal event must declare whether it is salient"
Assert-Contains $awareness 'private static bool IsHumanActor(string actor)' `
    "held-item and landing salience must key on the human actor"
Assert-Contains $awareness 'CompanionAwarenessInjectionPolicy.NextScanAfterSalientEvent(' `
    "a salient event must pull the next scan forward within the rate rules"
Assert-Contains $awareness 'internal bool IsUnsolicitedScanDue(float now)' `
    "scans must be cadence gated rather than per frame"
Assert-Contains $awareness 'CompanionAwarenessInjectionPolicy.IsRateCapped(' `
    "the delta path must consult the shared rate policy"
Assert-Contains $awareness '[AWARENESS] EVENT_CONTEXT_DEFERRED' `
    "rate deferrals must be auditable"
Assert-Contains $awareness 'CompanionAwarenessInjectionPolicy.SelectTrigger(' `
    "trigger selection must use the shared policy"
Assert-Contains $awareness 'CompanionAwarenessInjectionPolicy.ShouldResendEntity(' `
    "entities must be delta-compressed against their last send"
Assert-Contains $awareness '"delta_since_last_packet"' `
    "the packet must declare itself a delta"
Assert-Contains $awareness 'arrived unsolicited while' `
    "the packet must be self-describing"
Assert-Contains $awareness 'internal void ConfirmUnsolicitedContextDelivered(' `
    "awareness must expose a delivery commit for unsolicited packets"
Assert-Contains $awareness '_unsolicitedSentAt.Enqueue(context.CapturedAt);' `
    "each unsolicited send must enter the rate window"
Assert-Contains $awareness 'CommitPacket(context);' `
    "both channels must share one last-sent snapshot"
Assert-Contains $awareness 'private void CommitPacket(CompanionAwarenessTurnContext context)' `
    "the last-sent snapshot commit must be a single path"
Assert-Contains $awareness 'while (_journal.Count > MaximumJournalEntries)' `
    "the journal bound must survive the new channel"
Assert-Contains $awareness 'private const int MaximumNearbyProps = 6;' `
    "the existing nearby caps must survive the new channel"
$unsolicitedMethod = Extract-Span $awareness 'internal bool TryTakeUnsolicitedContext(' `
    'internal void ConfirmUnsolicitedContextDelivered(' "the unsolicited capture method"
Assert-NotContains $unsolicitedMethod 'FromImage(' `
    "unsolicited packets must stay text-only"
Assert-NotContains $unsolicitedMethod '_passiveDelivered' `
    "unsolicited packets must not consume the passive visual frame"
Assert-Order $unsolicitedMethod 'IsRateCapped(' 'CaptureNearbyProps(' `
    "the rate cap must be checked before any world scan"

Assert-Contains $controller 'internal static bool TryTakeUnsolicitedAwarenessContext(' `
    "the controller must expose the unsolicited capture"
Assert-Contains $controller 'IsUnsolicitedScanDue(now)' `
    "the controller must honor the scan cadence before capturing references"
Assert-Contains $controller 'CompanionAffordanceCandidates.TryCaptureAmbient(' `
    "ambient scans must use the reference capture without a human gaze cast"
Assert-Contains $controller '[AWARENESS] EVENT_CONTEXT_FAILED' `
    "capture faults must be isolated and observable"
$ambientMethod = Extract-Span $affordance 'internal static bool TryCaptureAmbient(' `
    'internal bool TrySelect(' "the ambient candidate capture"
Assert-NotContains $ambientMethod 'TryCaptureHumanReferenceIdentity(' `
    "ambient capture must never cast the human's gaze"

Assert-Contains $policy 'MinimumIntervalSeconds = 4f;' `
    "the minimum interval must be declared"
Assert-Contains $policy 'MaximumPacketsPerMinute = 8;' `
    "the per-minute cap must be declared"
Assert-Contains $policy 'HeartbeatIntervalSeconds = 30f;' `
    "the heartbeat must be low frequency"
Assert-Contains $policy 'EntityResendSeconds = 45f;' `
    "entity resends must be bounded"

Assert-Contains $audit 'EVENT_CONTEXT_INJECTED trigger=' `
    "the runtime audit must parse injections"
Assert-Contains $audit 'Unsolicited context injected while the human was speaking' `
    "the runtime audit must fail a non-silent injection"
Assert-Contains $audit 'Unsolicited context exceeded the per-minute cap' `
    "the runtime audit must fail a cap violation"

$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$probeRoot = Join-Path $tempRoot (
    "RamblersInjectionPolicyProbe-" + [Guid]::NewGuid().ToString("N"))
$probeRoot = [System.IO.Path]::GetFullPath($probeRoot)
if (-not $probeRoot.StartsWith(
        $tempRoot,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Injection policy probe path escaped the temporary directory."
}

New-Item -ItemType Directory -Path $probeRoot | Out-Null
try {
    $probeOutput = Join-Path $probeRoot "CompanionAwarenessInjectionPolicyProbe.exe"
    & $compiler `
        /nologo `
        /target:exe `
        /langversion:latest `
        /optimize+ `
        "/out:$probeOutput" `
        (Join-Path $ramblersRoot "src\CompanionAwarenessInjectionPolicy.cs") `
        (Join-Path $PSScriptRoot "CompanionAwarenessInjectionPolicyProbe.cs")
    if ($LASTEXITCODE -ne 0) {
        throw "Injection policy probe compilation failed with exit code $LASTEXITCODE."
    }

    & $probeOutput
    if ($LASTEXITCODE -ne 0) {
        throw "Injection policy probe failed with exit code $LASTEXITCODE."
    }
}
finally {
    if (Test-Path -LiteralPath $probeRoot) {
        Remove-Item -LiteralPath $probeRoot -Recurse -Force
    }
}

Write-Host "Unsolicited-context protocol checks passed."
Write-Host "  Proven: silent-human gating, response-free injection, shared rate policy, delta compression, one last-sent snapshot, and audit coverage."
Write-Host "  Not proven: live Unity state quality, model use of injected facts, or real-time cadence."
