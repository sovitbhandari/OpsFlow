# ADR 001: Modular monolith and direct EF use

OpsFlow keeps HTTP, domain rules, and persistence in one deployable API plus a worker. Microservices, a generic repository, CQRS, and event sourcing would split a small domain without a scaling requirement. Services call EF Core directly so conditional updates and transactions stay visible. The worker is a separate process so a slow SMTP call does not occupy API threads, not because it is a separate bounded context.
