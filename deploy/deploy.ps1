#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Makes an environment run a release of the site.

.DESCRIPTION
    The system's pipeline runs this in every environment, signed in to Azure as the tier's deploy identity (ADR-0007;
    the contract is the demo-environment-kit's "An application that brings its own runtime").

    settings.json beside this file says where the site runs in each environment: its regions, and whether an Azure
    Front Door stands in front of them (ADR-0008). For the environment asked for, this script
      1. makes sure every region has its Container Apps express environment, cae-<system>-<environment>-<code>.
         A template deployment cannot create one in this subscription: its validation counts an express environment
         against the limits of standard ones and refuses it, while the service accepts the request itself. So each is
         created with one direct request, and left as it is when it exists;
      2. applies infra/main.bicep as the deployment stack stack-<system>-<environment>-web in the tier's resource
         group: one container app per region running <registry>/<system>/web:<version>, and the Front Door. The
         stack denies changes by anyone but the deploy identity, and removes what leaves the template.

    -Context is a JSON file from the pipeline: system, resourceGroup, registryServer and deployPrincipalId are read.
    Exit code 0 on success. Nothing is written to standard error on success: the Azure CLI's own notices are kept
    and shown only when it fails.
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
$settings = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'settings.json') -Raw | ConvertFrom-Json -AsHashtable
if (-not $settings.environments.ContainsKey($Environment)) {
    Write-Host "FAIL settings.json says nothing about the environment '$Environment'"
    exit 1
}
$place = $settings.environments[$Environment]
$system = [string] $facts.system
$resourceGroup = [string] $facts.resourceGroup
$subscription = ([string] (az account show --query id --output tsv)).Trim()
$group = "/subscriptions/$subscription/resourceGroups/$resourceGroup"
$pullIdentityName = ([string] $settings.pullIdentity).Replace('{system}', $system).Replace('{environment}', $Environment)

# 1. The express environment of every region.
# Always a list: an environment with one region would otherwise be a single table, and its Count the table's keys.
$regions = @(foreach ($region in @($place.regions)) {
        @{
            location             = [string] $region.location
            code                 = [string] $region.code
            managedEnvironmentId = "$group/providers/Microsoft.App/managedEnvironments/cae-$system-$Environment-$($region.code)"
        }
    })
# The provisioning state and the mode of an environment; nothing when it does not exist or cannot be read yet.
# A function gives its caller nothing at all for an empty list, so every caller wraps the call in @( ).
function Get-EnvironmentState {
    param([string] $Id)
    $PSNativeCommandUseErrorActionPreference = $false
    $state = az rest --method get --url "https://management.azure.com${Id}?api-version=2026-07-01" --query '[properties.provisioningState, properties.environmentMode]' --output tsv 2>$null
    $PSNativeCommandUseErrorActionPreference = $true
    return @($state | Where-Object { $_ })
}
foreach ($region in $regions) {
    $found = @(Get-EnvironmentState -Id $region.managedEnvironmentId)
    if ($found.Count -ge 2 -and $found[1] -ne 'Express') {
        Write-Host "FAIL $($region.managedEnvironmentId) exists and is not an express environment ($($found[1]))"
        exit 1
    }
    if ($found.Count -eq 0) {
        $bodyFile = Join-Path ([IO.Path]::GetTempPath()) "express-$([Guid]::NewGuid().ToString('N')).json"
        @{ location = $region.location; tags = @{ system = $system; application = 'web'; stage = $Environment }; properties = @{ environmentMode = 'Express' } } |
            ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $bodyFile -Encoding utf8NoBOM
        try { az rest --method put --url "https://management.azure.com$($region.managedEnvironmentId)?api-version=2026-07-01" --body "@$bodyFile" --output none }
        finally { Remove-Item -LiteralPath $bodyFile -Force -ErrorAction SilentlyContinue }
        Write-Host "Creating the express environment of $($region.location) ($($region.code))"
    }
}
$deadline = (Get-Date).AddMinutes(20)
$waiting = @($regions)
while ($waiting.Count -gt 0) {
    $waiting = @(foreach ($region in $waiting) {
            # Right after the request an environment may not be readable yet: no answer counts as "not yet".
            $found = @(Get-EnvironmentState -Id $region.managedEnvironmentId)
            $state = if ($found.Count -gt 0) { [string] $found[0] } else { '' }
            if ($state -in 'Failed', 'Canceled') {
                Write-Host "FAIL the express environment of $($region.location) ended $state"
                exit 1
            }
            if ($state -ne 'Succeeded') { $region }
        })
    if ($waiting.Count -gt 0) {
        if ((Get-Date) -gt $deadline) {
            Write-Host "FAIL after 20 minutes these express environments are not ready: $(@($waiting | ForEach-Object { $_.location }) -join ', ')"
            exit 1
        }
        Start-Sleep -Seconds 10
    }
}
Write-Host "PASS express environments: $(@($regions | ForEach-Object { $_.code }) -join ', ')"

# 2. The apps, and the Front Door, as one stack.
$stack = "stack-$system-$Environment-web"
$frontDoor = [bool] $place.frontDoor
Write-Host "Applying $stack in ${resourceGroup}: $($regions.Count) region(s) run $($facts.registryServer)/$system/web:$Version$(if ($frontDoor) { ', behind Front Door' })"
$parametersFile = Join-Path ([IO.Path]::GetTempPath()) "parameters-$stack-$([Guid]::NewGuid().ToString('N')).json"
@{
    '$schema'      = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'
    contentVersion = '1.0.0.0'
    parameters     = @{
        system          = @{ value = $system }
        environmentName = @{ value = $Environment }
        version         = @{ value = $Version }
        registryServer  = @{ value = [string] $facts.registryServer }
        pullIdentityId  = @{ value = "$group/providers/Microsoft.ManagedIdentity/userAssignedIdentities/$pullIdentityName" }
        regions         = @{ value = @($regions) }
        frontDoor       = @{ value = $frontDoor }
        port            = @{ value = [int] $settings.port }
    }
} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $parametersFile -Encoding utf8NoBOM

$notices = Join-Path ([IO.Path]::GetTempPath()) "deploy-$stack-$([Guid]::NewGuid().ToString('N')).log"
$PSNativeCommandUseErrorActionPreference = $false
az stack group create `
    --name $stack `
    --resource-group $resourceGroup `
    --template-file (Join-Path $PSScriptRoot 'infra' 'main.bicep') `
    --parameters "@$parametersFile" `
    --action-on-unmanage deleteResources `
    --deny-settings-mode denyWriteAndDelete `
    --deny-settings-excluded-principals $facts.deployPrincipalId `
    --yes `
    --output none 2>$notices
$exitCode = $LASTEXITCODE
$PSNativeCommandUseErrorActionPreference = $true
Remove-Item -LiteralPath $parametersFile -Force -ErrorAction SilentlyContinue

if ($exitCode -ne 0) {
    Write-Host "FAIL $stack was not applied (exit code $exitCode):"
    Get-Content -LiteralPath $notices | ForEach-Object { Write-Host "  $_" }
    # An express environment says why an app could not start in the app's deploymentErrors.
    foreach ($region in $regions) {
        $app = "ca-$system-$Environment-web-$($region.code)"
        $PSNativeCommandUseErrorActionPreference = $false
        $errors = az rest --method get --url "https://management.azure.com$group/providers/Microsoft.App/containerApps/${app}?api-version=2026-07-01" --query properties.deploymentErrors --output tsv 2>$null
        $PSNativeCommandUseErrorActionPreference = $true
        if ($errors) { Write-Host "  $app reports: $errors" }
    }
    Remove-Item -LiteralPath $notices -Force -ErrorAction SilentlyContinue
    exit 1
}
Remove-Item -LiteralPath $notices -Force -ErrorAction SilentlyContinue
Write-Host "PASS ${stack}: release $Version in $(@($regions | ForEach-Object { $_.code }) -join ', ')"
