#requires -Version 5.1

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$audit = Get-Content -LiteralPath (Join-Path $PSScriptRoot "Audit-LatestRun.ps1") -Raw
$bridge = Get-Content -LiteralPath (Join-Path $PSScriptRoot "..\src\OpenAIRealtimeBridge.cs") -Raw
$client = Get-Content -LiteralPath (Join-Path $PSScriptRoot "..\src\OpenAIRealtimeClient.cs") -Raw
$plugin = Get-Content -LiteralPath (Join-Path $PSScriptRoot "..\src\RamblersPlugin.cs") -Raw
$kick = Get-Content -LiteralPath (Join-Path $PSScriptRoot "..\src\CompanionKickBehavior.cs") -Raw
$move = Get-Content -LiteralPath (Join-Path $PSScriptRoot "..\src\CompanionMoveToLocationBehavior.cs") -Raw
$build = Get-Content -LiteralPath (Join-Path $PSScriptRoot "..\build.ps1") -Raw
$sharedLogReaderPath = Join-Path $PSScriptRoot "..\scripts\Read-SharedLogLines.ps1"
$followRuntimeInvariantsPath = Join-Path $PSScriptRoot "..\scripts\FollowRuntimeInvariants.ps1"
$followRuntimeInvariants = Get-Content -LiteralPath $followRuntimeInvariantsPath -Raw
$physicalActionRuntimeInvariantsPath = Join-Path $PSScriptRoot "..\scripts\PhysicalActionRuntimeInvariants.ps1"
$physicalActionRuntimeInvariants = Get-Content -LiteralPath $physicalActionRuntimeInvariantsPath -Raw
$toolBatchSettlementRuntimeInvariantsPath = Join-Path $PSScriptRoot "..\scripts\ToolBatchSettlementRuntimeInvariants.ps1"
$toolBatchSettlementRuntimeInvariants = Get-Content -LiteralPath $toolBatchSettlementRuntimeInvariantsPath -Raw
. $sharedLogReaderPath
. $followRuntimeInvariantsPath
. $physicalActionRuntimeInvariantsPath
. $toolBatchSettlementRuntimeInvariantsPath

function Assert-Contains {
    param(
        [Parameter(Mandatory = $true)][string]$Needle,
        [Parameter(Mandatory = $true)][string]$Description
    )
    if ($audit.IndexOf($Needle, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Runtime-audit check failed: $Description"
    }
}

function Assert-SettlementContains {
    param(
        [Parameter(Mandatory = $true)][string]$Needle,
        [Parameter(Mandatory = $true)][string]$Description
    )
    if ($toolBatchSettlementRuntimeInvariants.IndexOf(
            $Needle,
            [System.StringComparison]::Ordinal) -lt 0) {
        throw "Runtime-audit check failed: $Description"
    }
}

Assert-Contains 'Source proof: version=$sourceVersion commit=$gitHead' `
    "the report must identify source version and commit"
Assert-Contains '& $buildPath -NoRestore -GamePath $GamePath' `
    "the gate must freshly compile current source before accepting build proof"
Assert-Contains 'Build proof: fresh=$buildSucceeded hash=$distHash' `
    "the report must distinguish fresh build identity"
Assert-Contains 'Deployment proof: hash=$deployedHash' `
    "the report must distinguish installed identity"
Assert-Contains 'Codec proof: builtHash=$distCodecHash deployedHash=$deployedCodecHash' `
    "the report must bind the deployed JPEG codec to the built dependency"
Assert-Contains 'Runtime proof: loadedVersion=$loadedVersion loadedHash=$loadedHash ready=$ready' `
    "the report must distinguish startup/runtime identity"
Assert-Contains 'Runtime mismatch: the latest run loaded a different DLL than the one currently deployed.' `
    "runtime evidence must bind to the exact deployed assembly"
Assert-Contains '@($sessionLines -match ''\[AGENT\] READY '').Count -gt 0' `
    "one unmatched scalar log line must not masquerade as READY"
Assert-Contains 'Visual QA: not assessed by this command' `
    "the report must not present structured evidence as visual proof"
Assert-Contains 'Deployment mismatch: built and installed DLL hashes differ.' `
    "a stale deployment must fail the gate"
Assert-Contains 'Identity mismatch:' `
    "multiple immutable identities for one call/action must fail the gate"
Assert-Contains 'Get-TargetResolutionEvidence -Line $line' `
    "target-resolution evidence must be parsed by the shared invariant"
Assert-Contains 'Add-TargetResolutionEvidence' `
    "target identity aggregation must use exact call identity"
Assert-Contains 'without a completed tool batch' `
    "successful physical actions must have a terminal batch"
Assert-Contains 'did not retain one exact human identity' `
    "successful player carry actions must retain one call-scoped human identity"
Assert-Contains 'this may be a stale blocker' `
    "an in-progress result without an active job must fail the gate"
Assert-Contains 'still has an unresolved deferred tool batch' `
    "unfinished jobs at log end must fail the gate"
Assert-SettlementContains 'ended its tool batch before cancellation reconciliation settled or detached' `
    "a model-visible terminal batch must not precede cancellation settlement"
Assert-SettlementContains 'still has unresolved cancellation reconciliation' `
    "reconciliation without a terminal or ownership-transfer marker must fail the gate"
Assert-SettlementContains 'without later controller settlement or ownership abandonment' `
    "detached reconciliation must remain owned through a controller terminal marker"

$orderedSettlement = @(
    '[AGENT] TOOL_BATCH_RECONCILIATION_STARTED responseId=response_ok, turnId=1, waitingIndex=0, reason=job_timeout.',
    '[AGENT] TOOL_BATCH_RECONCILED responseId=response_ok, turnId=1, reason=job_timeout.',
    '[AGENT] TOOL_BATCH_COMPLETED responseId=response_ok, turnId=1, calls=1, continuation=0.'
)
if (@(Get-ToolBatchSettlementViolations -Lines $orderedSettlement).Count -ne 0) {
    throw "Runtime-audit check failed: ordered reconciliation was rejected"
}

$earlyTerminal = @(
    '[AGENT] TOOL_BATCH_RECONCILIATION_STARTED responseId=response_early, turnId=2, waitingIndex=0, reason=job_timeout.',
    '[AGENT] TOOL_BATCH_COMPLETED responseId=response_early, turnId=2, calls=1, continuation=0.',
    '[AGENT] TOOL_BATCH_RECONCILED responseId=response_early, turnId=2, reason=job_timeout.'
)
$earlyViolations = @(Get-ToolBatchSettlementViolations -Lines $earlyTerminal)
if ($earlyViolations.Count -ne 1 -or
    $earlyViolations[0] -notmatch 'before cancellation reconciliation') {
    throw "Runtime-audit check failed: early model-visible completion escaped settlement ordering"
}

$settledDetachment = @(
    '[AGENT] TOOL_BATCH_RECONCILIATION_STARTED responseId=response_detached, turnId=3, waitingIndex=0, reason=client_replaced.',
    '[AGENT] TOOL_BATCH_RECONCILIATION_DETACHED responseId=response_detached, turnId=3, token=41, reason=client_replaced.',
    '[AGENT] TOOL_BATCH_CANCELLED responseId=response_detached, turnId=3, settlement=detached.',
    '[ACTION] JOB_TOKEN_RETIRED token=41, job=pick_up_item, reason=detached_reconciled.'
)
if (@(Get-ToolBatchSettlementViolations -Lines $settledDetachment).Count -ne 0) {
    throw "Runtime-audit check failed: controller-settled detachment was rejected"
}

$unfinishedDetachment = @(
    '[AGENT] TOOL_BATCH_RECONCILIATION_STARTED responseId=response_orphan, turnId=4, waitingIndex=0, reason=client_replaced.',
    '[AGENT] TOOL_BATCH_RECONCILIATION_DETACHED responseId=response_orphan, turnId=4, token=42, reason=client_replaced.',
    '[AGENT] TOOL_BATCH_CANCELLED responseId=response_orphan, turnId=4, settlement=detached.'
)
$detachedViolations = @(Get-ToolBatchSettlementViolations -Lines $unfinishedDetachment)
if ($detachedViolations.Count -ne 1 -or
    $detachedViolations[0] -notmatch 'without later controller settlement') {
    throw "Runtime-audit check failed: orphaned detached reconciliation passed"
}
Assert-Contains 'still has an unreleased presentation job' `
    "inspection presentation holds must be released before the run passes"
Assert-Contains 'discarded its tool output batch' `
    "rejected function output must fail the gate"
Assert-Contains 'without deferring that tool batch' `
    "stale blockers must be correlated by exact response rather than historical turn"
Assert-Contains 'stop or protocol error(s) after its latest READY' `
    "a dead or protocol-erroring Realtime client must not pass from historical readiness"
Assert-Contains 'no turn completed request, creation, first audio, and response.done in order' `
    "partial or crashed turns must not satisfy required spoken proof"
Assert-Contains 'status=completed.*firstAudioSeen=True' `
    "cancelled responses with partial audio must not satisfy spoken-turn proof"
Assert-Contains 'has a response request with no later response.done acknowledgement' `
    "a cancellation request without server completion must remain unfinished"
Assert-Contains 'stage=(?<stage>[^,]+)' `
    "turn-latency stages must be included in the compact report"
Assert-Contains 'Get-FollowTangentViolation -Line $line' `
    "the audit must inspect every uncommitted traversal-tangent decision"
if ($followRuntimeInvariants.IndexOf(
        'Follow replayed an uncommitted $kind traversal tangent',
        [System.StringComparison]::Ordinal) -lt 0) {
    throw "Runtime-audit check failed: a tangent outside its approach corridor must fail the runtime gate"
}
Assert-Contains 'Kick launches: $($kickLaunches.Count)' `
    "the compact report must expose exact kick launch evidence"
Assert-Contains 'Interaction confirmations: $($interactionEvidence.Count)' `
    "the compact report must expose exact native-affordance confirmation"
Assert-Contains '"go_to_location",' `
    "directed movement must receive the same successful physical-call audit gate"
Assert-Contains 'Directed-move arrivals: $($directedMoveEvidence.Count)' `
    "the compact report must expose call-scoped directed-move arrival evidence"
if ($move.IndexOf(
        'callId={CallIdForLog}, turnId={_turnId}',
        [System.StringComparison]::Ordinal) -lt 0) {
    throw "Runtime-audit check failed: directed movement terminal evidence is not call-scoped"
}
if ($physicalActionRuntimeInvariants.IndexOf(
        'without its exact native-affordance confirmation',
        [System.StringComparison]::Ordinal) -lt 0 -or
    $physicalActionRuntimeInvariants.IndexOf(
        'confirmed a different interaction target',
        [System.StringComparison]::Ordinal) -lt 0) {
    throw "Runtime-audit check failed: a successful interaction result must bind its exact deterministic postcondition"
}
Assert-Contains 'Get-InteractionConfirmationEvidence -Line $line' `
    "interaction confirmations must be parsed by their reusable invariant"
Assert-Contains 'Get-InteractionTargetViolation' `
    "each successful interaction call must bind resolution and confirmation identities"
Assert-Contains 'Get-TowardReferenceKickViolation -Details $launch' `
    "goal-directed kick evidence must run through its reusable geometric invariant"
Assert-Contains 'Get-KickLaunchEvidence -Line $line' `
    "kick launches must retain their exact model-call identity"
Assert-Contains 'Get-KickLaunchCallViolation' `
    "every successful kick must bind its resolved target to one exact launch"
Assert-Contains 'Set-Content -LiteralPath $OutputPath' `
    "the audit must leave a diagnostic artifact"
Assert-Contains '$allLines = @(Read-SharedLogLines $logPath)' `
    "a one-line log must retain array semantics"
Assert-Contains 'exit 1' `
    "invariant violations must produce a non-zero exit"
if ($plugin.IndexOf('assemblySha256={assemblySha256}', [System.StringComparison]::Ordinal) -lt 0) {
    throw "Runtime-audit check failed: startup must log the exact loaded assembly hash"
}
if ($build.IndexOf('System.Security.Cryptography.Algorithms.dll', [System.StringComparison]::Ordinal) -lt 0 -or
    $build.IndexOf('System.Security.Cryptography.Primitives.dll', [System.StringComparison]::Ordinal) -lt 0) {
    throw "Runtime-audit check failed: the build must include the managed assembly-hash dependencies"
}
if ($client.IndexOf('_logs.Enqueue("CONNECTION_STOPPED")', [System.StringComparison]::Ordinal) -lt 0) {
    throw "Runtime-audit check failed: a clean client disconnect must emit terminal evidence"
}
if ($physicalActionRuntimeInvariants.IndexOf(
        'launch direction did not point at its frozen destination',
        [System.StringComparison]::Ordinal) -lt 0 -or
    $physicalActionRuntimeInvariants.IndexOf(
        '$stockMaxForce -le 0.0',
        [System.StringComparison]::Ordinal) -lt 0) {
    throw "Runtime-audit check failed: the physical-action invariant must check destination geometry and stock force"
}
if ($kick.IndexOf(
        'System.FormattableString.Invariant(',
        [System.StringComparison]::Ordinal) -lt 0 -or
    $kick.IndexOf(
        'destinationPoint={DestinationPointForLog}',
        [System.StringComparison]::Ordinal) -lt 0) {
    throw "Runtime-audit check failed: kick telemetry must publish invariant numeric destination evidence"
}

$safeJumpTangent = Get-FollowTangentViolation -Line (
    '[FOLLOW] ROUTE_TANGENT breadcrumb=7, kind=jump, ' +
    'horizontalDistance=1.600, corridor=1.600.')
if ($null -ne $safeJumpTangent) {
    throw "Runtime-audit check failed: an on-boundary jump tangent was rejected"
}
$emptyLineViolation = Get-FollowTangentViolation -Line ''
if ($null -ne $emptyLineViolation) {
    throw "Runtime-audit check failed: an empty log line produced a tangent violation"
}
$outsideJumpTangent = Get-FollowTangentViolation -Line (
    '[FOLLOW] ROUTE_TANGENT breadcrumb=7, kind=jump, ' +
    'horizontalDistance=1.610, corridor=1.600.')
if ($null -eq $outsideJumpTangent) {
    throw "Runtime-audit check failed: a 1.61m jump tangent escaped the 1.60m corridor"
}
$outsideDropTangent = Get-FollowTangentViolation -Line (
    '[FOLLOW] ROUTE_TANGENT breadcrumb=8, kind=drop, ' +
    'horizontalDistance=1.810, corridor=1.800.')
if ($null -eq $outsideDropTangent) {
    throw "Runtime-audit check failed: a 1.81m drop tangent escaped the 1.80m corridor"
}

$firstInteraction = Get-InteractionConfirmationEvidence -Line (
    '[INTERACT] CONFIRMED kind=world_switch, referenceId=switch:local:1, ' +
    'observation=state=1, cancelRequested=False, callId=call_one, turnId=7.')
$secondInteraction = Get-InteractionConfirmationEvidence -Line (
    '[INTERACT] CONFIRMED kind=player_pose, referenceId=pose:local:2, ' +
    'observation=poseMatch=True, cancelRequested=False, callId=call_two, turnId=7.')
if ($null -eq $firstInteraction -or $null -eq $secondInteraction -or
    $firstInteraction.CallId -ne 'call_one' -or
    $secondInteraction.CallId -ne 'call_two' -or
    $firstInteraction.CallId -eq $secondInteraction.CallId) {
    throw "Runtime-audit check failed: two interactions in one turn did not retain distinct call identities"
}
$uncorrelatedInteraction = Get-InteractionConfirmationEvidence -Line (
    '[INTERACT] CONFIRMED kind=world_switch, referenceId=switch:local:1, ' +
    'observation=state=1, cancelRequested=False, turnId=7.')
if ($null -ne $uncorrelatedInteraction) {
    throw "Runtime-audit check failed: an interaction without a call identity was accepted"
}

$interactionTarget = Get-TargetResolutionEvidence -Line (
    '[ENTITY] TARGET_RESOLVED action=interact_with_object, target=human_reference, ' +
    'referenceId=switch:local:1, netId=0, callId=call_one, turnId=7.')
$interactionResolutions = @{}
[void](Add-TargetResolutionEvidence `
    -ResolvedIdentities $interactionResolutions `
    -Evidence $interactionTarget)
$interactionConfirmations = @{}
Add-InteractionConfirmationEvidence `
    -ConfirmedIdentities $interactionConfirmations `
    -Evidence $firstInteraction
if ($null -ne (Get-InteractionTargetViolation `
        -ResolvedIdentities $interactionResolutions `
        -ConfirmedIdentities $interactionConfirmations `
        -CallId 'call_one')) {
    throw "Runtime-audit check failed: a matching resolution and confirmation was rejected"
}
$mismatchedInteraction = Get-InteractionConfirmationEvidence -Line (
    '[INTERACT] CONFIRMED kind=world_switch, referenceId=switch:local:99, ' +
    'observation=state=1, cancelRequested=False, callId=call_mismatch, turnId=7.')
$mismatchedTarget = Get-TargetResolutionEvidence -Line (
    '[ENTITY] TARGET_RESOLVED action=interact_with_object, target=human_reference, ' +
    'referenceId=switch:local:1, netId=0, callId=call_mismatch, turnId=7.')
$mismatchedResolutions = @{}
[void](Add-TargetResolutionEvidence `
    -ResolvedIdentities $mismatchedResolutions `
    -Evidence $mismatchedTarget)
$mismatchedConfirmations = @{}
Add-InteractionConfirmationEvidence `
    -ConfirmedIdentities $mismatchedConfirmations `
    -Evidence $mismatchedInteraction
$mismatchViolation = Get-InteractionTargetViolation `
    -ResolvedIdentities $mismatchedResolutions `
    -ConfirmedIdentities $mismatchedConfirmations `
    -CallId 'call_mismatch'
if ($null -eq $mismatchViolation -or
    $mismatchViolation.IndexOf(
        'different interaction target',
        [System.StringComparison]::Ordinal) -lt 0) {
    throw "Runtime-audit check failed: mismatched resolution and confirmation identities passed"
}

$firstTarget = Get-TargetResolutionEvidence -Line (
    '[ENTITY] TARGET_RESOLVED action=pick_up_item, target=prop:one, ' +
    'referenceId=prop:net:11, netId=11, callId=call_one, turnId=7.')
$secondTarget = Get-TargetResolutionEvidence -Line (
    '[ENTITY] TARGET_RESOLVED action=pick_up_item, target=prop:two, ' +
    'referenceId=prop:net:22, netId=22, callId=call_two, turnId=7.')
$resolvedProbe = @{}
[void](Add-TargetResolutionEvidence -ResolvedIdentities $resolvedProbe -Evidence $firstTarget)
[void](Add-TargetResolutionEvidence -ResolvedIdentities $resolvedProbe -Evidence $secondTarget)
if ($resolvedProbe.Count -ne 2 -or
    $resolvedProbe['call_one|pick_up_item'].Count -ne 1 -or
    $resolvedProbe['call_two|pick_up_item'].Count -ne 1) {
    throw "Runtime-audit check failed: distinct same-turn physical calls were falsely merged"
}
$conflictingTarget = Get-TargetResolutionEvidence -Line (
    '[ENTITY] TARGET_RESOLVED action=pick_up_item, target=prop:other, ' +
    'referenceId=prop:net:33, netId=33, callId=call_one, turnId=7.')
[void](Add-TargetResolutionEvidence -ResolvedIdentities $resolvedProbe -Evidence $conflictingTarget)
if ($resolvedProbe['call_one|pick_up_item'].Count -ne 2) {
    throw "Runtime-audit check failed: one call retargeting to a second identity escaped aggregation"
}
$uncorrelatedTarget = Get-TargetResolutionEvidence -Line (
    '[ENTITY] TARGET_RESOLVED action=pick_up_item, target=prop:one, ' +
    'referenceId=prop:net:11, netId=11, turnId=7.')
if ($null -ne $uncorrelatedTarget) {
    throw "Runtime-audit check failed: target evidence without call identity was accepted"
}

$moveTarget = Get-TargetResolutionEvidence -Line (
    '[ENTITY] TARGET_RESOLVED action=go_to_location, target=human_indicated_location, ' +
    'source=human_gaze_raycast_hit, referenceId=location:1:2:3, ' +
    'destinationPoint=(1.000, 2.000, 3.000), callId=call_move, turnId=9.')
$moveArrival = Get-DirectedMoveArrivalEvidence -Line (
    '[ACTION] GO_TO_LOCATION_ARRIVED referenceId=location:1:2:3, ' +
    'destinationPoint=(1.000, 2.000, 3.000), horizontalDistance=0.20, ' +
    'verticalDistance=0.10, carryingPlayer=True, callId=call_move, turnId=9.')
if ($null -eq $moveTarget -or $null -eq $moveArrival -or
    $moveArrival.DestinationPoint -ne '(1.000, 2.000, 3.000)') {
    throw "Runtime-audit check failed: call-scoped directed-move telemetry was not parsed"
}
$moveResolutions = @{}
[void](Add-TargetResolutionEvidence `
    -ResolvedIdentities $moveResolutions `
    -Evidence $moveTarget)
$moveResolutionPoints = @{}
Add-DirectedMoveResolutionEvidence `
    -ResolutionsByCall $moveResolutionPoints `
    -Evidence $moveTarget
$moveArrivals = @{}
Add-DirectedMoveArrivalEvidence `
    -ArrivalsByCall $moveArrivals `
    -Evidence $moveArrival
$validMoveViolation = Get-DirectedMoveCallViolation `
    -ResolvedIdentities $moveResolutions `
    -ResolutionsByCall $moveResolutionPoints `
    -ArrivalsByCall $moveArrivals `
    -CallId 'call_move' `
    -TurnId 9
if ($null -ne $validMoveViolation) {
    throw "Runtime-audit check failed: matching directed-move evidence was rejected: $validMoveViolation"
}
$wrongMoveArrival = Get-DirectedMoveArrivalEvidence -Line (
    '[ACTION] GO_TO_LOCATION_ARRIVED referenceId=location:9:9:9, ' +
    'destinationPoint=(9.000, 9.000, 9.000), horizontalDistance=0.20, ' +
    'verticalDistance=0.10, carryingPlayer=True, callId=call_wrong_move, turnId=9.')
$wrongMoveTarget = Get-TargetResolutionEvidence -Line (
    '[ENTITY] TARGET_RESOLVED action=go_to_location, target=human_indicated_location, ' +
    'source=human_gaze_raycast_hit, referenceId=location:1:2:3, ' +
    'destinationPoint=(1.000, 2.000, 3.000), callId=call_wrong_move, turnId=9.')
$wrongMoveResolutions = @{}
[void](Add-TargetResolutionEvidence `
    -ResolvedIdentities $wrongMoveResolutions `
    -Evidence $wrongMoveTarget)
$wrongMoveResolutionPoints = @{}
Add-DirectedMoveResolutionEvidence `
    -ResolutionsByCall $wrongMoveResolutionPoints `
    -Evidence $wrongMoveTarget
$wrongMoveArrivals = @{}
Add-DirectedMoveArrivalEvidence `
    -ArrivalsByCall $wrongMoveArrivals `
    -Evidence $wrongMoveArrival
$wrongMoveViolation = Get-DirectedMoveCallViolation `
    -ResolvedIdentities $wrongMoveResolutions `
    -ResolutionsByCall $wrongMoveResolutionPoints `
    -ArrivalsByCall $wrongMoveArrivals `
    -CallId 'call_wrong_move' `
    -TurnId 9
if ($null -eq $wrongMoveViolation -or
    $wrongMoveViolation.IndexOf(
        'different destination',
        [System.StringComparison]::Ordinal) -lt 0) {
    throw "Runtime-audit check failed: mismatched directed-move destination passed"
}
$sameIdWrongPointArrival = Get-DirectedMoveArrivalEvidence -Line (
    '[ACTION] GO_TO_LOCATION_ARRIVED referenceId=location:1:2:3, ' +
    'destinationPoint=(9.000, 9.000, 9.000), horizontalDistance=0.20, ' +
    'verticalDistance=0.10, carryingPlayer=True, callId=call_move, turnId=9.')
$sameIdWrongPointArrivals = @{}
Add-DirectedMoveArrivalEvidence `
    -ArrivalsByCall $sameIdWrongPointArrivals `
    -Evidence $sameIdWrongPointArrival
$sameIdWrongPointViolation = Get-DirectedMoveCallViolation `
    -ResolvedIdentities $moveResolutions `
    -ResolutionsByCall $moveResolutionPoints `
    -ArrivalsByCall $sameIdWrongPointArrivals `
    -CallId 'call_move' `
    -TurnId 9
if ($null -eq $sameIdWrongPointViolation -or
    $sameIdWrongPointViolation.IndexOf(
        'coordinates different',
        [System.StringComparison]::Ordinal) -lt 0) {
    throw "Runtime-audit check failed: same-ID/different-point directed arrival passed"
}

$validKick = 'referenceId=1, direction=toward_reference, windUp=0.65, ' +
    'launchPosition=(1.000, 2.000, 3.000), launchDirection=(0.000, 0.000, 1.000), ' +
    'headPitch=0.10, stockPitch=5.00, stockMaxForce=10.00, ' +
    'destination=human_gaze, destinationPoint=(1.000, 8.000, 8.000), chargedFor=0.50.'
$originalCulture = [System.Globalization.CultureInfo]::CurrentCulture
try {
    [System.Globalization.CultureInfo]::CurrentCulture =
        [System.Globalization.CultureInfo]::GetCultureInfo('de-DE')
    $validKickViolation = Get-TowardReferenceKickViolation -Details $validKick
}
finally {
    [System.Globalization.CultureInfo]::CurrentCulture = $originalCulture
}
if ($null -ne $validKickViolation) {
    throw "Runtime-audit check failed: valid invariant kick telemetry was rejected under a comma-decimal locale: $validKickViolation"
}
$reversedKick = $validKick.Replace(
    'launchDirection=(0.000, 0.000, 1.000)',
    'launchDirection=(0.000, 0.000, -1.000)')
$reversedKickViolation = Get-TowardReferenceKickViolation -Details $reversedKick
if ($null -eq $reversedKickViolation -or
    $reversedKickViolation.IndexOf('did not point', [System.StringComparison]::Ordinal) -lt 0) {
    throw "Runtime-audit check failed: a reversed goal kick passed destination alignment"
}
$elevatedYawKick = $validKick.Replace(
    'destinationPoint=(1.000, 8.000, 8.000)',
    'destinationPoint=(1.000, 80.000, 8.000)')
if ($null -ne (Get-TowardReferenceKickViolation -Details $elevatedYawKick)) {
    throw "Runtime-audit check failed: destination height incorrectly contaminated the horizontal yaw check"
}
$pitchedLaunch = $validKick.Replace(
    'launchDirection=(0.000, 0.000, 1.000)',
    'launchDirection=(0.000, 0.100, 1.000)')
if ($null -eq (Get-TowardReferenceKickViolation -Details $pitchedLaunch)) {
    throw "Runtime-audit check failed: destination elevation in launchDirection escaped detection"
}

$kickTarget = Get-TargetResolutionEvidence -Line (
    '[ENTITY] TARGET_RESOLVED action=kick_item, target=prop:one, ' +
    'referenceId=1, netId=11, direction=toward_reference, destination=human_gaze, ' +
    'callId=call_kick, turnId=8.')
$kickResolutions = @{}
[void](Add-TargetResolutionEvidence `
    -ResolvedIdentities $kickResolutions `
    -Evidence $kickTarget)
$kickLaunch = Get-KickLaunchEvidence -Line (
    '[ACTION] KICK_LAUNCH_REQUESTED ' +
    $validKick.TrimEnd('.') +
    ', callId=call_kick, turnId=8.')
$kickLaunchesByCall = @{}
Add-KickLaunchEvidence `
    -LaunchesByCall $kickLaunchesByCall `
    -Evidence $kickLaunch
if ($null -ne (Get-KickLaunchCallViolation `
        -ResolvedIdentities $kickResolutions `
        -LaunchesByCall $kickLaunchesByCall `
        -CallId 'call_kick' `
        -TurnId 8 `
        -Direction 'toward_reference')) {
    throw "Runtime-audit check failed: a call-scoped exact goal kick was rejected"
}
$missingKickLaunchViolation = Get-KickLaunchCallViolation `
    -ResolvedIdentities $kickResolutions `
    -LaunchesByCall @{} `
    -CallId 'call_kick' `
    -TurnId 8 `
    -Direction 'toward_reference'
if ($null -eq $missingKickLaunchViolation -or
    $missingKickLaunchViolation.IndexOf(
        'without a call-scoped launch',
        [System.StringComparison]::Ordinal) -lt 0) {
    throw "Runtime-audit check failed: a successful goal kick without its own launch passed"
}
$wrongKickLaunch = Get-KickLaunchEvidence -Line (
    '[ACTION] KICK_LAUNCH_REQUESTED ' +
    $validKick.Replace('referenceId=1', 'referenceId=2').TrimEnd('.') +
    ', callId=call_wrong_target, turnId=8.')
$wrongTargetLaunches = @{}
Add-KickLaunchEvidence `
    -LaunchesByCall $wrongTargetLaunches `
    -Evidence $wrongKickLaunch
$wrongTargetResolutions = @{}
$wrongTargetResolution = Get-TargetResolutionEvidence -Line (
    '[ENTITY] TARGET_RESOLVED action=kick_item, target=prop:one, ' +
    'referenceId=1, netId=11, direction=toward_reference, destination=human_gaze, ' +
    'callId=call_wrong_target, turnId=8.')
[void](Add-TargetResolutionEvidence `
    -ResolvedIdentities $wrongTargetResolutions `
    -Evidence $wrongTargetResolution)
$wrongKickTargetViolation = Get-KickLaunchCallViolation `
    -ResolvedIdentities $wrongTargetResolutions `
    -LaunchesByCall $wrongTargetLaunches `
    -CallId 'call_wrong_target' `
    -TurnId 8 `
    -Direction 'toward_reference'
if ($null -eq $wrongKickTargetViolation -or
    $wrongKickTargetViolation.IndexOf(
        'different kick target',
        [System.StringComparison]::Ordinal) -lt 0) {
    throw "Runtime-audit check failed: a call-scoped launch of the wrong target passed"
}
$anonymousKickLaunch = Get-KickLaunchEvidence -Line (
    '[ACTION] KICK_LAUNCH_REQUESTED ' + $validKick)
if ($null -ne $anonymousKickLaunch) {
    throw "Runtime-audit check failed: anonymous kick launch evidence was accepted"
}
$stoppedMarkerIndex = $client.IndexOf('_logs.Enqueue("CONNECTION_STOPPED")', [System.StringComparison]::Ordinal)
$stoppedPublishIndex = $client.IndexOf('_stopped = true;', $stoppedMarkerIndex, [System.StringComparison]::Ordinal)
if ($stoppedPublishIndex -lt $stoppedMarkerIndex) {
    throw "Runtime-audit check failed: terminal evidence must queue before stopped-client publication"
}
if ($client.IndexOf('if (!_initialSessionConfigured)', [System.StringComparison]::Ordinal) -lt 0 -or
    $client.IndexOf('_logs.Enqueue("SESSION_UPDATED")', [System.StringComparison]::Ordinal) -lt 0) {
    throw "Runtime-audit check failed: later mode updates must not masquerade as a new connection READY"
}
$drainIndex = $bridge.IndexOf('DrainClientEvents();', [System.StringComparison]::Ordinal)
$ensureIndex = $bridge.IndexOf('EnsureClient();', [System.StringComparison]::Ordinal)
if ($drainIndex -lt 0 -or $ensureIndex -lt 0 -or $drainIndex -gt $ensureIndex) {
    throw "Runtime-audit check failed: stopped-client logs must drain before client replacement"
}

$probePath = Join-Path ([System.IO.Path]::GetTempPath()) (
    "RamblersSharedLog-" + [Guid]::NewGuid().ToString("N") + ".log")
$writerStream = $null
$writer = $null
try {
    $share = [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete
    $writerStream = New-Object System.IO.FileStream(
        $probePath,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::ReadWrite,
        $share)
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    $writer = New-Object System.IO.StreamWriter($writerStream, $utf8, 1024, $true)
    $writer.Write("only-line")
    $writer.Flush()
    $single = @(Read-SharedLogLines $probePath)
    if ($single.Count -ne 1 -or $single[0] -ne "only-line") {
        throw "Runtime-audit check failed: shared reader lost one-line array semantics"
    }

    $writer.Dispose()
    $writer = $null
    $writerStream.SetLength(0)
    $writerStream.Position = 0
    $writer = New-Object System.IO.StreamWriter($writerStream, $utf8, 1024, $true)
    $writer.Write("first`r`nsecond")
    $writer.Flush()
    $multiple = @(Read-SharedLogLines $probePath)
    if ($multiple.Count -ne 2 -or $multiple[0] -ne "first" -or
        $multiple[1] -ne "second") {
        throw "Runtime-audit check failed: shared reader did not preserve live multiline content"
    }
}
finally {
    if ($null -ne $writer) {
        $writer.Dispose()
    }
    if ($null -ne $writerStream) {
        $writerStream.Dispose()
    }
    if (Test-Path -LiteralPath $probePath) {
        Remove-Item -LiteralPath $probePath -Force
    }
}

Write-Host "Runtime-audit protocol checks passed."
Write-Host "  Proven: the local gate separates evidence layers and detects deployment, identity, lifecycle, stale-blocker, and unfinished-job failures."
Write-Host "  Not proven: live log coverage until the new DLL records a spoken/action turn."
