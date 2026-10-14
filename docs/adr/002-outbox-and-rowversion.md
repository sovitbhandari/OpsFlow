# ADR 002: Rowversion, conditional claim, and outbox

Two technicians can claim the same row from different processes. An in-process lock would not work across API replicas. The claim is a single conditional `UPDATE` in the same transaction as the audit and outbox rows. Other edits use `rowversion` and return 412 when the token is stale.

Email is not sent inside the database transaction or the EF retry loop. The outbox is at-least-once because SMTP success and the database acknowledgement cannot be one atomic commit.
