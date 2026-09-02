#requires -Version 5.1

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "..\scripts\Read-SharedLogLines.ps1")
. (Join-Path $PSScriptRoot "..\scripts\FollowRuntimeInvariants.ps1")
. (Join-Path $PSScriptRoot "..\scripts\PhysicalActionRuntimeInvariants.ps1")
. (Join-Path $PSScriptRoot "..\scripts\ToolBatchSettlementRuntimeInvariants.ps1")

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

Write-Host "Runtime-audit invariant checks passed."
