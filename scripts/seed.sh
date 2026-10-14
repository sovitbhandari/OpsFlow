#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}"
if [[ -z "${Seed__DemoPassword:-}" ]]; then
  echo "Set Seed__DemoPassword to a local-only password before seeding." >&2
  exit 1
fi
dotnet run --project src/OpsFlow.Api -- --seed
