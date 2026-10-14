# Concurrency and idempotency

## Claim

`POST /api/organizations/{organizationId}/work-orders/{id}/claim` updates the row only when `OrganizationId`, an empty assignee, and status `Open` all match. The statement is `ExecuteUpdate` inside the same database transaction as the activity, audit row, and outbox message. SQL Server serializes that conditional update. The process does not take an in-memory lock. One transaction commits; the other matches zero rows and returns 409.

Edits, assignment, unassignment, and status changes send the current `rowversion` in `If-Match`. EF compares that token as the original concurrency value. A stale token returns 412. The UI tells the user to refresh. It does not overwrite the other person's change, and claim does not show success before the server responds.

## Idempotency

Create and claim require `Idempotency-Key` (8 to 128 characters). The key is unique per organization, user, and operation. The stored hash is a canonical fingerprint of the payload. The same key and payload return the stored status and body after authorization is checked again. The same key with a different payload returns 409.

The first request inserts an in-progress row with a lease (default 120 seconds) and commits it. The business change and the completed response commit in a second transaction. A concurrent insert hits the unique index. If the first request is still inside its lease, the second receives 409. If the lease expired, the second request takes the lease with a conditional update and continues. Completed rows expire after 24 hours. The worker deletes expired completed rows and abandoned in-progress rows.

EF's SQL retry strategy re-runs the transaction delegate. The delegate clears the change tracker first and only writes to the database. Mail is not sent inside that delegate.

## SLA and reopen

Policies are elapsed time per priority. Default targets are 7 days, 3 days, 24 hours, and 4 hours. On hold pauses the clock: the due time is creation plus the policy plus completed hold time, and overdue checks ignore `OnHold`, `Resolved`, `Closed`, and `Cancelled`. Leaving hold adds the paused interval to the due time and returns the work order to the status it had before the hold.

Reopening clears `ResolvedAtUtc` and `ClosedAtUtc`. It does not reset the SLA. The work order becomes `InProgress` when someone is assigned and `Open` otherwise, and it can already be overdue.

## Search

Number searches use equality on the organization-scoped number. Text search uses `LIKE '%term%'` with escaped wildcards. A leading wildcard is not indexed full-text search. It can scan a large organization. That limitation is intentional in this version.
