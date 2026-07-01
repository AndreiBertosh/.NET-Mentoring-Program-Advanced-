// =====================================================================================
// DEMO-ONLY alert — guarantees a non-zero "Fired" count in the Azure portal.
// -------------------------------------------------------------------------------------
// Purpose: validate the alert -> action group -> email pipeline WITHOUT depending on
// application telemetry (useful when the Worker isn't emitting metrics yet).
//
// The KQL `print AggregatedValue = 1` always returns 1, so with threshold 0 the rule
// fires on its first evaluation (~1-2 min). It is wired to the SAME action group as the
// real rules, so the on-call email is also exercised.
//
// REMOVE AFTER THE DEMO:
//   az monitor scheduled-query delete -g <rg> -n jobrunner-demo-always-fires
// or set `enabled: false` and redeploy.
// =====================================================================================

@description('Name of the EXISTING Application Insights component to attach the alert to.')
param appInsightsName string

@description('Azure region for the alert rule (use the AI component region).')
param location string = resourceGroup().location

@description('Name of the EXISTING action group to notify (created by observability.bicep).')
param actionGroupName string = 'ag-jobrunner-oncall'

resource appInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: appInsightsName
}

resource actionGroup 'Microsoft.Insights/actionGroups@2023-01-01' existing = {
  name: actionGroupName
}

resource demoAlert 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = {
  name: 'jobrunner-demo-always-fires'
  location: location
  kind: 'LogAlert'
  properties: {
    displayName: 'Job Runner - DEMO (always fires)'
    description: 'Synthetic demo alert that always fires to validate the alert -> action group -> email pipeline. Disable or delete after the demo.'
    severity: 3
    enabled: true
    evaluationFrequency: 'PT1M'
    windowSize: 'PT5M'
    scopes: [ appInsights.id ]
    criteria: {
      allOf: [
        {
          query: 'customMetrics\n| summarize AggregatedValue = todouble(count()) + 1'
          timeAggregation: 'Total'
          metricMeasureColumn: 'AggregatedValue'
          operator: 'GreaterThan'
          threshold: 0
          failingPeriods: { numberOfEvaluationPeriods: 1, minFailingPeriodsToAlert: 1 }
        }
      ]
    }
    autoMitigate: false
    actions: {
      actionGroups: [ actionGroup.id ]
    }
  }
}

output demoAlertName string = demoAlert.name
