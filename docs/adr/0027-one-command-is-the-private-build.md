# ADR-0027: One command is the private build, and the facts say the Build ran it

- **Status:** Accepted
- **Date:** 2026-10-15

## Context

The fleet that watches this system has a standard, "Private build" (the demo-environment-kit's decision 0028,
accepted by Jeffrey Palermo; FLEET-014). An application meets it when the build facts it serves carry
`"build": { "command", "ranBy", "run" }`: the one command of its private build, `"ranBy": "integration"` when the
integration build ran that command to make what is deployed, and the address of that run. The fleet reads it from
`/_build` of every production environment (`Test-BuildFact` of the kit's `test-fleet.ps1`) and calls the
application behind until `command` is there and `ranBy` is `integration`. It is a reading, never a finding.

The kit's decision 0046 says how the word gets there: the private build's command writes `private-build.json`,
`{ "command": "<the command as a developer types it>" }`, when it runs in the integration build, and something
that knows the Build's run takes it into the facts, only when the Build workflow of that commit runs the command.
For the kit's work-order app its Release does that. This site brings its own runtime and bakes its facts into its
image in the Build ([ADR-0012](0012-the-site-publishes-its-build-facts.md)), so it does it itself.

The site had no private build. Job `test` of the Build repeated the compile and the two test runs as steps of its
own, and a developer ran `dotnet build` and `dotnet test` by hand: two definitions of the build.

## Decision

- **`build.ps1` at the root is the private build**, run as `pwsh ./build.ps1`. It empties `TestResults`, compiles
  the solution in Release (the static analysis: the .NET analyzers, a warning is an error, with its log), and runs
  the unit and the integration tests without a second compile, with their results and coverage, all under
  `TestResults`. It stops at the first step that fails. When all three pass it writes its word,
  `TestResults/private-build.json`: `{ "command": "pwsh ./build.ps1" }`.
- **Job `test` of the Build runs `pwsh ./build.ps1`**, as a developer types it, and no compile or test of its own.
  The compile of the infrastructure code stays a step of its own (it needs the Azure CLI), and so do the full-system
  tests (they need the image, which is built after). The job names, the artifacts and what the kit pins are as
  they were; the word travels in the artifact `test-results`.
- **`scripts/Write-BuildFacts.ps1` takes the word into the facts** as `build`, with the kit's rules: one word under
  the results, a command of 1 to 200 printable characters on one line. On GitHub Actions, and only when a `run:` of
  `.github/workflows/build.yml` runs exactly that command (as its value, or as a line of a block), `ranBy` is
  `integration` and `run` is `buildUrl`, the address of the Build's run that made the image. Anywhere else `ranBy`
  is `private` and `run` is `null`. Without a word, or when the workflow does not run the command, `build` is
  `null` and the script says why.
- **The Build requires it.** Job `image` writes the facts with `-Require ..., build`: an image whose facts do not
  name the private build is not built.

## Consequences

- From the first release built after this, `/_build` of every environment answers
  `"build": { "command": "pwsh ./build.ps1", "ranBy": "integration", "run": "https://github.com/jeffreypalermo-sites/jeffreypalermo.com/actions/runs/<id>" }`,
  and the wall reads "Private build" as met for jpcom once production runs that release.
- What it proves is what the kit's 0028 says, no more: the command exists, the Build's workflow runs it, and what
  is deployed came from that run. Not that a developer runs it before a push.
- A developer runs the same command as the Build; its results in `TestResults` are what the Build hands to job
  `image`, and `scripts/Write-BuildFacts.ps1` at a desk says `"ranBy": "private"`.
- The tests that pinned the steps of job `test` now pin `build.ps1` and that job `test` runs it.

## Alternatives

- **The kit's `add-private-build.ps1` and an artifact `private-build`.** That script edits facts that the Release
  stamps after the Build. This site's facts are inside the image the Build tested; changing them after would
  release facts the image does not carry. The rules are the same, applied before `docker build`.
- **Write `build` in the workflow.** A statement beside the build: it stays true when the Build stops running the
  command, which the kit's 0028 rejects.
