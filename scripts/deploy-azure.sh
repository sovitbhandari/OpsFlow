#!/usr/bin/env bash
# Manual deployment helper. Do not run this during local development.
set -euo pipefail
: "${RESOURCE_GROUP:?}"
: "${ACR_NAME:?}"
: "${IMAGE_TAG:?}"
: "${SQL_ADMIN_PASSWORD:?}"

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
az acr login --name "$ACR_NAME"
login_server="$(az acr show --name "$ACR_NAME" --query loginServer -o tsv)"
docker build -f Dockerfile.api -t "$login_server/opsflow-api:$IMAGE_TAG" .
docker build -f Dockerfile.worker -t "$login_server/opsflow-worker:$IMAGE_TAG" .
docker push "$login_server/opsflow-api:$IMAGE_TAG"
docker push "$login_server/opsflow-worker:$IMAGE_TAG"
az deployment group create \
  --resource-group "$RESOURCE_GROUP" \
  --template-file infra/bicep/main.bicep \
  --parameters namePrefix="${NAME_PREFIX:-opsflowdev}" imageTag="$IMAGE_TAG" sqlAdminLogin="${SQL_ADMIN_LOGIN:-opsflowadmin}" sqlAdminPassword="$SQL_ADMIN_PASSWORD" allowedSqlIps="${ALLOWED_SQL_IPS:-[]}"
echo "Start the migration job only after grant-sql-users.sql has been applied:"
echo "  az containerapp job start --resource-group $RESOURCE_GROUP --name ${NAME_PREFIX:-opsflowdev}-migrate"
