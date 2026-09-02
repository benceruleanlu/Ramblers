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
    "RamblersJobSettlementProbe-" + [Guid]::NewGuid().ToString("N"))
$probeRoot = [System.IO.Path]::GetFullPath($probeRoot)
if (-not $probeRoot.StartsWith(
        $tempRoot,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Job settlement probe path escaped the temporary directory."
}

New-Item -ItemType Directory -Path $probeRoot | Out-Null
try {
    $probeOutput = Join-Path $probeRoot "CompanionJobSettlementProtocolProbe.exe"
    & $compiler `
        /nologo `
        /target:exe `
        /langversion:latest `
        /optimize+ `
        "/out:$probeOutput" `
        (Join-Path $ramblersRoot "src\CompanionJobSettlementProtocol.cs") `
        (Join-Path $PSScriptRoot "CompanionJobSettlementProtocolProbe.cs")
    if ($LASTEXITCODE -ne 0) {
        throw "Job settlement probe compilation failed with exit code $LASTEXITCODE."
    }

    & $probeOutput
    if ($LASTEXITCODE -ne 0) {
        throw "Job settlement probe failed with exit code $LASTEXITCODE."
    }
}
finally {
    if (Test-Path -LiteralPath $probeRoot) {
        Remove-Item -LiteralPath $probeRoot -Recurse -Force
    }
}

Write-Host "Job-settlement protocol probe passed."
