<#
.SYNOPSIS
    Generates load for the Job Runner observability demo.

.DESCRIPTION
    Creates jobs via the Job Manager API and immediately schedules them as "due now",
    so the Worker's SchedulePollingService dispatches them to JobRunnerConsumer within
    ~30s. The runner executes each (0.5-3s, ~80% success), producing the custom metrics:
        TasksProcessedCount, FailedTasksCount, LongRunningTasksCount, JobExecutionDurationMs

    Prerequisites:
      - docker compose up -d            (SQL primary/replica)
      - API running   :  cd src/ReplicationDemo.Api   ; dotnet run
      - Worker running:  $env:DOTNET_ENVIRONMENT='Development'; dotnet run --project src/ReplicationDemo.Worker

.PARAMETER ApiBaseUrl
    Base URL of the Job Manager API. Default http://localhost:5241.

.PARAMETER JobCount
    Number of jobs/schedules to create per batch. Default 30.

.PARAMETER Loop
    Keep generating batches until Ctrl+C (use this to build up enough volume to trip alerts).

.PARAMETER IntervalSeconds
    Delay between batches when -Loop is set. Default 30.

.EXAMPLE
    ./demo-load.ps1
    Single batch of 30 jobs.

.EXAMPLE
    ./demo-load.ps1 -JobCount 50 -Loop -IntervalSeconds 20
    Sustained load: 50 jobs every 20s until Ctrl+C (good for firing alerts).
#>
[CmdletBinding()]
param(
    [string] $ApiBaseUrl = 'http://localhost:5241',
    [int]    $JobCount = 30,
    [switch] $Loop,
    [int]    $IntervalSeconds = 30
)

$ErrorActionPreference = 'Stop'
$ApiBaseUrl = $ApiBaseUrl.TrimEnd('/')
$jobsUrl      = "$ApiBaseUrl/api/job-manager/jobs"
$schedulesUrl = "$ApiBaseUrl/api/orchestrator/schedules"

function Invoke-Batch {
    param([int] $Count, [int] $BatchNumber)

    $created = 0
    $failed  = 0

    for ($i = 1; $i -le $Count; $i++) {
        $suffix = "$BatchNumber-$i"
        try {
            $jobBody = @{
                name          = "Demo Job $suffix"
                frequency     = 'Daily'
                executionTime = '08:00:00'        # bound to TimeSpan
                apiEndpoint   = "https://example.com/demo/$suffix"
            } | ConvertTo-Json

            $job = Invoke-RestMethod -Method Post -Uri $jobsUrl -ContentType 'application/json' -Body $jobBody

            $scheduleBody = @{
                jobId       = $job.id
                nextRunTime = (Get-Date).ToUniversalTime().ToString('o')   # due now
            } | ConvertTo-Json

            Invoke-RestMethod -Method Post -Uri $schedulesUrl -ContentType 'application/json' -Body $scheduleBody | Out-Null
            $created++
        }
        catch {
            $failed++
            Write-Warning "Job $suffix failed: $($_.Exception.Message)"
        }
    }

    Write-Host ("[batch {0}] created {1}/{2} due schedules ({3} failed)" -f $BatchNumber, $created, $Count, $failed) -ForegroundColor Green
}

Write-Host "Target API : $ApiBaseUrl"  -ForegroundColor Cyan
Write-Host "Batch size : $JobCount"    -ForegroundColor Cyan
Write-Host "Mode       : $(if ($Loop) { "loop every ${IntervalSeconds}s (Ctrl+C to stop)" } else { 'single batch' })" -ForegroundColor Cyan
Write-Host ""

$batch = 0
do {
    $batch++
    Invoke-Batch -Count $JobCount -BatchNumber $batch
    if ($Loop) { Start-Sleep -Seconds $IntervalSeconds }
} while ($Loop)

Write-Host ""
Write-Host "Done. The Worker dispatches due schedules every ~30s; metrics appear in" -ForegroundColor Yellow
Write-Host "Application Insights -> customMetrics within ~2-3 minutes." -ForegroundColor Yellow
