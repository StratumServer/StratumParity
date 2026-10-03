#!/usr/bin/env bash
set -euo pipefail

# Differential runner: builds the scenario suite once, then stages and runs it
# against two installs, and compares the TRX outputs with `atlas diff`.
#
# Usage: scripts/run-parity.sh <vanilla-install> <stratum-install>
#
# One build, two staged runs. `atlas stage` copies the target install's
# VintagestoryAPI.dll+pdb into the built test output before each run, so the
# fork's VintagestoryLib never runs against the vanilla API copy (that mix
# fails at boot with MissingFieldException). Requires the Atlas CLI at the same version
# as the Pixnop.Atlas.XUnit package the project references:
#   dotnet tool install -g Pixnop.Atlas.Cli --version 0.16.0

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 <vanilla-install> <stratum-install>" >&2
  exit 2
fi

if ! command -v atlas >/dev/null 2>&1; then
  echo "atlas CLI not found; install it with: dotnet tool install -g Pixnop.Atlas.Cli --version 0.16.0" >&2
  exit 2
fi

vanilla="$(cd -- "$1" && pwd)"
stratum="$(cd -- "$2" && pwd)"
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"
results="$repo_root/results"
outdir="$repo_root/scenarios/StratumParity.Scenarios/bin/Release/net10.0"
mkdir -p "$results"

echo "== Build (once) =="
( cd "$repo_root" \
  && VINTAGE_STORY="$vanilla" dotnet build scenarios/StratumParity.Scenarios -c Release )

run_suite() {
  local label="$1" install="$2"
  echo "== $label ($install) =="
  ( cd "$repo_root" \
    && VINTAGE_STORY="$install" atlas stage "$outdir" \
    && VINTAGE_STORY="$install" PARITY_EXPECTED_FLAVOR="$label" \
       dotnet test scenarios/StratumParity.Scenarios -c Release --no-build \
         --logger "trx;LogFileName=$label.trx" --results-directory "$results" )
}

run_suite vanilla "$vanilla"
run_suite stratum "$stratum"

echo
echo "== Comparison =="
atlas diff "$results/vanilla.trx" "$results/stratum.trx"
