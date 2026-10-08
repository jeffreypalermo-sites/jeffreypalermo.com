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
         created with one direct request, and left as it is when it exists. A region Azure refuses stops the
         deployment before anything is applied, with every refused region named;
      2. applies infra/main.bicep as the deployment stack stack-<system>-<environment>-web in the tier's resource
         group: one container app per region running <registry>/<system>/web:<version>, and the Front Door. The
         stack denies changes by anyone but the deploy identity, and removes what leaves the template;
      3. where there is a Front Door, empties its cache (ADR-0013). The edge keeps the site's answers for days, so
         without this a reader would get pages of the release before. First every region must answer as the
         release at its own address: emptied sooner, the edge could fill again from a region that still runs the
         release before. Then the script asks Azure to purge everything and waits until Azure reports it done.

    A step against Azure that fails is run once more after a pause: the platform fails by itself at times (the first
    production deployment of the regions: "(500 InternalError): managed identity bootstrap failed"; hours later the
    same deployment passed). It is not run again when the error says the request itself is wrong or not allowed,
    which no second attempt changes. When the second attempt fails too, what Azure said both times is shown.

    -Context is a JSON file from the pipeline: system, resourceGroup, registryServer and deployPrincipalId are read.
    Exit code 0 on success. Nothing is written to standard error on success: the Azure CLI's own notices are kept
    and shown only when it fails.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Environment,
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $Context,
    # How long to wait before the second attempt at a step Azure failed, and how long a region may take to answer as
    # the release before the Front Door's cache is emptied. The pipeline passes neither.
    [int] $RetryPauseSeconds = 60,
    [int] $TimeoutSeconds = 600
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
$refused = @()
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
        $PSNativeCommandUseErrorActionPreference = $false
        $answer = az rest --method put --url "https://management.azure.com$($region.managedEnvironmentId)?api-version=2026-07-01" --body "@$bodyFile" --output none 2>&1
        $accepted = $LASTEXITCODE -eq 0
        $PSNativeCommandUseErrorActionPreference = $true
        Remove-Item -LiteralPath $bodyFile -Force -ErrorAction SilentlyContinue
        if ($accepted) {
            Write-Host "Creating the express environment of $($region.location) ($($region.code))"
        }
        else {
            $refused += "$($region.location) ($($region.code)): $((@($answer) | ForEach-Object { [string] $_ }) -join ' ')"
        }
    }
}
# Azure may refuse a region for this subscription (West Europe did: "not accepting new customers"), and says so
# only when asked to create something there. Every region is asked before this stops, so one run names them all.
if ($refused.Count -gt 0) {
    Write-Host "FAIL Azure refused the express environment of $($refused.Count) of $($regions.Count) region(s); nothing was deployed:"
    $refused | ForEach-Object { Write-Host "  $_" }
    Write-Host "  Take the region out of settings.json or put another in its place (scripts/test-regions.ps1 asks Azure which it accepts)."
    exit 1
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

# Runs the Azure CLI and keeps what it says. Nothing of it reaches this script's standard error: the CLI writes
# notices there also when it succeeds.
function Invoke-Az {
    param([Parameter(Mandatory)] [string[]] $Arguments)
    $saidFile = Join-Path ([IO.Path]::GetTempPath()) "az-$([Guid]::NewGuid().ToString('N')).log"
    $PSNativeCommandUseErrorActionPreference = $false
    $output = az @Arguments 2>$saidFile
    $exitCode = $LASTEXITCODE
    $PSNativeCommandUseErrorActionPreference = $true
    $said = @(if (Test-Path -LiteralPath $saidFile) { Get-Content -LiteralPath $saidFile })
    Remove-Item -LiteralPath $saidFile -Force -ErrorAction SilentlyContinue
    return [pscustomobject] @{
        ExitCode = $exitCode
        Output   = (@($output) | ForEach-Object { [string] $_ }) -join "`n"
        # What the CLI wrote to standard error: its notices, and its error when it failed.
        Said     = [string[]] @($said | ForEach-Object { [string] $_ })
    }
}

# The errors of Azure Resource Manager that no second attempt changes: the template or the request is wrong, or Azure
# does not allow it. Every other failure may be the platform's own, and is tried once more.
$errorsNoAttemptChanges = @(
    'InvalidTemplate', 'InvalidTemplateDeployment', 'InvalidDeploymentParameterValue', 'InvalidRequestContent',
    'RequestDisallowedByPolicy', 'RequestDisallowedByAzure', 'LocationNotAvailableForResourceType',
    'NoRegisteredProviderFound', 'MissingSubscriptionRegistration'
)
# The first such error in what the CLI said; an empty text when there is none.
function Find-ErrorNoAttemptChanges {
    param([string[]] $Said)
    $text = @($Said) -join "`n"
    foreach ($code in $errorsNoAttemptChanges) {
        # The code as a word of its own: "InvalidTemplate" is not found in "InvalidTemplateDeployment".
        if ($text -cmatch "(?<![A-Za-z])$code(?![A-Za-z])") { return $code }
    }
    # The Bicep file does not compile.
    if ($text -cmatch 'Error BCP\d+') { return $Matches[0] }
    return ''
}

# Runs a step against Azure. When it fails, and the error is not one that no attempt changes, waits and runs it once
# more. Gives every attempt back, so a failure can show what Azure said each time.
function Invoke-AzOnceMore {
    param([Parameter(Mandatory)] [string] $What, [Parameter(Mandatory)] [string[]] $Arguments)
    $attempts = @(Invoke-Az -Arguments $Arguments)
    $hopeless = ''
    if ($attempts[0].ExitCode -ne 0) {
        $hopeless = Find-ErrorNoAttemptChanges -Said $attempts[0].Said
        if (-not $hopeless) {
            Write-Host "$What failed (exit code $($attempts[0].ExitCode)). Azure said:"
            @($attempts[0].Said) | ForEach-Object { Write-Host "  $_" }
            Write-Host "Trying once more in $RetryPauseSeconds seconds: the platform fails by itself at times."
            Start-Sleep -Seconds $RetryPauseSeconds
            $attempts += @(Invoke-Az -Arguments $Arguments)
        }
    }
    return [pscustomobject] @{
        Succeeded             = $attempts[-1].ExitCode -eq 0
        Attempts              = $attempts
        ErrorNoAttemptChanges = $hopeless
    }
}

# Says why a step failed: one attempt that could not succeed, or two attempts with what Azure said each time.
function Write-Failure {
    param([Parameter(Mandatory)] [string] $What, [Parameter(Mandatory)] $Result)
    $attempts = @($Result.Attempts)
    if ($attempts.Count -eq 1) {
        Write-Host "FAIL $What (exit code $($attempts[0].ExitCode)). Not tried again: no second attempt changes $($Result.ErrorNoAttemptChanges)."
        @($attempts[0].Said) | ForEach-Object { Write-Host "  $_" }
        return
    }
    Write-Host "FAIL $What, in two attempts $RetryPauseSeconds seconds apart."
    Write-Host "  First attempt (exit code $($attempts[0].ExitCode)):"
    @($attempts[0].Said) | ForEach-Object { Write-Host "    $_" }
    Write-Host "  Second attempt (exit code $($attempts[1].ExitCode)):"
    @($attempts[1].Said) | ForEach-Object { Write-Host "    $_" }
}

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

$applied = Invoke-AzOnceMore -What "Applying $stack" -Arguments @(
    'stack', 'group', 'create',
    '--name', $stack,
    '--resource-group', $resourceGroup,
    '--template-file', (Join-Path $PSScriptRoot 'infra' 'main.bicep'),
    '--parameters', "@$parametersFile",
    '--action-on-unmanage', 'deleteResources',
    '--deny-settings-mode', 'denyWriteAndDelete',
    '--deny-settings-excluded-principals', [string] $facts.deployPrincipalId,
    '--yes',
    '--output', 'none'
)
Remove-Item -LiteralPath $parametersFile -Force -ErrorAction SilentlyContinue

if (-not $applied.Succeeded) {
    Write-Failure -What "$stack was not applied" -Result $applied
    # An express environment says why an app could not start in the app's deploymentErrors.
    foreach ($region in $regions) {
        $app = "ca-$system-$Environment-web-$($region.code)"
        $PSNativeCommandUseErrorActionPreference = $false
        $errors = az rest --method get --url "https://management.azure.com$group/providers/Microsoft.App/containerApps/${app}?api-version=2026-07-01" --query properties.deploymentErrors --output tsv 2>$null
        $PSNativeCommandUseErrorActionPreference = $true
        if ($errors) { Write-Host "  $app reports: $errors" }
    }
    exit 1
}
Write-Host "PASS ${stack}: release $Version in $(@($regions | ForEach-Object { $_.code }) -join ', ')$(if (@($applied.Attempts).Count -gt 1) { ', at the second attempt' })"
if (-not $frontDoor) { exit 0 }

# 3. The Front Door's cache (ADR-0013).
# One value of the stack's outputs; nothing when the stack does not have it. A list comes back as its items, so a
# caller that expects a list wraps the call in @( ).
function Get-StackOutput {
    param($Outputs, [Parameter(Mandatory)] [string] $Name)
    if ($Outputs -is [System.Collections.IDictionary] -and $Outputs.Contains($Name)) { return $Outputs[$Name].value }
}
$shown = Invoke-Az -Arguments @('stack', 'group', 'show', '--name', $stack, '--resource-group', $resourceGroup, '--output', 'json')
if ($shown.ExitCode -ne 0) {
    Write-Host "FAIL the outputs of $stack could not be read (exit code $($shown.ExitCode)); the Front Door's cache was not emptied:"
    @($shown.Said) | ForEach-Object { Write-Host "  $_" }
    exit 1
}
$outputs = ($shown.Output | ConvertFrom-Json -AsHashtable).outputs
$endpointId = [string] (Get-StackOutput -Outputs $outputs -Name 'frontDoorEndpointId')
$frontDoorUrl = [string] (Get-StackOutput -Outputs $outputs -Name 'frontDoorUrl')
$apps = @(Get-StackOutput -Outputs $outputs -Name 'regions')
if (-not $endpointId -or -not $frontDoorUrl -or $apps.Count -eq 0) {
    Write-Host "FAIL $stack does not name its regions and its Front Door endpoint; the Front Door's cache was not emptied"
    exit 1
}

# Every region first, at its own address. The stack is applied before every region has started the new revision,
# and a region that still runs the release before would fill the emptied edge with its pages again.
$testSite = Join-Path $PSScriptRoot 'test-site.ps1'
foreach ($app in $apps) {
    & $testSite -BaseUrl ([string] $app.url) -Version $Version -TimeoutSeconds $TimeoutSeconds -SkipContract
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAIL $($app.code) does not run release $Version; the Front Door's cache was not emptied"
        exit 1
    }
}

# Everything the endpoint keeps under its own host name: a purge names the paths and the domains it is for.
$domains = @(([Uri] $frontDoorUrl).Host)
$purgeFile = Join-Path ([IO.Path]::GetTempPath()) "purge-$stack-$([Guid]::NewGuid().ToString('N')).json"
@{ contentPaths = @('/*'); domains = @($domains) } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $purgeFile -Encoding utf8NoBOM
Write-Host "Emptying the Front Door's cache: /* of $($domains -join ', ')"
# "az afd endpoint purge" is not part of the Azure CLI itself (it comes with an extension the worker may not have).
# This is the same request, and the CLI waits until Azure reports the purge done.
$purged = Invoke-AzOnceMore -What "Emptying the Front Door's cache" -Arguments @(
    'resource', 'invoke-action',
    '--action', 'purge',
    '--ids', $endpointId,
    '--api-version', '2024-02-01',
    '--request-body', "@$purgeFile",
    '--output', 'none'
)
Remove-Item -LiteralPath $purgeFile -Force -ErrorAction SilentlyContinue
if (-not $purged.Succeeded) {
    Write-Failure -What "The Front Door's cache was not emptied" -Result $purged
    Write-Host "  Every region runs release $Version, but the edge may still give what it kept of the release before, for up to seven days."
    Write-Host "  Run the deployment again."
    exit 1
}
Write-Host "PASS the Front Door's cache is emptied: readers get release $Version$(if (@($purged.Attempts).Count -gt 1) { ' (at the second attempt)' })"
