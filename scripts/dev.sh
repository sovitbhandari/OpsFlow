#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}"
echo "Starting Mailpit. SQL Server is not started."
docker compose up -d mailpit
echo "API:    dotnet run --project src/OpsFlow.Api"
echo "Worker: dotnet run --project src/OpsFlow.Worker"
echo "Web:    npm run dev --prefix src/opsflow-web"
echo "Mail:   http://localhost:8025"
