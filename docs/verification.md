# Verification report

Recorded on this machine on 13 October 2026. Counts below are the commands that actually ran. Nothing in this file is an estimate of coverage, throughput, or cloud deployment.

## Passed

| Check | Result |
| --- | --- |
| `dotnet test tests/OpsFlow.UnitTests` | Passed 14, failed 0, skipped 0 |
| `OPSFLOW_USE_TESTCONTAINERS=true dotnet test tests/OpsFlow.IntegrationTests` | Passed 9, failed 0, skipped 0. Duration reported by the runner: 44 s. SQL Server 2022 started with Testcontainers |
| `OPSFLOW_USE_TESTCONTAINERS=true dotnet test tests/OpsFlow.WorkerTests` | Passed 2, failed 0, skipped 0. Duration reported by the runner: 15 s |
| `npm run lint` and `npm run build` in `src/opsflow-web` | Lint reported no warnings. Typecheck and Vite production build completed |
| `GET /health/live` and `GET /health/ready` on the local API | Both returned `Healthy` against the local SQL database `OpsFlow` |
| OpenAPI document `GET /openapi/v1.json` | 22 operations. The React client calls only paths present in that document |
| `docker build` for `Dockerfile.api` and `Dockerfile.worker` | Images `opsflow-api:local` and `opsflow-worker:local` were created. SDK image `mcr.microsoft.com/dotnet/sdk:10.0.402`, runtime image `mcr.microsoft.com/dotnet/aspnet:10.0.13` |
| Browser walkthrough on http://127.0.0.1:5173 | Requester sign-in, dashboard queue loaded from the API, create work order WO-1005, technician claim, status change to In progress with activity entries, Harborline requester opening that work order sees “Work order was not found.” |

Integration coverage that passed includes unauthenticated requests, missing antiforgery, cross-tenant 404, in-organization 403, invalid and valid attachments, concurrent claims with one winner, stale `If-Match` 412, idempotent replay, a different payload for the same key, concurrent duplicate creates, transaction rollback with no leftover audit or outbox rows, and the reporting procedure’s organization and date filter.

Worker coverage that passed includes retry then dead-letter, lease recovery, and duplicate SLA escalation prevention.

## Executed and not a success metric

| Check | Result |
| --- | --- |
| `dotnet run --project benchmarks/OpsFlow.Benchmark` without `OPSFLOW_BENCHMARK_CONNECTION` | Printed `not measured` and the required connection message. No timings or plans were collected |
| Optional SQL Server Compose profile on this Apple Silicon Mac | `opsflow-sqlserver-1` became healthy, `OpsFlow` was created, migrations applied, and the development seed ran. Microsoft still does not officially support this emulation; a later start can fail |

## Not executed

| Check | Why |
| --- | --- |
| Playwright `e2e/critical.spec.ts` | Playwright browsers were not installed. The same flows were exercised in a local browser instead |
| `az bicep build` | The Azure CLI is not installed on this machine |
| Azure deployment | Not authorized. No resources were created |
| Query benchmark before/after the composite index | No disposable database named with `OpsFlowBenchmark` was configured |
| NuGet restore inside a clean CI runner | Lock files were generated locally with `RestorePackagesWithLockFile`. CI has not been observed |

## Fixes applied while verifying

- Login tests and the SPA now send the anonymous antiforgery token on login, then request a new token after the authentication cookie is issued.
- Organization membership listing orders by name before projecting the DTO, so EF can translate the query.
- The auth provider is mounted around the router. Without it the UI threw before rendering.
- Container SDK tags use `10.0.402`. `10.0.13` is the ASP.NET runtime tag, not an SDK tag.
- Login and write rate-limit permits are configuration values so the integration host can raise them without changing production defaults.
