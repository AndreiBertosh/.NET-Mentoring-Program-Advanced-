// =====================================================================================
// Observability for the ReplicationDemo Job Runner (ReplicationDemo.Worker)
// -------------------------------------------------------------------------------------
// Deploys, against an existing Application Insights component:
//   • 1 Action Group        (email + optional Teams/Slack webhook notification channel)
//   • 6 Scheduled Query (log) alert rules over Application Insights telemetry:
//       - CPU usage  >= 70%                    (built-in, performanceCounters)
//       - Memory     >= 85% of limit           (built-in, performanceCounters)
//       - Requests / executions count > burst  (built-in, throughput guardrail)
//       - p95 execution duration > threshold   (built-in, requests/customMetrics)
//       - FailedTasksCount  > N  AND failure-rate > X%   (custom metric)
//       - LongRunningTasksCount > N            (custom metric)
//
// Scheduled-query rules are used (rather than metric alerts) so every alert reads the
// same low-cardinality custom metrics, supports KQL percentiles/rates, and gives full
// control over the evaluation window. All queries filter cloud_RoleName == roleName.
// =====================================================================================

@description('Name of the EXISTING Application Insights component to monitor.')
param appInsightsName string

@description('Azure region for the alert/action-group resources (use the AI component region).')
param location string = resourceGroup().location

@description('cloud_RoleName stamped by the Worker (RoleNameInitializer). Used to scope every query.')
param roleName string = 'ReplicationDemo.Worker'

@description('Email address that receives alert notifications.')
param alertEmail string

@description('Optional Teams/Slack incoming-webhook URL. Leave empty to disable the webhook channel.')
param teamsWebhookUrl string = ''

@description('CPU alert threshold (percent).')
param cpuThresholdPercent int = 70

@description('Memory alert threshold (percent of process working-set limit).')
param memoryThresholdPercent int = 85

@description('p95 execution-duration alert threshold (milliseconds).')
param p95DurationThresholdMs int = 5000

@description('Absolute FailedTasksCount threshold over the evaluation window.')
param failedTasksThreshold int = 10

@description('Failure-rate threshold (percent) over the evaluation window.')
param failureRatePercent int = 20

@description('LongRunningTasksCount threshold over the evaluation window.')
param longRunningThreshold int = 20

@description('Executions-per-window burst guardrail (count).')
param executionBurstThreshold int = 5000

// -------------------------------------------------------------------------------------
// Existing Application Insights component
// -------------------------------------------------------------------------------------
resource appInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: appInsightsName
}

var hasWebhook = !empty(teamsWebhookUrl)

// -------------------------------------------------------------------------------------
// Action Group — the notification channel
// -------------------------------------------------------------------------------------
resource actionGroup 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: 'ag-jobrunner-oncall'
  location: 'global'
  properties: {
    groupShortName: 'JobRunner'
    enabled: true
    emailReceivers: [
      {
        name: 'oncall-email'
        emailAddress: alertEmail
        useCommonAlertSchema: true
      }
    ]
    webhookReceivers: hasWebhook ? [
      {
        name: 'teams-or-slack'
        serviceUri: teamsWebhookUrl
        useCommonAlertSchema: true
      }
    ] : []
  }
}

// -------------------------------------------------------------------------------------
// Helper: shared alert defaults
// -------------------------------------------------------------------------------------
var alertActions = {
  actionGroups: [ actionGroup.id ]
}

// =====================================================================================
// 1) CPU usage >= 70%  (Severity 2 — Warning)
// =====================================================================================
resource cpuAlert 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = {
  name: 'jobrunner-cpu-high'
  location: location
  kind: 'LogAlert'
  properties: {
    displayName: 'Job Runner — CPU >= ${cpuThresholdPercent}%'
    description: 'Process CPU at/above ${cpuThresholdPercent}%. First check: queue backlog & MaxConcurrentCalls, then scale out instances. Op-hint: confirm no hot-loop / retry storm.'
    severity: 2
    enabled: true
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    scopes: [ appInsights.id ]
    criteria: {
      allOf: [
        {
          query: 'performanceCounters\n| where name in ("% Processor Time", "cpu-usage")\n| where cloud_RoleName == "${roleName}"\n| summarize AggregatedValue = avg(value) by bin(timestamp, 5m), cloud_RoleInstance'
          timeAggregation: 'Average'
          metricMeasureColumn: 'AggregatedValue'
          dimensions: [ { name: 'cloud_RoleInstance', operator: 'Include', values: [ '*' ] } ]
          operator: 'GreaterThanOrEqual'
          threshold: cpuThresholdPercent
          failingPeriods: { numberOfEvaluationPeriods: 3, minFailingPeriodsToAlert: 2 }
        }
      ]
    }
    autoMitigate: true
    actions: alertActions
  }
}

// =====================================================================================
// 2) Memory >= 85%  (Severity 2 — Warning)
// =====================================================================================
resource memoryAlert 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = {
  name: 'jobrunner-memory-high'
  location: location
  kind: 'LogAlert'
  properties: {
    displayName: 'Job Runner — Memory >= ${memoryThresholdPercent}%'
    description: 'Process working-set at/above ${memoryThresholdPercent}%. First check: managed-memory growth / leak, large message batches; then scale up. Op-hint: review GC heap size counter.'
    severity: 2
    enabled: true
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    scopes: [ appInsights.id ]
    criteria: {
      allOf: [
        {
          query: 'performanceCounters\n| where name in ("% Committed Bytes In Use", "working-set")\n| where cloud_RoleName == "${roleName}"\n| summarize AggregatedValue = avg(value) by bin(timestamp, 5m), cloud_RoleInstance'
          timeAggregation: 'Average'
          metricMeasureColumn: 'AggregatedValue'
          dimensions: [ { name: 'cloud_RoleInstance', operator: 'Include', values: [ '*' ] } ]
          operator: 'GreaterThanOrEqual'
          threshold: memoryThresholdPercent
          failingPeriods: { numberOfEvaluationPeriods: 3, minFailingPeriodsToAlert: 2 }
        }
      ]
    }
    autoMitigate: true
    actions: alertActions
  }
}

// =====================================================================================
// 3) Execution burst — # of executions per window  (Severity 3 — Informational guardrail)
// =====================================================================================
resource burstAlert 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = {
  name: 'jobrunner-execution-burst'
  location: location
  kind: 'LogAlert'
  properties: {
    displayName: 'Job Runner — execution burst > ${executionBurstThreshold}/15m'
    description: 'Unusually high number of executions. First check: upstream scheduler / poison-retry loop; confirm Service Bus delivery counts. Op-hint: compare with TasksProcessedPerHour trend.'
    severity: 3
    enabled: true
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    scopes: [ appInsights.id ]
    criteria: {
      allOf: [
        {
          query: 'customMetrics\n| where name == "TasksProcessedCount"\n| where cloud_RoleName == "${roleName}"\n| summarize AggregatedValue = sum(valueSum)'
          timeAggregation: 'Total'
          metricMeasureColumn: 'AggregatedValue'
          operator: 'GreaterThan'
          threshold: executionBurstThreshold
          failingPeriods: { numberOfEvaluationPeriods: 1, minFailingPeriodsToAlert: 1 }
        }
      ]
    }
    autoMitigate: true
    actions: alertActions
  }
}

// =====================================================================================
// 4) p95 execution duration > threshold  (Severity 2 — Warning)
// =====================================================================================
resource p95Alert 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = {
  name: 'jobrunner-duration-p95-high'
  location: location
  kind: 'LogAlert'
  properties: {
    displayName: 'Job Runner — p95 duration > ${p95DurationThresholdMs}ms'
    description: 'Execution latency degraded (p95). First check: downstream API latency, DB replica lag, throttling. Op-hint: correlate with LongRunningTasksCount tile.'
    severity: 2
    enabled: true
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    scopes: [ appInsights.id ]
    criteria: {
      allOf: [
        {
          query: 'customMetrics\n| where name == "JobExecutionDurationMs"\n| where cloud_RoleName == "${roleName}"\n| extend p95 = todouble(valueMax)\n| summarize AggregatedValue = percentile(p95, 95) by bin(timestamp, 5m)'
          timeAggregation: 'Average'
          metricMeasureColumn: 'AggregatedValue'
          operator: 'GreaterThan'
          threshold: p95DurationThresholdMs
          failingPeriods: { numberOfEvaluationPeriods: 3, minFailingPeriodsToAlert: 2 }
        }
      ]
    }
    autoMitigate: true
    actions: alertActions
  }
}

// =====================================================================================
// 5) Failed tasks — absolute count AND failure rate  (Severity 1 — Critical)
// =====================================================================================
resource failedTasksAlert 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = {
  name: 'jobrunner-failed-tasks-high'
  location: location
  kind: 'LogAlert'
  properties: {
    displayName: 'Job Runner — failed tasks > ${failedTasksThreshold} OR failure-rate > ${failureRatePercent}%'
    description: 'Failed executions exceeded threshold. First check: DLQ size & dead-letter reason, downstream API errors, recent deploy. Op-hint: inspect exceptions table for the role.'
    severity: 1
    enabled: true
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    scopes: [ appInsights.id ]
    criteria: {
      allOf: [
        {
          query: 'let win = 15m;\nlet failed = toscalar(customMetrics | where name == "FailedTasksCount" and cloud_RoleName == "${roleName}" | summarize sum(valueSum));\nlet total  = toscalar(customMetrics | where name == "TasksProcessedCount" and cloud_RoleName == "${roleName}" | summarize sum(valueSum));\nprint Failed = coalesce(failed, 0.0), Total = coalesce(total, 0.0)\n| extend FailureRate = iff(Total > 0, 100.0 * Failed / Total, 0.0)\n| where Failed > ${failedTasksThreshold} or FailureRate > ${failureRatePercent}\n| summarize AggregatedValue = max(Failed)'
          timeAggregation: 'Maximum'
          metricMeasureColumn: 'AggregatedValue'
          operator: 'GreaterThan'
          threshold: 0
          failingPeriods: { numberOfEvaluationPeriods: 1, minFailingPeriodsToAlert: 1 }
        }
      ]
    }
    autoMitigate: true
    actions: alertActions
  }
}

// =====================================================================================
// 6) Long-running tasks > N  (Severity 2 — Warning)
// =====================================================================================
resource longRunningAlert 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = {
  name: 'jobrunner-long-running-high'
  location: location
  kind: 'LogAlert'
  properties: {
    displayName: 'Job Runner — long-running tasks > ${longRunningThreshold}/15m'
    description: 'Too many tasks exceeded the long-running threshold. First check: downstream dependency slowness, lock contention, large payloads. Op-hint: review p95/p99 duration tiles.'
    severity: 2
    enabled: true
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    scopes: [ appInsights.id ]
    criteria: {
      allOf: [
        {
          query: 'customMetrics\n| where name == "LongRunningTasksCount"\n| where cloud_RoleName == "${roleName}"\n| summarize AggregatedValue = sum(valueSum) by bin(timestamp, 5m)'
          timeAggregation: 'Total'
          metricMeasureColumn: 'AggregatedValue'
          operator: 'GreaterThan'
          threshold: longRunningThreshold
          failingPeriods: { numberOfEvaluationPeriods: 2, minFailingPeriodsToAlert: 2 }
        }
      ]
    }
    autoMitigate: true
    actions: alertActions
  }
}

output actionGroupId string = actionGroup.id
output alertRuleNames array = [
  cpuAlert.name
  memoryAlert.name
  burstAlert.name
  p95Alert.name
  failedTasksAlert.name
  longRunningAlert.name
]
