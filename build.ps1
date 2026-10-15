#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    The private build of the site: the one command a developer runs before a push, and the one the Build's job test
    runs (ADR-0027).

.DESCRIPTION
    pwsh ./build.ps1 runs, in this order, and stops at the first that fails:
      1. the compile of the solution in Release, which is the static analysis: the .NET analyzers run in it and a
         warning is an error (Directory.Build.props). Its log is TestResults/compile.msbuild.log;
      2. the unit tests, and 3. the integration tests, without a second compile, with their results (*.trx) and
         their coverage (*.opencover.xml) under TestResults.
    It empties TestResults first, so what is there afterwards is of this run alone. scripts/Write-BuildFacts.ps1 reads
    that folder (ADR-0012).

    When the compile and both test runs have passed, it writes TestResults/private-build.json, the private build's
    word: { "command": "pwsh ./build.ps1" }, the command as a developer types it. The Build keeps TestResults as the
    artifact test-results, and the facts of the image name the command from that word: "ranBy": "integration" when
    the Build ran it, "private" when the facts are written at a desk (the demo-environment-kit's decisions 0028 and
    0046). A run that fails writes no word.

    Not in it: the compile of the infrastructure code (needs the Azure CLI; a step of its own in the Build), and the
    full-system tests, which run against the container image the Build makes after this (tests/AcceptanceTests).

.EXAMPLE
    pwsh ./build.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The command as a developer types it, from the root of the repository. The Build's job test runs exactly this.
$command = 'pwsh ./build.ps1'
# coverlet.collector, in the format that keeps every branch path, so the two runs can be taken together. Generated
# code under obj/ is not the site's code.
$coverage = 'XPlat Code Coverage;Format=opencover;ExcludeByFile=**/obj/**'

$root = $PSScriptRoot
$results = Join-Path $root 'TestResults'

function Invoke-Step {
    param([Parameter(Mandatory)] [string] $Name, [Parameter(Mandatory)] [string[]] $Arguments)
    Write-Host "== $Name"
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAIL $Name (dotnet exited with $LASTEXITCODE): $command stops here and writes no word"
        exit $LASTEXITCODE
    }
}

Push-Location $root
try {
    if (Test-Path -LiteralPath $results) { Remove-Item -LiteralPath $results -Recurse -Force }
    New-Item -ItemType Directory -Path $results -Force | Out-Null

    Invoke-Step 'Compile' @('build', 'JeffreyPalermo.slnx', '--configuration', 'Release', '-flp:LogFile=TestResults/compile.msbuild.log;Verbosity=minimal;Summary')
    Invoke-Step 'Unit tests' @('test', 'tests/UnitTests', '--configuration', 'Release', '--no-build', '--logger', 'trx', '--results-directory', 'TestResults', '--collect', $coverage)
    Invoke-Step 'Integration tests' @('test', 'tests/IntegrationTests', '--configuration', 'Release', '--no-build', '--logger', 'trx', '--results-directory', 'TestResults', '--collect', $coverage)

    [ordered]@{ command = $command } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $results 'private-build.json') -Encoding utf8NoBOM
    Write-Host "PASS $command`: compiled with warnings as errors, unit and integration tests passed; results in TestResults"
}
finally {
    Pop-Location
}
