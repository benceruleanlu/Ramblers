#requires -Version 5.1

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
