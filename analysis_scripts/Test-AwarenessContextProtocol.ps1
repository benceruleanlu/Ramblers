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
        throw "Awareness-context check failed: $Description"
    }
}

function Assert-NotContains {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Needle,
        [Parameter(Mandatory = $true)][string]$Description
    )
    if ($Text.IndexOf($Needle, [System.StringComparison]::Ordinal) -ge 0) {
        throw "Awareness-context check failed: $Description"
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
        throw "Awareness-context check failed: $Description"
    }
}

$prompt = Read-Source "src\AgentPrompt.cs"
$bridge = Read-Source "src\OpenAIRealtimeBridge.cs"
$client = Read-Source "src\OpenAIRealtimeClient.cs"
$awareness = Read-Source "src\CompanionAwareness.cs"
$ambient = Read-Source "src\CompanionAmbientGaze.cs"
$controller = Read-Source "src\CompanionController.cs"
$interactionTarget = Read-Source "src\CompanionInteractionTarget.cs"
$entityReferences = Read-Source "src\CompanionEntityReferences.cs"

$queueStart = $client.IndexOf(
    "internal bool QueueTurnContext",
    [System.StringComparison]::Ordinal)
$queueEnd = $client.IndexOf(
    "internal void TruncateAudio",
    $queueStart,
    [System.StringComparison]::Ordinal)
if ($queueStart -lt 0 -or $queueEnd -le $queueStart) {
    throw "Awareness-context check failed: QueueTurnContext method boundaries were not found"
}
$queueMethod = $client.Substring($queueStart, $queueEnd - $queueStart)

Assert-Contains $prompt 'Items beginning [GAME_CONTEXT] are' `
    "the prompt must recognize the structured perception packet"
Assert-Contains $prompt 'private nonverbal perception paired with the preceding human' `
    "the packet must be paired with speech without impersonating the human"
Assert-Contains $prompt 'never reply to the packet itself' `
    "the model must not answer synthetic context as a separate utterance"
Assert-Contains $prompt 'Do not start unsolicited ' `
    "the prompt must begin the unsolicited-commentary prohibition"
Assert-Contains $prompt 'commentary merely because context arrived.' `
    "passive awareness must remain conversationally silent"
Assert-Contains $prompt 'use inspect_reference yourself' `
    "precise visual inspection must remain an autonomous fallback"

Assert-Contains $bridge 'CompanionController.TryTakeAwarenessTurnContext(' `
    "one context snapshot must be frozen at the human-turn boundary"
Assert-Order $bridge 'CompanionController.TryTakeAwarenessTurnContext(' `
    '_client.QueueTurnContext(awarenessContext.Message);' `
    "capture must precede queuing"
Assert-Order $bridge '_client.QueueTurnContext(awarenessContext.Message);' `
    '_client.RequestResponse(turnId);' `
    "the context item must enter the conversation before response.create"
Assert-Order $bridge '_client.QueueTurnContext(awarenessContext.Message);' `
    'CompanionController.ConfirmAwarenessTurnContextDelivered(' `
    "events and visual memory must be consumed only after queue success"
Assert-Contains $bridge '[AWARENESS] TURN_CONTEXT_CAPTURED' `
    "runtime evidence must report context delivery"
Assert-Contains $bridge 'events={awarenessContext.EventCount}' `
    "runtime evidence must include the event count"
Assert-Contains $bridge 'nearbyProps={awarenessContext.NearbyPropCount}' `
    "runtime evidence must include the nearby-prop count"
Assert-Contains $bridge 'nearbyInteractables={awarenessContext.NearbyInteractableCount}' `
    "runtime evidence must include actionable switch discovery"
Assert-Contains $bridge 'rememberedProps={awarenessContext.RememberedPropCount}' `
    "runtime evidence must include cross-turn entity memory"
Assert-Contains $bridge 'actionableEntities={awarenessContext.EntityReferences?.Count ?? 0}' `
    "runtime evidence must report exact actionable entity bindings"
Assert-Contains $bridge 'visualAttached={awarenessContext.HasImage}' `
    "runtime evidence must identify image attachment"
Assert-Contains $bridge '[AWARENESS] TURN_CONTEXT_QUEUE_FAILED' `
    "context serialization failure must remain observable"
Assert-Order $bridge '[AWARENESS] TURN_CONTEXT_QUEUE_FAILED' `
    '_client.RequestResponse(turnId);' `
    "an awareness queue failure must not suppress the human response"

Assert-Contains $queueMethod 'type = "conversation.item.create"' `
    "context must use a normal Realtime conversation item"
Assert-Contains $queueMethod 'role = "user"' `
    "the nonverbal item must be visible to the next model response"
Assert-NotContains $queueMethod 'QueueResponseCreate' `
    "queuing context must never trigger an unsolicited response"
Assert-NotContains $queueMethod 'RequestResponse' `
    "the bridge alone must own response timing"
Assert-Contains $queueMethod 'return QueueJson(new' `
    "context delivery must report actual outbound admission"
Assert-Contains $client 'private bool QueueRaw(string json)' `
    "outbound queue admission must be observable to one-shot callers"
Assert-Contains $client 'if (_disposed || _cancellation.IsCancellationRequested)' `
    "queue admission must reject a closed client"

Assert-Contains $awareness 'private const int MaximumJournalEntries = 8;' `
    "the recent event journal must remain bounded"
Assert-Contains $awareness 'private const float JournalLifetimeSeconds = 120f;' `
    "old events must expire"
Assert-Contains $awareness 'private const int MaximumNearbyProps = 6;' `
    "nearby prop context must remain compact"
Assert-Contains $awareness 'private const int MaximumNearbyPlayers = 3;' `
    "nearby player context must remain compact"
Assert-Contains $awareness 'private const int MaximumNearbyInteractables = 6;' `
    "nearby interaction context must remain compact"
Assert-Contains $awareness 'payloadIndices.TryGetValue(' `
    "one exact switch must not consume multiple bounded context entries"
Assert-Contains $awareness 'while (_journal.Count > MaximumJournalEntries)' `
    "the journal bound must be enforced"
Assert-Contains $awareness 'var props = Prop.allProps;' `
    "nearby props must come from the game's maintained registry"
Assert-Contains $awareness 'left.distance_from_human_m)' `
    "the compact prop list must retain items relevant to either player"
Assert-NotContains $awareness 'Resources.FindObjectsOfTypeAll<Prop>' `
    "awareness must not perform an exhaustive Unity object scan"
Assert-Contains $interactionTarget '"prop:net:" + identity.netId' `
    "network props must carry stable identity"
Assert-Contains $interactionTarget '"prop:local:" + prop.GetInstanceID()' `
    "local props must have an explicit scoped fallback identity"
Assert-Contains $awareness 'recently_seen_props = rememberedProps' `
    "recent object context must survive beyond the immediate nearby list"
Assert-Contains $awareness 'private const float RememberedPropLifetimeSeconds = 45f;' `
    "cross-turn object memory must be short lived"
Assert-Contains $awareness 'entityReferences.Add(target);' `
    "remembered context IDs must remain actionable against the same object"
Assert-Contains $awareness 'internal void ConfirmTurnContextDelivered(' `
    "awareness must expose an explicit successful-delivery commit boundary"
Assert-Contains $awareness 'context.DeliveredThroughEventSequence' `
    "event delivery must commit only through the captured sequence"
Assert-Contains $entityReferences '_props.TryGetValue(stableId, out target)' `
    "action targeting must resolve only an exact model-selected ID"
Assert-Contains $entityReferences '!target.TryGetCurrentPoint(out point)' `
    "remembered action handles must revalidate live availability"
Assert-Contains $awareness 'Private nonverbal game perception for the preceding human utterance.' `
    "each packet must be self-describing"

Assert-Contains $ambient '_conversationActive ||' `
    "passive capture must be disabled during conversation"
Assert-Contains $ambient 'now - _glanceStartedAt < VisualMemorySettleSeconds' `
    "a visible glance must settle before becoming memory"
Assert-Contains $ambient '_attention.IsAimWithin(' `
    "the visible gaze must actually reach its target"
Assert-Contains $awareness 'private const float PassiveCaptureIntervalSeconds = 30f;' `
    "passive visual capture must be cadence limited"
Assert-Contains $awareness 'private const float PassiveVisualFreshnessSeconds = 45f;' `
    "old passive frames must not be presented as current"
Assert-Contains $awareness 'CompanionVisionCapture.TryCapture(' `
    "passive vision must reuse the established capture path"
Assert-Contains $awareness '_nextPassiveCaptureAt = now + PassiveCandidateRetrySeconds;' `
    "a failed or unchanged capture candidate must retry before the normal cadence"
Assert-Contains $awareness 'var attachVisual = !_passiveDelivered' `
    "a retained frame must be attached at most once"
Assert-Contains $awareness 'if (context.HasImage && context.PassiveCapturedAt >= 0f' `
    "visual delivery must be explicitly committed after successful queueing"
Assert-Contains $awareness 'Mathf.Approximately(context.PassiveCapturedAt, _passiveCapturedAt)' `
    "delivery confirmation must not consume a newer passive frame"
Assert-Contains $awareness '_passiveDelivered = true;' `
    "one-shot visual delivery must be enforced"

Assert-Contains $controller '_awareness.Tick(now);' `
    "deterministic state observation must run with the companion"
Assert-Order $controller '_actions.TickLateFrame(now);' `
    '_actions.TryTakeAmbientObservation(now, out candidate)' `
    "ambient observation must follow the visible late-frame gaze update"
Assert-Contains $controller '_awareness.TryRememberPassiveView(now, candidate);' `
    "only settled ambient candidates may enter visual memory"
Assert-Contains $controller '[AWARENESS] PASSIVE_VIEW_UPDATE_FAILED' `
    "passive-view faults must be isolated from action-job failure handling"
Assert-Contains $controller '[AWARENESS] BIND_FAILED' `
    "awareness bind faults must degrade without preventing companion spawn"

Write-Host "Awareness-context checks passed."
Write-Host "  Proven: bounded structured context, turn ordering, silent delivery, stable nearby identities, settled ambient capture, freshness, one-shot images, and telemetry."
Write-Host "  Not proven: live Unity state quality, model interpretation, or captured image composition."
