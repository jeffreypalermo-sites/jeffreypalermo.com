#!/usr/bin/env bash
# Assembles the package the system's pipeline runs in every environment (ADR-0007): scripts/build-deploy-package.sh <out-dir>
#   <out-dir>/            the deploy/ folder as committed: deploy.ps1, verify.ps1, test-site.ps1, settings.json, infra/
#   <out-dir>/bin/        tools/UrlContract, published self-contained for linux-x64 (the pipeline's worker has no .NET)
#   <out-dir>/contract/   the URL contract and its reviewed exceptions, as of this commit
# With bin/ and contract/ beside it, test-site.ps1 replays the URL contract after the health check, so a release that
# breaks a legacy URL fails its deployment. The Build keeps the folder as the artifact deploy-package.
set -euo pipefail

if [ "$#" -ne 1 ]; then
  echo "usage: scripts/build-deploy-package.sh <out-dir>" >&2
  exit 2
fi
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="$1"

rm -rf "$out"
mkdir -p "$out/contract"
cp -R "$root/deploy/." "$out/"
dotnet publish "$root/tools/UrlContract" --configuration Release --runtime linux-x64 --self-contained \
  -p:PublishSingleFile=true -p:DebugType=none --output "$out/bin" --nologo --verbosity quiet
cp "$root/tests/contract/url-contract.tsv" "$root/tests/contract/exceptions.tsv" "$out/contract/"
echo "Deploy package in $out: $(du -sh "$out" | cut -f1)"
