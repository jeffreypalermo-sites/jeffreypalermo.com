#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Verifies that an environment runs a release of the site.

.DESCRIPTION
    The system's pipeline runs this after deploy.ps1, in every environment, signed in to Azure as the tier's deploy
    identity (ADR-0007; the contract is the demo-environment-kit's "An application that brings its own runtime").
    It finds the environment's container app and waits until its /_health/ready answers as the release
    (test-site.ps1). Exit code 0: the environment runs the release.
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
$app = "ca-$($facts.system)-$Environment-web"
$fqdn = ([string] (az containerapp show --name $app --resource-group $facts.resourceGroup --query properties.configuration.ingress.fqdn --output tsv)).Trim()
if (-not $fqdn) {
    Write-Host "FAIL $app has no public address in $($facts.resourceGroup)"
    exit 1
}

& (Join-Path $PSScriptRoot 'test-site.ps1') -BaseUrl "https://$fqdn" -Version $Version
exit $LASTEXITCODE
