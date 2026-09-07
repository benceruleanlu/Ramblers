#requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$CompilerPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ramblersRoot = Split-Path -Parent $PSScriptRoot
$probeOutputDirectory = Join-Path $env:TEMP "ramblers-traversal-recorder-probe"
$probeOutput = Join-Path $probeOutputDirectory "CompanionTraversalRecorderProbe.exe"
New-Item -ItemType Directory -Path $probeOutputDirectory -Force | Out-Null

& $CompilerPath `
    /nologo `
    /target:exe `
    /optimize+ `
    "/out:$probeOutput" `
    (Join-Path $ramblersRoot "src\CompanionTraversalRecorder.cs") `
    (Join-Path $PSScriptRoot "CompanionTraversalRecorderProbe.cs")
if ($LASTEXITCODE -ne 0) {
    throw "Traversal recorder probe compilation failed with exit code $LASTEXITCODE."
}

& $probeOutput
if ($LASTEXITCODE -ne 0) {
    throw "Traversal recorder probe failed with exit code $LASTEXITCODE."
}
