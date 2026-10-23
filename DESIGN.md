# Design Notes

This is a runnable slice of the accounts-payable system sketched in `docs/design.png`: companies pay contractors against invoices, singly or in batches, with duplicate-payment protection, tenant isolation, payment detail and a statement. `PLAN_1.md` is the implementation plan this was built from; this document records what was decided, what was traded off, and what was deliberately left out. A cleaner redrawing of the whiteboard is in [`docs/design-refined.svg`](docs/design-refined.svg).

## 1. What was built

| Area | Where | Notes |
|---|---|---|
| Domain model | `Plooto.Ap.Domain` | `Company`, `CompanyPreferences`, `Contractor`, `Invoice`, `Payment`, `ApprovalRequest`, plus `IdempotencyRecord`, `PaymentEvent` (outbox) and `StatementLine` (read model). State machines throw `DomainRuleException` on illegal transitions. No EF or ASP.NET references. |
| Persistence | `Plooto.Ap.Infrastructure` | EF Core 8 + SQLite, one `InitialCreate` migration applied at startup, one thin repository per aggregate. |
| Validation Service | `Plooto.Ap.Services.Validation` | Synchronous business rules before any row or message exists. |
| Payment Service | `Plooto.Ap.Services.Payment` | `PaymentService` (transactional writes) and the consumers `PaymentProcessor`, `StatementProjector`, `PaymentRailsDispatcher`, plus a simulated payment rail. |
| Notification Service | `Plooto.Ap.Services.Notification` | `NotificationDispatcher` (console log). |
| Messaging | `Plooto.Ap.Messaging` | Outbox publisher, `OutboxDispatcher`, in-process channel transport, consumer base class, `CdcProjector`. |
| API host | `Plooto.Ap.Api` | The single deployable. Minimal API, API-key auth, idempotency filter, the `PaymentOrchestrator`. |
| Client | `Client.Sdk`, `Client.UseCases`, `Client.Console` | Typed `ApiClient`, CSV import, one use-case class per menu option, a thin console shell. |
| Tests | `tests/*` | Domain, repositories, services, messaging pipeline, HTTP integration, client. |

### Diagram to code

| Diagram | In code |
|---|---|
| LB, App Server | Kestrel hosting `Plooto.Ap.Api` (single instance locally; the LB is a deployment concern) |
| `tb` (diamond between App Server and validation) | `PaymentOrchestrator`: sequences Validation, then Payment. A router, not a queue. |
| validation srv | `ValidationService` |
| `notifications` queue, notixn srv, Approver Service/UI | outbox rows on queue `notifications`, `NotificationDispatcher`, `POST .../approvals/{id}/decision` |
| unlabeled rectangle (validation to payment) | queue `validated-payments`, consumed by `PaymentProcessor` |
| tb company prefs | `CompanyPreferences` (approval threshold, default currency, rail), `GET/PUT .../preferences` |
| payment serv | `PaymentService` + `PaymentProcessor` |
| pmt rails | `IPaymentRailClient` / `SimulatedPaymentRailClient` |
| pymtevt | `PaymentEvent` table (the transactional outbox) |
| CDC | `OutboxDispatcher` + `CdcProjector` (queue `payment-events-cdc`) |
| Pymt Queue | queues `pymt-queue` (`StatementProjector`) and `pymt-queue-rails` (`PaymentRailsDispatcher`) |

These mappings are interpretations of a hand-drawn sketch; the ones that were judgement calls are listed under Assumptions.

## 2. Key design decisions

**Single deployable, service-per-project.** Each box from the diagram is its own class-library project so the boundaries are visible in the solution, but they all run inside one host. A `.csproj` boundary is not a process boundary, and `System.Threading.Channels` cannot cross a process boundary at all. One process is one Azure compute unit instead of four. The rule that keeps this honest: `Services.*` projects never reference each other, only communicate through `IEventPublisher`, and share only types that live in `Domain`. Splitting one out later means swapping the transport, not redesigning calls.

**Transactional outbox with an in-process channel transport.** A payment write, its approval request and its outbox event commit in one SQLite transaction. `OutboxDispatcher` relays committed rows onto a named `Channel<T>` and consumers react. The channel message is only a doorbell carrying the outbox row id: the consumer re-reads the row and marks it `Processed` only when its work is finished. That gives honest at-least-once delivery, including after a crash (a startup sweep and a periodic sweep re-queue rows stuck in `Dispatched`). Every handler is therefore idempotent by checking current state, not by trusting the message. Failed attempts back off exponentially and dead-letter after five tries. The `IEventPublisher` and `IMessageTransport` seams are where Azure Service Bus would be swapped in with no producer or consumer changes.

**Duplicate payments are prevented in layers, and the database is the last one.**
1. `Idempotency-Key` header (required on both payment POSTs). The key is claimed with an atomic `INSERT ... ON CONFLICT DO NOTHING` on `(CompanyId, Key)`. Same key and same body replays the stored response; same key with a different body is a 409; a concurrent duplicate gets a 409 with `Retry-After`; a stale in-progress claim (crashed worker) is taken over after 30s.
2. `Payment.IdempotencyKey` is unique per company, so even a service-level bug cannot insert two payments for one key.
3. A filtered unique index allows at most one live payment (`Pending`, `Approved`, `Processing`, `Completed`) per invoice. `Failed` and `Cancelled` payments release the invoice so it can be retried with a new key. This also covers the same invoice appearing in two different batches.
4. The rail call is idempotent on `PaymentId`, so a redelivered message cannot double-transfer on a real rail.

**Tenant isolation, twice.** Every repository method takes an explicit `companyId` and filters on it. In addition, `CompanyScopeFilter` rejects any request whose route `{companyId}` differs from the API key's company with a 403, before the handler runs. An id that belongs to another tenant but is requested under your own company route returns 404, indistinguishable from "does not exist". Global EF query filters were considered and rejected: they need a scoped "current company" accessor and `IgnoreQueryFilters` mistakes are silent, whereas explicit parameters are easy to test.

**Authentication is a deliberately minimal static API key per company** (README: "simple token-based authentication for demonstration purposes"). The key is random, shown once on company creation, and only its SHA-256 is stored. Company creation is the one open endpoint, because it is how a caller obtains a key.

**Approvals.** Preferences can require human sign-off above a threshold. Such a payment stays `Pending` and an `ApprovalRequested` event goes to `notifications`. The decision endpoint stands in for the out-of-scope Approver Service/UI. Approve releases it to `validated-payments`; reject cancels it and leaves the invoice `Submitted` so it can be resubmitted. Decisions are one-shot, guarded by a concurrency token.

**Batch payments** return `207 Multi-Status` with a per-item result. Items are independent (partial success is representable), each gets a key derived from the batch key, and a batch where nothing succeeded is not cached so the client can fix it and retry with the same key.

**Failure semantics of the payment rail.** An explicit decline fails the payment immediately. A timeout or transient error is retried via outbox backoff and only fails the payment on the last attempt, because the transfer may actually have succeeded. The timeout is enforced with `WaitAsync` so a rail that ignores cancellation cannot block the consumer.

**Money and time on SQLite.** `decimal` is stored as TEXT, so the `Amount > 0` CHECK casts to REAL, and sums are done in memory. `DateTimeOffset` is stored as UTC ticks so range filters and ordering work. Enums are stored as strings.

**Errors.** Business outcomes travel as `ServiceResult` values; exceptions are for bugs. A small middleware maps `DomainRuleException` to 409, argument errors and malformed bodies to 400, and everything else to a generic 500 that leaks nothing.

**Client architecture.** `Client.Sdk` (HTTP, CSV) and `Client.UseCases` (one class per menu option) never touch `System.Console` or file paths; CSV import and template download are stream-based. The console project is only prompts and printing. A Blazor front-end would reference `Client.UseCases` and replace the prompts with forms and an `InputFile`, with no other change. The `ApiClient` retries network errors and 5xx with backoff, never retries 4xx, and never retries a POST that has no idempotency key.

## 3. Trade-offs considered

| Choice | Alternative | Why this one |
|---|---|---|
| In-process `Channel<T>` + outbox | MassTransit in-memory; real broker | Zero infrastructure, runs with `dotnet run`, durable via the outbox. MassTransit's in-memory transport loses unconsumed messages on a crash, and adding its outbox brings back the complexity of the hand-rolled one. |
| One host, many projects | One Web API per service | Cheaper and simpler to operate; channels cannot span processes. Splitting is possible later but requires the Service Bus swap. |
| Explicit `companyId` parameters | Global query filters | Easier to reason about and test; mitigated by the HTTP-level scope filter. |
| Records processed by state checks | Exactly-once machinery | True exactly-once is not available locally; idempotent handlers over at-least-once delivery are honest and testable. |
| Static API keys | OAuth/OIDC | In keeping with the exercise scope. |
| SQLite + EF migrations | Postgres/SQL Server | Matches the README's choice; the schema and queries avoid SQLite-only tricks except where noted (filtered unique index, raw upsert, which both exist in Postgres). |
| Statement as a projected read model | Compute on read from payments | Demonstrates the `Pymt Queue` consumer path; costs eventual consistency, which the tests handle by polling. |
| Reject on `Items` limit of 100 | Unbounded batch | Bounds request time and transaction size. |

## 4. Intentionally scoped out

- **True CDC.** Debezium/WAL tailing is impractical on SQLite. The outbox row plus a polling dispatcher is the local approximation, labelled as such.
- **Real payment rails, real notifications, an approver UI.** A deterministic simulator (cents of `.13` decline, `.66` never answers), console log lines, and a decision endpoint stand in.
- **Approval SLA/timeout.** A payment awaiting approval stays `Pending` indefinitely.
- **Reconciliation of dead-lettered processing events.** If a processing event is dead-lettered (for example a persistent database fault), the payment is left `Processing` and needs an operator. A reconciliation sweep is the obvious next feature.
- **Hard deletes of financial records.** Invoices are voided, contractors deactivated; payments are append-only and status driven.
- **Multi-currency statements.** Lines carry a currency, but the running balance sums amounts without conversion.
- **Pagination everywhere.** Contractors are paged; invoice and payment lists are capped at 500 rows. Statement is not paged.
- **Rate limiting, key rotation, audit trail of reads, webhooks/SignalR for status push** (clients poll).
- **Docker image was authored but not built in the development environment** (no Docker installed there); the `Dockerfile` follows the project layout and the run instructions note this.

## 5. Assumptions (reading the whiteboard)

- `tb` is a router/orchestrator, not a store or queue.
- The unlabeled rectangle between validation and payment is one queue with an acknowledgement path, not two queues.
- `pymtevt` and the outbox are the same thing; `Pymt Queue` fans out to two consumers (statement projection and rail/notification dispatch), modelled as two subscriptions.
- An invoice is `Submitted` when captured through the API or CSV; it becomes `Approved` when cleared for payment (automatically, or by an approver) and `Paid` when the rail confirms.
- A payment under the approval threshold is auto-approved by company policy, so `Pending` always means "waiting for a human".
- Payments are denominated in the invoice's currency; company default currency applies when none is given.

## 6. How the non-functional requirements map

| Requirement | Today | To scale |
|---|---|---|
| ~30,000 payments/day (about 0.35/s average, perhaps tens/s at peak) | Comfortably within one process and one SQLite file in WAL mode. | Postgres, Service Bus for the transport, horizontal API replicas (consumers become competing subscribers). |
| 100,000 users | Stateless API with per-company key lookup (indexed hash). | Cache key lookups; move to short-lived tokens. |
| Single payment latency | The API call is a handful of indexed queries and one transaction; the rail runs asynchronously, so request latency does not include it. | Same. |
| "Five nines" | Not claimed: one process, one file. | Multiple instances behind a load balancer, managed database with failover, broker with redelivery and dead-lettering. The outbox, idempotency and replay-safe handlers are what make running several instances safe. |
| Correctness / no duplicates | Layered protection above, covered by concurrency tests. | Unchanged. |
| Data isolation | Route filter + explicit scoping + tests for every write and read route. | Per-tenant encryption or row-level security in the database. |

## 7. Testing approach

Real SQLite everywhere (in-memory with the real migrations, or a temp file when background services run concurrently), because the EF in-memory provider would not enforce the unique indexes and CHECK constraints the duplicate-payment guarantees rest on. Highlights: concurrent identical requests produce exactly one payment; the outbox protocol is exercised step by step and end to end (lost doorbell, duplicate delivery, consumer failure, dead letter, rail decline and hang); the full HTTP surface runs through `WebApplicationFactory`; the console's use cases run against the real API.

`dotnet test` runs everything with no external services.
