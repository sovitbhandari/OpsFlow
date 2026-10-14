# Runbook

## Database outage

`/health/live` stays healthy without SQL. `/health/ready` fails when the database check fails. Stop routing traffic to unready API replicas. The worker logs the cycle error and retries on the next poll. It does not mark jobs failed just because SQL was unreachable. After SQL returns, run no automatic migration. Confirm the latest migration is already applied before accepting traffic.

## Worker backlog

Inspect notification jobs as an organization admin. Pending rows with a future `NextAttemptAtUtc` are waiting for backoff. `Processing` rows with an old lease are recovered on the next poll. `Failed` rows need a manual retry after the SMTP problem is fixed. Scale the worker above one replica only if the lease query stays fast; do not scale it to zero, because nothing else wakes it.

## Failed notifications

Open the notification page, read `lastError`, fix Mailpit or the SMTP host, and retry. A retry can send a duplicate message. That matches at-least-once delivery.

## Deployment

1. Build images and push them to the registry in the Bicep deployment.
2. Apply `infra/bicep/grant-sql-users.sql` once, as the SQL administrator, after the managed identities exist.
3. Start the manual migration job. Do not start the new API revision first.
4. Confirm `/health/ready`.
5. To roll back the app, point the container apps at the previous image tag. Roll the database back only with a reviewed migration `Down` or a restore. Forward-only migrations are safer.

## Cost and cleanup

The template creates a Log Analytics workspace, Application Insights, a Basic registry, a storage account, Azure SQL General Purpose serverless (1 vCore, 60-minute auto-pause, 2 GB max in the default parameters), a Container Apps environment, an API app, a worker that stays at least at one replica, and a manual migration job. The main cost drivers are SQL compute while awake, Container Apps vCPU-seconds, and Log Analytics ingestion. This is not a promise of free hosting and it does not quote prices.

```bash
az group delete --name YOUR_RESOURCE_GROUP
```

Delete the resource group to remove the resources this template creates. It does not delete backups retained by Azure SQL outside the group until the backup window ends.

## Development firewall

Add your workstation IP with the `allowedSqlIps` parameter. Do not add `0.0.0.0/0`. From a developer machine, `Azure AD` authentication with `Encrypt=True;TrustServerCertificate=False` is the intended Azure SQL path.
