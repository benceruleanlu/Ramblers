#requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$CompilerPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ramblersRoot = Split-Path -Parent $PSScriptRoot
$expectedCodecHash = "70921000BEB9CA762A8ACDB93AC6F6C39DB8A351A6FA12ACA3EDDBC652855F04"

$codecPath = Join-Path $ramblersRoot `
    "vendor\StbImageWriteSharp\1.16.7\StbImageWriteSharp.dll"
if (-not (Test-Path -LiteralPath $codecPath -PathType Leaf)) {
    throw "Vision-image transport check failed: pinned codec DLL is missing."
}
$codecHash = (Get-FileHash -LiteralPath $codecPath -Algorithm SHA256).Hash
if ($codecHash -ne $expectedCodecHash) {
    throw "Vision-image transport check failed: pinned codec hash changed."
}

$compilerPath = (Resolve-Path -LiteralPath $CompilerPath).Path
$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$probeRoot = Join-Path $tempRoot ("RamblersJpegProtocol-" + [Guid]::NewGuid().ToString("N"))
$probeRoot = [System.IO.Path]::GetFullPath($probeRoot)
if (-not $probeRoot.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Vision-image transport check failed: temporary probe path escaped the temp root."
}

New-Item -ItemType Directory -Path $probeRoot | Out-Null
try {
    $probeExe = Join-Path $probeRoot "JpegEncoderProtocolProbe.exe"
    $probeImage = Join-Path $probeRoot "probe.jpg"
    $frameworkRoot = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319"
    $systemPath = Join-Path $frameworkRoot "System.dll"
    $netstandardPath = Join-Path $frameworkRoot "netstandard.dll"
    $drawingPath = Join-Path $frameworkRoot "System.Drawing.dll"
    foreach ($frameworkPath in @($systemPath, $netstandardPath, $drawingPath)) {
        if (-not (Test-Path -LiteralPath $frameworkPath -PathType Leaf)) {
            throw "Vision-image transport check failed: test framework input is missing: $frameworkPath"
        }
    }

    $compilerArguments = @(
        "/noconfig",
        "/target:exe",
        "/langversion:latest",
        "/nullable:disable",
        "/optimize+",
        "/reference:$systemPath",
        "/reference:$netstandardPath",
        "/reference:$drawingPath",
        "/reference:$codecPath",
        "/out:$probeExe",
        (Join-Path $ramblersRoot "src\JpegEncoder.cs"),
        (Join-Path $ramblersRoot "analysis_scripts\JpegEncoderProtocolProbe.cs")
    )
    & $compilerPath @compilerArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Vision-image transport check failed: JPEG probe compilation failed."
    }

    Copy-Item -LiteralPath $codecPath -Destination $probeRoot
    & $probeExe $probeImage
    if ($LASTEXITCODE -ne 0) {
        throw "Vision-image transport check failed: JPEG probe execution failed."
    }
}
finally {
    if (Test-Path -LiteralPath $probeRoot) {
        Remove-Item -LiteralPath $probeRoot -Recurse -Force
    }
}

Write-Host "Vision-image transport probe passed."
