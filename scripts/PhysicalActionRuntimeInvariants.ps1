#requires -Version 5.1

Set-StrictMode -Version Latest

function Get-TargetResolutionEvidence {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Line)

    if ($Line -notmatch '\[ENTITY\] TARGET_RESOLVED action=(?<action>[^,]+), .*?referenceId=(?<reference>[^,]+), .*?callId=(?<call>[^,]+), turnId=(?<turn>\d+)\.') {
        return $null
    }

    $turnId = [long]$Matches["turn"]
    $callId = $Matches["call"]
    $action = $Matches["action"]
    $referenceId = $Matches["reference"]
    $directionMatch = [regex]::Match(
        $Line,
        '(?:^|, )direction=(?<direction>[^,]+)')
    $destinationMatch = [regex]::Match(
        $Line,
        '(?:^|, )destinationPoint=(?<destination>\([^)]+\))')

    return [pscustomobject]@{
        TurnId = $turnId
        CallId = $callId
        Action = $action
        ReferenceId = $referenceId
        Direction = if ($directionMatch.Success) {
            $directionMatch.Groups["direction"].Value
        }
        else {
            $null
        }
        DestinationPoint = if ($destinationMatch.Success) {
            $destinationMatch.Groups["destination"].Value
        }
        else {
            $null
        }
    }
}

function Get-KickLaunchEvidence {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Line)

    if ($Line -notmatch '\[ACTION\] KICK_LAUNCH_REQUESTED (?<details>.*), callId=(?<call>[^,]+), turnId=(?<turn>\d+)\.$') {
        return $null
    }

    $details = $Matches["details"]
    $callId = $Matches["call"]
    $turnId = [long]$Matches["turn"]
    $referenceMatch = [regex]::Match(
        $details,
        '(?:^|, )referenceId=(?<reference>[^,]+)')
    $directionMatch = [regex]::Match(
        $details,
        '(?:^|, )direction=(?<direction>[^,]+)')
    if (-not $referenceMatch.Success -or -not $directionMatch.Success) {
        return $null
    }

    return [pscustomobject]@{
        TurnId = $turnId
        CallId = $callId
        ReferenceId = $referenceMatch.Groups["reference"].Value
        Direction = $directionMatch.Groups["direction"].Value
        Details = $details
    }
}

function Add-KickLaunchEvidence {
    param(
        [Parameter(Mandatory = $true)][hashtable]$LaunchesByCall,
        [Parameter(Mandatory = $true)]$Evidence
    )

    if (-not $LaunchesByCall.ContainsKey($Evidence.CallId)) {
        $LaunchesByCall[$Evidence.CallId] =
            New-Object System.Collections.ArrayList
    }
    [void]$LaunchesByCall[$Evidence.CallId].Add($Evidence)
}

function Get-KickLaunchCallViolation {
    param(
        [Parameter(Mandatory = $true)][hashtable]$ResolvedIdentities,
        [Parameter(Mandatory = $true)][hashtable]$LaunchesByCall,
        [Parameter(Mandatory = $true)][string]$CallId,
        [Parameter(Mandatory = $true)][long]$TurnId,
        [Parameter(Mandatory = $true)][string]$Direction
    )

    $identityKey = $CallId + "|kick_item"
    if (-not $ResolvedIdentities.ContainsKey($identityKey)) {
        return "Call $CallId succeeded as kick_item without its exact resolved target."
    }
    $resolved = $ResolvedIdentities[$identityKey]
    if ($resolved.Count -ne 1) {
        return "Call $CallId did not retain one immutable kick target."
    }
    if (-not $LaunchesByCall.ContainsKey($CallId)) {
        return "Call $CallId succeeded as kick_item without a call-scoped launch."
    }

    $launches = @($LaunchesByCall[$CallId])
    if ($launches.Count -ne 1) {
        return "Call $CallId emitted $($launches.Count) launches instead of exactly one."
    }
    $launch = $launches[0]
    $resolvedReference = @($resolved)[0]
    if ($launch.ReferenceId -ne $resolvedReference) {
        return "Call $CallId launched a different kick target than the one resolved."
    }
    if ($launch.TurnId -ne $TurnId) {
        return "Call $CallId attached its kick launch to a different turn."
    }
    if ($launch.Direction -ne $Direction) {
        return "Call $CallId launched in a different direction than the one resolved."
    }

    if ($Direction -eq "toward_reference") {
        $geometryViolation = Get-TowardReferenceKickViolation `
            -Details $launch.Details
        if ($null -ne $geometryViolation) {
            return "Call ${CallId}: $geometryViolation"
        }
    }
    return $null
}

function Add-TargetResolutionEvidence {
    param(
        [Parameter(Mandatory = $true)][hashtable]$ResolvedIdentities,
        [Parameter(Mandatory = $true)]$Evidence
    )

    $identityKey = $Evidence.CallId + "|" + $Evidence.Action
    if (-not $ResolvedIdentities.ContainsKey($identityKey)) {
        $ResolvedIdentities[$identityKey] =
            New-Object System.Collections.Generic.HashSet[string]
    }
    [void]$ResolvedIdentities[$identityKey].Add($Evidence.ReferenceId)
    return $identityKey
}

function Get-DirectedMoveArrivalEvidence {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Line)

    if ($Line -notmatch '\[ACTION\] GO_TO_LOCATION_ARRIVED referenceId=(?<reference>[^,]+), destinationPoint=(?<destination>\([^)]+\)), (?<details>.*), callId=(?<call>[^,]+), turnId=(?<turn>\d+)\.') {
        return $null
    }

    return [pscustomobject]@{
        TurnId = [long]$Matches["turn"]
        CallId = $Matches["call"]
        ReferenceId = $Matches["reference"]
        DestinationPoint = $Matches["destination"]
        Details = $Matches["details"]
    }
}

function Add-DirectedMoveArrivalEvidence {
    param(
        [Parameter(Mandatory = $true)][hashtable]$ArrivalsByCall,
        [Parameter(Mandatory = $true)]$Evidence
    )

    if (-not $ArrivalsByCall.ContainsKey($Evidence.CallId)) {
        $ArrivalsByCall[$Evidence.CallId] =
            New-Object System.Collections.ArrayList
    }
    [void]$ArrivalsByCall[$Evidence.CallId].Add($Evidence)
}

function Add-DirectedMoveResolutionEvidence {
    param(
        [Parameter(Mandatory = $true)][hashtable]$ResolutionsByCall,
        [Parameter(Mandatory = $true)]$Evidence
    )

    if (-not $ResolutionsByCall.ContainsKey($Evidence.CallId)) {
        $ResolutionsByCall[$Evidence.CallId] =
            New-Object System.Collections.ArrayList
    }
    [void]$ResolutionsByCall[$Evidence.CallId].Add($Evidence)
}

function Get-DirectedMoveCallViolation {
    param(
        [Parameter(Mandatory = $true)][hashtable]$ResolvedIdentities,
        [Parameter(Mandatory = $true)][hashtable]$ResolutionsByCall,
        [Parameter(Mandatory = $true)][hashtable]$ArrivalsByCall,
        [Parameter(Mandatory = $true)][string]$CallId,
        [Parameter(Mandatory = $true)][long]$TurnId
    )

    $identityKey = $CallId + "|go_to_location"
    if (-not $ResolvedIdentities.ContainsKey($identityKey) -or
        $ResolvedIdentities[$identityKey].Count -ne 1) {
        return "Call $CallId did not retain one immutable directed-move destination."
    }
    if (-not $ArrivalsByCall.ContainsKey($CallId)) {
        return "Call $CallId succeeded as go_to_location without a call-scoped arrival."
    }
    if (-not $ResolutionsByCall.ContainsKey($CallId)) {
        return "Call $CallId succeeded as go_to_location without a call-scoped destination point."
    }

    $arrivals = @($ArrivalsByCall[$CallId])
    $resolutions = @($ResolutionsByCall[$CallId])
    if ($arrivals.Count -ne 1) {
        return "Call $CallId emitted $($arrivals.Count) arrivals instead of exactly one."
    }
    if ($resolutions.Count -ne 1) {
        return "Call $CallId emitted $($resolutions.Count) destination resolutions instead of exactly one."
    }
    $arrival = $arrivals[0]
    $resolution = $resolutions[0]
    $resolvedReference = @($ResolvedIdentities[$identityKey])[0]
    if ($resolution.ReferenceId -ne $resolvedReference -or
        $arrival.ReferenceId -ne $resolvedReference) {
        return "Call $CallId arrived at a different destination than the one resolved."
    }
    if ($resolution.TurnId -ne $TurnId -or
        $arrival.TurnId -ne $TurnId) {
        return "Call $CallId attached its arrival to a different turn."
    }
    if ([string]::IsNullOrWhiteSpace($resolution.DestinationPoint) -or
        [string]::IsNullOrWhiteSpace($arrival.DestinationPoint)) {
        return "Call $CallId omitted its frozen destination point."
    }
    if ($resolution.DestinationPoint -ne $arrival.DestinationPoint) {
        return "Call $CallId arrived at coordinates different from its frozen destination point."
    }
    return $null
}

function Get-InteractionConfirmationEvidence {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Line)

    if ($Line -notmatch '\[INTERACT\] CONFIRMED kind=(?<kind>[^,]+), referenceId=(?<reference>[^,]+), (?<details>.*), callId=(?<call>[^,]+), turnId=(?<turn>\d+)\.') {
        return $null
    }

    return [pscustomobject]@{
        TurnId = [long]$Matches["turn"]
        CallId = $Matches["call"]
        Kind = $Matches["kind"]
        ReferenceId = $Matches["reference"]
        Details = $Matches["details"]
    }
}

function Add-InteractionConfirmationEvidence {
    param(
        [Parameter(Mandatory = $true)][hashtable]$ConfirmedIdentities,
        [Parameter(Mandatory = $true)]$Evidence
    )

    if (-not $ConfirmedIdentities.ContainsKey($Evidence.CallId)) {
        $ConfirmedIdentities[$Evidence.CallId] =
            New-Object System.Collections.Generic.HashSet[string]
    }
    [void]$ConfirmedIdentities[$Evidence.CallId].Add($Evidence.ReferenceId)
}

function Get-InteractionTargetViolation {
    param(
        [Parameter(Mandatory = $true)][hashtable]$ResolvedIdentities,
        [Parameter(Mandatory = $true)][hashtable]$ConfirmedIdentities,
        [Parameter(Mandatory = $true)][string]$CallId
    )

    if (-not $ConfirmedIdentities.ContainsKey($CallId)) {
        return "Call $CallId succeeded as interact_with_object without its exact native-affordance confirmation."
    }

    $identityKey = $CallId + "|interact_with_object"
    if (-not $ResolvedIdentities.ContainsKey($identityKey)) {
        return "Call $CallId confirmed an interaction without its exact resolved target."
    }

    $resolved = $ResolvedIdentities[$identityKey]
    $confirmed = $ConfirmedIdentities[$CallId]
    if ($resolved.Count -ne 1 -or $confirmed.Count -ne 1) {
        return "Call $CallId did not retain one immutable interaction target and confirmation."
    }

    $resolvedReference = @($resolved)[0]
    if (-not $confirmed.Contains($resolvedReference)) {
        return "Call $CallId confirmed a different interaction target than the one resolved."
    }
    return $null
}

function Get-TowardReferenceKickViolation {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Details)

    if ($Details -notmatch '(?:^|, )direction=toward_reference(?:,|$)') {
        return $null
    }

    $number = '[-+0-9.Ee]+'
    if ($Details -notmatch "launchDirection=\((?<x>$number),\s*(?<y>$number),\s*(?<z>$number)\)") {
        return "A toward-reference kick omitted its parseable launchDirection telemetry."
    }

    try {
        $launchX = [double]::Parse($Matches["x"], [System.Globalization.CultureInfo]::InvariantCulture)
        $launchY = [double]::Parse($Matches["y"], [System.Globalization.CultureInfo]::InvariantCulture)
        $launchZ = [double]::Parse($Matches["z"], [System.Globalization.CultureInfo]::InvariantCulture)
    }
    catch {
        return "A toward-reference kick reported invalid launchDirection telemetry."
    }
    if ([double]::IsNaN($launchX) -or [double]::IsInfinity($launchX) -or
        [double]::IsNaN($launchY) -or [double]::IsInfinity($launchY) -or
        [double]::IsNaN($launchZ) -or [double]::IsInfinity($launchZ) -or
        [math]::Abs($launchY) -gt 0.02) {
        return "A toward-reference kick encoded destination elevation in launchDirection instead of stock head pitch."
    }

    if ($Details -notmatch "launchPosition=\((?<x>$number),\s*(?<y>$number),\s*(?<z>$number)\)") {
        return "A toward-reference kick omitted its parseable launchPosition telemetry."
    }
    try {
        $launchPositionX = [double]::Parse($Matches["x"], [System.Globalization.CultureInfo]::InvariantCulture)
        $launchPositionZ = [double]::Parse($Matches["z"], [System.Globalization.CultureInfo]::InvariantCulture)
    }
    catch {
        return "A toward-reference kick reported invalid launchPosition telemetry."
    }

    if ($Details -notmatch "destinationPoint=\((?<x>$number),\s*(?<y>$number),\s*(?<z>$number)\)") {
        return "A toward-reference kick omitted its frozen destination point."
    }
    try {
        $destinationX = [double]::Parse($Matches["x"], [System.Globalization.CultureInfo]::InvariantCulture)
        $destinationZ = [double]::Parse($Matches["z"], [System.Globalization.CultureInfo]::InvariantCulture)
    }
    catch {
        return "A toward-reference kick reported an invalid frozen destination point."
    }

    $expectedX = $destinationX - $launchPositionX
    $expectedZ = $destinationZ - $launchPositionZ
    $expectedMagnitude = [math]::Sqrt($expectedX * $expectedX + $expectedZ * $expectedZ)
    if ($expectedMagnitude -gt 0.01) {
        $launchMagnitude = [math]::Sqrt($launchX * $launchX + $launchZ * $launchZ)
        if ($launchMagnitude -le 0.01) {
            return "A toward-reference kick had no horizontal launch direction toward its frozen destination."
        }
        $alignment = ($launchX * $expectedX + $launchZ * $expectedZ) /
                     ($launchMagnitude * $expectedMagnitude)
        if ($alignment -lt 0.995) {
            return "A toward-reference kick launch direction did not point at its frozen destination."
        }
    }

    if ($Details -notmatch "headPitch=(?<head>$number), stockPitch=(?<pitch>$number), stockMaxForce=(?<force>$number)") {
        return "A toward-reference kick omitted finite stock head-pitch/force telemetry."
    }
    try {
        $headPitch = [double]::Parse($Matches["head"], [System.Globalization.CultureInfo]::InvariantCulture)
        $stockPitch = [double]::Parse($Matches["pitch"], [System.Globalization.CultureInfo]::InvariantCulture)
        $stockMaxForce = [double]::Parse($Matches["force"], [System.Globalization.CultureInfo]::InvariantCulture)
    }
    catch {
        return "A toward-reference kick reported invalid stock head-pitch/force telemetry."
    }
    if ([double]::IsNaN($headPitch) -or [double]::IsInfinity($headPitch) -or
        [double]::IsNaN($stockPitch) -or [double]::IsInfinity($stockPitch) -or
        [double]::IsNaN($stockMaxForce) -or [double]::IsInfinity($stockMaxForce) -or
        $stockMaxForce -le 0.0) {
        return "A toward-reference kick reported invalid stock head-pitch/force telemetry."
    }
    if ($Details -match '(?:^|, )destination=none(?:,|$)') {
        return "A toward-reference kick launched without a frozen destination."
    }

    return $null
}
