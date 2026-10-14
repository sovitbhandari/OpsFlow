# OpsFlow

OpsFlow is a work-order and service-management application: an ASP.NET Core modular monolith, a separate .NET worker, a React UI, and SQL Server persistence.

It runs natively on macOS (Apple Silicon) and in Linux containers. Azure SQL is the preferred database. A SQL Server 2022 container is optional and is not started unless you select that Compose profile.

## Stack

- .NET SDK 10.0.402, ASP.NET Core, C#, EF Core 10.0.13 SQL Server provider
- React, TypeScript, Vite, TanStack Query, React Router
- xUnit, Testcontainers for SQL Server on Linux CI, Playwright for a small browser suite
- OpenAPI plus Scalar at `/scalar` during development
- Docker Compose, GitHub Actions, Azure DevOps YAML, Bicep

## SQL Server on Apple Silicon

The optional SQL Server 2022 image is `linux/amd64`. Docker Desktop can try to emulate it, but Microsoft does not officially support SQL Server containers on Apple Silicon. The container may fail to start or crash. Azure SQL Database is the supported remote database. This repo does not use Azure SQL Edge, LocalDB, SQLite, PostgreSQL, or the EF InMemory provider as stand-ins.

Local SQL connections may set `TrustServerCertificate=True` because the container uses a development certificate. Azure SQL connection strings must use `Encrypt=True` and `TrustServerCertificate=False`.

## Local setup

Install the ARM64 .NET 10 SDK, Node.js 22 LTS, and Docker Desktop. Trust the HTTPS development certificate only if you terminate TLS locally (`dotnet dev-certs https --trust`). The default launch profile is HTTP on port 5080 so the Vite proxy can store cookies.

```bash
cp .env.example .env
# Quote connection strings in .env. They contain spaces, and an unquoted value is split by the shell.
# Set ConnectionStrings__OpsFlow and Seed__DemoPassword in your shell or user secrets.
# Demo password: at least 12 characters with upper, lower, digit, and symbol.
# Never put a real password in the repository.

docker compose up -d
dotnet run --project src/OpsFlow.Api -- --migrate
ASPNETCORE_ENVIRONMENT=Development Seed__DemoPassword='your-local-password' dotnet run --project src/OpsFlow.Api -- --seed
dotnet run --project src/OpsFlow.Api
dotnet run --project src/OpsFlow.Worker
npm ci --prefix src/opsflow-web
npm run dev --prefix src/opsflow-web
```

- API: http://localhost:5080
- Vite UI: http://localhost:5173 (proxies `/api`)
- Mailpit: http://localhost:8025 (SMTP localhost:1025)
- Scalar: http://localhost:5080/scalar

Seed runs only when `ASPNETCORE_ENVIRONMENT=Development` and `Seed__DemoPassword` is set. It creates Northwind Facilities and Harborline Medical, plus one user in both organizations. It does nothing if organizations already exist. It refuses placeholder password `REPLACE_ME`.

Optional local SQL, only when you are not using Azure SQL:

```bash
docker compose --profile sql up -d
```

Use a connection string whose database is one you can drop, with `Encrypt=True;TrustServerCertificate=True` for that container only.

The API does not apply migrations on startup. Use `scripts/migrate.sh` or `dotnet run --project src/OpsFlow.Api -- --migrate`.

## Environment variables

| Name | Purpose |
| --- | --- |
| `ConnectionStrings__OpsFlow` | SQL Server or Azure SQL connection string |
| `Seed__DemoPassword` | Development seed password. Not used in production startup |
| `Attachments__Provider` | `Local` or `AzureBlob` |
| `Attachments__LocalPath` | Development file root |
| `Attachments__BlobContainerUri` | Private container URI when using Azure Blob |
| `DataProtection__KeysPath` | Local data-protection key directory |
| `DataProtection__BlobUri` | Blob URI for shared keys in Azure |
| `Smtp__Host` / `Smtp__Port` | Defaults to Mailpit. Real SMTP is off unless you point these at a server |
| `OpenTelemetry__OtlpEndpoint` | Optional OTLP endpoint |
| `OPSFLOW_USE_TESTCONTAINERS` | `true` to start SQL Server for integration tests |
| `OPSFLOW_TEST_CONNECTION` | Dedicated test database whose name contains `OpsFlowTests` |
| `OPSFLOW_BENCHMARK_CONNECTION` | Disposable database whose name contains `OpsFlowBenchmark` |
| `SQL_SA_PASSWORD` | Only for the optional Compose SQL profile |
| `RateLimiting__LoginPermitLimit` | Login attempts per minute per API instance. Default 10 |
| `RateLimiting__WritePermitLimit` | Authenticated writes per minute per API instance. Default 120 |

## Tests

```bash
dotnet test tests/OpsFlow.UnitTests
OPSFLOW_USE_TESTCONTAINERS=true dotnet test tests/OpsFlow.IntegrationTests
OPSFLOW_USE_TESTCONTAINERS=true dotnet test tests/OpsFlow.WorkerTests
```

Integration tests fail, rather than skip, when neither Testcontainers nor `OPSFLOW_TEST_CONNECTION` is configured. They refuse to run unless the database name contains `OpsFlowTests`. CI on `ubuntu-24.04` sets `OPSFLOW_USE_TESTCONTAINERS=true`.

Playwright lives in `e2e/`. Install browsers with `npx playwright install`, seed the database, start the API and Vite app, then `npm test` in `e2e/` with `Seed__DemoPassword` set.

## Benchmarks

Results in this repository are **not measured** until you run the benchmark yourself.

```bash
OPSFLOW_BENCHMARK_CONNECTION='Server=...;Database=OpsFlowBenchmark;...' dotnet run --project benchmarks/OpsFlow.Benchmark
```

The tool migrates that database, seeds 100,000 work orders if needed, drops only `IX_WorkOrders_Org_Status_Created_Number` after the name check, measures, then recreates the index. It will not run against a database whose name does not contain `OpsFlowBenchmark`.

## Azure

No cloud resources are created by cloning or building this repo. Deployment is a manual GitHub Actions `workflow_dispatch` or `scripts/deploy-azure.sh`. See `docs/runbook.md` and `infra/bicep/main.bicep`.

## Documentation

- `docs/architecture.md`
- `docs/permissions.md`
- `docs/concurrency-and-idempotency.md`
- `docs/worker.md`
- `docs/database.md`
- `docs/security.md`
- `docs/runbook.md`
- `docs/demo.md`
- `docs/verification.md`
- `docs/progress.md`
