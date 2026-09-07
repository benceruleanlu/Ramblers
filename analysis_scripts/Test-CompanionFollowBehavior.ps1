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
    "RamblersFollowBehaviorProbe-" + [Guid]::NewGuid().ToString("N"))))
if (-not $probeRoot.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Follow behavior probe path escaped the temporary directory."
}

New-Item -ItemType Directory -Path $probeRoot | Out-Null
try {
    $probeOutput = Join-Path $probeRoot "CompanionFollowBehaviorProbe.exe"
    & $compiler `
        /nologo `
        /target:exe `
        /langversion:latest `
        /optimize+ `
        "/out:$probeOutput" `
        (Join-Path $ramblersRoot "src\BreadcrumbTrail.cs") `
        (Join-Path $ramblersRoot "src\CompanionTraversalRecorder.cs") `
        (Join-Path $ramblersRoot "src\CompanionTraversalReplay.cs") `
        (Join-Path $ramblersRoot "src\CompanionLocalRoutePlanner.cs") `
        (Join-Path $ramblersRoot "src\CompanionFollowNavigation.cs") `
        (Join-Path $ramblersRoot "src\CompanionFollowBehavior.cs") `
        (Join-Path $PSScriptRoot "CompanionFollowBehaviorProbe.cs")
    if ($LASTEXITCODE -ne 0) {
        throw "Companion follow behavior probe compilation failed with exit code $LASTEXITCODE."
    }
    & $probeOutput
    if ($LASTEXITCODE -ne 0) {
        throw "Companion follow behavior probe failed with exit code $LASTEXITCODE."
    }
}
finally {
    if (Test-Path -LiteralPath $probeRoot) {
        Remove-Item -LiteralPath $probeRoot -Recurse -Force
    }
}
