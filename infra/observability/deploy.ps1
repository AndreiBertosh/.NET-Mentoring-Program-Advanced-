# =====================================================================================
# Deploys Job Runner observability (alerts + workbook) to Azure.
# Requires: Azure CLI (az) logged in, and the Application Insights component to exist.
# Usage:
#   ./deploy.ps1 -ResourceGroup rg-jobs -AppInsightsName ai-jobs -AlertEmail you@example.com
# =====================================================================================
param(
    [Parameter(Mandatory = $true)] [string] $ResourceGroup,
    [Parameter(Mandatory = $true)] [string] $AppInsightsName,
    [Parameter(Mandatory = $true)] [string] $AlertEmail,
    [string] $TeamsWebhookUrl = '',
    [string] $RoleName = 'ReplicationDemo.Worker'
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "Deploying alerts..." -ForegroundColor Cyan
az deployment group create `
    --resource-group $ResourceGroup `
    --name 'jobrunner-observability-alerts' `
    --template-file (Join-Path $here 'observability.bicep') `
    --parameters `
        appInsightsName=$AppInsightsName `
        roleName=$RoleName `
        alertEmail=$AlertEmail `
        teamsWebhookUrl=$TeamsWebhookUrl

Write-Host "Deploying workbook (dashboard)..." -ForegroundColor Cyan
az deployment group create `
    --resource-group $ResourceGroup `
    --name 'jobrunner-observability-workbook' `
    --template-file (Join-Path $here 'workbook.bicep') `
    --parameters `
        appInsightsName=$AppInsightsName `
        roleName=$RoleName

Write-Host "Done." -ForegroundColor Green
