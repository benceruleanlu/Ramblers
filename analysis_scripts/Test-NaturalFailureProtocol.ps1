#requires -Version 7.0

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ramblersRoot = Split-Path -Parent $PSScriptRoot

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
        throw "Natural-failure check failed: $Description"
    }
}

function Assert-NotContains {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Needle,
        [Parameter(Mandatory = $true)][string]$Description
    )
    if ($Text.IndexOf($Needle, [System.StringComparison]::Ordinal) -ge 0) {
        throw "Natural-failure check failed: $Description"
    }
}

$result = Read-Source "src\AgentToolResult.cs"
$bridge = Read-Source "src\OpenAIRealtimeBridge.cs"
$prompt = Read-Source "src\AgentPrompt.cs"

Assert-Contains $result '[JsonIgnore]' `
    "exact diagnostic error codes must stay out of model-facing JSON"
Assert-NotContains $result '[JsonPropertyName("error")]' `
    "the model-facing result must not expose a raw error field"
Assert-Contains $result 'return "could_not_identify_object";' `
    "object ambiguity must have a player-safe status"
Assert-Contains $result 'return "temporarily_busy";' `
    "concurrent actions must have a player-safe status"
Assert-Contains $result 'return "game_action_unavailable";' `
    "other failures must have a player-safe status"
Assert-Contains $result 'Do not blame the player or mention tools, codes, diagnostics, or internal mechanics.' `
    "unavailable actions must produce natural in-world guidance"
Assert-Contains $bridge 'diagnosticError={diagnosticError ?? "none"}' `
    "developer logs must retain the exact failure code"
Assert-Contains $bridge 'dispatch.Result.Error' `
    "immediate failure diagnostics must cross the logging boundary"
Assert-Contains $bridge 'result.Error' `
    "deferred failure diagnostics must cross the logging boundary"
Assert-Contains $prompt 'never invent or expose diagnostic terminology' `
    "the cross-cutting prompt must forbid reconstructed technical language"

$probeName = "NaturalFailureProbe_" + [Guid]::NewGuid().ToString("N")
$probeSource = @"
public static class $probeName
{
    public static string FailureJson(string error)
    {
        return AgentToolResult.Failure(error).ToJson();
    }

    public static string SuccessJson()
    {
        return AgentToolResult.Success("jump", "jump_queued", "standing").ToJson();
    }
}
"@
$probeTypes = Add-Type -TypeDefinition ($result + [Environment]::NewLine + $probeSource) `
    -Language CSharp -PassThru
$probeType = $probeTypes | Where-Object { $_.Name -eq $probeName }
if ($null -eq $probeType) {
    throw "Natural-failure check failed: serialization probe type was not emitted"
}

$cases = @(
    @{ Error = "human_reference_not_captured"; Status = "could_not_identify_object" },
    @{ Error = "item_not_known"; Status = "could_not_identify_object" },
    @{ Error = "object_not_known"; Status = "could_not_identify_object" },
    @{ Error = "pick_up_item_in_progress"; Status = "temporarily_busy" },
    @{ Error = "bot_authority_unavailable"; Status = "game_action_unavailable" }
)
foreach ($case in $cases) {
    $json = $probeType.GetMethod("FailureJson").Invoke($null, @($case.Error))
    $parsed = $json | ConvertFrom-Json
    if ($parsed.ok -ne $false -or $parsed.status -ne $case.Status) {
        throw "Natural-failure check failed: $($case.Error) serialized with the wrong player-safe status"
    }
    if ($parsed.PSObject.Properties.Name -contains "error") {
        throw "Natural-failure check failed: $($case.Error) leaked a raw error field"
    }
    if ([string]::IsNullOrWhiteSpace($parsed.guidance)) {
        throw "Natural-failure check failed: $($case.Error) omitted reply guidance"
    }
}
$successJson = $probeType.GetMethod("SuccessJson").Invoke($null, @())
$success = $successJson | ConvertFrom-Json
if ($success.ok -ne $true -or $success.action -ne "jump" -or
    $success.status -ne "jump_queued" -or $success.state -ne "standing" -or
    $success.PSObject.Properties.Name -contains "guidance") {
    throw "Natural-failure check failed: successful action serialization changed"
}

Write-Host "Natural-failure protocol checks passed."
Write-Host "  Proven: serialized failures omit raw diagnostics and map identification, busy, and unavailable cases to player-safe status plus in-world guidance; exact codes stay in logs and success output is unchanged."
Write-Host "  Not proven: live model wording for every failure category."
