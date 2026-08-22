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
$probeRoot = Join-Path $tempRoot `
    ("RamblersJumpQueueProbe-" + [Guid]::NewGuid().ToString("N"))
$probeRoot = [System.IO.Path]::GetFullPath($probeRoot)
if (-not $probeRoot.StartsWith(
        $tempRoot,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Jump queue probe path escaped the temporary directory."
}

New-Item -ItemType Directory -Path $probeRoot | Out-Null
try {
    $probeOutput = Join-Path $probeRoot "CompanionJumpQueueProbe.exe"
    & $compiler `
        /nologo `
        /target:exe `
        /langversion:latest `
        /optimize+ `
        "/out:$probeOutput" `
        (Join-Path $ramblersRoot "src\CompanionJumpQueue.cs") `
        (Join-Path $PSScriptRoot "CompanionJumpQueueProbe.cs")
    if ($LASTEXITCODE -ne 0) {
        throw "Companion jump queue probe compilation failed with exit code $LASTEXITCODE."
    }

    & $probeOutput
    if ($LASTEXITCODE -ne 0) {
        throw "Companion jump queue probe failed with exit code $LASTEXITCODE."
    }
}
finally {
    if (Test-Path -LiteralPath $probeRoot) {
        Remove-Item -LiteralPath $probeRoot -Recurse -Force
    }
}
