# Interview demo

Prerequisites: migrated database, development seed, API on port 5080, worker running, Mailpit on port 8025, and `Seed__DemoPassword` set locally.

1. Sign in as `tech.a.northwind@opsflow.example` and `tech.b.northwind@opsflow.example` in two browsers. Open the same unassigned work order. Click Claim in both. One succeeds. The other shows a conflict. The database has one assignee, one claim audit row, and one outbox row.
2. Repeat a create with the same `Idempotency-Key` and body. The work-order id does not change and no second row appears. Change the title and reuse the key. The API returns 409.
3. As a dispatcher, move a work order to on hold. The SLA pill says paused. Resolve it, then reopen it as an admin. Resolution time clears and the original due time remains. To show escalation without waiting for a long policy, set `DueAtUtc` in the past on a disposable work order and watch the worker write a single `SlaEscalated` outbox row. Mailpit shows the message. Run the worker cycle again and confirm there is still one escalation.
4. For the index, follow the benchmark command in the README against `OpsFlowBenchmark` only. Compare logical reads and the plan snippet in `benchmarks/results/latest.json`. If that file is absent, say the result was not measured.

`scripts/concurrency-demo.sh` runs the claim race with curl when `OPSFLOW_DEMO_ORG_ID` and `Seed__DemoPassword` are set.
