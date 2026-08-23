#requires -Version 5.1

Set-StrictMode -Version Latest

function Get-ToolBatchSettlementViolations {
    [CmdletBinding()]
    param(
        [AllowEmptyCollection()]
        [string[]]$Lines
    )

    $active = @{}
    $detached = @{}
    $violations = New-Object System.Collections.Generic.List[string]

    foreach ($line in @($Lines)) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        if ($line -match 'TOOL_BATCH_RECONCILIATION_STARTED responseId=(?<response>[^,]+), turnId=(?<turn>\d+)') {
            $active[$Matches["response"]] = $Matches["turn"]
        }

        if ($line -match 'TOOL_BATCH_(RECONCILED|RECONCILIATION_ABANDONED) responseId=(?<response>[^,]+), turnId=(?<turn>\d+)') {
            [void]$active.Remove($Matches["response"])
        }

        if ($line -match 'TOOL_BATCH_RECONCILIATION_DETACHED responseId=(?<response>[^,]+), turnId=(?<turn>\d+), token=(?<token>\d+)') {
            $response = $Matches["response"]
            $token = $Matches["token"]
            [void]$active.Remove($response)
            $detached[$token] = [pscustomobject]@{
                Response = $response
                Turn = $Matches["turn"]
            }
        }

        if ($line -match '\[ACTION\] (JOB_TOKEN_RETIRED|JOB_SETTLEMENT_ABANDONED) token=(?<token>\d+), .*reason=(detached_reconciled|detached_timeout)') {
            [void]$detached.Remove($Matches["token"])
        }

        if ($line -match 'TOOL_BATCH_(COMPLETED|TIMEOUT|CANCELLED) responseId=(?<response>[^,]+), turnId=(?<turn>\d+)') {
            $response = $Matches["response"]
            if ($active.ContainsKey($response)) {
                $violations.Add(
                    "Turn $($Matches["turn"]) response $response ended its tool batch before cancellation reconciliation settled or detached.")
            }
        }
    }

    foreach ($response in $active.Keys) {
        $violations.Add(
            "Turn $($active[$response]) response $response still has unresolved cancellation reconciliation at the end of the log.")
    }
    foreach ($token in $detached.Keys) {
        $entry = $detached[$token]
        $violations.Add(
            "Turn $($entry.Turn) response $($entry.Response) detached job token $token without later controller settlement or ownership abandonment.")
    }

    return @($violations)
}
