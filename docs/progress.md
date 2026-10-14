# Implementation progress

OpsFlow is implemented in this repository as a modular monolith API, a separate worker, shared domain and infrastructure libraries, and a React client.

Completed:

- Organizations, memberships, cookie authentication, antiforgery, and the role matrix
- Work orders, comments, activity, SLA deadlines, attachments, audit, and reporting
- Concurrent claim, rowversion conflicts, and persistent idempotency
- Outbox worker, Mailpit SMTP, escalation uniqueness, retries, and dead-letter admin retry
- EF migration, tenant constraints, and `dbo.usp_OrganizationWorkOrderReport`
- Docker Compose profiles, Bicep, GitHub Actions, and Azure DevOps YAML
- Unit, SQL integration, and worker tests that have been executed locally

Still open, with the reason recorded in `docs/verification.md`:

- Playwright has not been executed
- The 100,000-row benchmark has not been measured
- Bicep has not been compiled here because the Azure CLI is absent
- No Azure resources have been deployed
