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

$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$probeRoot = Join-Path $tempRoot (
    "RamblersAffordanceProtocolProbe-" + [Guid]::NewGuid().ToString("N"))
$probeRoot = [System.IO.Path]::GetFullPath($probeRoot)
if (-not $probeRoot.StartsWith(
        $tempRoot,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Affordance protocol probe path escaped the temporary directory."
}

New-Item -ItemType Directory -Path $probeRoot | Out-Null
try {
    $probeOutput = Join-Path $probeRoot "CompanionAffordanceProtocolProbe.exe"
    & $compiler `
        /nologo `
        /target:exe `
        /langversion:latest `
        /optimize+ `
        "/out:$probeOutput" `
        (Join-Path $ramblersRoot "src\CompanionAffordanceProtocol.cs") `
        (Join-Path $PSScriptRoot "CompanionAffordanceProtocolProbe.cs")
    if ($LASTEXITCODE -ne 0) {
        throw "Affordance protocol probe compilation failed with exit code $LASTEXITCODE."
    }
    & $probeOutput
    if ($LASTEXITCODE -ne 0) {
        throw "Affordance protocol probe failed with exit code $LASTEXITCODE."
    }

    $activationProbeOutput = Join-Path $probeRoot `
        "CompanionAffordanceActivationProbe.exe"
    & $compiler `
        /nologo `
        /target:exe `
        /langversion:latest `
        /optimize+ `
        /nowarn:0649 `
        "/out:$activationProbeOutput" `
        (Join-Path $ramblersRoot "src\CompanionAffordanceProtocol.cs") `
        (Join-Path $ramblersRoot "src\CompanionAffordanceDriver.cs") `
        (Join-Path $PSScriptRoot "CompanionAffordanceActivationProbe.cs")
    if ($LASTEXITCODE -ne 0) {
        throw "Affordance activation probe compilation failed with exit code $LASTEXITCODE."
    }

    & $activationProbeOutput
    if ($LASTEXITCODE -ne 0) {
        throw "Affordance activation probe failed with exit code $LASTEXITCODE."
    }
}
finally {
    if (Test-Path -LiteralPath $probeRoot) {
        Remove-Item -LiteralPath $probeRoot -Recurse -Force
    }
}

Write-Host "Grounded-affordance protocol probes passed."
