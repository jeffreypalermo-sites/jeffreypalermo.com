#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Waits until a running site answers as the given release.

.DESCRIPTION
    Asks <BaseUrl>/_health/ready until it answers 200 with "ready <Version>": the site is up, has loaded its content,
    and is the release that was deployed, not the one before it. An environment that scaled to zero, or a new
    revision that is still starting, answers late: the wait allows for that.

    In the deploy package (scripts/build-deploy-package.sh) the contract verifier and the URL contract lie beside
    this file, in bin/ and contract/. Then it also replays the whole URL contract against the site: a release that
    breaks a legacy URL does not pass. In the repository, without them, the health check is the whole check.

    Exit code 0 when everything asked passes, 1 otherwise. verify.ps1 uses it for a deployed environment; the
    full-system tests run it against the container.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $BaseUrl,
    [Parameter(Mandatory)] [string] $Version,
    [int] $TimeoutSeconds = 600,
    # How many answers in a row must be right. Behind a front door that rotates over regions, more than one: each
    # request may reach another region, and none may still run the release before.
    [int] $Consecutive = 1,
    # The health check only, also where the contract verifier is at hand.
    [switch] $SkipContract
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$uri = "$($BaseUrl.TrimEnd('/'))/_health/ready"
$expected = "ready $Version"
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$last = 'no answer yet'
$right = 0
while ($true) {
    $matched = $false
    try {
        $response = Invoke-WebRequest -Uri $uri -TimeoutSec 60 -SkipHttpErrorCheck
        $last = "$($response.StatusCode) '$(([string] $response.Content).Trim())'"
        $matched = $response.StatusCode -eq 200 -and ([string] $response.Content).Trim() -eq $expected
    }
    catch {
        $last = $_.Exception.Message
    }
    $right = if ($matched) { $right + 1 } else { 0 }
    if ($right -ge $Consecutive) {
        Write-Host "PASS $uri answers '$expected'$(if ($Consecutive -gt 1) { " $Consecutive times in a row" })"
        break
    }
    if ((Get-Date) -ge $deadline) {
        Write-Host "FAIL $uri did not answer '$expected'$(if ($Consecutive -gt 1) { " $Consecutive times in a row" }) within $TimeoutSeconds seconds; last: $last"
        exit 1
    }
    if (-not $matched) { Start-Sleep -Seconds 5 }
}

$verifier = Join-Path $PSScriptRoot 'bin' 'JeffreyPalermo.Tools.UrlContract'
if ($SkipContract -or -not (Test-Path -LiteralPath $verifier)) {
    exit 0
}
# The package reaches the pipeline as a build artifact and then a zip, and neither keeps a file's execute permission.
if (-not $IsWindows) { chmod +x $verifier }
# The verifier prints each violation and a summary line, and exits 1 on any violation.
& $verifier verify "$($BaseUrl.TrimEnd('/'))/" (Join-Path $PSScriptRoot 'contract' 'url-contract.tsv') (Join-Path $PSScriptRoot 'contract' 'exceptions.tsv')
if ($LASTEXITCODE -ne 0) {
    Write-Host "FAIL $BaseUrl breaks the URL contract as release $Version"
    exit 1
}
Write-Host "PASS $BaseUrl keeps the URL contract as release $Version"
exit 0
