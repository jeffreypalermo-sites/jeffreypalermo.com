#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Verifies that an environment runs a release of the site.

.DESCRIPTION
    The system's pipeline runs this after deploy.ps1, in every environment, signed in to Azure as the tier's deploy
    identity (ADR-0007; the contract is the demo-environment-kit's "An application that brings its own runtime").
    From the outputs of the site's stack it checks, with test-site.ps1:
      - every region's app directly: /_health/ready answers as the release, and the home page is the release's;
      - the URL contract against the first region's app;
      - with a Front Door (ADR-0008): its address answers as the release several times in a row, so every region
        in the rotation was asked, and then the whole URL contract through it. A new or changed Front Door takes
        some minutes to serve: the wait allows for that.
        The Front Door keeps the site's pages in its cache (ADR-0013), which deploy.ps1 emptied after it applied the
        release. The health answer is never kept, so it still comes from the regions; one that a cache gave does
        not count. The home page through the Front Door must name the release: while the edge that answers still
        holds the page of the release before, the check waits, and fails when the wait is over.
    Exit code 0: the environment runs the release, in every region and through its front door.

    When the context names a nodesFile, the script then writes there what the environment runs on: every region's
    app, the Front Door address, and the paths that answer for health, liveness and version. The pipeline records
    it in the system repository, and the system's health dashboard shows a tile for each (ADR-0011).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Environment,
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $Context,
    # How long a region may take to answer as the release, and how long the Front Door may. The pipeline passes
    # neither.
    [int] $TimeoutSeconds = 600,
    [int] $FrontDoorTimeoutSeconds = 1800
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
    if ($first) { & $testSite -BaseUrl $region.url -Version $Version -TimeoutSeconds $TimeoutSeconds }
    else { & $testSite -BaseUrl $region.url -Version $Version -TimeoutSeconds $TimeoutSeconds -SkipContract }
    if ($LASTEXITCODE -ne 0) { exit 1 }
    $first = $false
}

if ($frontDoorUrl) {
    Write-Host "==> Front Door"
    # Twice around the rotation, so no region still answers as an older release; up to 30 minutes for a new profile.
    & $testSite -BaseUrl $frontDoorUrl -Version $Version -Consecutive ($regions.Count * 2) -TimeoutSeconds $FrontDoorTimeoutSeconds
    if ($LASTEXITCODE -ne 0) { exit 1 }
}

# The system's health dashboard shows a tile per node (ADR-0011). Only the site knows its nodes, so it reports them
# where the pipeline asks (nodesFile in the context), and the pipeline records them in the system repository.
$nodesFile = if ($facts.PSObject.Properties['nodesFile']) { [string] $facts.nodesFile } else { '' }
if ($nodesFile) {
    [ordered] @{
        frontDoor    = if ($frontDoorUrl) { $frontDoorUrl.TrimEnd('/') } else { $null }
        healthPath   = '/_health/ready'
        alivePath    = '/_health/live'
        versionPath  = '/_version'
        # The system's hourly health report would wake every region that has scaled to zero (ADR-0008): it stays away.
        healthReport = $false
        # Every region serves in the rotation: none is a standby.
        nodes        = @($regions | ForEach-Object {
                [ordered] @{ name = [string] $_.app; region = [string] $_.location; role = 'primary'; url = ([string] $_.url).TrimEnd('/') }
            })
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $nodesFile -Encoding utf8NoBOM
    Write-Host "Reported $($regions.Count) node(s)$(if ($frontDoorUrl) { ' and the Front Door' }) for the system's dashboard"
}
Write-Host "PASS $Environment runs release $Version in $($regions.Count) region(s)$(if ($frontDoorUrl) { " and through $frontDoorUrl" })"
exit 0
