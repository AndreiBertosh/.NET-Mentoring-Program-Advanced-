<#
.SYNOPSIS
    End-to-end test that fires a Job Runner alert and verifies it triggered in Azure Monitor.

.DESCRIPTION
    Validates the full observability chain for the demo:
        load -> Worker custom metrics -> Application Insights -> scheduled-query alert -> Action Group.

    Steps:
      1. Pre-flight: az login + Job Manager API reachability.
      2. Generate a burst of "due now" jobs so the Worker produces failures
         (FailedTasksCount) - enough to cross the failed-tasks threshold.
      3. Poll Application Insights until FailedTasksCount telemetry appears.
      4. Poll the Alerts Management API until the target rule fires (or times out).
      5. Print a PASS / FAIL summary.

    Prerequisites (must already be running):
      - docker compose up -d            (SQL primary/replica)
      - API    :  cd src/ReplicationDemo.Api ; dotnet run
      - Worker :  $env:DOTNET_ENVIRONMENT='Development'; dotnet run --project src/ReplicationDemo.Worker
      - Alerts deployed via deploy.ps1, against the same App Insights component the Worker reports to.

    NOTE: alert rules evaluate every 5 min over a 15 min window, so a real fire can take
    5-10 minutes. To make the demo fast, lower the threshold first, e.g.:
        az deployment group create -g <rg> --template-file observability.bicep `
          --parameters appInsightsName=<ai> alertEmail=you@example.com `
                       failedTasksThreshold=2 failureRatePercent=5

.PARAMETER ResourceGroup
    Resource group containing the Application Insights component and the alert rules.

.PARAMETER AppInsightsName
    Name of the Application Insights component the Worker reports to.

.PARAMETER ApiBaseUrl
    Base URL of the Job Manager API. Default http://localhost:5241.

.PARAMETER AlertRuleName
    Scheduled-query rule to wait for. Default 'jobrunner-failed-tasks-high'.

.PARAMETER RoleName
    cloud_RoleName stamped by the Worker. Default 'ReplicationDemo.Worker'.

.PARAMETER JobCount
    Jobs per burst batch. Default 80.

.PARAMETER Batches
    Number of burst batches to send up front. Default 2.

.PARAMETER MaxWaitMinutes
    How long to wait for the alert to fire before declaring FAIL. Default 12.

.EXAMPLE
    ./test-alert.ps1 -ResourceGroup rg-jobs -AppInsightsName ai-jobs

.EXAMPLE
    ./test-alert.ps1 -ResourceGroup rg -AppInsightsName ai `
                     -AlertRuleName jobrunner-long-running-high -JobCount 120
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $ResourceGroup,
    [Parameter(Mandatory = $true)] [string] $AppInsightsName,
    [string] $ApiBaseUrl     = 'http://localhost:5241',
    [string] $AlertRuleName  = 'jobrunner-failed-tasks-high',
    [string] $RoleName       = 'ReplicationDemo.Worker',
    [int]    $JobCount       = 80,
    [int]    $Batches        = 2,
    [int]    $MaxWaitMinutes = 12
)

$ErrorActionPreference = 'Stop'
# Native (az) commands are polled in loops; a non-zero exit must NOT abort the test.
$PSNativeCommandUseErrorActionPreference = $false
$ApiBaseUrl   = $ApiBaseUrl.TrimEnd('/')
$jobsUrl      = "$ApiBaseUrl/api/job-manager/jobs"
$schedulesUrl = "$ApiBaseUrl/api/orchestrator/schedules"
$here         = Split-Path -Parent $MyInvocation.MyCommand.Path

function Write-Step  ($m) { Write-Host "`n=== $m ===" -ForegroundColor Cyan }
function Write-Ok    ($m) { Write-Host "  [OK]   $m" -ForegroundColor Green }
function Write-Info  ($m) { Write-Host "  [..]   $m" -ForegroundColor DarkGray }
function Write-Fail  ($m) { Write-Host "  [FAIL] $m" -ForegroundColor Red }

# ---------------------------------------------------------------------------
# 1. Pre-flight
# ---------------------------------------------------------------------------
Write-Step '1/4 Pre-flight checks'

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw "Azure CLI 'az' not found on PATH. Open a new terminal or run the PATH refresh first."
}

$account = az account show -o json 2>$null | ConvertFrom-Json
if (-not $account) { throw "Not logged in to Azure. Run 'az login' first." }
Write-Ok "Azure account: $($account.user.name)  /  sub: $($account.name)"

$appId = az monitor app-insights component show `
            --app $AppInsightsName -g $ResourceGroup --query appId -o tsv 2>$null
if ([string]::IsNullOrWhiteSpace($appId)) {
    throw "Application Insights component '$AppInsightsName' not found in RG '$ResourceGroup'."
}
Write-Ok "App Insights component found (appId $appId)"

$rule = az monitor scheduled-query show -g $ResourceGroup -n $AlertRuleName -o json 2>$null | ConvertFrom-Json
if (-not $rule) {
    throw "Alert rule '$AlertRuleName' not found in RG '$ResourceGroup'. Deploy it with deploy.ps1 first."
}
$ruleId = $rule.id
Write-Ok "Alert rule '$AlertRuleName' exists (enabled=$($rule.enabled))"

try {
    Invoke-RestMethod -Method Get -Uri $jobsUrl -TimeoutSec 10 | Out-Null
    Write-Ok "Job Manager API reachable at $ApiBaseUrl"
}
catch {
    throw "Job Manager API not reachable at $ApiBaseUrl. Start it: cd src/ReplicationDemo.Api ; dotnet run"
}

# Mark the moment the test starts so we only count alerts fired from now on.
$testStartUtc = (Get-Date).ToUniversalTime()

# ---------------------------------------------------------------------------
# 2. Generate failure load
# ---------------------------------------------------------------------------
Write-Step "2/4 Generating load ($Batches batch(es) x $JobCount jobs)"

function Invoke-Batch {
    param([int] $Count, [int] $BatchNumber)
    $created = 0; $failed = 0
    for ($i = 1; $i -le $Count; $i++) {
        $suffix = "test-$BatchNumber-$i"
        try {
            $jobBody = @{
                name          = "Alert Test Job $suffix"
                frequency     = 'Daily'
                executionTime = '08:00:00'
                apiEndpoint   = "https://example.com/alert-test/$suffix"
            } | ConvertTo-Json
            $job = Invoke-RestMethod -Method Post -Uri $jobsUrl -ContentType 'application/json' -Body $jobBody

            $scheduleBody = @{
                jobId       = $job.id
                nextRunTime = (Get-Date).ToUniversalTime().ToString('o')   # due now
            } | ConvertTo-Json
            Invoke-RestMethod -Method Post -Uri $schedulesUrl -ContentType 'application/json' -Body $scheduleBody | Out-Null
            $created++
        }
        catch { $failed++ }
    }
    Write-Info ("batch {0}: created {1}/{2} due schedules ({3} api errors)" -f $BatchNumber, $created, $Count, $failed)
}

for ($b = 1; $b -le $Batches; $b++) { Invoke-Batch -Count $JobCount -BatchNumber $b }
Write-Ok "Load submitted. Worker dispatches due schedules every ~30s and records metrics."

# ---------------------------------------------------------------------------
# 3. Wait for FailedTasksCount telemetry to land
# ---------------------------------------------------------------------------
Write-Step '3/4 Waiting for failure telemetry in Application Insights'

$metricsKql = @"
customMetrics
| where name == 'FailedTasksCount' and cloud_RoleName == '$RoleName'
| summarize Failed = sum(valueSum)
"@

$deadline = (Get-Date).AddMinutes([Math]::Min(6, $MaxWaitMinutes))
$failedSeen = $false
while ((Get-Date) -lt $deadline) {
    $res = az monitor app-insights query --app $AppInsightsName -g $ResourceGroup `
              --analytics-query $metricsKql -o json 2>$null | ConvertFrom-Json
    $num = 0.0
    if ($res.tables -and $res.tables.Count -gt 0) {
        $tbl = $res.tables[0]
        # Locate the 'Failed' column by name so we never read a positional timestamp column.
        $idx = 0
        if ($tbl.columns) {
            for ($c = 0; $c -lt $tbl.columns.Count; $c++) {
                if ($tbl.columns[$c].name -eq 'Failed') { $idx = $c; break }
            }
        }
        if ($tbl.rows -and $tbl.rows.Count -gt 0) {
            $raw = $tbl.rows[0][$idx]
            [double]::TryParse([string]$raw, [ref]$num) | Out-Null
        }
    }
    if ($num -gt 0) {
        Write-Ok "FailedTasksCount telemetry present (sum = $num)"
        $failedSeen = $true
        break
    }
    Write-Info 'no failure telemetry yet, retrying in 20s...'
    Start-Sleep -Seconds 20
}
if (-not $failedSeen) {
    Write-Fail 'No FailedTasksCount telemetry arrived. Is the Worker running and pointed at this App Insights?'
}

# ---------------------------------------------------------------------------
# 4. Wait for the alert to fire
# ---------------------------------------------------------------------------
Write-Step "4/4 Waiting for alert '$AlertRuleName' to fire (up to $MaxWaitMinutes min)"

function Get-FiredAlert {
    param([string] $RuleId, [datetime] $SinceUtc)
    # Alerts Management API - fired alert instances for this rule.
    # NOTE: pass query params via --uri-parameters; embedding '&' in the URL breaks the
    # Windows az.cmd wrapper (cmd.exe treats '&' as a command separator).
    $uri = "https://management.azure.com/subscriptions/$($account.id)/providers/Microsoft.AlertsManagement/alerts"
    $json = az rest --method get --url $uri --uri-parameters api-version=2019-05-05-preview timeRange=1d -o json 2>$null | ConvertFrom-Json
    if (-not $json.value) { return $null }
    return $json.value | Where-Object {
        $_.properties.essentials.alertRule -eq $AlertRuleName -and
        [datetime]$_.properties.essentials.startDateTime -ge $SinceUtc.AddMinutes(-2)
    } | Select-Object -First 1
}

$alertDeadline = (Get-Date).AddMinutes($MaxWaitMinutes)
$fired = $null
while ((Get-Date) -lt $alertDeadline) {
    $fired = Get-FiredAlert -RuleId $ruleId -SinceUtc $testStartUtc
    if ($fired) { break }
    $remaining = [int]($alertDeadline - (Get-Date)).TotalSeconds
    Write-Info "alert not fired yet (rules evaluate every 5m); ${remaining}s budget left, retrying in 30s..."
    Start-Sleep -Seconds 30
}

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
Write-Step 'RESULT'
if ($fired) {
    $e = $fired.properties.essentials
    Write-Ok  "ALERT FIRED: $($e.alertRule)"
    Write-Host "         severity : $($e.severity)"      -ForegroundColor Green
    Write-Host "         state    : $($e.alertState) / $($e.monitorCondition)" -ForegroundColor Green
    Write-Host "         started  : $($e.startDateTime)" -ForegroundColor Green
    Write-Host "         target   : $($e.targetResource)" -ForegroundColor Green
    Write-Host "`nTEST PASSED - check the inbox for the Action Group 'ag-jobrunner-oncall' email." -ForegroundColor Green
    exit 0
}
else {
    Write-Fail "Alert '$AlertRuleName' did not fire within $MaxWaitMinutes minutes."
    Write-Host "  Possible causes / next checks:" -ForegroundColor Yellow
    Write-Host "    - Threshold not crossed: lower it (failedTasksThreshold/failureRatePercent) and re-run." -ForegroundColor Yellow
    Write-Host "    - Worker not running or pointed at a different App Insights component." -ForegroundColor Yellow
    Write-Host "    - Evaluation lag: rules run every 5m over a 15m window; try -MaxWaitMinutes 15." -ForegroundColor Yellow
    Write-Host "    - Inspect manually:  Azure Portal -> Monitor -> Alerts." -ForegroundColor Yellow
    exit 1
}
