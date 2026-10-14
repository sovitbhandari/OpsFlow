#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet build OpsFlow.slnx
dotnet test tests/OpsFlow.UnitTests --no-build
if [[ "${OPSFLOW_RUN_INTEGRATION:-}" == "true" ]]; then
  dotnet test tests/OpsFlow.IntegrationTests --no-build
  dotnet test tests/OpsFlow.WorkerTests --no-build
fi
cd src/opsflow-web
npm ci
npm run lint
npm run build
echo "Validation completed."
