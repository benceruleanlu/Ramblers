#requires -Version 7.0

[CmdletBinding()]
param(
    [string]$CompilerPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($CompilerPath)) {
    $CompilerPath = [Environment]::GetEnvironmentVariable("RAMBLERS_CSC_PATH")
}
if ([string]::IsNullOrWhiteSpace($CompilerPath)) {
    $CompilerPath = Join-Path $repositoryRoot (
        ".tools\roslyn-4.14.0\expanded\tasks\net472\csc.exe")
}
if (-not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) {
    throw (
        "The pinned Roslyn compiler is unavailable: $CompilerPath`n" +
        "Pass -CompilerPath or set RAMBLERS_CSC_PATH.")
}
$CompilerPath = (Resolve-Path -LiteralPath $CompilerPath).Path
$powerShellPath = (Get-Process -Id $PID).Path

$tests = @(
    Get-ChildItem -LiteralPath $PSScriptRoot -Filter "Test-*.ps1" -File |
        Sort-Object -Property Name
)
if ($tests.Count -eq 0) {
    throw "No protocol tests were found under $PSScriptRoot."
}

$passed = 0
$startedAt = [DateTimeOffset]::UtcNow
foreach ($test in $tests) {
    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile(
        $test.FullName,
        [ref]$tokens,
        [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) {
        $messages = ($parseErrors | ForEach-Object Message) -join "; "
        throw "Protocol test failed to parse: $($test.Name): $messages"
    }

    $arguments = @()
    $acceptsCompiler = $false
    if ($null -ne $ast.ParamBlock) {
        foreach ($parameter in $ast.ParamBlock.Parameters) {
            if ($parameter.Name.VariablePath.UserPath -eq "CompilerPath") {
                $acceptsCompiler = $true
                break
            }
        }
    }
    if ($acceptsCompiler) {
        $arguments += "-CompilerPath"
        $arguments += $CompilerPath
    }

    Write-Host "[$($passed + 1)/$($tests.Count)] $($test.Name)"
    & $powerShellPath -NoProfile -NonInteractive -File $test.FullName @arguments
    if ($LASTEXITCODE -ne 0) {
        throw (
            "Protocol test failed with exit code ${LASTEXITCODE}: " +
            $test.FullName)
    }
    $passed++
}

$elapsed = [DateTimeOffset]::UtcNow - $startedAt
Write-Host (
    "All $passed protocol tests passed in " +
    "{0:F1}s." -f $elapsed.TotalSeconds)
