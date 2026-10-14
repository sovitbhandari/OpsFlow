# Worker

`OpsFlow.Worker` is a `BackgroundService`. Run it with `dotnet run --project src/OpsFlow.Worker` or the Linux container. It does not install a Windows Service.

Each cycle:

1. Finds overdue work orders in `Open`, `Assigned`, or `InProgress` whose due time has passed and that have no escalation row.
2. Inserts one `SlaEscalations` row, an outbox message, activity, and audit in one transaction. `WorkOrderId` is unique, so two workers cannot record two escalations for the same work order.
3. Claims outbox rows with `READPAST, UPDLOCK, ROWLOCK` and an expiring lease. A second worker skips locked rows and does not take an active lease.
4. Sends one SMTP message per recipient. Attempt count, next attempt, and the last error are stored. Delay is exponential with jitter, capped at five minutes. After the configured attempts (default 5) the job becomes `Failed`.
5. Deletes expired idempotency records.

Admins can list jobs and retry failed or completed ones. Retry clears the lease and attempt count and queues the job again.

## Delivery

The outbox commit and the SMTP send are different steps. If the process crashes after the server accepts the message and before SQL marks the job succeeded, the lease expires and another attempt sends it again. Ordinary SMTP cannot provide exactly-once delivery. OpsFlow is at-least-once.

Logs include job id, type, and attempt count. They do not include the message body, passwords, or attachment bytes. Mailpit is the default sink. Nothing in the default configuration sends mail to a public provider.
