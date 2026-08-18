#requires -Version 5.1

[CmdletBinding()]
param(
    [string]$GamePath,
    [string]$CompilerPath,
    [string]$OutputPath,
    [switch]$NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = $PSScriptRoot
$compilerVersion = "4.14.0"
$compilerPackageHash = "941A9CF3EA618D88D01A3DD6B1A45A06BCF07716A9F81CE4031CAA3EDD24A845"
$compilerPackageUrl =
    "https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers.toolset/$compilerVersion/" +
    "microsoft.net.compilers.toolset.$compilerVersion.nupkg"
$compilerToolRoot = Join-Path $repositoryRoot ".tools\roslyn-$compilerVersion"
$defaultCompilerPath = Join-Path $compilerToolRoot "expanded\tasks\net472\csc.exe"

function Resolve-FullPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$BasePath
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $BasePath $Path))
}

function Get-SteamLibraryPaths {
    $steamRoots = @()
    $registryLocations = @(
        @{ Path = "HKCU:\Software\Valve\Steam"; Name = "SteamPath" },
        @{ Path = "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam"; Name = "InstallPath" }
    )

    foreach ($location in $registryLocations) {
        $properties = Get-ItemProperty -Path $location.Path -ErrorAction SilentlyContinue
        if ($null -ne $properties) {
            $value = $properties.($location.Name)
            if (-not [string]::IsNullOrWhiteSpace($value)) {
                $steamRoots += [System.IO.Path]::GetFullPath($value)
            }
        }
    }

    $steamRoots += @(
        "C:\Program Files (x86)\Steam",
        "C:\Program Files\Steam"
    )

    $libraryPaths = @()
    foreach ($steamRoot in ($steamRoots | Select-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $steamRoot -PathType Container)) {
            continue
        }

        $libraryPaths += $steamRoot
        $libraryFoldersPath = Join-Path $steamRoot "steamapps\libraryfolders.vdf"
        if (-not (Test-Path -LiteralPath $libraryFoldersPath -PathType Leaf)) {
            continue
        }

        $libraryFolders = Get-Content -LiteralPath $libraryFoldersPath -Raw
        foreach ($match in [regex]::Matches($libraryFolders, '"path"\s+"([^"]+)"')) {
            $libraryPath = $match.Groups[1].Value.Replace("\\", "\")
            if (Test-Path -LiteralPath $libraryPath -PathType Container) {
                $libraryPaths += [System.IO.Path]::GetFullPath($libraryPath)
            }
        }
    }

    return $libraryPaths | Select-Object -Unique
}

function Find-BigWalkPath {
    foreach ($libraryPath in Get-SteamLibraryPaths) {
        $steamAppsPath = Join-Path $libraryPath "steamapps"
        $installDirectory = "Big Walk"
        $manifestPath = Join-Path $steamAppsPath "appmanifest_1478500.acf"

        if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
            $manifest = Get-Content -LiteralPath $manifestPath -Raw
            $installMatch = [regex]::Match($manifest, '"installdir"\s+"([^"]+)"')
            if ($installMatch.Success) {
                $installDirectory = $installMatch.Groups[1].Value
            }
        }

        $candidate = Join-Path $steamAppsPath "common\$installDirectory"
        if (Test-Path -LiteralPath (Join-Path $candidate "Big Walk.exe") -PathType Leaf) {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }

    return $null
}

function Install-PinnedCompiler {
    $packagePath = Join-Path $compilerToolRoot "microsoft.net.compilers.toolset.$compilerVersion.nupkg"
    $expandedPath = Join-Path $compilerToolRoot "expanded"
    $temporaryPackagePath = "$packagePath.download"
    $temporaryExpandedPath = "$expandedPath.tmp-$PID"

    New-Item -ItemType Directory -Path $compilerToolRoot -Force | Out-Null

    if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
        Write-Host "Restoring Microsoft.Net.Compilers.Toolset $compilerVersion to .tools/ ..."
        if (Test-Path -LiteralPath $temporaryPackagePath) {
            Remove-Item -LiteralPath $temporaryPackagePath -Force
        }

        Invoke-WebRequest -Uri $compilerPackageUrl -OutFile $temporaryPackagePath -UseBasicParsing
        $downloadHash = (Get-FileHash -LiteralPath $temporaryPackagePath -Algorithm SHA256).Hash
        if ($downloadHash -ne $compilerPackageHash) {
            Remove-Item -LiteralPath $temporaryPackagePath -Force
            throw "Compiler package hash mismatch. Expected $compilerPackageHash but received $downloadHash."
        }

        Move-Item -LiteralPath $temporaryPackagePath -Destination $packagePath
    }

    $packageHash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
    if ($packageHash -ne $compilerPackageHash) {
        throw (
            "The cached compiler package failed verification: $packagePath`n" +
            "Expected SHA-256 $compilerPackageHash but found $packageHash. Remove the file and retry."
        )
    }

    if (Test-Path -LiteralPath $expandedPath) {
        throw (
            "The compiler cache is incomplete: $expandedPath`n" +
            "Remove that directory and retry."
        )
    }

    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::ExtractToDirectory($packagePath, $temporaryExpandedPath)

        $temporaryCompilerPath = Join-Path $temporaryExpandedPath "tasks\net472\csc.exe"
        if (-not (Test-Path -LiteralPath $temporaryCompilerPath -PathType Leaf)) {
            throw "The compiler package did not contain tasks\net472\csc.exe."
        }

        Move-Item -LiteralPath $temporaryExpandedPath -Destination $expandedPath
    }
    finally {
        if (Test-Path -LiteralPath $temporaryExpandedPath) {
            Remove-Item -LiteralPath $temporaryExpandedPath -Recurse -Force
        }
    }
}

if ([string]::IsNullOrWhiteSpace($GamePath)) {
    $GamePath = [Environment]::GetEnvironmentVariable("RAMBLERS_GAME_PATH")
}

if ([string]::IsNullOrWhiteSpace($GamePath)) {
    $GamePath = Find-BigWalkPath
}

if ([string]::IsNullOrWhiteSpace($GamePath)) {
    throw (
        "Big Walk was not found in the registered Steam libraries. " +
        "Pass -GamePath or set RAMBLERS_GAME_PATH."
    )
}

$GamePath = Resolve-FullPath -Path $GamePath -BasePath (Get-Location).Path
if (-not (Test-Path -LiteralPath (Join-Path $GamePath "Big Walk.exe") -PathType Leaf)) {
    throw "Big Walk.exe was not found under: $GamePath"
}

if ([string]::IsNullOrWhiteSpace($CompilerPath)) {
    $CompilerPath = [Environment]::GetEnvironmentVariable("RAMBLERS_CSC_PATH")
}

if ([string]::IsNullOrWhiteSpace($CompilerPath)) {
    $CompilerPath = $defaultCompilerPath
    if (-not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) {
        if ($NoRestore) {
            throw (
                "The pinned Roslyn compiler is not cached under .tools/. " +
                "Run without -NoRestore, pass -CompilerPath, or set RAMBLERS_CSC_PATH."
            )
        }

        Install-PinnedCompiler
    }
}

if (Test-Path -LiteralPath $CompilerPath -PathType Leaf) {
    $CompilerPath = (Resolve-Path -LiteralPath $CompilerPath).Path
}
else {
    $compilerCommand = Get-Command -Name $CompilerPath -CommandType Application -ErrorAction SilentlyContinue
    if ($null -eq $compilerCommand) {
        throw "The C# compiler was not found: $CompilerPath"
    }

    $CompilerPath = $compilerCommand.Source
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repositoryRoot "dist\Ramblers.dll"
}
else {
    $OutputPath = Resolve-FullPath -Path $OutputPath -BasePath $repositoryRoot
}

# Compile every source under src/. Sorting keeps the compiler argument order
# stable so /deterministic+ still yields a reproducible assembly.
$sourceRoot = Join-Path $repositoryRoot "src"
$sourcePaths = @(
    Get-ChildItem -LiteralPath $sourceRoot -Filter *.cs -Recurse -File |
        Sort-Object -Property FullName |
        ForEach-Object { $_.FullName }
)

if ($sourcePaths.Count -eq 0) {
    throw "No C# sources were found under: $sourceRoot"
}

$referenceSpecs = @(
    @{ Path = "dotnet\System.Private.CoreLib.dll" },
    @{ Path = "dotnet\System.Runtime.dll" },
    @{ Path = "dotnet\netstandard.dll" },
    @{ Path = "dotnet\System.Collections.dll" },
    @{ Path = "dotnet\System.Collections.Concurrent.dll" },
    @{ Path = "dotnet\System.Linq.dll" },
    @{ Path = "dotnet\System.Memory.dll" },
    @{ Path = "dotnet\System.Net.WebSockets.dll"; Alias = "websockets" },
    @{ Path = "dotnet\System.Net.WebSockets.Client.dll"; Alias = "websocketclient" },
    @{ Path = "dotnet\System.Private.Uri.dll"; Alias = "privateuri" },
    @{ Path = "dotnet\System.Text.Json.dll" },
    @{ Path = "dotnet\System.IO.Compression.dll" },
    @{ Path = "BepInEx\core\BepInEx.Core.dll" },
    @{ Path = "BepInEx\core\BepInEx.Unity.Common.dll" },
    @{ Path = "BepInEx\core\BepInEx.Unity.IL2CPP.dll" },
    @{ Path = "BepInEx\core\0Harmony.dll" },
    @{ Path = "BepInEx\core\Il2CppInterop.Runtime.dll" },
    @{ Path = "BepInEx\core\Il2CppInterop.Common.dll" },
    @{ Path = "BepInEx\interop\Assembly-CSharp.dll" },
    @{ Path = "BepInEx\interop\Mirror.dll" },
    @{ Path = "BepInEx\interop\DissonanceVoip.dll" },
    @{ Path = "BepInEx\interop\Il2Cppmscorlib.dll" },
    @{ Path = "BepInEx\interop\UnityEngine.CoreModule.dll" },
    @{ Path = "BepInEx\interop\UnityEngine.AudioModule.dll" },
    @{ Path = "BepInEx\interop\AudioSystem.dll" },
    @{ Path = "BepInEx\interop\UnityEngine.PhysicsModule.dll" },
    @{ Path = "BepInEx\interop\UnityEngine.InputLegacyModule.dll" }
)

$missingPaths = @()
foreach ($referenceSpec in $referenceSpecs) {
    $referencePath = Join-Path $GamePath $referenceSpec.Path
    if (-not (Test-Path -LiteralPath $referencePath -PathType Leaf)) {
        $missingPaths += $referencePath
    }
}

if ($missingPaths.Count -gt 0) {
    $formattedPaths = ($missingPaths | ForEach-Object { "  - $_" }) -join "`n"
    throw (
        "Build inputs are missing:`n$formattedPaths`n" +
        "Install BepInEx IL2CPP and launch Big Walk once so its interop assemblies are generated."
    )
}

$outputDirectory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

$compilerArguments = @(
    "/noconfig",
    "/nostdlib+",
    "/target:library",
    "/langversion:latest",
    "/nullable:disable",
    "/optimize+",
    "/deterministic+",
    "/debug:portable",
    "/pathmap:$repositoryRoot=/_/",
    "/out:$OutputPath"
)

foreach ($referenceSpec in $referenceSpecs) {
    $referencePath = Join-Path $GamePath $referenceSpec.Path
    if ($referenceSpec.ContainsKey("Alias")) {
        $compilerArguments += "/reference:$($referenceSpec.Alias)=$referencePath"
    }
    else {
        $compilerArguments += "/reference:$referencePath"
    }
}

$compilerArguments += $sourcePaths

Write-Host "Building Ramblers"
Write-Host "  Game:     $GamePath"
Write-Host "  Compiler: $CompilerPath"
Write-Host "  Output:   $OutputPath"

& $CompilerPath @compilerArguments
if ($LASTEXITCODE -ne 0) {
    throw "C# compilation failed with exit code $LASTEXITCODE."
}

$outputHash = (Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256).Hash
Write-Host "Build succeeded."
Write-Host "  SHA-256:  $outputHash"
