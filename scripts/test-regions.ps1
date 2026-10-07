#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Asks Azure which regions accept a Container Apps express environment for this subscription.

.DESCRIPTION
    Azure refuses some regions for some subscriptions ("The selected region is currently not accepting new
    customers"), and says so only when asked to create something there: no quota or list shows it. The first
    deployment to uat with two regions failed that way, on West Europe (ADR-0008).

    For every location asked about, this script requests an express environment named cae-probe-<location> in the
    resource group given, notes whether Azure accepted the request, and deletes what it created. An express
    environment without an app costs nothing. Run it, signed in to Azure, before a region is added to
    deploy/settings.json.

    Without -Location it asks about every location in deploy/settings.json.
    Exit code 0 when every location was accepted and every probe is gone, 1 otherwise.

.EXAMPLE
    scripts/test-regions.ps1 -ResourceGroup rg-jpcom-nonprod -Location northeurope, swedencentral
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory)] [string] $ResourceGroup,
    # One or more, separated by commas, spaces or both: from a shell every way of writing a list arrives differently,
    # and what follows the first location lands in $More.
    [string[]] $Location = @(),
    [Parameter(ValueFromRemainingArguments)] [string[]] $More = @(),
    # How long to wait for the probes to be deleted.
    [int] $TimeoutSeconds = 1800
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$ProgressPreference = 'SilentlyContinue'

$Location = @(@($Location) + @($More) | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if ($Location.Count -eq 0) {
    $settings = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..' 'deploy' 'settings.json') -Raw | ConvertFrom-Json -AsHashtable
    $Location = @($settings.environments.Values | ForEach-Object { $_.regions } | ForEach-Object { [string] $_.location } | Sort-Object -Unique)
}
$subscription = ([string] (az account show --query id --output tsv)).Trim()
$group = "/subscriptions/$subscription/resourceGroups/$ResourceGroup"
function Get-ProbeUrl {
    param([string] $Place)
    return "https://management.azure.com$group/providers/Microsoft.App/managedEnvironments/cae-probe-${Place}?api-version=2026-07-01"
}

$accepted = @()
$refused = @()
foreach ($place in $Location) {
    $bodyFile = Join-Path ([IO.Path]::GetTempPath()) "probe-$([Guid]::NewGuid().ToString('N')).json"
    @{ location = $place; tags = @{ purpose = 'region probe, safe to delete' }; properties = @{ environmentMode = 'Express' } } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $bodyFile -Encoding utf8NoBOM
    $PSNativeCommandUseErrorActionPreference = $false
    $answer = az rest --method put --url (Get-ProbeUrl $place) --body "@$bodyFile" --output none 2>&1
    $ok = $LASTEXITCODE -eq 0
    $PSNativeCommandUseErrorActionPreference = $true
    Remove-Item -LiteralPath $bodyFile -Force -ErrorAction SilentlyContinue
    if ($ok) {
        $accepted += $place
        Write-Host "PASS $place accepts an express environment"
    }
    else {
        $refused += $place
        Write-Host "FAIL $place refuses: $((@($answer) | ForEach-Object { [string] $_ }) -join ' ')"
    }
}

# Delete the probes. One that is still being created refuses the delete, so ask again until it is gone.
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$left = @($accepted)
while ($left.Count -gt 0) {
    $left = @(foreach ($place in $left) {
            $PSNativeCommandUseErrorActionPreference = $false
            az rest --method delete --url (Get-ProbeUrl $place) --output none 2>$null
            $found = az rest --method get --url (Get-ProbeUrl $place) --query 'properties.provisioningState' --output tsv 2>$null
            $PSNativeCommandUseErrorActionPreference = $true
            if (@($found | Where-Object { $_ }).Count -gt 0) { $place }
        })
    if ($left.Count -gt 0) {
        if ((Get-Date) -gt $deadline) {
            Write-Host "FAIL after $TimeoutSeconds seconds these probes are still in ${ResourceGroup}: $(@($left | ForEach-Object { "cae-probe-$_" }) -join ', '). Delete them by hand."
            exit 1
        }
        Start-Sleep -Seconds 15
    }
}
if ($accepted.Count -gt 0) { Write-Host "Deleted the probes of: $($accepted -join ', ')" }
Write-Host "$($accepted.Count) accepted, $($refused.Count) refused$(if ($refused.Count -gt 0) { ": $($refused -join ', ')" })"
exit ($refused.Count -gt 0 ? 1 : 0)
