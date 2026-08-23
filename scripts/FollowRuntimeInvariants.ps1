#requires -Version 5.1

Set-StrictMode -Version Latest

function Get-FollowTangentViolation {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Line
    )

    if ($Line -notmatch (
            '\[FOLLOW\] ROUTE_TANGENT .*kind=(?<kind>jump|drop), ' +
            'horizontalDistance=(?<distance>[0-9.]+), ' +
            'corridor=(?<corridor>[0-9.]+)\.')) {
        return $null
    }

    $kind = $Matches["kind"]
    $distance = [float]::Parse(
        $Matches["distance"],
        [System.Globalization.CultureInfo]::InvariantCulture)
    $reportedCorridor = [float]::Parse(
        $Matches["corridor"],
        [System.Globalization.CultureInfo]::InvariantCulture)
    $expectedCorridor = if ($kind -eq "jump") { [float]1.6 } else { [float]1.8 }

    if ([Math]::Abs($reportedCorridor - $expectedCorridor) -gt [float]0.001) {
        return (
            "Follow reported an unexpected $kind tangent corridor " +
            "$($reportedCorridor.ToString('F3', [System.Globalization.CultureInfo]::InvariantCulture))m; " +
            "expected $($expectedCorridor.ToString('F3', [System.Globalization.CultureInfo]::InvariantCulture))m."
        )
    }

    # Telemetry is rounded to millimeters, so retain half a millimeter of
    # comparison tolerance at the inclusive source-code boundary.
    if ($distance -gt $expectedCorridor + [float]0.0005) {
        return (
            "Follow replayed an uncommitted $kind traversal tangent " +
            "$($distance.ToString('F3', [System.Globalization.CultureInfo]::InvariantCulture))m " +
            "from its marker; limit is " +
            "$($expectedCorridor.ToString('F3', [System.Globalization.CultureInfo]::InvariantCulture))m."
        )
    }

    return $null
}
