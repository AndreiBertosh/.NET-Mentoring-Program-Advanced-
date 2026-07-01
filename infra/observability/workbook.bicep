// =====================================================================================
// Job Runner Observability Workbook (the "dashboard")
// -------------------------------------------------------------------------------------
// Deploys an Azure Monitor Workbook bound to the Application Insights component with
// tiles for: throughput (TasksProcessedPerHour), failures + failure rate, long-running
// tasks, p95/p99 execution duration, and CPU/Memory. All tiles filter cloud_RoleName.
// =====================================================================================

@description('Name of the EXISTING Application Insights component the workbook reads from.')
param appInsightsName string

@description('Azure region for the workbook resource.')
param location string = resourceGroup().location

@description('cloud_RoleName stamped by the Worker.')
param roleName string = 'ReplicationDemo.Worker'

@description('Stable GUID for the workbook (change to deploy a second copy).')
param workbookId string = guid(resourceGroup().id, 'jobrunner-observability-workbook')

resource appInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: appInsightsName
}

var workbookContent = {
  version: 'Notebook/1.0'
  items: [
    {
      type: 1
      content: { json: '# Job Runner Observability — ${roleName}\nThroughput, failures, long-running tasks, latency percentiles, CPU & memory.' }
    }
    {
      type: 3
      name: 'TasksProcessedPerHour'
      content: {
        version: 'KqlItem/1.0'
        query: 'customMetrics\n| where name == "TasksProcessedCount" and cloud_RoleName == "${roleName}"\n| summarize TasksPerHour = sum(valueSum) by bin(timestamp, 1h)\n| render timechart'
        size: 0
        title: 'Tasks Processed Per Hour'
        timeContext: { durationMs: 86400000 }
        queryType: 0
        resourceType: 'microsoft.insights/components'
      }
    }
    {
      type: 3
      name: 'FailedVsTotal'
      content: {
        version: 'KqlItem/1.0'
        query: 'let failed = customMetrics | where name == "FailedTasksCount" and cloud_RoleName == "${roleName}" | summarize Failed = sum(valueSum) by bin(timestamp, 15m);\nlet total = customMetrics | where name == "TasksProcessedCount" and cloud_RoleName == "${roleName}" | summarize Total = sum(valueSum) by bin(timestamp, 15m);\ntotal | join kind=leftouter failed on timestamp | extend Failed = coalesce(Failed, 0.0) | extend FailureRatePct = iff(Total > 0, 100.0 * Failed / Total, 0.0) | project timestamp, Total, Failed, FailureRatePct | render timechart'
        size: 0
        title: 'Failed Tasks & Failure Rate (%)'
        timeContext: { durationMs: 86400000 }
        queryType: 0
        resourceType: 'microsoft.insights/components'
      }
    }
    {
      type: 3
      name: 'LongRunning'
      content: {
        version: 'KqlItem/1.0'
        query: 'customMetrics\n| where name == "LongRunningTasksCount" and cloud_RoleName == "${roleName}"\n| summarize LongRunning = sum(valueSum) by bin(timestamp, 15m)\n| render columnchart'
        size: 0
        title: 'Long-Running Tasks (over threshold)'
        timeContext: { durationMs: 86400000 }
        queryType: 0
        resourceType: 'microsoft.insights/components'
      }
    }
    {
      type: 3
      name: 'DurationPercentiles'
      content: {
        version: 'KqlItem/1.0'
        query: 'customMetrics\n| where name == "JobExecutionDurationMs" and cloud_RoleName == "${roleName}"\n| summarize p50 = percentile(todouble(valueMax), 50), p95 = percentile(todouble(valueMax), 95), p99 = percentile(todouble(valueMax), 99) by bin(timestamp, 15m)\n| render timechart'
        size: 0
        title: 'Execution Duration p50 / p95 / p99 (ms)'
        timeContext: { durationMs: 86400000 }
        queryType: 0
        resourceType: 'microsoft.insights/components'
      }
    }
    {
      type: 3
      name: 'CpuMemory'
      content: {
        version: 'KqlItem/1.0'
        query: 'performanceCounters\n| where cloud_RoleName == "${roleName}"\n| where name in ("% Processor Time", "cpu-usage", "% Committed Bytes In Use", "working-set")\n| summarize Value = avg(value) by bin(timestamp, 5m), name\n| render timechart'
        size: 0
        title: 'CPU & Memory'
        timeContext: { durationMs: 86400000 }
        queryType: 0
        resourceType: 'microsoft.insights/components'
      }
    }
  ]
  isLocked: false
}

resource workbook 'Microsoft.Insights/workbooks@2023-06-01' = {
  name: workbookId
  location: location
  kind: 'shared'
  properties: {
    displayName: 'Job Runner Observability'
    category: 'workbook'
    serializedData: string(workbookContent)
    sourceId: appInsights.id
    version: '1.0'
  }
}

output workbookResourceId string = workbook.id
