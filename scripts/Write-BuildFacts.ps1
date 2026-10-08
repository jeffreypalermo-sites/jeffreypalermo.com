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
      tests                      unit, integration, fullSystem: from the *.trx files under -ResultsPath, the tests
                                 that passed in each layer, the layer named by the test assembly (UnitTests,
                                 IntegrationTests, AcceptanceTests). passed, failed and skipped: over all of those
                                 files, so over tests that ran.
                                 acceptance: declared, not a result. The URLs of tests/contract/url-contract.tsv,
                                 which deploy/verify.ps1 replays against the first environment after the Build; a
                                 release that fails one goes no further. acceptanceIs says so in the facts
      coverage                   from the *.opencover.xml files under -ResultsPath (coverlet.collector with
                                 Format=opencover), every run taken together: a line or a branch path counts as
                                 covered when any run went through it
      complexity                 cyclomatic complexity of each method in those files: 1, plus 1 for every
                                 condition with two ways out, plus n - 1 for every switch with n ways out
      crap                       the CRAP score of each method: complexity² × (1 - line coverage)³ + complexity.
                                 The threshold is 30, as its authors set it
      analysis                   from the *.msbuild.log files under -ResultsPath, the log of the compile
                                 (dotnet build -flp:LogFile=<file>;Verbosity=minimal;Summary): problems is the
                                 warnings and the errors it counted, projects the assemblies it built. The tool
                                 is the .NET analyzers: analysisLevel and warningsAsErrors are read from
                                 Directory.Build.props. suppressions counts what the files git tracks switch off
                                 (see Get-Suppressions). Null without a log: a count nobody made is not zero

    -Require names what the caller needs measured: a section (coverage) or one part of it (tests.unit). When one of
    them is null the script writes nothing and ends with exit code 1.

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
    # The folder with the test results (*.trx), the coverage (*.opencover.xml) and the log of the compile
    # (*.msbuild.log), searched with its subfolders. Default: TestResults in the repository.
    [string] $ResultsPath = '',
    # Read the test results from the artifacts of the run -RunId instead of -ResultsPath. Needs gh, signed in.
    [switch] $DownloadArtifacts,
    # What must not be null, for example: tests.unit, coverage, analysis.
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
# The project AcceptanceTests holds the full-system tests: they run in the Build against the image, before a release.
# Acceptance, in the facts, is what a release must pass in the first environment it is deployed to ($contract).
$layers = [ordered]@{ unit = 'unittests.dll'; integration = 'integrationtests.dll'; fullSystem = 'acceptancetests.dll' }
$crapThreshold = 30
# The URL contract. deploy/verify.ps1 replays every row of it against a deployed environment (test-site.ps1 hands the
# file to tools/UrlContract, which reads one URL per line after the heading). The reviewed exceptions beside it change
# the answer a URL must give, not whether it is asked, so they are not taken off.
$contract = 'tests/contract/url-contract.tsv'
$msbuildFiles = @('.csproj', '.props', '.targets')
$sourceFiles = @('.cs', '.razor')
$analyzerSettings = @('.editorconfig', '.globalconfig')

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

# The files git tracks that may hold code: not content/ and not migration/. Null outside a git checkout.
function Get-TrackedFiles {
    param([string] $Root)
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { return $null }
    $tracked = @(git -C $Root -c core.quotepath=off ls-files 2>$null)
    if ($LASTEXITCODE -ne 0) { return $null }
    $files = @($tracked | Where-Object { $path = $_; -not ($notCode | Where-Object { $path.StartsWith($_, [StringComparison]::Ordinal) }) } |
        Where-Object { Test-Path -LiteralPath (Join-Path $Root $_) -PathType Leaf })
    # As one thing: a list without files is still a checkout, and a function gives its caller nothing for an empty list.
    return , $files
}

function Get-CodeFacts {
    param([string] $Root, [string[]] $Tracked)
    $sizes = @{}
    foreach ($path in $Tracked) {
        $fullPath = Join-Path $Root $path
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

# How many checks a release must pass in the first environment it is deployed to: the URLs of the contract, one line
# each after the heading, as tools/UrlContract reads the file. Null where there is no contract.
function Get-DeclaredAcceptance {
    param([string] $Root)
    $file = Join-Path $Root $contract
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { return $null }
    $rows = 0
    foreach ($line in [IO.File]::ReadLines($file)) {
        if ($line.Length -gt 0) { $rows++ }
    }
    return [int] [Math]::Max(0, $rows - 1)
}

function Get-TestFacts {
    param([IO.FileInfo[]] $Files, $Declared)
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
    if ($read -eq 0 -and $null -eq $Declared) { return $null }

    $ran = @{}
    foreach ($layer in $layers.Keys) { $ran[$layer] = $passedIn.ContainsKey($layer) ? [int] $passedIn[$layer] : $null }
    # unit, integration and acceptance are the three levels the dashboard and the fleet read. passed, failed and
    # skipped are results: they count tests that ran, so never the declared acceptance checks.
    return [ordered]@{
        unit         = $ran.unit
        integration  = $ran.integration
        acceptance   = $Declared
        passed       = $read -gt 0 ? [int] $passed : $null
        failed       = $read -gt 0 ? [int] $failed : $null
        skipped      = $read -gt 0 ? [int] $skipped : $null
        fullSystem   = $ran.fullSystem
        acceptanceIs = $null -eq $Declared ? $null : [ordered]@{
            kind    = 'declared'
            counted = "the URLs of $contract, one check each"
            run     = 'by deploy/verify.ps1 against the first environment, after the Build. A release that fails one goes no further'
            result  = 'not known when these facts are written'
        }
    }
}

# What the compile of this build found, from its log (dotnet build -flp:LogFile=<file>;Verbosity=minimal;Summary):
# the warnings and the errors MSBuild counted at the end, and the assemblies it built. Null without a log, and
# without a log that ends with the count: a compile that was cut short found nobody knows what.
function Get-CompileFacts {
    param([IO.FileInfo[]] $Logs)
    if ($Logs.Count -eq 0) { return $null }
    $problems = 0
    $projects = @{}
    foreach ($log in $Logs) {
        $text = [IO.File]::ReadAllText($log.FullName)
        $warnings = [regex]::Matches($text, '(?m)^\s*(\d+) Warning\(s\)\s*$')
        $errors = [regex]::Matches($text, '(?m)^\s*(\d+) Error\(s\)\s*$')
        if ($warnings.Count -eq 0 -or $errors.Count -eq 0) {
            Write-Host "The compile log $($log.FullName) does not end with its count of warnings and errors: no analysis is reported."
            return $null
        }
        $problems += [int] $warnings[$warnings.Count - 1].Groups[1].Value + [int] $errors[$errors.Count - 1].Groups[1].Value
        foreach ($built in [regex]::Matches($text, '(?m)^\s*(\S+) -> .+\.dll\s*$')) { $projects[$built.Groups[1].Value] = $true }
    }
    return [ordered]@{ problems = [int] $problems; projects = [int] $projects.Count }
}

# The ids a NoWarn or a #pragma names: CA1822;CS1591 or CA1822, CS1591. $(NoWarn), the list so far, is not one.
function Get-RuleIds {
    param([string] $Text)
    return @($Text -split '[;,\s]+' | Where-Object { $_ -and -not $_.StartsWith('$(', [StringComparison]::Ordinal) })
}

# One value the build is set to in Directory.Build.props, which holds for every project; null when it sets none.
function Get-BuildProperty {
    param([string] $Root, [string] $Name)
    $file = Join-Path $Root 'Directory.Build.props'
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { return $null }
    $project = [xml]::new()
    $project.Load($file)
    $node = $project.SelectSingleNode("//*[local-name()='$Name']")
    return $node ? $node.InnerText.Trim() : $null
}

# What the files git tracks switch off, so that no problem is found where one would be. A zero with suppressions is
# another zero than one without.
#   noWarn                rule ids in <NoWarn> and in NoWarn="..." of the MSBuild files
#   warningsNotAsErrors   rule ids in <WarningsNotAsErrors>: a warning that no longer stops the build
#   pragmaWarningDisable  rule ids after #pragma warning disable in C# and Razor files; a directive that names none
#                         switches off every rule and counts as one
#   suppressMessage       [SuppressMessage(...)] and [UnconditionalSuppressMessage(...)] at the start of a line
#   editorconfigNone      rules and categories whose severity is none in .editorconfig and .globalconfig
#   analyzersOff          RunAnalyzers, RunAnalyzersDuringBuild or EnableNETAnalyzers set to false in the MSBuild files
# Also: whether a project sets TreatWarningsAsErrors to anything but true.
function Get-Suppressions {
    param([string] $Root, [string[]] $Tracked)
    $counts = [ordered]@{ noWarn = 0; warningsNotAsErrors = 0; pragmaWarningDisable = 0; suppressMessage = 0; editorconfigNone = 0; analyzersOff = 0 }
    $warningsAllowed = $false
    foreach ($path in $Tracked) {
        $fullPath = Join-Path $Root $path
        $name = [IO.Path]::GetFileName($path)
        $extension = [IO.Path]::GetExtension($name).ToLowerInvariant()
        if ($extension -in $msbuildFiles) {
            $project = [xml]::new()
            $project.Load($fullPath)
            # get_InnerText(): PowerShell's own view of an attribute has no InnerText.
            foreach ($node in $project.SelectNodes("//*[local-name()='NoWarn'] | //@NoWarn")) { $counts.noWarn += @(Get-RuleIds $node.get_InnerText()).Count }
            foreach ($node in $project.SelectNodes("//*[local-name()='WarningsNotAsErrors']")) { $counts.warningsNotAsErrors += @(Get-RuleIds $node.InnerText).Count }
            foreach ($node in $project.SelectNodes("//*[local-name()='RunAnalyzers' or local-name()='RunAnalyzersDuringBuild' or local-name()='EnableNETAnalyzers']")) {
                if ($node.InnerText.Trim() -eq 'false') { $counts.analyzersOff++ }
            }
            foreach ($node in $project.SelectNodes("//*[local-name()='TreatWarningsAsErrors']")) {
                if ($node.InnerText.Trim() -ne 'true') { $warningsAllowed = $true }
            }
        }
        elseif ($extension -in $sourceFiles) {
            foreach ($line in [IO.File]::ReadLines($fullPath)) {
                if ($line -match '^\s*#pragma\s+warning\s+disable\b(?<rules>[^/]*)') {
                    $counts.pragmaWarningDisable += [Math]::Max(1, @(Get-RuleIds $Matches.rules).Count)
                }
                elseif ($line -match '^\s*\[[^\]"]*\b(Unconditional)?SuppressMessage(Attribute)?\s*\(') { $counts.suppressMessage++ }
            }
        }
        elseif ($name -in $analyzerSettings -or $extension -in $analyzerSettings) {
            foreach ($line in [IO.File]::ReadLines($fullPath)) {
                if ($line -match '^\s*dotnet_(analyzer_)?diagnostic\.[^=#;]*severity\s*=\s*none\b') { $counts.editorconfigNone++ }
            }
        }
    }
    return [ordered]@{
        total           = [int] ($counts.Values | Measure-Object -Sum).Sum
        counts          = $counts
        warningsAllowed = $warningsAllowed
    }
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
    $logs = @($found ? (Get-ChildItem -LiteralPath $ResultsPath -Recurse -File -Filter '*.msbuild.log' | Sort-Object FullName) : @())
    $tests = Get-TestFacts $trx (Get-DeclaredAcceptance $RepositoryRoot)
    $methods = @(Get-Methods $coverage)
    $compile = Get-CompileFacts $logs
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

$tracked = Get-TrackedFiles $RepositoryRoot
$facts = [ordered]@{
    version    = $Version
    commit     = $Commit ? $Commit : $null
    commitUrl  = ($github -and $Commit) ? "$github/commit/$Commit" : $null
    builtAt    = $moment.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", [Globalization.CultureInfo]::InvariantCulture)
    buildUrl   = ($github -and $RunId) ? "$github/actions/runs/$RunId" : $null
    code       = $null -eq $tracked ? $null : (Get-CodeFacts $RepositoryRoot $tracked)
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
if ($null -ne $compile) {
    $level = Get-BuildProperty $RepositoryRoot 'AnalysisLevel'
    $suppressions = $null -eq $tracked ? $null : (Get-Suppressions $RepositoryRoot $tracked)
    $facts.analysis = [ordered]@{
        tool             = '.NET analyzers'
        analysisLevel    = $level ? $level : $null
        # Every project compiles with it only when the build says so and no project says otherwise.
        warningsAsErrors = (Get-BuildProperty $RepositoryRoot 'TreatWarningsAsErrors') -eq 'true' -and -not ($null -ne $suppressions -and $suppressions.warningsAllowed)
        problems         = $compile.problems
        projects         = $compile.projects
        suppressions     = $null -eq $suppressions ? $null : $suppressions.total
        suppressed       = $null -eq $suppressions ? $null : $suppressions.counts
    }
}

# A name with a dot is one part of a section: tests.unit.
$missing = @($Require | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Where-Object {
        $value = $facts
        foreach ($part in $_.Split('.')) { $value = ($value -is [Collections.IDictionary] -and $value.Contains($part)) ? $value[$part] : $null }
        $null -eq $value
    })
if ($missing.Count -gt 0) {
    Write-Host "FAIL the build facts lack $($missing -join ', '): nothing to measure $($missing.Count -eq 1 ? 'it' : 'them') from in $ResultsPath and $RepositoryRoot"
    exit 1
}

$directory = Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath, (Get-Location).Path))
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$facts | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM

$said = @("version $Version")
if ($Commit) { $said += "commit $($Commit.Substring(0, [Math]::Min(7, $Commit.Length)))" }
if ($facts.code) { $said += "$($facts.code.linesOfCode) lines of code in $($facts.code.files) files" }
if ($tests -and $null -ne $tests.passed) { $said += "$($tests.passed) tests passed, $($tests.failed) failed, $($tests.skipped) skipped" }
if ($tests -and $null -ne $tests.acceptance) { $said += "$($tests.acceptance) acceptance checks declared" }
if ($facts.coverage) { $said += "$($facts.coverage.linePercent) % of lines and $($facts.coverage.branchPercent) % of branches covered" }
if ($facts.complexity) { $said += "complexity $($facts.complexity.average) on average, $($facts.complexity.max) at most, in $($facts.complexity.methods) methods" }
if ($facts.crap) { $said += "CRAP $($facts.crap.max) at most, $($facts.crap.overThreshold) over $crapThreshold" }
if ($null -ne $facts.analysis) { $said += "$($facts.analysis.problems) problems in the compile of $($facts.analysis.projects) projects, $($facts.analysis.suppressions ?? 'uncounted') suppressions" }
$nothing = @($facts.Keys | Where-Object { $null -eq $facts[$_] })
if ($nothing.Count -gt 0) { $said += "null: $($nothing -join ', ')" }
Write-Host "$OutputPath`: $($said -join '; ')"
