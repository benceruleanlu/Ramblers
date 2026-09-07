[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$CompilerPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ramblersRoot = Split-Path -Parent $PSScriptRoot
$compiler = (Resolve-Path -LiteralPath $CompilerPath).Path
$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$probeRoot = [System.IO.Path]::GetFullPath((Join-Path $tempRoot (
    "RamblersNavigationGeometryProbe-" + [Guid]::NewGuid().ToString("N"))))
if (-not $probeRoot.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Navigation geometry probe escaped the temporary directory."
}
New-Item -ItemType Directory -Path $probeRoot | Out-Null
try {
    $probeOutput = Join-Path $probeRoot "CompanionNavigationGeometryProbe.exe"
    & $compiler /nologo /target:exe /langversion:latest /optimize+ "/out:$probeOutput" `
        (Join-Path $ramblersRoot "src\CompanionNavigationGeometry.cs") `
        (Join-Path $ramblersRoot "src\CompanionNavigationReproProbe.cs") `
        (Join-Path $ramblersRoot "src\CompanionLocalRoutePlanner.cs") `
        (Join-Path $ramblersRoot "src\CompanionLocomotion.cs") `
        (Join-Path $PSScriptRoot "CompanionNavigationGeometryProbe.cs")
    if ($LASTEXITCODE -ne 0) {
        throw "Navigation geometry probe compilation failed: $LASTEXITCODE."
    }
    & $probeOutput
    if ($LASTEXITCODE -ne 0) {
        throw "Navigation geometry probe failed: $LASTEXITCODE."
    }
}
finally {
    if (Test-Path -LiteralPath $probeRoot) {
        Remove-Item -LiteralPath $probeRoot -Recurse -Force
    }
}
