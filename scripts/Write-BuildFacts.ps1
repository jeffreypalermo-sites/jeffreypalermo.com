#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Writes build-facts.json: what a release of the site was built from and what the Build measured (ADR-0012).

.DESCRIPTION
    The Build runs this before it builds the container image. The image carries the file beside the app and the
    site answers it at /_build, where the system's health dashboard reads it. The name of the script and the
    parameters -OutputPath and -DownloadArtifacts are the demo-environment-kit's convention for every application.

    A section whose input is not there is null. No number is estimated.

      version, commit, builtAt   from the parameters; otherwise "dev", the commit checked out, and now
      commitUrl, buildUrl        on GitHub Actions only (GITHUB_SERVER_URL, GITHUB_REPOSITORY, GITHUB_RUN_ID)
      code                       lines that are not blank, in the files git tracks, by language (see $languages).
                                 Not counted: content/ and migration/ (the posts and the WordPress snapshot),
                                 documentation, data, images and fonts. Null outside a git checkout
      tests                      from the *.trx files under -ResultsPath: the tests that passed in each layer, the
                                 layer named by the test assembly (UnitTests, IntegrationTests, AcceptanceTests),
                                 and passed, failed and skipped over all of them
      coverage                   from the *.opencover.xml files under -ResultsPath (coverlet.collector with
                                 Format=opencover), every run taken together: a line or a branch path counts as
                                 covered when any run went through it
      complexity                 cyclomatic complexity of each method in those files: 1, plus 1 for every
                                 condition with two ways out, plus n - 1 for every switch with n ways out
      crap                       the CRAP score of each method: complexity² × (1 - line coverage)³ + complexity.
                                 The threshold is 30, as its authors set it
      analysis                   null: no static analysis runs in this Build

    -Require names the sections the caller needs measured. When one of them is null the script writes nothing and
    ends with exit code 1.

.EXAMPLE
    scripts/Write-BuildFacts.ps1 -Version 1.0.41 -ResultsPath TestResults -OutputPath build-facts.json

.EXAMPLE
    scripts/Write-BuildFacts.ps1 -DownloadArtifacts -Version 1.0.41 -RunId 37567446935 -OutputPath built/build-facts.json
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [string] $OutputPath = 'build-facts.json',
    # The release: what the image is built with (build argument VERSION), so the site and its facts agree.
    [string] $Version = 'dev',
    [string] $Commit = '',
    # The Build's run on GitHub Actions. Default: the run this script is part of.
    [string] $RunId = '',
    # When the Build ran. Default: now.
    [string] $BuiltAt = '',
    # The folder with the test results (*.trx) and the coverage (*.opencover.xml), searched with its subfolders.
    # Default: TestResults in the repository.
    [string] $ResultsPath = '',
    # Read the test results from the artifacts of the run -RunId instead of -ResultsPath. Needs gh, signed in.
    [switch] $DownloadArtifacts,
    # Sections that must not be null, for example: tests, coverage.
    [string[]] $Require = @(),
    [string] $RepositoryRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# What counts as code, by the end of the file's name. A file without an extension whose first line names a shell
# (#!/usr/bin/env bash) is Shell.
$languages = [ordered]@{
    '.cs'      = 'C#'
    '.razor'   = 'Razor'
    '.css'     = 'CSS'
    '.ps1'     = 'PowerShell'
    '.psm1'    = 'PowerShell'
    '.sh'      = 'Shell'
    '.bicep'   = 'Bicep'
    '.yml'     = 'YAML'
    '.yaml'    = 'YAML'
    '.json'    = 'JSON'
    '.csproj'  = 'MSBuild'
    '.props'   = 'MSBuild'
    '.targets' = 'MSBuild'
    '.slnx'    = 'MSBuild'
}
$notCode = @('content/', 'migration/')
$artifacts = @('test-results', 'test-results-full-system')
$layers = [ordered]@{ unit = 'unittests.dll'; integration = 'integrationtests.dll'; acceptance = 'acceptancetests.dll' }
$crapThreshold = 30

function Get-Language {
    param([string] $Path, [string] $FullPath)
    $name = [IO.Path]::GetFileName($Path)
    if ($name -eq 'Dockerfile') { return 'Dockerfile' }
    $extension = [IO.Path]::GetExtension($name).ToLowerInvariant()
    if ($extension) { return $languages[$extension] }
    $first = [IO.File]::ReadLines($FullPath) | Select-Object -First 1
    if ($first -match '^#!.*\b(ba|da|z)?sh\b') { return 'Shell' }
    return $null
}

function Get-CodeFacts {
    param([string] $Root)
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { return $null }
    $tracked = @(git -C $Root -c core.quotepath=off ls-files 2>$null)
    if ($LASTEXITCODE -ne 0) { return $null }

    $sizes = @{}
    foreach ($path in $tracked) {
        if ($notCode | Where-Object { $path.StartsWith($_, [StringComparison]::Ordinal) }) { continue }
        $fullPath = Join-Path $Root $path
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { continue }
        $language = Get-Language $path $fullPath
        if (-not $language) { continue }
        $lines = 0
        foreach ($line in [IO.File]::ReadLines($fullPath)) {
            if (-not [string]::IsNullOrWhiteSpace($line)) { $lines++ }
        }
        if (-not $sizes.ContainsKey($language)) { $sizes[$language] = @{ lines = 0L; files = 0 } }
        $sizes[$language].lines += $lines
        $sizes[$language].files++
    }

    $byLanguage = @($sizes.GetEnumerator() | Sort-Object @{ Expression = { $_.Value.lines }; Descending = $true }, Name |
        ForEach-Object { [ordered]@{ name = $_.Name; lines = [long] $_.Value.lines; files = [int] $_.Value.files } })
    return [ordered]@{
        linesOfCode = [long] (($byLanguage | ForEach-Object { $_.lines } | Measure-Object -Sum).Sum ?? 0)
        files       = [int] (($byLanguage | ForEach-Object { $_.files } | Measure-Object -Sum).Sum ?? 0)
        languages   = $byLanguage
    }
}

function Get-TestFacts {
    param([IO.FileInfo[]] $Files)
    $passedIn = @{}
    $passed = 0; $failed = 0; $skipped = 0; $read = 0
    foreach ($file in $Files) {
        $run = [xml]::new()
        $run.Load($file.FullName)
        $results = @($run.SelectNodes("/*[local-name()='TestRun']/*[local-name()='Results']/*[local-name()='UnitTestResult']"))
        if ($results.Count -eq 0) { continue }
        $read++
        $definition = $run.SelectSingleNode("/*[local-name()='TestRun']/*[local-name()='TestDefinitions']/*[local-name()='UnitTest']")
        $assembly = $definition ? $definition.GetAttribute('storage').ToLowerInvariant() : ''
        $layer = $layers.Keys | Where-Object { $assembly.EndsWith($layers[$_], [StringComparison]::Ordinal) } | Select-Object -First 1
        foreach ($result in $results) {
            switch ($result.GetAttribute('outcome')) {
                'Passed' {
                    $passed++
                    if ($layer) { $passedIn[$layer] = 1 + ($passedIn.ContainsKey($layer) ? $passedIn[$layer] : 0) }
                }
                { $_ -in 'Failed', 'Error', 'Timeout', 'Aborted' } { $failed++ }
                default { $skipped++ }
            }
        }
    }
    if ($read -eq 0) { return $null }

    $tests = [ordered]@{}
    foreach ($layer in $layers.Keys) { $tests[$layer] = $passedIn.ContainsKey($layer) ? [int] $passedIn[$layer] : $null }
    $tests.passed = [int] $passed
    $tests.failed = [int] $failed
    $tests.skipped = [int] $skipped
    return $tests
}

# Every method of the coverage files, the runs taken together: its lines and its branch paths, each with whether any
# run went through it. A branch path is one way out of one condition: the condition's place (its line, and its offset
# in the compiled code, which alone is not unique where a method holds lambdas) and the way taken (path).
function Get-Methods {
    param([IO.FileInfo[]] $Files)
    $methods = @{}
    # A run's file is found twice when the trx logger attached a copy of it: read each content once.
    $distinct = $Files | Group-Object { (Get-FileHash -LiteralPath $_.FullName).Hash } | ForEach-Object { $_.Group[0] }
    foreach ($file in $distinct) {
        $session = [xml]::new()
        $session.Load($file.FullName)
        foreach ($module in $session.SelectNodes('/CoverageSession/Modules/Module[ModuleName]')) {
            $moduleName = $module.SelectSingleNode('ModuleName').InnerText
            foreach ($class in $module.SelectNodes('Classes/Class[FullName]')) {
                $className = $class.SelectSingleNode('FullName').InnerText
                foreach ($method in $class.SelectNodes('Methods/Method[Name]')) {
                    $key = "$moduleName|$className|$($method.SelectSingleNode('Name').InnerText)"
                    if (-not $methods.ContainsKey($key)) { $methods[$key] = @{ lines = @{}; paths = @{} } }
                    $known = $methods[$key]
                    foreach ($point in $method.SelectNodes('SequencePoints/SequencePoint')) {
                        $line = $point.GetAttribute('sl')
                        $known.lines[$line] = ($known.lines.ContainsKey($line) -and $known.lines[$line]) -or [long] $point.GetAttribute('vc') -gt 0
                    }
                    foreach ($point in $method.SelectNodes('BranchPoints/BranchPoint')) {
                        $path = "$($point.GetAttribute('sl')):$($point.GetAttribute('offset'))/$($point.GetAttribute('path'))"
                        $known.paths[$path] = ($known.paths.ContainsKey($path) -and $known.paths[$path]) -or [long] $point.GetAttribute('vc') -gt 0
                    }
                }
            }
        }
    }

    # A method without a line has nothing a test could go through: it is not counted.
    return @($methods.Values | Where-Object { $_.lines.Count -gt 0 } | ForEach-Object {
            $conditions = @($_.paths.Keys | ForEach-Object { $_.Split('/')[0] } | Sort-Object -Unique).Count
            $lines = $_.lines.Count
            $linesCovered = @($_.lines.Values | Where-Object { $_ }).Count
            $complexity = 1 + $_.paths.Count - $conditions
            $uncovered = 1 - $linesCovered / $lines
            [pscustomobject]@{
                Lines         = $lines
                LinesCovered  = $linesCovered
                Paths         = $_.paths.Count
                PathsCovered  = @($_.paths.Values | Where-Object { $_ }).Count
                Complexity    = $complexity
                Crap          = $complexity * $complexity * [Math]::Pow($uncovered, 3) + $complexity
            }
        })
}

function Get-Sum {
    param([object[]] $Items, [string] $Property)
    return [long] (($Items | Measure-Object -Property $Property -Sum).Sum ?? 0)
}

function Get-Percent {
    param([long] $Part, [long] $Whole)
    return $Whole -eq 0 ? $null : [Math]::Round(100.0 * $Part / $Whole, 2)
}

if (-not $RepositoryRoot) { $RepositoryRoot = Join-Path $PSScriptRoot '..' }
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
if (-not $RunId) { $RunId = [string] $env:GITHUB_RUN_ID }
$github = ($env:GITHUB_SERVER_URL -and $env:GITHUB_REPOSITORY) ? "$($env:GITHUB_SERVER_URL.TrimEnd('/'))/$env:GITHUB_REPOSITORY" : $null

$downloaded = $null
if ($DownloadArtifacts) {
    if (-not $RunId) {
        Write-Host 'FAIL -DownloadArtifacts needs the run whose artifacts to read: pass -RunId.'
        exit 1
    }
    $downloaded = Join-Path ([IO.Path]::GetTempPath()) "build-facts-$([Guid]::NewGuid().ToString('N'))"
    foreach ($artifact in $artifacts) {
        $arguments = @('run', 'download', $RunId, '--name', $artifact, '--dir', (Join-Path $downloaded $artifact))
        if ($env:GITHUB_REPOSITORY) { $arguments += @('--repo', $env:GITHUB_REPOSITORY) }
        $answer = gh @arguments 2>&1
        if ($LASTEXITCODE -ne 0) { Write-Host "The artifact $artifact of run $RunId was not read: $("$answer".Trim())" }
    }
    $ResultsPath = $downloaded
}
elseif (-not $ResultsPath) {
    $ResultsPath = Join-Path $RepositoryRoot 'TestResults'
}

try {
    $found = Test-Path -LiteralPath $ResultsPath -PathType Container
    $trx = @($found ? (Get-ChildItem -LiteralPath $ResultsPath -Recurse -File -Filter '*.trx' | Sort-Object FullName) : @())
    $coverage = @($found ? (Get-ChildItem -LiteralPath $ResultsPath -Recurse -File -Filter '*.opencover.xml' | Sort-Object FullName) : @())
    $tests = Get-TestFacts $trx
    $methods = @(Get-Methods $coverage)
}
finally {
    if ($downloaded -and (Test-Path -LiteralPath $downloaded)) { Remove-Item -LiteralPath $downloaded -Recurse -Force }
}

if (-not $Commit) {
    $head = (Get-Command git -ErrorAction SilentlyContinue) ? [string] (git -C $RepositoryRoot rev-parse --verify --quiet HEAD 2>$null) : ''
    $Commit = $head ? $head.Trim() : [string] $env:GITHUB_SHA
}
$moment = $BuiltAt ?
    [DateTimeOffset]::Parse($BuiltAt, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal) :
    [DateTimeOffset]::UtcNow

$facts = [ordered]@{
    version    = $Version
    commit     = $Commit ? $Commit : $null
    commitUrl  = ($github -and $Commit) ? "$github/commit/$Commit" : $null
    builtAt    = $moment.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", [Globalization.CultureInfo]::InvariantCulture)
    buildUrl   = ($github -and $RunId) ? "$github/actions/runs/$RunId" : $null
    code       = Get-CodeFacts $RepositoryRoot
    tests      = $tests
    coverage   = $null
    complexity = $null
    crap       = $null
    analysis   = $null
}
if ($methods.Count -gt 0) {
    $lines = Get-Sum $methods 'Lines'
    $linesCovered = Get-Sum $methods 'LinesCovered'
    $paths = Get-Sum $methods 'Paths'
    $pathsCovered = Get-Sum $methods 'PathsCovered'
    $facts.coverage = [ordered]@{
        linePercent     = Get-Percent $linesCovered $lines
        branchPercent   = Get-Percent $pathsCovered $paths
        lines           = $lines
        linesCovered    = $linesCovered
        branches        = $paths
        branchesCovered = $pathsCovered
    }
    $facts.complexity = [ordered]@{
        average = [Math]::Round(($methods | Measure-Object -Property Complexity -Average).Average, 2)
        max     = [int] ($methods | Measure-Object -Property Complexity -Maximum).Maximum
        methods = [int] $methods.Count
    }
    $facts.crap = [ordered]@{
        max           = [Math]::Round(($methods | Measure-Object -Property Crap -Maximum).Maximum, 2)
        threshold     = $crapThreshold
        overThreshold = [int] @($methods | Where-Object { $_.Crap -gt $crapThreshold }).Count
    }
}

$missing = @($Require | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ } |
    Where-Object { -not $facts.Contains($_) -or $null -eq $facts[$_] })
if ($missing.Count -gt 0) {
    Write-Host "FAIL the build facts lack $($missing -join ', '): nothing to measure $($missing.Count -eq 1 ? 'it' : 'them') from in $ResultsPath"
    exit 1
}

$directory = Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath, (Get-Location).Path))
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$facts | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM

$said = @("version $Version")
if ($Commit) { $said += "commit $($Commit.Substring(0, [Math]::Min(7, $Commit.Length)))" }
if ($facts.code) { $said += "$($facts.code.linesOfCode) lines of code in $($facts.code.files) files" }
if ($tests) { $said += "$($tests.passed) tests passed, $($tests.failed) failed, $($tests.skipped) skipped" }
if ($facts.coverage) { $said += "$($facts.coverage.linePercent) % of lines and $($facts.coverage.branchPercent) % of branches covered" }
if ($facts.complexity) { $said += "complexity $($facts.complexity.average) on average, $($facts.complexity.max) at most, in $($facts.complexity.methods) methods" }
if ($facts.crap) { $said += "CRAP $($facts.crap.max) at most, $($facts.crap.overThreshold) over $crapThreshold" }
$nothing = @($facts.Keys | Where-Object { $null -eq $facts[$_] })
if ($nothing.Count -gt 0) { $said += "null: $($nothing -join ', ')" }
Write-Host "$OutputPath`: $($said -join '; ')"
