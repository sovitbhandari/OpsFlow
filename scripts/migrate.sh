#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet run --project src/OpsFlow.Api -- --migrate
