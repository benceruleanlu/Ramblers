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
        throw "Traversal-follow protocol check failed: $Description"
    }
}

function Assert-NotContains {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Needle,
        [Parameter(Mandatory = $true)][string]$Description
    )
    if ($Text.IndexOf($Needle, [System.StringComparison]::Ordinal) -ge 0) {
        throw "Traversal-follow protocol check failed: $Description"
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
        throw "Traversal-follow protocol check failed: $Description"
    }
}

$trail = Read-Source "src\BreadcrumbTrail.cs"
$follow = Read-Source "src\CompanionFollowBehavior.cs"
$locomotion = Read-Source "src\CompanionLocomotion.cs"
$jump = Read-Source "src\CompanionJumpActuator.cs"
$actions = Read-Source "src\CompanionActions.cs"

Assert-Contains $trail 'internal bool RequiresJump { get; }' `
    "breadcrumbs must retain a recorded human jump transition"
Assert-Contains $trail 'internal bool RequiresDrop { get; }' `
    "breadcrumbs must retain a recorded human ledge departure"
Assert-Contains $trail 'internal Vector3 TravelDirection { get; }' `
    "traversal breadcrumbs must retain the human route tangent"
Assert-Contains $trail 'var verticallyNear = Mathf.Abs(from.y - point.Position.y) <= verticalTolerance;' `
    "breadcrumb arrival must reject a different vertical level"
Assert-Contains $trail 'HasCrossedPointPlane(from, point, passLateralTolerance)' `
    "a body that crosses a nearby waypoint plane must advance instead of orbiting it"
Assert-Contains $trail '_count > 1' `
    "plane crossing must never discard the final live target"
Assert-Contains $trail 'Vector3.Distance(previous, current)' `
    "route length must preserve vertical travel"
Assert-Contains $trail 'point.Sequence != committedJumpSequence' `
    "an uncommitted jump marker must not be pruned as horizontally reached"
Assert-Contains $trail 'point.Sequence != committedDropSequence' `
    "an uncommitted drop marker must not be pruned as horizontally reached"

Assert-Order $follow 'if (UpdateCarryState(now))' `
    'ObserveHumanTraversal();' `
    "carry state must pause route recording before a carried body can append stale points"
Assert-Contains $follow 'human.hands.heldCharacter' `
    "follow must detect the stock player-carry relationship"
Assert-Contains $follow '"[FOLLOW] CARRY_STARTED "' `
    "pickup must visibly invalidate the old route"
Assert-Contains $follow '"[FOLLOW] CARRY_RELEASED "' `
    "release must visibly rebase follow at the new location"
Assert-Contains $follow 'if (_humanJumpInProgress)' `
    "follow must collapse an airborne human route until its landing outcome is known"
Assert-Contains $follow 'landingRise >= MeaningfulJumpLandingRise' `
    "only a materially higher landing may become a recorded jump instruction"
Assert-Contains $follow '"[FOLLOW] TRAIL_JUMP_IGNORED reason=same_level_landing "' `
    "same-level recreational jumps must be explicitly rejected as route instructions"
Assert-NotContains $follow '"[FOLLOW] TRAIL_JUMP "' `
    "raw jump input must never be copied directly into the route"
Assert-Contains $follow 'else if (_humanWasGrounded && !grounded)' `
    "walking off a ledge must create an explicit drop marker"
Assert-Contains $follow 'routeVerticalDistance <= HoldingVerticalTolerance' `
    "stacked floors must never satisfy the holding distance by X/Z alone"
Assert-Contains $follow '"[FOLLOW] TRAVERSAL_LOOKAHEAD "' `
    "an upcoming traversal marker must not wait for exact arrival at the edge"
Assert-Contains $follow '"[FOLLOW] ROUTE_ADVANCE reason=passed_plane "' `
    "waypoint overshoot must be visible in runtime evidence"
Assert-Contains $follow 'return breadcrumb.TravelDirection;' `
    "jump and drop commitment must follow the human's recorded route tangent"
Assert-Contains $follow '(!traversalCommitted || now < _directTraversalUntil)' `
    "an expired traversal commitment must steer back to its point instead of running forever"
Assert-Contains $follow '_jump.TryRequestTraversal(' `
    "recorded route traversal must queue a deterministic grounded jump"
Assert-Contains $follow '"[FOLLOW] DROP_COMMIT "' `
    "a descending breadcrumb must commit forward movement at a ledge"
Assert-Contains $follow '"stuck_recovery"' `
    "stuck observation must feed a bounded traversal recovery"
Assert-Contains $locomotion 'CommitTraversalDirection(' `
    "a committed jump or drop must retain forward intent"
Assert-Contains $locomotion 'ground.GetSlopedMoveForce(direction, out steepScalar)' `
    "steering must consult the same stock slope solver used by the player motor"
Assert-Contains $locomotion 'hitDescription = DescribeHit(hit);' `
    "blocked-path evidence must identify the collider reported by the rigidbody sweep"
Assert-Contains $locomotion 'hit.normal.y >= WalkableSweepNormalY' `
    "upward floor-seam contacts must not masquerade as walls"
Assert-Contains $locomotion 'rigidbody.SweepTest(' `
    "clearance must stay on the runtime-proven single-sweep path"
Assert-NotContains $locomotion 'SweepTestAll(' `
    "the stripped multi-hit sweep must never disable navigation at runtime"
Assert-Contains $follow '$"probes={_locomotion.LastProbeSummary}, "' `
    "status evidence must include every evaluated steering candidate"
Assert-Contains $locomotion 'Vector3.Distance(_progressAnchor, _body.Position)' `
    "jumping and falling must count as spatial progress"

Assert-Contains $jump 'jumper.jumpInQueue = true;' `
    "automatic traversal must use the verified stock queued-jump path"
Assert-Contains $jump 'jumper.LocalFixedUpdate(ref velocity);' `
    "the server-owned bot must execute the stock jump calculation"
Assert-Order $actions '_follow.TickFixed(now, MovementAllowed, MovementBlocker);' `
    '_jump.TickFixed(now, _posture.Current);' `
    "follow must queue traversal before the jump actuator runs that physics tick"

Assert-NotContains $follow 'Teleport(' `
    "follow recovery must never teleport"
Assert-NotContains $follow 'transform.position =' `
    "follow must never warp either player"
Assert-NotContains $locomotion 'AddForce' `
    "locomotion must continue through the stock remote-player motor"

Write-Host "Traversal-follow protocol checks passed."
Write-Host "  Proven: carry rebasing, 3D jump/drop route retention, traversal lookahead/tangents, slope-aware steering, bounded stuck jump, stock motor/jump paths, no teleport."
Write-Host "  Not proven by this static check: live route quality, visible jump timing, ledge choice, or eventual arrival."
