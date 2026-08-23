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
        throw "Interaction-reference check failed: $Description"
    }
}

function Assert-NotContains {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Needle,
        [Parameter(Mandatory = $true)][string]$Description
    )
    if ($Text.IndexOf($Needle, [System.StringComparison]::Ordinal) -ge 0) {
        throw "Interaction-reference check failed: $Description"
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
        throw "Interaction-reference check failed: $Description"
    }
}

$catalog = Read-Source "src\AgentToolCatalog.cs"
$router = Read-Source "src\AgentToolRouter.cs"
$awareness = Read-Source "src\CompanionAwareness.cs"
$entities = Read-Source "src\CompanionEntityReferences.cs"
$reference = Read-Source "src\CompanionInteractionReference.cs"
$discovery = Read-Source "src\CompanionInteractableDiscovery.cs"
$protocol = Read-Source "src\CompanionInteractionReferenceProtocol.cs"
$affordanceTarget = Read-Source "src\CompanionAffordanceTarget.cs"
$affordanceProtocol = Read-Source "src\CompanionAffordanceProtocol.cs"
$affordanceProbe = Read-Source "analysis_scripts\CompanionAffordanceProtocolProbe.cs"

$exactResolverStart = $affordanceTarget.IndexOf(
    'internal bool TrySelectExactWorldReference(',
    [System.StringComparison]::Ordinal)
$exactResolverEnd = $affordanceTarget.IndexOf(
    'internal bool TryWithCompanionHeldProp(',
    $exactResolverStart,
    [System.StringComparison]::Ordinal)
if ($exactResolverStart -lt 0 -or $exactResolverEnd -le $exactResolverStart) {
    throw "Interaction-reference check failed: exact context resolver method was not found"
}
$exactResolver = $affordanceTarget.Substring(
    $exactResolverStart,
    $exactResolverEnd - $exactResolverStart)

$directSelectionStart = $affordanceTarget.IndexOf(
    'internal bool TrySelect(',
    [System.StringComparison]::Ordinal)
$directSelectionEnd = $affordanceTarget.IndexOf(
    'internal bool TrySelectExactWorldReference(',
    $directSelectionStart,
    [System.StringComparison]::Ordinal)
if ($directSelectionStart -lt 0 -or
    $directSelectionEnd -le $directSelectionStart) {
    throw "Interaction-reference check failed: direct human resolver method was not found"
}
$directSelection = $affordanceTarget.Substring(
    $directSelectionStart,
    $directSelectionEnd - $directSelectionStart)

$recentCaptureStart = $discovery.IndexOf(
    'private CompanionInteractableObservation[] CaptureRecent(',
    [System.StringComparison]::Ordinal)
$recentCaptureEnd = $discovery.IndexOf(
    'private void Remember(',
    $recentCaptureStart,
    [System.StringComparison]::Ordinal)
if ($recentCaptureStart -lt 0 -or $recentCaptureEnd -le $recentCaptureStart) {
    throw "Interaction-reference check failed: recent interaction method was not found"
}
$recentCapture = $discovery.Substring(
    $recentCaptureStart,
    $recentCaptureEnd - $recentCaptureStart)

Assert-Contains $catalog 'nearby_interactables or recently_seen_interactables' `
    "interaction should advertise exact context IDs"
Assert-Contains $catalog 'interaction:net:<network-id>:castable:<instance-id>' `
    "the target schema should accept stable context IDs"
Assert-Contains $catalog 'ask a natural clarification instead of choosing by distance' `
    "semantic ambiguity should be resolved conversationally, never by proximity"
Assert-NotContains $catalog '@enum = new[] { "human_reference", "companion_held_item" }' `
    "the schema must not reject model-selected exact context IDs"
Assert-Contains $router 'TryResolveInteraction(' `
    "routing should resolve a model-selected context ID"
Assert-Contains $router 'turnReference.AffordanceCandidates,' `
    "context IDs must resolve through the same turn-frozen actor snapshot"
Assert-Contains $router 'turnReference.AffordanceCandidates.TrySelect(' `
    "human and held aliases should retain turn-frozen selection"
Assert-Contains $affordanceTarget 'internal bool HumanReferenceAvailable => _humanReference != null;' `
    "human-reference availability must describe the frozen typed affordance"
Assert-Order $directSelection 'IsExactWorldReference(' 'target = _humanReference;' `
    "direct human-reference selection must validate identity before returning its frozen driver"
Assert-NotContains $directSelection 'TryCaptureWorldOutcome(' `
    "direct human-reference selection must not recapture mutable prerequisite state"
Assert-Contains $affordanceTarget 'private void RebuildHumanReferenceAfterStateTransition(' `
    "verified hands transitions should own explicit human-reference rebuilding"
$humanRebuildOccurrences = [regex]::Matches(
    $affordanceTarget,
    'RebuildHumanReferenceAfterStateTransition\(').Count
if ($humanRebuildOccurrences -ne 3) {
    throw "Interaction-reference check failed: both hands-transition paths must rebuild or invalidate the human reference"
}
Assert-Contains $entities '_interactions.TryGetValue(stableId, out reference)' `
    "only an ID present in the same turn map may resolve"
Assert-Contains $entities 'reference.TryResolve(candidates, out target, out error)' `
    "context routing must revalidate its frozen identity"

Assert-Contains $awareness 'nearby_interactables = nearbyInteractables' `
    "nearby exact interactions should enter private context"
Assert-Contains $awareness 'recently_seen_interactables = rememberedInteractables' `
    "recent exact interactions should survive a bounded follow-up window"
Assert-Contains $awareness 'capability_boundary =' `
    "the limited registry coverage should be explicit"
Assert-Contains $discovery 'internal const int MaximumNearbyInteractables = 6;' `
    "nearby interaction output must remain compact"
Assert-Contains $discovery 'internal const int MaximumRecentInteractables = 4;' `
    "recent interaction output must remain compact"
Assert-Contains $discovery 'internal const int MaximumRememberedInteractables = 24;' `
    "remembered interaction storage must have a hard cap"
Assert-Contains $discovery 'MaximumSpawnedRootsInspected = 256;' `
    "spawned-root traversal must have a hard work bound"
Assert-Contains $discovery 'MaximumSpawnedHierarchyNodes = 1024;' `
    "spawned hierarchy traversal must have a hard work bound"
Assert-Contains $discovery 'var spawned = NetworkServer.spawned;' `
    "networked controls should come from Mirror's maintained spawned registry"
Assert-Contains $discovery 'var homes = PropHome.allPropHomes;' `
    "local placement homes should use their game-owned lifecycle registry"
Assert-Contains $discovery 'var props = Prop.allProps;' `
    "prop-attached interactions should use the exercised prop registry"
Assert-Contains $discovery 'affordanceCandidates.TryGetExactHumanWorldReference(' `
    "recent observation should reuse the already-frozen utterance target"
Assert-Contains $affordanceTarget 'internal bool CanResolveContextReferences(CompanionBody body)' `
    "context publication must have an exact actor-resolver validity check"
Assert-Contains $discovery '!affordanceCandidates.CanResolveContextReferences(body)' `
    "context IDs must not be advertised without a valid turn resolver"
Assert-Order $discovery '!affordanceCandidates.CanResolveContextReferences(body)' `
    'Il2CppType.Of<CastableTarget>()' `
    "resolver admission must precede registry enumeration"
Assert-NotContains $discovery 'TryCaptureHumanReferenceIdentity(' `
    "discovery must not recast human gaze after turn capture"
Assert-Contains $discovery 'transform.GetComponent(castableType)' `
    "hierarchies should use non-generic component lookup"
Assert-Contains $discovery 'component.TryCast<CastableTarget>()' `
    "non-generic lookup should retain exact typed identity"
Assert-NotContains $discovery 'GetComponentsInChildren<CastableTarget>' `
    "discovery should not introduce an unprobed IL2CPP generic instantiation"
Assert-NotContains $discovery 'Resources.' `
    "discovery must not scan Unity's global resource table"
Assert-NotContains $discovery 'FindObjects' `
    "discovery must not search the whole scene"

Assert-Contains $reference 'CompanionAffordanceSource.ContextEntity' `
    "context interactions should retain a distinct source"
Assert-Contains $reference 'candidates.TrySelectExactWorldReference(' `
    "execution should rematerialize through the turn-bound actor context"
Assert-Contains $reference 'IsExactCastableIdentity()' `
    "selection must revalidate the exact CastableTarget"
Assert-Contains $reference 'currentIdentity == _networkIdentity' `
    "networked references must retain the same identity component"
Assert-Contains $reference 'var outcomes = castableTarget == null ? null : castableTarget.outcomes;' `
    "reference capture should freeze prerequisite-free raw structural metadata"
Assert-Contains $reference 'CompanionInteractionReferenceProtocol.MergeStructuralKind(' `
    "reference capture should preserve all supported structural kinds"
Assert-NotContains $reference 'CompanionAffordanceTarget.TryCaptureWorldOutcome(' `
    "reference capture must not freeze an actor-dependent typed outcome"
Assert-NotContains $reference 'CompanionAffordanceTarget.TryCaptureStructuralWorldOutcome(' `
    "reference capture must not construct a typed driver"
Assert-NotContains $reference 'CompanionBody body' `
    "raw reference capture must not require actor state"
Assert-Contains $affordanceTarget 'TryCaptureStructuralWorldOutcome(' `
    "context capture must retain one exact structural target when dynamic outcome is unavailable"
Assert-Contains $exactResolver 'IsExactWorldReference(' `
    "context-ID routing must revalidate the raw exact identity"
Assert-Order $exactResolver 'CompanionAffordanceTarget.TryCaptureDynamicWorldOutcome(' `
    'CompanionAffordanceTarget.TryCaptureStructuralWorldOutcome(' `
    "exact context rematerialization must prefer current dynamic admission before structural fallback"
Assert-NotContains $exactResolver 'nearest' `
    "exact context rematerialization must never nearest-retarget"
Assert-Contains $affordanceProtocol 'internal static CompanionAffordanceReadinessState ClassifyWorldReadiness(' `
    "far structural targets need an executable readiness policy"
Assert-Contains $affordanceProbe 'a structurally captured far target approaches before dynamic outcome admission' `
    "the executable probe must cover unavailable dynamic outcome before approach"
Assert-Contains $reference 'MaximumNameHierarchyDepth = 5;' `
    "natural naming must inspect only a bounded local hierarchy"
Assert-Contains $reference 'NaturalNameFromHierarchy(' `
    "generic component names should fall back to meaningful parents"
Assert-Contains $protocol 'internal const int MaximumNaturalNameLength = 56;' `
    "model-facing names must have a fixed maximum length"
Assert-Contains $protocol 'return string.IsNullOrEmpty(naturalName)' `
    "sanitized-empty names must receive a nonempty fallback"
Assert-Contains $discovery 'private void TrimRememberedToLimit()' `
    "remembered storage must enforce its hard cap"
Assert-Contains $discovery 'CompareRememberedForEviction(' `
    "oldest eviction must use the deterministic pure ordering policy"
Assert-Order $recentCapture 'TrimRememberedToLimit();' 'foreach (var pair in _remembered)' `
    "recent iteration must enforce the storage cap before visiting memories"
Assert-NotContains $reference 'nearest' `
    "exact reference resolution must never nearest-retarget"
Assert-NotContains $reference 'FindObjects' `
    "exact reference resolution must never reacquire another object"

$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$probeRoot = Join-Path $tempRoot (
    "RamblersInteractionReferenceProbe-" + [Guid]::NewGuid().ToString("N"))
$probeRoot = [System.IO.Path]::GetFullPath($probeRoot)
if (-not $probeRoot.StartsWith(
        $tempRoot,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Interaction reference probe path escaped the temporary directory."
}

New-Item -ItemType Directory -Path $probeRoot | Out-Null
try {
    $probeOutput = Join-Path $probeRoot "CompanionInteractionReferenceProtocolProbe.exe"
    & $compiler `
        /nologo `
        /target:exe `
        /langversion:latest `
        /optimize+ `
        "/out:$probeOutput" `
        (Join-Path $ramblersRoot "src\CompanionInteractionReferenceProtocol.cs") `
        (Join-Path $PSScriptRoot "CompanionInteractionReferenceProtocolProbe.cs")
    if ($LASTEXITCODE -ne 0) {
        throw "Interaction reference probe compilation failed with exit code $LASTEXITCODE."
    }
    & $probeOutput
    if ($LASTEXITCODE -ne 0) {
        throw "Interaction reference probe failed with exit code $LASTEXITCODE."
    }

    $affordanceProbeOutput = Join-Path $probeRoot "CompanionAffordanceProtocolProbe.exe"
    & $compiler `
        /nologo `
        /target:exe `
        /langversion:latest `
        /optimize+ `
        "/out:$affordanceProbeOutput" `
        (Join-Path $ramblersRoot "src\CompanionAffordanceProtocol.cs") `
        (Join-Path $PSScriptRoot "CompanionAffordanceProtocolProbe.cs")
    if ($LASTEXITCODE -ne 0) {
        throw "Affordance readiness probe compilation failed with exit code $LASTEXITCODE."
    }
    & $affordanceProbeOutput
    if ($LASTEXITCODE -ne 0) {
        throw "Affordance readiness probe failed with exit code $LASTEXITCODE."
    }
}
finally {
    if (Test-Path -LiteralPath $probeRoot) {
        Remove-Item -LiteralPath $probeRoot -Recurse -Force
    }
}

Write-Host "Interaction-reference protocol checks passed."
Write-Host "  Proven: frozen exact identities, prerequisite-free structural capture, bounded nonempty names, resolver-gated bounded memory, frozen direct aliases, dynamic-first context binding, and no global or nearest retargeting."
Write-Host "  Not proven: live registry coverage, model target choice, or native interaction effects."
