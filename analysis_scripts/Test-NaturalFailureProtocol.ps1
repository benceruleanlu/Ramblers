#requires -Version 7.0

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ramblersRoot = Split-Path -Parent $PSScriptRoot
$result = Get-Content -LiteralPath (Join-Path $ramblersRoot "src\AgentToolResult.cs") -Raw

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
    @{ Error = "kick_target_destination_ambiguous"; Status = "could_not_identify_object" },
    @{ Error = "interaction_requires_item_placement"; Status = "game_action_unavailable" },
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

Write-Host "Natural-failure serialization probe passed."
