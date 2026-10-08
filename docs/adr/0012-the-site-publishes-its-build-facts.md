# ADR-0012: The site publishes the facts of its build

- **Status:** Accepted
- **Date:** 2026-10-06, amended 2026-10-08 (tests at three levels, static analysis)

## Context

Jeffrey's rule for every deployed application of the fleet (2026-10-06): it publishes what it was built from
(version, commit, build run) and how good that build is (lines of code, tests, coverage, complexity, static
analysis). The application's build writes the facts into what it releases, the deployed application serves them,
and `deployables[].buildPath` in the system's `system.json` says where. The fleet that watches this system found the
site without them (issue 35 of the demo-environment-kit, "jpcom: no code metrics for web").

Two readers ask. The system's health dashboard reads the answer in the visitor's browser, from another origin, as
it reads health and version ([ADR-0011](0011-the-systems-dashboard-reads-the-site.md)). Its parser takes a JSON
object with `version`, `commit`, `commitUrl`, `builtAt`, `buildUrl` and the sections `code`, `tests`, `coverage`,
`complexity`, `crap` and `analysis`; every part is optional. The system's nightly check asks each environment for
the version that is deployed, a commit and a count of lines of code.

The Build made the image while the unit and integration tests ran, in two jobs side by side
([ADR-0006](0006-deliver-through-the-demo-environment-kit.md)). An image that carries what the tests measured
cannot be built before they end.

A third reader came on 2026-10-08. The fleet reads the facts of every production application and takes two things
from them: tests at three levels (`tests.unit`, `tests.integration` and `tests.acceptance`, each a number above
zero), and static analysis (`analysis` is there and is not `null`; it does not look inside). The fleet runs no test
and no analyzer. It reads what the application counts for itself. It found neither here: `tests.acceptance` and
`analysis` were `null`.

The owner's standard says what the two mean (2026-10-07). The three levels are unit, integration, and acceptance
against the deployed first environment. Static analysis is warnings as errors and one analyzer on every build, with
its count of findings among the code metrics. The tool and the threshold belong to the application.

The site did both and said neither:

- Every deployment is verified by `deploy/verify.ps1` ([ADR-0007](0007-the-site-owns-its-runtime.md)). In `tdd`,
  the first environment, it replays every URL of the contract against the deployed site. A release that breaks one
  fails its deployment, the pin is reverted, and it is not promoted.
- Every project compiles with the .NET analyzers at the level `Directory.Build.props` sets, and a warning is an
  error. A build with a finding does not end, so it releases nothing.

## Decision

- **The Build writes `build-facts.json`** with `scripts/Write-BuildFacts.ps1`. The name of the script and its
  parameters `-OutputPath` and `-DownloadArtifacts` are the kit's convention for every application.
- **The image is built after the tests and carries the file.** Job `image` needs job `test`, downloads its results,
  writes the facts beside the `Dockerfile` and builds the image, once. The `Dockerfile` copies the file to
  `/app/build-facts.json`. The full-system tests run against that image and Release pushes it: the image that
  passed is still the image released.
- **The site answers the file at `GET /_build`**, with the three health answers of ADR-0011 and like them:
  `application/json`, every origin allowed, never cached.
- **The site believes the file only when it names the release the site reports** at `/_health/ready` and
  `/_version`. Without such a file (a developer's machine, a test host, a file of another build) the answer is
  `{"version":"<release>"}`: what the process knows itself, and never an error.
- **A section the Build did not measure is `null`.** No number is estimated.
- **Acceptance is declared, and the facts say so.** `tests.acceptance` is the number of checks a release must pass
  in the first environment before it goes further. The image carries its facts before it is deployed anywhere, so
  the Build cannot know the result. It counts what is declared, from the repository, and `tests.acceptanceIs` says
  which kind of count this is: `"kind": "declared"`, what is counted, who runs it, and that the result is not known
  when the facts are written. The dashboard and the fleet read the number and leave `acceptanceIs` alone. A person
  reads it.
- **The full-system tests are a fourth count, `tests.fullSystem`.** The project `tests/AcceptanceTests` keeps its
  name. Its tests run in the Build against the image, before a release, and not against a deployed environment:
  by the standard's words they are not the acceptance level. They are counted on their own and never added to
  `tests.acceptance`.
- **`passed`, `failed` and `skipped` are results.** They count tests that ran. The declared acceptance checks are
  in none of them: `passed` is not the sum of the three levels.
- **The static analysis is the compile.** The Build's compile writes its log, the log goes with the test results,
  and the script counts what the compile found. Without a log `analysis` is `null`: a count nobody made is not a
  zero. The script also counts what the code switches off, because a zero with suppressions is another zero.
- **The Build requires both.** `-Require tests.unit, tests.integration, tests.acceptance, coverage, analysis`: a
  release whose facts lack one of them is not built.

What each part is:

| Part | Definition |
|---|---|
| `version` | `MAJOR_VERSION.MINOR_VERSION.<run number>`, the value the image is built with. `dev` on a developer's machine |
| `commit`, `commitUrl` | The commit that is checked out, and its page. The address is `null` outside GitHub Actions |
| `builtAt`, `buildUrl` | When the facts were written (UTC), and the Build's run. The address is `null` outside GitHub Actions |
| `code` | Lines that are not blank, in the files git tracks, by language: C#, Razor, CSS, PowerShell, Shell, Bicep, YAML, JSON, MSBuild, Dockerfile. Comments count. Not counted: `content/` and `migration/` (the posts and the WordPress snapshot), documentation, data (the URL contract), images and fonts. `linesOfCode` and `files` are the sums; `languages` has the largest first |
| `tests.unit`, `tests.integration` | From the `trx` files of the test runs: the tests of that layer that passed. The layer is named by the test assembly |
| `tests.acceptance` | Declared, not a result: the URLs of `tests/contract/url-contract.tsv`, one check each. The script counts the lines of the file after the heading, as the verifier (`tools/UrlContract`) reads them: 9,337. `deploy/verify.ps1` replays every one against the first region of every environment, so against `tdd`, which has one region and no Front Door. The 12 reviewed exceptions are not taken off: an exception changes the answer a URL must give, not whether it is asked. Not counted: the two waits of `deploy/test-site.ps1` (the health answer and the home page must name the release), which are the wait for the deployment and are asked once per region; and the second replay through the Front Door in `uat` and `prod`, which asks the same URLs again. `null` in a tree without the contract |
| `tests.acceptanceIs` | What kind of count `acceptance` is, in words: `kind` is `declared`, `counted` names the file, `run` names `deploy/verify.ps1` and the first environment, `result` says it is not known when the facts are written. `null` when `acceptance` is |
| `tests.passed`, `tests.failed`, `tests.skipped` | Results, over every `trx` file read: tests that ran. In the Build these are the unit and the integration tests, so `passed` is `unit` + `integration`. The declared acceptance checks are never in them. `null` without a `trx` file |
| `tests.fullSystem` | The tests of the project `tests/AcceptanceTests` that passed: the published app and the container image over real HTTP, and Chromium. `null` in the Build: they run against the image, which carries the facts already. A number when the script reads the artifacts of a Build that ended (`-DownloadArtifacts`) |
| `coverage` | What the unit and the integration tests go through, both runs taken together, in the five projects under `src/` and `tools/`. A line or a branch path counts as covered when either run went through it. `linePercent` is `linesCovered` of `lines`, `branchPercent` is `branchesCovered` of `branches`, each to two decimals. Generated code under `obj/` (the compiled regular expressions) is left out. The full-system tests run the site in a container and are not measured |
| `complexity` | Cyclomatic complexity of each method coverlet records: 1, plus 1 for every condition with two ways out, plus n - 1 for every switch with n ways out, counted in the compiled code. A method is whatever coverlet records as one: methods and constructors, and also property accessors, lambdas, local functions and the compiled form of an async method. `average`, `max` and `methods` are over all of them |
| `crap` | The CRAP score of each method: complexity² × (1 - its line coverage)³ + complexity. `max` is the highest, `threshold` is 30, as the score's authors set it, and `overThreshold` counts the methods above it |
| `analysis.tool`, `analysisLevel`, `warningsAsErrors` | `.NET analyzers`: the analyzers of the .NET SDK, which run in every compile. `analysisLevel` is `AnalysisLevel` of `Directory.Build.props` (`latest-recommended`). `warningsAsErrors` is true when `Directory.Build.props` sets `TreatWarningsAsErrors` to `true` and no project file sets it to anything else |
| `analysis.problems`, `analysis.projects` | From the log of the compile (`*.msbuild.log` among the results). `problems` is the warnings plus the errors MSBuild counted at its end, the compiler's and the analyzers' alike. `projects` is the assemblies the log says were built. A log that does not end with the count is not read, and `analysis` is `null`. Because a warning is an error, the compile of a build that released something found 0 |
| `analysis.suppressions`, `analysis.suppressed` | What the files git tracks switch off, outside `content/` and `migration/`. `suppressions` is the sum of `suppressed`: `noWarn` (rule ids in `<NoWarn>` and in `NoWarn="..."`), `warningsNotAsErrors` (rule ids in `<WarningsNotAsErrors>`), `pragmaWarningDisable` (rule ids after `#pragma warning disable` in C# and Razor files; a directive that names none counts as one), `suppressMessage` (`[SuppressMessage]` and `[UnconditionalSuppressMessage]` at the start of a line), `editorconfigNone` (rules and categories with severity `none` in `.editorconfig` and `.globalconfig`), `analyzersOff` (`RunAnalyzers`, `RunAnalyzersDuringBuild` or `EnableNETAnalyzers` set to `false`). `null` outside a git checkout |

Coverage is collected with `coverlet.collector`, which the test projects already reference, in the OpenCover
format. Cobertura, the collector's default, keeps one percentage per condition: when the unit tests take one way
out and the integration tests the other, the two files cannot say whether the condition is half or fully covered.
OpenCover keeps every branch path, so the two runs add up exactly.

The complexity is not the number coverlet writes as a method's `complexity`. That number counts every way out of
every condition, two for an `if`: 70 for `LegacyUrlResolver.LegacyRule`, where the definition above gives 36. The
facts carry the cyclomatic complexity, worked out from the same branch points.

First measured, on the commit that added the facts, with the Build's commands run by hand: 11,355 lines of code in
154 files, 8,363 of them C#; 414 unit and 173 integration tests; 94.44 % of 1,800 lines and 91.38 % of 1,229 branch
paths covered; complexity 2.44 on average and 36 at most, over 430 methods; CRAP 110 at most, with three methods
over 30: the `Main` of each of the two tools, which no test starts (110 and 56), and
`LegacyUrlResolver.LegacyRule` (36, every line covered).

Measured again on 2026-10-08, on the commit that added the three levels and the analysis, with the Build's
commands run by hand: 648 unit and 385 integration tests passed, 1,033 together; 9,337 acceptance checks declared;
the compile of 8 projects found 0 problems; the code switches off nothing, 0 of each of the six kinds.

## Consequences

- **The Build takes about two minutes longer.** The five Builds before this change took 2.5 to 4.3 minutes (3.3 on
  average) with the two jobs side by side: `test` 1.3 to 2.3 minutes (1.6 on average), `image` 2.3 to 4.1 (3.1 on
  average). One after the other, and with coverage (20 to 30 seconds more for the integration tests, measured
  locally: 31 to 43 seconds without, 58 to 64 with), a Build takes about 5 minutes.
- **A failing unit or integration test stops the Build before the image.** That run has no full-system test
  results.
- **A Build whose results hold no unit tests, no integration tests, no coverage or no log of the compile fails**,
  and so does one whose tree holds no URL contract (`-Require`): the wait for job `test` would buy nothing.
- **The alternatives.** Keeping the jobs side by side and publishing only version, commit and code costs no time
  and leaves out tests and coverage, which the rule asks for. Adding the file to the image after both jobs ended
  would release an image that no test ran against. Running the unit and integration tests inside job `image`
  saves the second checkout and compile, an estimated 40 seconds of the two minutes, and leaves one job that does
  everything.
- **The numbers differ from other tools'.** A tool that shows coverlet's own `complexity` shows the higher number.
  A tool that leaves comments out counts fewer lines of code.
- **The three levels and the analysis cost the Build nothing that can be measured.** The compile writes one more
  file, 1,532 bytes at minimal verbosity. The script reads the contract (9,338 lines) and the C#, Razor and MSBuild
  files a second time: 2.7 seconds before and after, measured locally.
- **`tests.acceptance` says what a release must pass, not that it passed.** Whether it passed is known from where
  it runs. A release that fails in `tdd` is not promoted, so a release that answers in `uat` or `prod` passed
  there. In `tdd` itself the site answers `/_build` as soon as it is deployed, before the checks have run. The
  number changes only with the contract.
- **The dashboard's card adds the three levels up** (`BuildInfo` of the demo-environment-kit): 10,370 here, of
  which 9,337 are declared and 1,033 ran. `passed` is the number of tests that ran in the Build and passed.
- **`analysis.problems` is 0 in every release.** That is what a warning as an error means. The number that can
  move is `suppressions`. `BuildFactsScriptTests` holds it at 0 for this repository: whoever switches a rule off
  changes that test in the same change.
- **A compile that was up to date finds nothing again.** MSBuild does not run the compiler for a project that has
  not changed, and its log then counts 0 without having looked. In the Build the checkout is new, so every project
  is compiled. On a developer's machine the log of a second `dotnet build` says less than the log of the first.
- **What the analysis does not cover.** The Bicep template is compiled by another step of the Build, which fails on
  a diagnostic, and is not in the count. The image's own `dotnet publish` compiles the three projects under `src/`
  once more with the same settings and writes no log. A setting given on a command line is not in a file the
  script reads: `BuildFactsContractTests` holds that no step that compiles sets one. No Qodana runs, and the facts
  do not use the name `qodanaProblems`. The dashboard's card reads only that name, so it shows no analysis for the
  site. The fleet asks only that `analysis` is there.
- **A developer's `docker build` has no facts** unless `scripts/Write-BuildFacts.ps1` ran first; `/_build` then
  answers the version alone. `build-facts.json` is ignored by git.
- **Still to do in the system repository:** `"buildPath": "/_build"` for the deployable `web` in `system.json`.
  Until then the dashboard does not ask the site for its build, and neither does the system's check.
