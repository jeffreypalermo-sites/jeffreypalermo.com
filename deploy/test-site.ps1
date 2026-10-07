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
    [int] $TimeoutSeconds = 600
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$uri = "$($BaseUrl.TrimEnd('/'))/_health/ready"
$expected = "ready $Version"
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$last = 'no answer yet'
while ($true) {
    try {
        $response = Invoke-WebRequest -Uri $uri -TimeoutSec 20 -SkipHttpErrorCheck
        $last = "$($response.StatusCode) '$(([string] $response.Content).Trim())'"
        if ($response.StatusCode -eq 200 -and ([string] $response.Content).Trim() -eq $expected) {
            Write-Host "PASS $uri answers '$expected'"
            break
        }
    }
    catch {
        $last = $_.Exception.Message
    }
    if ((Get-Date) -ge $deadline) {
        Write-Host "FAIL $uri did not answer '$expected' within $TimeoutSeconds seconds; last: $last"
        exit 1
    }
    Start-Sleep -Seconds 5
}

$verifier = Join-Path $PSScriptRoot 'bin' 'JeffreyPalermo.Tools.UrlContract'
if (-not (Test-Path -LiteralPath $verifier)) {
    exit 0
}
# The verifier prints each violation and a summary line, and exits 1 on any violation.
& $verifier verify "$($BaseUrl.TrimEnd('/'))/" (Join-Path $PSScriptRoot 'contract' 'url-contract.tsv') (Join-Path $PSScriptRoot 'contract' 'exceptions.tsv')
if ($LASTEXITCODE -ne 0) {
    Write-Host "FAIL $BaseUrl breaks the URL contract as release $Version"
    exit 1
}
Write-Host "PASS $BaseUrl keeps the URL contract as release $Version"
exit 0
