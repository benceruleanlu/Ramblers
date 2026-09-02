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
}
finally {
    if (Test-Path -LiteralPath $probeRoot) {
        Remove-Item -LiteralPath $probeRoot -Recurse -Force
    }
}

Write-Host "Interaction-reference protocol probe passed."
