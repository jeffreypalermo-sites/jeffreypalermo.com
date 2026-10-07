#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Verifies that an environment runs a release of the site.

.DESCRIPTION
    The system's pipeline runs this after deploy.ps1, in every environment, signed in to Azure as the tier's deploy
    identity (ADR-0007; the contract is the demo-environment-kit's "An application that brings its own runtime").
    From the outputs of the site's stack it checks, with test-site.ps1:
      - every region's app directly: /_health/ready answers as the release;
      - the URL contract against the first region's app;
      - with a Front Door (ADR-0008): its address answers as the release several times in a row, so every region
        in the rotation was asked, and then the whole URL contract through it. A new or changed Front Door takes
        some minutes to serve: the wait allows for that.
    Exit code 0: the environment runs the release, in every region and through its front door.
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
$stack = "stack-$($facts.system)-$Environment-web"
$outputs = (az stack group show --name $stack --resource-group $facts.resourceGroup --output json | ConvertFrom-Json -AsHashtable).outputs
$regions = @($outputs.regions.value)
$frontDoorUrl = [string] $outputs.frontDoorUrl.value
if ($regions.Count -eq 0) {
    Write-Host "FAIL $stack lists no region"
    exit 1
}
$testSite = Join-Path $PSScriptRoot 'test-site.ps1'

$first = $true
foreach ($region in $regions) {
    Write-Host "==> $($region.code) ($($region.location))"
    if ($first) { & $testSite -BaseUrl $region.url -Version $Version }
    else { & $testSite -BaseUrl $region.url -Version $Version -SkipContract }
    if ($LASTEXITCODE -ne 0) { exit 1 }
    $first = $false
}

if ($frontDoorUrl) {
    Write-Host "==> Front Door"
    # Twice around the rotation, so no region still answers as an older release; up to 30 minutes for a new profile.
    & $testSite -BaseUrl $frontDoorUrl -Version $Version -Consecutive ($regions.Count * 2) -TimeoutSeconds 1800
    if ($LASTEXITCODE -ne 0) { exit 1 }
}
Write-Host "PASS $Environment runs release $Version in $($regions.Count) region(s)$(if ($frontDoorUrl) { " and through $frontDoorUrl" })"
exit 0
