#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Waits until a running site answers as the given release.

.DESCRIPTION
    Asks <BaseUrl>/_health/ready until it answers 200 with "ready <Version>": the site is up, has loaded its content,
    and is the release that was deployed, not the one before it. An environment that scaled to zero, or a new
    revision that is still starting, answers late: the wait allows for that. Exit code 0 when it does, 1 when the
    time is up. verify.ps1 uses it for a deployed environment; the full-system tests run it against the container.
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
            exit 0
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
