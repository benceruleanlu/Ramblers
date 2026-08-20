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

function Read-Source {
    param([Parameter(Mandatory = $true)][string]$RelativePath)
    return Get-Content -LiteralPath (Join-Path $ramblersRoot $RelativePath) -Raw
}

function Assert-Contains {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Needle,
        [Parameter(Mandatory = $true)][string]$Description
    )
    if ($Text.IndexOf($Needle, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Vision-image transport check failed: $Description"
    }
}

$capture = Read-Source "src\CompanionVisionCapture.cs"
$encoder = Read-Source "src\JpegEncoder.cs"
$transport = Read-Source "src\OpenAIRealtimeClient.cs"
$build = Read-Source "build.ps1"

Assert-Contains $capture 'private const int CaptureWidth = 640;' `
    "the reviewed 16:9 capture width must stay explicit"
Assert-Contains $capture 'private const int CaptureHeight = 360;' `
    "the reviewed 16:9 capture height must stay explicit"
Assert-Contains $capture 'JpegEncoder.EncodeRgb24(' `
    "capture must use the managed JPEG encoder"
Assert-Contains $capture 'MediaType = JpegEncoder.MediaType' `
    "the data URI must advertise the bytes as JPEG"
Assert-Contains $encoder 'internal const string MediaType = "image/jpeg";' `
    "the encoder media type must match the Realtime contract"
Assert-Contains $encoder 'internal const int DefaultQuality = 82;' `
    "the bandwidth/fidelity choice must remain reviewable"
Assert-Contains $transport 'detail = "high",' `
    "the model processing detail must not change implicitly"
Assert-Contains $build '$compilerArguments += "/reference:$jpegEncoderPath"' `
    "the plugin must compile against the pinned managed codec"
Assert-Contains $build 'Copy-Item -LiteralPath $jpegEncoderPath' `
    "the runtime dependency must be emitted beside every build"

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

Write-Host "Vision-image transport checks passed."
Write-Host "  Proven: pinned codec integrity, JPEG decode, dimensions, orientation, quality, media type, explicit high detail, and build output wiring."
Write-Host "  Not proven: live Unity capture content or Realtime model acceptance in the deployed game."
