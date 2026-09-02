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
    "RamblersInjectionPolicyProbe-" + [Guid]::NewGuid().ToString("N"))
$probeRoot = [System.IO.Path]::GetFullPath($probeRoot)
if (-not $probeRoot.StartsWith(
        $tempRoot,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Injection policy probe path escaped the temporary directory."
}

New-Item -ItemType Directory -Path $probeRoot | Out-Null
try {
    $probeOutput = Join-Path $probeRoot "CompanionAwarenessInjectionPolicyProbe.exe"
    & $compiler `
        /nologo `
        /target:exe `
        /langversion:latest `
        /optimize+ `
        "/out:$probeOutput" `
        (Join-Path $ramblersRoot "src\CompanionAwarenessInjectionPolicy.cs") `
        (Join-Path $PSScriptRoot "CompanionAwarenessInjectionPolicyProbe.cs")
    if ($LASTEXITCODE -ne 0) {
        throw "Injection policy probe compilation failed with exit code $LASTEXITCODE."
    }

    & $probeOutput
    if ($LASTEXITCODE -ne 0) {
        throw "Injection policy probe failed with exit code $LASTEXITCODE."
    }
}
finally {
    if (Test-Path -LiteralPath $probeRoot) {
        Remove-Item -LiteralPath $probeRoot -Recurse -Force
    }
}

Write-Host "Unsolicited-context policy probe passed."
