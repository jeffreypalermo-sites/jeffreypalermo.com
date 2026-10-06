#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Makes an environment run a release of the site.

.DESCRIPTION
    The system's pipeline runs this in every environment, signed in to Azure as the tier's deploy identity (ADR-0007;
    the contract is the demo-environment-kit's "An application that brings its own runtime"). It applies
    infra/main.bicep as the deployment stack stack-<system>-<environment>-web in the tier's resource group: the
    container app, running the image <registry>/<system>/web:<version>. The stack denies changes by anyone but the
    deploy identity, so this script is the only way the app changes. The first run in an environment creates the
    app; every later one updates it in place.

    -Context is a JSON file from the pipeline: system, resourceGroup, registryServer and deployPrincipalId are read.
    settings.json beside this file says where the runtime lives. Exit code 0 on success. Nothing is written to
    standard error on success: the Azure CLI's own notices are kept and shown only when it fails.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Environment,
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $Context
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$ProgressPreference = 'SilentlyContinue'

$facts = Get-Content -LiteralPath $Context -Raw | ConvertFrom-Json
$settings = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'settings.json') -Raw | ConvertFrom-Json
$system = [string] $facts.system
$resourceGroup = [string] $facts.resourceGroup
$subscription = ([string] (az account show --query id --output tsv)).Trim()

$managedEnvironmentId = "/subscriptions/$subscription/resourceGroups/$($settings.containerAppsEnvironment.resourceGroup)/providers/Microsoft.App/managedEnvironments/$($settings.containerAppsEnvironment.name)"
$pullIdentityName = ([string] $settings.pullIdentity).Replace('{environment}', $Environment)
$pullIdentityId = "/subscriptions/$subscription/resourceGroups/$resourceGroup/providers/Microsoft.ManagedIdentity/userAssignedIdentities/$pullIdentityName"

$stack = "stack-$system-$Environment-web"
$app = "ca-$system-$Environment-web"
Write-Host "Applying $stack in ${resourceGroup}: $app runs $($facts.registryServer)/$system/web:$Version"

$notices = Join-Path ([IO.Path]::GetTempPath()) "deploy-$stack-$([Guid]::NewGuid().ToString('N')).log"
$PSNativeCommandUseErrorActionPreference = $false
az stack group create `
    --name $stack `
    --resource-group $resourceGroup `
    --template-file (Join-Path $PSScriptRoot 'infra' 'main.bicep') `
    --parameters system=$system environmentName=$Environment version=$Version `
    registryServer=$($facts.registryServer) managedEnvironmentId=$managedEnvironmentId pullIdentityId=$pullIdentityId port=$($settings.port) `
    --action-on-unmanage deleteResources `
    --deny-settings-mode denyWriteAndDelete `
    --deny-settings-excluded-principals $facts.deployPrincipalId `
    --yes `
    --output none 2>$notices
$exitCode = $LASTEXITCODE
$PSNativeCommandUseErrorActionPreference = $true

if ($exitCode -ne 0) {
    Write-Host "FAIL $stack was not applied (exit code $exitCode):"
    Get-Content -LiteralPath $notices | ForEach-Object { Write-Host "  $_" }
    # An express environment says why an app could not start in the app's deploymentErrors.
    $PSNativeCommandUseErrorActionPreference = $false
    $errors = az rest --method get --url "https://management.azure.com/subscriptions/$subscription/resourceGroups/$resourceGroup/providers/Microsoft.App/containerApps/${app}?api-version=2026-07-01" --query properties.deploymentErrors --output tsv 2>$null
    $PSNativeCommandUseErrorActionPreference = $true
    if ($errors) { Write-Host "  $app reports: $errors" }
    Remove-Item -LiteralPath $notices -Force -ErrorAction SilentlyContinue
    exit 1
}
Remove-Item -LiteralPath $notices -Force -ErrorAction SilentlyContinue
Write-Host "PASS $app runs release $Version in $Environment"
