# ADR-0012: The site publishes the facts of its build

- **Status:** Accepted
- **Date:** 2026-10-06

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

What each part is:

| Part | Definition |
|---|---|
| `version` | `MAJOR_VERSION.MINOR_VERSION.<run number>`, the value the image is built with. `dev` on a developer's machine |
| `commit`, `commitUrl` | The commit that is checked out, and its page. The address is `null` outside GitHub Actions |
| `builtAt`, `buildUrl` | When the facts were written (UTC), and the Build's run. The address is `null` outside GitHub Actions |
| `code` | Lines that are not blank, in the files git tracks, by language: C#, Razor, CSS, PowerShell, Shell, Bicep, YAML, JSON, MSBuild, Dockerfile. Comments count. Not counted: `content/` and `migration/` (the posts and the WordPress snapshot), documentation, data (the URL contract), images and fonts. `linesOfCode` and `files` are the sums; `languages` has the largest first |
| `tests` | From the `trx` files of the test runs. `unit` and `integration`: the tests of that layer that passed. `passed`, `failed`, `skipped`: all of them. `acceptance` is `null` in the Build: the full-system tests run against the image, which carries the facts already |
| `coverage` | What the unit and the integration tests go through, both runs taken together, in the five projects under `src/` and `tools/`. A line or a branch path counts as covered when either run went through it. `linePercent` is `linesCovered` of `lines`, `branchPercent` is `branchesCovered` of `branches`, each to two decimals. Generated code under `obj/` (the compiled regular expressions) is left out. The full-system tests run the site in a container and are not measured |
| `complexity` | Cyclomatic complexity of each method coverlet records: 1, plus 1 for every condition with two ways out, plus n - 1 for every switch with n ways out, counted in the compiled code. A method is whatever coverlet records as one: methods and constructors, and also property accessors, lambdas, local functions and the compiled form of an async method. `average`, `max` and `methods` are over all of them |
| `crap` | The CRAP score of each method: complexity² × (1 - its line coverage)³ + complexity. `max` is the highest, `threshold` is 30, as the score's authors set it, and `overThreshold` counts the methods above it |
| `analysis` | `null`. No static analysis runs in this Build |

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

## Consequences

- **The Build takes about two minutes longer.** The five Builds before this change took 2.5 to 4.3 minutes (3.3 on
  average) with the two jobs side by side: `test` 1.3 to 2.3 minutes (1.6 on average), `image` 2.3 to 4.1 (3.1 on
  average). One after the other, and with coverage (20 to 30 seconds more for the integration tests, measured
  locally: 31 to 43 seconds without, 58 to 64 with), a Build takes about 5 minutes.
- **A failing unit or integration test stops the Build before the image.** That run has no full-system test
  results.
- **A Build whose test results hold no tests or no coverage fails** (`-Require tests, coverage`): the wait for job
  `test` would buy nothing.
- **The alternatives.** Keeping the jobs side by side and publishing only version, commit and code costs no time
  and leaves out tests and coverage, which the rule asks for. Adding the file to the image after both jobs ended
  would release an image that no test ran against. Running the unit and integration tests inside job `image`
  saves the second checkout and compile, an estimated 40 seconds of the two minutes, and leaves one job that does
  everything.
- **The numbers differ from other tools'.** A tool that shows coverlet's own `complexity` shows the higher number.
  A tool that leaves comments out counts fewer lines of code.
- **A developer's `docker build` has no facts** unless `scripts/Write-BuildFacts.ps1` ran first; `/_build` then
  answers the version alone. `build-facts.json` is ignored by git.
- **Still to do in the system repository:** `"buildPath": "/_build"` for the deployable `web` in `system.json`.
  Until then the dashboard does not ask the site for its build, and neither does the system's check.
