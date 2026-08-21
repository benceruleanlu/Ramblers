#requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$CompilerPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ramblersRoot = Split-Path -Parent $PSScriptRoot
$probeOutputDirectory = Join-Path $env:TEMP "ramblers-breadcrumb-probe"
$probeOutput = Join-Path $probeOutputDirectory "BreadcrumbTrailShortcutProbe.exe"
New-Item -ItemType Directory -Path $probeOutputDirectory -Force | Out-Null

& $CompilerPath `
    /nologo `
    /target:exe `
    /optimize+ `
    "/out:$probeOutput" `
    (Join-Path $ramblersRoot "src\BreadcrumbTrail.cs") `
    (Join-Path $PSScriptRoot "BreadcrumbTrailShortcutProbe.cs")
if ($LASTEXITCODE -ne 0) {
    throw "Breadcrumb shortcut probe compilation failed with exit code $LASTEXITCODE."
}

& $probeOutput
if ($LASTEXITCODE -ne 0) {
    throw "Breadcrumb shortcut probe failed with exit code $LASTEXITCODE."
}
