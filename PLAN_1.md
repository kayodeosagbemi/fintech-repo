# PLAN_1 — Accounts Payable System: Systematic Implementation Plan

This plan turns the hand-drawn interview design (`docs/design.png`) and the README's notes into a concrete, buildable C# / .NET 8 slice. It is written to be handed to a fresh Claude session (or any engineer) with no other context and implemented with high fidelity — every section names actual class names, routes, tables, and test names rather than describing intentions.

**How this plan was produced:** three parallel planning passes, each scoped to one layer (domain/persistence, API/auth/idempotency, messaging architecture), were merged into this single document. Cross-references between sections (e.g. "see messaging plan") refer to §3.

**Refined architecture diagram:** [`docs/design-refined.svg`](docs/design-refined.svg) — a reinterpretation of `docs/design.png` with every shape labeled with its concrete technology (queue = named `Channel<T>` + consumer class, cylinder = SQLite table, dashed box = external/simulated/out of scope). §3.1 documents the mapping and flags every interpretive assumption explicitly.

> **Addendum (Round 2):** sections §4–§8 below revise and extend the original §1–§3 in response to follow-up requirements: fewer deployed components (lower Azure cost), explicit error-path coverage, a service-per-project solution layout with an explicit answer on whether `Channels` can cross process boundaries (they can't — see §3.3.3), a console test client, a CSV invoice-import format, and a client architecture that lets a Blazor front-end replace the console app without touching business logic. §1–§3 remain correct as written except where §4–§8 explicitly supersede or amend them (callouts are inline).

**Suggested implementation order** (ties all sections together into one sequence of commits — matches the README's "commit early and incrementally" expectation):

1. Solution scaffold: all projects per §4 (`Domain`, `Infrastructure`, `Messaging`, `Services.Validation`, `Services.Payment`, `Services.Notification`, `Api`, `Client.Sdk`, `Client.UseCases`, `Client.Console`) + test projects. Commit.
2. Domain entities + invariants (§1.1, including the `Payment.Approve()`/`MarkCancelled()` additions from §5.1), with `InvoiceTests`/`PaymentTests` (§1.5) passing against no DB. Commit.
3. EF Core + SQLite wiring, migrations, `data/` folder + `.gitignore` entry (§1.4). Commit.
4. Repositories (§1.3) + `ValidationService`/`PaymentService` orchestration, placed in their §4 projects, with repository/service tests (§1.5) against real SQLite. Commit.
5. Messaging backbone: `IEventPublisher`, `Channel<T>`-based local transport, `OutboxDispatcher` with the retry/dead-letter columns from §5.4, and the consumer `BackgroundService`s (`PaymentProcessor`, `NotificationDispatcher`, `PaymentRailsDispatcher`, `StatementProjector`) — all registered inside the single `Plooto.Ap.Api` host per §3.3.3/§4. Commit.
6. Minimal API endpoints wired to repositories/services (§2.2), including the approval-decision endpoint (§5.3), unauthenticated first to validate the happy path end-to-end. Commit.
7. API key auth (`ApiKeyAuthenticationHandler`, `CompanyScopeFilter`, §2.3) + `AuthenticationTests`/`DataIsolationTests` (§2.5). Commit.
8. Idempotency (`IdempotencyRecord`, request flow, §2.4) + `IdempotencyTests` including the concurrent-duplicate-request test (§2.5). Commit.
9. Batch payments + `BatchPaymentTests`, statement endpoint, approval-rejection path end-to-end, and the failure-mode tests from §5.2. Commit.
10. CSV invoice-import format: `InvoiceCsvService` (parse/template) in `Client.Sdk` (§7), with `Client.Tests` covering the row-error cases. Commit.
11. Console client: `Client.UseCases` + the menu-driven `Client.Console` front-end (§6), exercised against a running API instance. Commit.
12. `DESIGN.md`, `RUN-INSTRUCTIONS.md` (update to mention the console client), `AI-TRANSCRIPT.md` per the README's required artifacts — fold in this plan's documented assumptions (§3.1) and the Round 2 trade-offs (§3.3.2/§3.3.3/§5.4) as "trade-offs considered" / "scoped out" content. Commit.

Steps 2–4 can run before 5–9 are fully designed; 5 (messaging backbone) should land before 6 wires payment creation to it, since payment creation needs `IEventPublisher` injected from the start to keep the outbox write in the same transaction as the domain write (§3.3). Steps 10–11 depend on 6–9 being reachable over HTTP, but not on each other's internals beyond the shared DTOs (§8.1).

---

## 1. Domain Models, CRUD & Persistence

### 1.1 Nouns → C# classes

Source: README noun list (`Company`, `Contractor/Payee`, `Invoice`, `Payment`) plus diagram entities that imply additional persisted state (`validation srv`, `notixn srv`, `payment serv` writing to `pymtevt`/CDC/`Pymt Queue`, `tb` = transaction/idempotency store, `tb company prefs` = per-company preferences).

All classes live in `Plooto.Ap.Domain`, namespace `Plooto.Ap.Domain.Entities` unless noted. Use `record class` for entities with mutable collections avoided; plain mutable properties with `private set` + factory/methods for invariants (not full DDD — keep pragmatic, but *forbid* public parameterless mutation of money/status fields — route through methods so invariants hold).

```csharp
namespace Plooto.Ap.Domain.Entities;

public class Company
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = default!;
    public string Slug { get; private set; } = default!;     // used as tenant key in claims
    public DateTimeOffset CreatedAt { get; private set; }

    public static Company Create(string name, string slug) { ... }
}

public class CompanyPreferences            // "tb company prefs" in diagram
{
    public Guid CompanyId { get; private set; }               // PK = FK, 1:1 with Company
    public string DefaultCurrency { get; private set; } = "USD";
    public bool RequireApprovalAboveThreshold { get; private set; }
    public decimal ApprovalThreshold { get; private set; }
    public string PreferredPaymentRail { get; private set; } = "ACH"; // see "pmt rails" diamond
}

public class Contractor                     // "Contractor/Payee"
{
    public Guid Id { get; private set; }
    public Guid CompanyId { get; private set; }  // tenant isolation FK — every query filters on this
    public string Name { get; private set; } = default!;
    public string Email { get; private set; } = default!;
    public string PayoutAccountRef { get; private set; } = default!; // opaque bank/rail token, never raw PAN/account
    public bool IsActive { get; private set; } = true;
}

public enum InvoiceStatus { Draft, Submitted, Approved, Rejected, Paid, Void }

public class Invoice
{
    public Guid Id { get; private set; }
    public Guid CompanyId { get; private set; }     // isolation key
    public Guid ContractorId { get; private set; }
    public string InvoiceNumber { get; private set; } = default!; // external/human ref, unique per CompanyId
    public decimal Amount { get; private set; }       // > 0, enforced in factory + DB CHECK
    public string Currency { get; private set; } = "USD";
    public InvoiceStatus Status { get; private set; }
    public DateOnly DueDate { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ApprovedAt { get; private set; }

    public static Invoice Create(Guid companyId, Guid contractorId, string invoiceNumber, decimal amount, string currency, DateOnly dueDate) { ... }
    public void Approve() { ... }   // Status: Submitted -> Approved, else throws InvalidOperationException
    public void MarkPaid() { ... }  // called by PaymentService once Payment completes
}

public enum PaymentStatus { Pending, Approved, Processing, Completed, Failed, Cancelled }
public enum PaymentMethod { Single, Batch }

public class Payment
{
    public Guid Id { get; private set; }
    public Guid CompanyId { get; private set; }      // isolation key, denormalized from Invoice for cheap filtering
    public Guid InvoiceId { get; private set; }
    public Guid ContractorId { get; private set; }
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = default!;
    public PaymentStatus Status { get; private set; }
    public PaymentMethod Method { get; private set; }
    public Guid? BatchId { get; private set; }        // null for single payments, groups batch payments
    public string IdempotencyKey { get; private set; } = default!;  // see 1.1a and API plan
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public string? FailureReason { get; private set; }

    public static Payment Create(Guid companyId, Guid invoiceId, Guid contractorId, decimal amount, string currency, PaymentMethod method, string idempotencyKey, Guid? batchId = null) { ... }
    public void MarkProcessing() { ... }
    public void MarkCompleted() { ... }
    public void MarkFailed(string reason) { ... }
}
```

**Diagram-derived supporting entities** (needed for correctness/idempotency/outbox requirements, not just decoration):

```csharp
// "tb" diamond = idempotency/transaction ledger the App Server/validation srv consults before admitting a request
public class IdempotencyRecord
{
    public string Key { get; private set; } = default!;      // PK: client-supplied Idempotency-Key header value, scoped per CompanyId
    public Guid CompanyId { get; private set; }
    public string RequestHash { get; private set; } = default!; // hash of method+path+body to detect key-reuse-with-different-payload
    public int ResponseStatusCode { get; private set; }
    public string ResponseBody { get; private set; } = default!;
    public DateTimeOffset CreatedAt { get; private set; }
}

// "pymtevt" diamond -> "CDC" -> "Pymt Queue": payment lifecycle events persisted via the Outbox pattern
// before being relayed onto the message bus (see messaging plan for the relay/CDC mechanism).
public class PaymentEvent
{
    public long Id { get; private set; }               // auto-increment, used as outbox cursor
    public Guid PaymentId { get; private set; }
    public Guid CompanyId { get; private set; }
    public string EventType { get; private set; } = default!;  // PaymentCreated, PaymentApproved, PaymentCompleted, PaymentFailed
    public string PayloadJson { get; private set; } = default!;
    public DateTimeOffset OccurredAt { get; private set; }
    public DateTimeOffset? PublishedAt { get; private set; }   // null = not yet relayed to bus; set by outbox dispatcher
}

// "notifications" rectangle target; raised by validation srv, consumed by notixn srv / Approver Service
public enum ApprovalStatus { Pending, Approved, Rejected }

public class ApprovalRequest
{
    public Guid Id { get; private set; }
    public Guid CompanyId { get; private set; }
    public Guid InvoiceId { get; private set; }
    public ApprovalStatus Status { get; private set; }
    public string? ApproverEmail { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }
}
```

**Invariants summary**:
- Every tenant-owned row carries `CompanyId`; no query method may omit a `CompanyId` filter (enforced via explicit repository parameters, see 1.4's note on why global query filters were rejected for this scope).
- `Amount > 0` on Invoice/Payment — enforced both in the factory method (`ArgumentOutOfRangeException`) and a DB `CHECK` constraint as defense in depth.
- `Payment.CompanyId` must equal `Invoice.CompanyId` and `Contractor.CompanyId` — validated in `PaymentService.CreatePaymentAsync`, not left to the DB.
- `IdempotencyRecord.Key` + `CompanyId` is the uniqueness boundary (idempotency keys are not globally unique, only per-tenant) — see §2.4 for the header contract.
- Status transitions (`Invoice.Approve`, `Payment.MarkCompleted`, etc.) must validate the current state and throw `InvalidOperationException` on illegal transitions — this is what the "unit test invariants" tests in 1.5 target.

**Hand-off to other plan sections**: `PaymentEvent` rows are written in the same DB transaction as the `Payment` status change (outbox pattern) — the relay/dispatch of these rows onto the actual message bus, and what technology backs `notifications` / `Pymt Queue` / `CDC`, is specified in §3. §2 owns the `Idempotency-Key` HTTP header contract and how `IdempotencyRecord` is read/written inside the minimal API pipeline.

### 1.2 Project / folder structure

> **Superseded by §4.** This is the original minimal-project layout; §4 splits `Validation`/`Payment`/`Notification` out into their own class-library projects (still one deployable host) for solution-level clarity. `Domain` and `Infrastructure` below are unaffected by that change. Treat this subsection as background on the persistence-layer internals, and §4 as the authoritative top-level project list.

Original minimal layout — 4 projects total (3 src + 1 test):

```
/src
  Plooto.Ap.Domain/              # POCO entities, enums, value invariants. No EF, no ASP.NET references.
    Entities/
      Company.cs
      CompanyPreferences.cs
      Contractor.cs
      Invoice.cs
      Payment.cs
      PaymentEvent.cs
      IdempotencyRecord.cs
      ApprovalRequest.cs
    Enums/ (or inline with entities — fine for this size)

  Plooto.Ap.Infrastructure/      # EF Core, SQLite, repositories/services, outbox dispatcher
    Persistence/
      ApDbContext.cs
      Configurations/            # IEntityTypeConfiguration<T> per entity (Fluent API, not attributes)
        CompanyConfiguration.cs
        InvoiceConfiguration.cs
        PaymentConfiguration.cs
        ... etc
      Migrations/                # generated by `dotnet ef migrations add`
    Repositories/
      ICompanyRepository.cs / CompanyRepository.cs
      IContractorRepository.cs / ContractorRepository.cs
      IInvoiceRepository.cs / InvoiceRepository.cs
      IPaymentRepository.cs / PaymentRepository.cs
      IIdempotencyStore.cs / IdempotencyStore.cs
    Services/
      PaymentService.cs           # orchestrates Payment+Invoice+PaymentEvent in one transaction
    Messaging/                    # see §3.3 — IEventPublisher impl, OutboxDispatcher, Channels, consumers

  Plooto.Ap.Api/                  # Minimal API host — see §2 for endpoint/auth/idempotency details
    Program.cs
    Endpoints/
    Contracts/
    Auth/
    Middleware/
    data/                         # SQLite file lives here at runtime; .gitignore'd (see 1.4)

/tests
  Plooto.Ap.Tests/                # xUnit project covering Domain + Infrastructure
    Domain/
      InvoiceTests.cs
      PaymentTests.cs
    Infrastructure/
      PaymentRepositoryTests.cs
      InvoiceRepositoryTests.cs
      ContractorRepositoryTests.cs
      CompanyRepositoryTests.cs
      IdempotencyStoreTests.cs
      PaymentServiceTests.cs
    TestSupport/
      SqliteTestDbFactory.cs

  Plooto.Ap.Api.Tests/             # xUnit + WebApplicationFactory — see §2.5
```

`Plooto.Ap.Api` references `Infrastructure`; `Infrastructure` references `Domain`; `Domain` has zero project references. This keeps `dotnet test` fast and lets domain invariant tests run with no DB at all.

### 1.3 CRUD design per entity

**Recommendation**: use a thin repository interface per aggregate root (`Company`, `Contractor`, `Invoice`, `Payment`) backed directly by `ApDbContext`, injected into minimal API endpoint handlers — *not* a generic `IRepository<T>` and *not* raw `DbContext` injected straight into endpoints. Rationale: minimal APIs are terse enough that handlers calling `dbContext.Set<T>()` directly becomes untestable/duplicated validation logic across endpoints; one repository per aggregate keeps query-filter-by-CompanyId logic centralized in one place instead of scattered across handlers, which matters for the "data isolation" non-functional requirement. A generic repository adds indirection with no payoff at this scale (4 entities).

```csharp
public interface ICompanyRepository
{
    Task<Company?> GetAsync(Guid id, CancellationToken ct);
    Task<Company> CreateAsync(Company company, CancellationToken ct);
}

public interface IContractorRepository
{
    Task<Contractor?> GetAsync(Guid companyId, Guid contractorId, CancellationToken ct);
    Task<IReadOnlyList<Contractor>> ListAsync(Guid companyId, CancellationToken ct);
    Task<Contractor> CreateAsync(Contractor contractor, CancellationToken ct);
    Task UpdateAsync(Contractor contractor, CancellationToken ct);
    Task DeactivateAsync(Guid companyId, Guid contractorId, CancellationToken ct);  // soft delete, not hard delete
}

public interface IInvoiceRepository
{
    Task<Invoice?> GetAsync(Guid companyId, Guid invoiceId, CancellationToken ct);
    Task<IReadOnlyList<Invoice>> ListAsync(Guid companyId, InvoiceStatus? statusFilter, CancellationToken ct);
    Task<Invoice> CreateAsync(Invoice invoice, CancellationToken ct);
    Task UpdateAsync(Invoice invoice, CancellationToken ct);
}

public interface IPaymentRepository
{
    Task<Payment?> GetAsync(Guid companyId, Guid paymentId, CancellationToken ct);
    Task<Payment?> GetByIdempotencyKeyAsync(Guid companyId, string idempotencyKey, CancellationToken ct);
    Task<IReadOnlyList<Payment>> ListAsync(Guid companyId, CancellationToken ct);
    Task<IReadOnlyList<Payment>> ListByBatchAsync(Guid companyId, Guid batchId, CancellationToken ct);
    Task<Payment> CreateAsync(Payment payment, CancellationToken ct);
    Task UpdateAsync(Payment payment, CancellationToken ct);
}

// Orchestration service — not pure CRUD, composes Invoice+Payment+PaymentEvent+IdempotencyRecord in one DB transaction
public interface IPaymentService
{
    Task<Payment> CreateSinglePaymentAsync(Guid companyId, Guid invoiceId, string idempotencyKey, CancellationToken ct);
    Task<IReadOnlyList<Payment>> CreateBatchPaymentAsync(Guid companyId, IReadOnlyList<Guid> invoiceIds, string idempotencyKey, CancellationToken ct);
    Task<Payment> CompletePaymentAsync(Guid paymentId, CancellationToken ct);  // called by payment-rail callback/worker
}
```

No `DeleteAsync` on `Invoice`/`Payment` — financial records are append-only/status-driven per the "avoid duplicate payments" and auditability requirements; only `Contractor` supports a soft-delete-style `DeactivateAsync`. Document this explicitly as a scoped decision in `DESIGN.md`.

### 1.4 Persistence — EF Core 8 + SQLite

**NuGet packages** (on `Plooto.Ap.Infrastructure`):
- `Microsoft.EntityFrameworkCore.Sqlite` (9.x works against net8.0, or pin to 8.x to match target framework exactly)
- `Microsoft.EntityFrameworkCore.Design` (needed for `dotnet ef migrations add`, dev-dependency only)
- CLI tool: `dotnet tool install --global dotnet-ef` (document in RUN-INSTRUCTIONS, don't vendor it)

**DbContext**:
```csharp
public class ApDbContext : DbContext
{
    public ApDbContext(DbContextOptions<ApDbContext> options) : base(options) { }

    public DbSet<Company> Companies => Set<Company>();
    public DbSet<CompanyPreferences> CompanyPreferences => Set<CompanyPreferences>();
    public DbSet<Contractor> Contractors => Set<Contractor>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentEvent> PaymentEvents => Set<PaymentEvent>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<ApprovalRequest> ApprovalRequests => Set<ApprovalRequest>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApDbContext).Assembly);
}
```

**Connection string / startup wiring** (`Program.cs` in `Plooto.Ap.Api`):
```csharp
var dataDir = Path.Combine(builder.Environment.ContentRootPath, "data");
Directory.CreateDirectory(dataDir);          // create data/ if missing, on every startup
var dbPath = Path.Combine(dataDir, "ap.db");
builder.Services.AddDbContext<ApDbContext>(opts => opts.UseSqlite($"Data Source={dbPath}"));
// ... after app build:
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<ApDbContext>().Database.Migrate();
```
`data/` must be added to `.gitignore` (confirm repo root `.gitignore` covers `data/` — add `data/` and `*.db` entries if not already present; check/update as part of implementation, not assumed).

**Migration strategy**: single `InitialCreate` migration generated via `dotnet ef migrations add InitialCreate --project src/Plooto.Ap.Infrastructure --startup-project src/Plooto.Ap.Api`, applied automatically via `Database.Migrate()` at startup (no manual `dotnet ef database update` step required to run locally — document this in RUN-INSTRUCTIONS.md). Do not use `EnsureCreated()` — it's incompatible with migrations and would mask schema drift.

**Indexes / constraints** (set in `IEntityTypeConfiguration<T>` classes):
- `Contractor`: index on `CompanyId`.
- `Invoice`: index on `CompanyId`; unique index on `(CompanyId, InvoiceNumber)`.
- `Payment`: index on `CompanyId`; index on `InvoiceId`; **unique index on `(CompanyId, IdempotencyKey)`** — this is the DB-level backstop for duplicate-payment prevention, not just an app-level check.
- `PaymentEvent`: index on `(PublishedAt)` where null — used by the outbox dispatcher to find unpublished rows efficiently (EF Core filtered index via `.HasFilter("\"PublishedAt\" IS NULL")`).
- `IdempotencyRecord`: PK on `(CompanyId, Key)` composite, not just `Key`.
- CHECK constraints: `Amount > 0` on `Invoice` and `Payment` via `.ToTable(t => t.HasCheckConstraint(...))`.

**Global query filter for isolation**: applying `modelBuilder.Entity<Invoice>().HasQueryFilter(e => e.CompanyId == _currentCompanyId)` style filters is tempting but requires a scoped "current company" accessor wired through DI — recommend **against** this for the take-home scope (adds complexity/footguns with `IgnoreQueryFilters` mistakes) and instead rely on repositories always taking `companyId` as an explicit parameter (see 1.3) as the single enforcement point. Note this as a documented trade-off in `DESIGN.md`: explicit-parameter isolation is simpler to reason about and test than implicit global filters, at the cost of relying on repository authors not forgetting the filter — mitigated by the isolation-specific unit tests in 1.5 and the HTTP-level `CompanyScopeFilter` in §2.3 (defense in depth).

**Seed data**: a `DevSeeder` static class in `Plooto.Ap.Infrastructure/Persistence/DevSeeder.cs`, called conditionally from `Program.cs` only when `builder.Environment.IsDevelopment()` — seeds 2 companies, 2-3 contractors each, a handful of invoices in varying statuses. Keep it idempotent (check `if (!db.Companies.Any())` before seeding) so repeated local runs don't duplicate data.

### 1.5 Unit test plan — Domain & Infrastructure layers

**Test project**: `tests/Plooto.Ap.Tests/Plooto.Ap.Tests.csproj`, xUnit (`xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`), plus `FluentAssertions` (optional but improves readability — note as a taste choice, not required) and `Microsoft.EntityFrameworkCore.Sqlite` for the real-SQLite-in-tests approach below.

**SQLite test strategy**: use the **open-connection `DataSource=:memory:` approach**, not per-test temp files. Rationale: SQLite's `:memory:` mode is wiped the instant the connection closes, so a single `SqliteConnection` is opened once per test (or per test class via `IAsyncLifetime`), kept open for the DbContext's lifetime, and closed in teardown — this is fast (no disk I/O, no temp file cleanup races) and still exercises real SQLite SQL generation/constraints (unlike EF Core's `UseInMemoryDatabase` provider, which does **not** enforce unique indexes/CHECK constraints and would silently pass over duplicate-payment-prevention tests). Temp-file-per-test is only worth it if tests need to inspect the DB file externally, which they don't here.

```csharp
// TestSupport/SqliteTestDbFactory.cs
public static class SqliteTestDbFactory
{
    public static ApDbContext CreateOpenContext()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<ApDbContext>().UseSqlite(connection).Options;
        var ctx = new ApDbContext(options);
        ctx.Database.Migrate();          // runs real migrations, not EnsureCreated, so schema matches prod
        return ctx;                       // caller disposes ctx; connection is tied to its lifetime via a wrapper if needed
    }
}
```

**Domain tests** (no DB, pure invariant checks):
- `InvoiceTests`:
  - `Create_WithZeroAmount_Throws`
  - `Create_WithNegativeAmount_Throws`
  - `Approve_WhenSubmitted_TransitionsToApproved`
  - `Approve_WhenAlreadyApproved_Throws`
  - `MarkPaid_WhenNotApproved_Throws`
- `PaymentTests`:
  - `Create_CompanyMismatchBetweenInvoiceAndContractor_Throws` (if this check lives on the entity rather than the service — otherwise this test moves to `PaymentServiceTests`)
  - `MarkCompleted_FromPending_Throws` (must go through Processing first)
  - `MarkCompleted_FromProcessing_SetsCompletedAtAndStatus`
  - `MarkFailed_SetsFailureReasonAndStatus`

**Infrastructure / repository tests** (real SQLite via `SqliteTestDbFactory`):
- `CompanyRepositoryTests`:
  - `CreateAsync_PersistsAndReturnsId`
  - `GetAsync_UnknownId_ReturnsNull`
- `ContractorRepositoryTests`:
  - `ListAsync_OnlyReturnsOwningCompanysContractors` (seeds 2 companies, asserts isolation)
  - `DeactivateAsync_SetsIsActiveFalse_DoesNotHardDelete`
- `InvoiceRepositoryTests`:
  - `CreateAsync_DuplicateInvoiceNumberSameCompany_ThrowsDbUpdateException` (unique index enforcement)
  - `CreateAsync_SameInvoiceNumberDifferentCompany_Succeeds` (isolation doesn't collide)
  - `ListAsync_FiltersByStatus`
- `PaymentRepositoryTests`:
  - `CreateAsync_PersistsAndReturnsId`
  - `CreateAsync_DuplicateIdempotencyKeySameCompany_ThrowsDbUpdateException`
  - `CreateAsync_SameIdempotencyKeyDifferentCompany_Succeeds`
  - `GetByIdempotencyKeyAsync_ReturnsExistingPayment_WhenPresent`
  - `ListAsync_OnlyReturnsOwningCompanysPayments`
- `IdempotencyStoreTests`:
  - `Record_ThenLookup_ReturnsStoredResponse`
  - `Record_DuplicateKeySameCompany_Throws`
- `PaymentServiceTests` (integration-ish, still in-memory SQLite):
  - `CreateSinglePaymentAsync_HappyPath_CreatesPaymentAndEventInSameTransaction`
  - `CreateSinglePaymentAsync_CalledTwiceWithSameIdempotencyKey_ReturnsOriginalPaymentNoDuplicateRow` — this is the core "avoid duplicate payments" correctness test called out in the README non-functional requirements
  - `CreateBatchPaymentAsync_CreatesOnePaymentPerInvoiceUnderSharedBatchId`
  - `CompletePaymentAsync_InvalidTransition_Throws`

Target: every repository method has at least a happy-path test and an isolation-boundary test; every entity with a state machine has a test per illegal transition. This layer's tests should all run with `dotnet test` requiring no external services — pure in-process SQLite.

---

## 2. Minimal API, Authentication & Idempotency

### 2.1 Project shape

- `Api` project: .NET 8 Minimal API (`Program.cs`, top-level statements), references `Infrastructure`/`Domain` (§1) and the messaging abstraction (`IEventPublisher`, owned by §3).
- Endpoints are grouped by entity under `Api/Endpoints/*Endpoints.cs`, each exposing a static `MapXEndpoints(this IEndpointRouteBuilder app)` extension called from `Program.cs`.
- DTOs live in `Api/Contracts/{Requests,Responses}` — never expose EF entities directly.
- `Api/Auth/` holds the token auth handler; `Api/Middleware/` holds the idempotency middleware/filter.

### 2.2 Minimal API endpoint map

All routes are mounted under `/api/v1`. Every route except token issuance is **company-scoped**: the `companyId` route segment must match the authenticated token's `CompanyId` claim (see §2.3). Route groups use `MapGroup("/companies/{companyId:guid}")` plus a shared `RequireAuthorization()` + company-scope filter.

```csharp
var companies = app.MapGroup("/api/v1/companies");
var scoped = app.MapGroup("/api/v1/companies/{companyId:guid}")
    .RequireAuthorization()
    .AddEndpointFilter<CompanyScopeFilter>();
```

**Company**

| Method | Route | Request | Response | Status |
|---|---|---|---|---|
| POST | `/api/v1/companies` | `CreateCompanyRequest { Name }` | `CompanyResponse` (incl. seeded `ApiKey` — shown once) | 201 |
| GET | `/api/v1/companies/{companyId}` | — | `CompanyResponse` | 200 / 404 |
| GET | `/api/v1/companies` | admin-only / omitted in MVP | `CompanyResponse[]` | 200 |

Company creation is the one unauthenticated (or admin-token-gated) endpoint — it's how a caller obtains an API key. Mark it `AllowAnonymous` or protect with a separate seed/admin key; document the choice explicitly in code comments since it's a deliberate MVP shortcut.

**Contractor** (scoped: `/api/v1/companies/{companyId}/contractors`)

| Method | Route | Request | Response | Status |
|---|---|---|---|---|
| POST | `.../contractors` | `CreateContractorRequest { Name, PaymentDetails }` | `ContractorResponse` | 201 |
| GET | `.../contractors/{contractorId}` | — | `ContractorResponse` | 200 / 404 |
| GET | `.../contractors` | query: `?page=&pageSize=` | `ContractorResponse[]` | 200 |
| PUT | `.../contractors/{contractorId}` | `UpdateContractorRequest` | `ContractorResponse` | 200 / 404 |
| DELETE | `.../contractors/{contractorId}` | — | — | 204 / 404 |

**Invoice** (scoped: `/api/v1/companies/{companyId}/invoices`)

| Method | Route | Request | Response | Status |
|---|---|---|---|---|
| POST | `.../invoices` | `CreateInvoiceRequest { ContractorId, Amount, Currency, DueDate, Description }` | `InvoiceResponse` | 201 |
| GET | `.../invoices/{invoiceId}` | — | `InvoiceResponse` | 200 / 404 |
| GET | `.../invoices` | query: `?status=&contractorId=` | `InvoiceResponse[]` | 200 |
| PUT | `.../invoices/{invoiceId}` | `UpdateInvoiceRequest` | `InvoiceResponse` | 200 / 404 / 409 (if already paid) |
| DELETE | `.../invoices/{invoiceId}` | — | — | 204 / 404 / 409 (if already paid) |

**Payment** (scoped: `/api/v1/companies/{companyId}/payments`) — the core workflow surface

| Method | Route | Request | Response | Status |
|---|---|---|---|---|
| POST | `.../payments` | `CreatePaymentRequest { InvoiceId }` + header `Idempotency-Key` | `PaymentResponse` | 201 / 200 (replay) / 409 (conflict) |
| POST | `.../payments/batch` | `CreateBatchPaymentRequest { Items: [{InvoiceId}] }` + header `Idempotency-Key` | `BatchPaymentResponse { Results: [{InvoiceId, Status, PaymentId?, Error?}] }` | 207 Multi-Status |
| GET | `.../payments/{paymentId}` | — | `PaymentDetailResponse` (status, amount, rail, timestamps, linked invoice) | 200 / 404 |
| GET | `.../payments` | query: `?status=&contractorId=&from=&to=` | `PaymentResponse[]` | 200 |
| GET | `.../statement` | query: `?from=&to=` | `StatementResponse { CompanyId, Period, Lines: [{Date, Type, CounterParty, Amount, RunningBalance}], Totals }` | 200 |

Notes:
- Single payment creation calls `IPaymentService.CreateSinglePaymentAsync`, which validates invoice ownership (invoice.CompanyId == route companyId), invoice status (not already paid), writes the `Payment` row, and calls `IEventPublisher.PublishAsync(..., "validated-payments")` in the same DB transaction (outbox pattern — mechanics owned by §3, but the API layer is responsible for the transactional boundary).
- Batch returns `207 Multi-Status` with per-item results so partial failure is representable without an all-or-nothing transaction; each item is processed independently and should reuse the idempotency machinery per-item (see §2.4).
- Transaction statement endpoint satisfies the README's "Transaction statement" / "Ability to view details of payments" functional requirements.

### 2.3 Authentication: simple per-company static API key

Deliberately minimal, local-only scheme — explicitly **not** OAuth/OIDC/Identity Server, matching the README's "simple token-based authentication for demonstration purposes."

**Issuance**: on `POST /api/v1/companies`, generate a cryptographically random API key (`RandomNumberGenerator.GetHexString(32)`), store its **hash** (SHA-256) in the `Company.ApiKeyHash` column, and return the plaintext key once in the response body. The caller stores it client-side; it cannot be retrieved again (standard API-key UX).

**Transport**: `Authorization: Bearer <api-key>` header.

**Validation**: a custom `AuthenticationHandler<ApiKeyAuthenticationOptions>` (`Api/Auth/ApiKeyAuthenticationHandler.cs`) registered via:

```csharp
builder.Services
    .AddAuthentication(ApiKeyAuthenticationOptions.Scheme)
    .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationOptions.Scheme, _ => { });
builder.Services.AddAuthorization();
```

Handler logic: extract bearer token → hash it → look up `Company` by `ApiKeyHash` (indexed) → on match, build `ClaimsPrincipal` with custom claim type `"company_id"` → `AuthenticateResult.Success`. No match → `AuthenticateResult.Fail`, pipeline returns `401`.

**Authorization / data isolation (the critical part — authZ, not just authN)**: an `IEndpointFilter` (`CompanyScopeFilter`) applied to every scoped route group compares the route's `{companyId}` to the token's `company_id` claim:

```csharp
public class CompanyScopeFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var routeCompanyId = ctx.HttpContext.Request.RouteValues["companyId"];
        var tokenCompanyId = ctx.HttpContext.User.FindFirst("company_id")?.Value;
        if (routeCompanyId?.ToString() != tokenCompanyId)
            return Results.Forbid(); // 403, never leak existence of other companies' data
        return await next(ctx);
    }
}
```

This is the mechanism that enforces the README's "Data Isolation between Companies/Payees" requirement at the HTTP boundary — independent of and in addition to repository-level `WHERE CompanyId = @CompanyId` scoping from §1 (defense in depth: even a missed `WHERE` clause can't leak cross-tenant data past this filter, since the filter blocks before the handler runs). A 403 (not 404) is returned for wrong-company access to avoid ambiguity; 404 is reserved for same-company-but-missing-resource.

### 2.4 Idempotency design

README requirement: "Avoid duplicate payments/trxns." Applies to `POST .../payments` and `POST .../payments/batch`.

**Mechanism**: client-supplied `Idempotency-Key` header (any opaque string, e.g. a UUID the client generates once per logical operation and resends on retry).

**Storage** — `IdempotencyRecord` table (defined in §1.1, behavior specified here):

| Column | Type | Notes |
|---|---|---|
| `Id` | GUID PK | |
| `CompanyId` | GUID | FK, part of uniqueness scope |
| `Key` | string | the `Idempotency-Key` header value |
| `RequestHash` | string | SHA-256 of normalized request body, detects key-reuse-with-different-payload |
| `ResponseStatusCode` | int | cached |
| `ResponseBody` | string (JSON) | cached |
| `Status` | enum: `InProgress`, `Completed`, `Failed` | |
| `CreatedAt` / `CompletedAt` | datetime | |

Unique index: `(CompanyId, Key)` — enforced at the DB level (SQLite `CREATE UNIQUE INDEX`), which is the real race-safety net, not just an app-level check.

**Request flow** (`IdempotencyFilter` / middleware wrapping the payment endpoints):

1. If no `Idempotency-Key` header on `POST .../payments` or `.../payments/batch` → `400 Bad Request` (header is required on these two routes, not globally).
2. Attempt `INSERT` of a new `IdempotencyRecord(CompanyId, Key, RequestHash, Status=InProgress)`.
   - **Insert succeeds** → proceed to the real handler. On handler completion, `UPDATE` the record with `Status=Completed`, response status/body, in the *same transaction* as the payment write (so a crash between "payment written" and "idempotency record completed" is impossible — both commit or neither does).
   - **Insert fails on unique constraint** (record already exists for this `CompanyId, Key`):
     - If `RequestHash` matches the stored hash and `Status=Completed` → return the cached `ResponseStatusCode`/`ResponseBody` verbatim (replay).
     - If `RequestHash` differs → `409 Conflict` ("Idempotency-Key reused with a different request body").
     - If `Status=InProgress` (a concurrent duplicate request raced in) → `409 Conflict` with a `Retry-After` hint. This is the "concurrent duplicate requests" race case — the DB unique constraint is what actually prevents two payments from being created; the app-level check is just for a fast, clean response.
3. Batch payments: the `Idempotency-Key` header scopes the **whole batch call** (one record, one cached response for the entire `BatchPaymentResponse`). Per-item safety additionally comes from each batch item's own natural dedup key — e.g. reject/skip a batch item whose `InvoiceId` already has a `Completed` payment (enforced via a unique `(CompanyId, InvoiceId)` constraint on a "committed payment" marker, independent of the idempotency table). Document both layers: batch-level idempotency key = "don't resubmit this exact batch call," invoice-level uniqueness = "never pay the same invoice twice even from different batch calls."

### 2.5 Unit / integration test plan

**Test project**: `Plooto.Ap.Api.Tests` (xUnit), using `WebApplicationFactory<Program>` for integration tests against a real SQLite temp-file/`:memory:` DB (not mocked — exercise real unique-constraint behavior for idempotency races).

**`AuthenticationTests`**
- `MissingToken_Returns401`
- `InvalidToken_Returns401`
- `ValidToken_WrongCompanyRoute_Returns403` — token for Company A, route `companies/{B}/...`
- `ValidToken_MatchingCompanyRoute_Succeeds`

**`DataIsolationTests`**
- `GetInvoice_BelongingToOtherCompany_Returns403NotData` — ensures no leakage via direct ID guess
- `ListInvoices_OnlyReturnsOwnCompanyInvoices` — seed two companies, assert list never contains the other's rows
- `GetPayment_CrossCompanyById_Returns403`
- `CreateContractor_ForOtherCompany_Returns403`

**`CompanyEndpointsTests` / `ContractorEndpointsTests` / `InvoiceEndpointsTests`** (CRUD happy paths)
- `Create{Entity}_ValidRequest_Returns201WithLocationHeader`
- `Get{Entity}_Existing_Returns200`
- `Get{Entity}_NotFound_Returns404`
- `Update{Entity}_Existing_Returns200AndPersists`
- `Delete{Entity}_Existing_Returns204`
- `UpdateInvoice_AlreadyPaid_Returns409`

**`PaymentEndpointsTests`**
- `CreatePayment_ValidInvoice_Returns201AndPersistsPayment`
- `CreatePayment_InvoiceFromOtherCompany_Returns403`
- `CreatePayment_AlreadyPaidInvoice_Returns409`
- `GetPaymentDetail_Existing_ReturnsFullDetail`
- `GetStatement_ReturnsCorrectRunningBalance`

**`IdempotencyTests`**
- `CreatePayment_MissingIdempotencyKey_Returns400`
- `CreatePayment_DuplicateKeySameBody_ReturnsCachedResponse_DoesNotCreateSecondPaymentRow` — assert DB row count stays 1
- `CreatePayment_DuplicateKeyDifferentBody_Returns409`
- `CreatePayment_ConcurrentDuplicateRequests_OnlyOnePaymentCreated` — fire N parallel `Task.WhenAll` requests with same key, assert exactly one `201`/success path committed and others get cached/409 responses, and DB has exactly one `Payment` row (this is the test that actually validates the unique-constraint race-safety net, not just app logic)
- `BatchPayment_DuplicateKey_ReturnsCachedBatchResponse`

**`BatchPaymentTests`**
- `BatchPayment_AllValid_ReturnsAll201sIn207`
- `BatchPayment_OneInvalidInvoice_ReturnsPartialSuccessWithPerItemErrors`
- `BatchPayment_DuplicateInvoiceAcrossTwoBatchCalls_SecondAttemptRejectedPerItem` — validates the invoice-level uniqueness layer from §2.4, independent of the idempotency key

Target: these tests should run against the real SQLite persistence layer (temp file or `:memory:` with a shared connection kept open for the test's lifetime, since SQLite in-memory DBs are connection-scoped) so unique-constraint and transaction behavior is actually exercised, not mocked away.

---

## 3. Messaging Architecture & Local Approximation

### 3.1 Diagram reinterpretation (explicit assumptions)

The source diagram (`docs/design.png`) is a hand-drawn whiteboard sketch from a system-design interview. The shapes below are **reinterpreted**, not literal — every mapping in this subsection is an assumption made to turn an ambiguous sketch into something buildable. Flag these as assumptions in `DESIGN.md` so a reader doesn't mistake them for certainties. The refined diagram at [`docs/design-refined.svg`](docs/design-refined.svg) renders this interpretation directly.

**The four rectangles (queues / message buses):**

| Rectangle (diagram) | Producer | Consumer(s) | Proposed technical name | Payload / event |
|---|---|---|---|---|
| `notifications` (between validation srv and notixn srv) | Validation Service | Notification Service → Approver Service/UI | `notifications-queue` | `ApprovalRequested` (invoice validated, needs human approval) and later `PaymentNotification` (status changes) |
| *(unlabeled rectangle, between validation srv and payment serv)* | Validation Service | Payment Service | `validated-payments-queue` | `PaymentValidated` (invoice/payment passed validation rules, cleared for processing). **Assumption**: the diagram shows a bidirectional arrow here; interpreted as request (validation→payment) plus an ack/rejection path (payment→validation) rather than two distinct queues, to keep the component count matching the diagram. |
| `CDC` (between `pymtevt` and `Pymt Queue`) | `pymtevt` (payment event log) | Pymt Queue | `payment-events-cdc-stream` | `PaymentProcessed` / `PaymentFailed` domain events, replayed as queue entries |
| `Pymt Queue` | Payment Service (direct) and CDC stream (indirect); loops back to App Server | App Server (status callback), downstream consumers (e.g. statement/ledger updaters) | `pymt-queue` | `PaymentQueued` / `PaymentStatusChanged` |

**The four diamonds (decision points / external systems / stores) — all assumptions:**

- **`tb`** — interpreted as a **transaction bus / router** sitting between App Server and Validation Service. Could also plausibly be "transaction bank" or a ledger abstraction; treated here as an internal routing/orchestration layer, not a literal queue, since it's drawn as a diamond (decision shape) not a rectangle. In code: a thin `ITransactionRouter` or simply inlined into the Minimal API endpoint — **not** a separate messaging component.
- **`tb company prefs`** — interpreted as a **per-company preferences/config store** (e.g. approval thresholds, auto-approve limits, notification settings) that Payment Service reads. Modeled as the `CompanyPreferences` table/entity from §1.1, not a queue.
- **`pmt rails`** — interpreted as the **external payment rail integration** (ACH/wire/card network). Locally approximated by an `IPaymentRailClient` with a fake/simulator implementation (`SimulatedPaymentRailClient`) that randomly or deterministically succeeds/fails after a short delay — not a real integration.
- **`pymtevt`** — interpreted as a **payment event log/table** (append-only) that Payment Service writes to after processing, and that the CDC stream taps. This is effectively the **outbox table** (`PaymentEvent` from §1.1 / `OutboxMessage` below) — the diagram's `pymtevt` diamond and the outbox pattern are the same thing in this plan.

### 3.2 End-to-end flow narrative

**Single payment:** A client calls the Minimal API (behind the conceptual `LB`→`App Server`) to submit a payment. The App Server/endpoint validates the request shape and auth, then routes it (`tb`) to the Validation Service, which checks business rules synchronously (invoice exists, not already paid, amounts match, company/payee isolation) and returns a 4xx immediately on failure — no message is ever published for a request that fails validation (full error-path table: §5.2). On success, the API creates the `Payment` row in the same call, `Status=Pending`, and returns its id to the client right away (so every `201` response carries a real `PaymentId`, per §2.2). If the payment requires human sign-off (per `tb company prefs`' threshold), an `ApprovalRequest` is created alongside it and `ApprovalRequested` is emitted onto `notifications-queue` — the Payment stays `Pending` until an approver resolves it via the decision endpoint (§5.3); on rejection the Payment moves straight to `Cancelled` and nothing is ever enqueued for processing. If no approval is required, `PaymentValidated` is emitted onto `validated-payments-queue` immediately (and on approval, the same emission happens at that point instead). Once `PaymentValidated` is consumed, the Payment Service checks idempotency (§2.4), calls the simulated `pmt rails` client, and — regardless of outcome — writes a row to the `pymtevt` outbox table in the **same DB transaction** as the payment status update. Locally, a background dispatcher reads `pymtevt`, treats it as the CDC source, and republishes onto the `CDC` stream, which feeds `Pymt Queue` (§3.3.1); the cost-consolidated cloud mapping in §3.3.2 collapses this into one hop. Consumers of `Pymt Queue` (e.g. a statement/ledger projector, and an async status-poll path back to the App Server) update read models and notify the caller of final status. Dispatcher/consumer failure handling (retries, dead-lettering) is specified in §5.4, not here.

**Batch payment:** Same pipeline, but the API accepts a batch of payment requests, assigns each a `BatchId` plus its own idempotency key, and fans them out individually through the same Validation → (optional approval) → Payment → `pymtevt`/CDC → `Pymt Queue` pipeline, each as an independent message. The batch itself is tracked as a lightweight aggregate (`PaymentBatch` with a count of succeeded/failed/pending children) so the client can poll one `BatchId` for overall progress instead of tracking N individual payment IDs. No new messaging primitive is needed for batching — it's purely fan-out at the producer side using the same queues.

### 3.3 Local-first technology recommendation

**Primary recommendation: Transactional Outbox + in-process `System.Threading.Channels`.**

Every domain write that needs to notify the rest of the system (payment validated, payment processed, approval needed, etc.) also inserts an `OutboxMessage` row in the **same SQLite transaction** as the domain write (e.g. `SaveChangesAsync` wrapping both the `Payment` update and the `OutboxMessage` insert — this reuses the `PaymentEvent` table from §1.1 as the outbox). A single `BackgroundService` (`OutboxDispatcher`) polls the outbox table on an interval (e.g. every 250ms–1s), reads unprocessed rows ordered by sequence, and publishes each onto the appropriate in-process channel based on a `QueueName`/`MessageType` column, then marks the row dispatched. Each logical queue from the diagram is a `System.Threading.Channels.Channel<T>` (unbounded or bounded with backpressure), and each consumer is its own `BackgroundService` doing `await foreach (var msg in channel.Reader.ReadAllAsync(...))`.

Why this over alternatives:
- **No external infra** — runs with plain `dotnet run`, no Docker/Redis/RabbitMQ/Kafka required, satisfying the "run everything locally" constraint directly.
- **Durability the naive alternative lacks** — publishing straight to an in-memory channel from the request handler would lose messages on crash/restart between "domain write committed" and "message published." The outbox table makes the write durable in SQLite first; the channel is just a fast in-process fan-out mechanism for already-durable, already-committed events. If the process restarts mid-dispatch, the dispatcher re-reads undispatched outbox rows on startup — at-least-once delivery.
- **Maps cleanly onto the diagram** — each rectangle becomes exactly one named channel + one named consumer class, so the mapping from design to code is traceable 1:1 (table in §3.3.1 below).
- **Low code cost** — no new NuGet dependency beyond EF Core + BCL; a reviewer can read the whole pipeline in ~200 lines.

**Lower-effort alternative (mention, don't implement by default): MassTransit with the in-memory transport.** MassTransit gives you consumers, retry/redelivery policies, and a publish/subscribe API (`IPublishEndpoint`, `IConsumer<T>`) with very little boilerplate, and its in-memory transport needs no external broker either — also satisfying "run locally." The tradeoff: MassTransit's in-memory transport has **no persistence** — messages published but not yet consumed are lost on process crash/restart, and there's no built-in outbox unless you add MassTransit's own EF Core outbox integration (which reintroduces most of the complexity of the hand-rolled approach anyway, plus a new dependency surface). Recommended only as a drop-in swap if the implementing agent wants less hand-written plumbing and durability-across-restarts is not a hard requirement for the demo. If swapped in, the `IEventPublisher` abstraction below stays the same — only its implementation changes.

**Shared abstraction both approaches sit behind:**
```csharp
public interface IEventPublisher
{
    Task PublishAsync<T>(T message, string queueName, CancellationToken ct = default);
}
```
Domain/application code (Validation Service, Payment Service) depends only on `IEventPublisher` and never on channels, outbox tables, or MassTransit directly — this is what lets the messaging backbone be swapped without touching producers/consumers' business logic.

#### 3.3.1 Rectangle → channel/consumer mapping (recommended approach)

| Diagram rectangle | Channel name | Producer | Consumer `BackgroundService` |
|---|---|---|---|
| `notifications` | `"notifications"` → `Channel<ApprovalRequested>` / `Channel<PaymentNotification>` | Validation Service (via `IEventPublisher`) | `NotificationDispatcher : BackgroundService` → updates Approver Service/UI read model |
| *(unlabeled)* | `"validated-payments"` → `Channel<PaymentValidated>` | Validation Service | `PaymentProcessor : BackgroundService` (the consuming half of "Payment Service") |
| `CDC` | `"payment-events-cdc"` → `Channel<PaymentEvent>` | `OutboxDispatcher` reading the `pymtevt`/`OutboxMessage` table | `CdcProjector : BackgroundService` |
| `Pymt Queue` | `"pymt-queue"` → `Channel<PaymentQueued>` / `Channel<PaymentStatusChanged>` | `CdcProjector` and Payment Service (direct status updates) | `PaymentRailsDispatcher : BackgroundService` (calls `IPaymentRailClient`), plus `StatementProjector : BackgroundService` for the App-Server-facing read model |

**Future/production swap:** because every producer depends only on `IEventPublisher.PublishAsync<T>(message, queueName)`, a production deployment can replace the `Channels`-backed implementation with one that publishes to Azure Service Bus, RabbitMQ, or Kafka topics named identically to the channels above (`notifications`, `validated-payments`, `payment-events-cdc`, `pymt-queue`), and replace each `BackgroundService` consumer with a message-broker-triggered consumer (Azure Function, Kafka consumer group, etc.) — no change needed to Validation Service, Payment Service, or any domain logic. §3.3.2 makes this concrete and consolidated rather than a 1:1 lift of the four local channels.

### 3.3.2 Cloud cost consolidation — collapsing 4 local channels into 1 Azure Service Bus topic

The four local `Channel<T>`s above map 1:1 onto the diagram for local-dev clarity (free to run, easy to reason about), but lifting each one — plus the implied `CdcProjector` hop — straight to its own Azure Service Bus queue is overkill once real money is on the line. Two concrete simplifications reduce both the message-bus footprint and the compute footprint when this moves to Azure:

1. **Collapse CDC into the outbox dispatcher itself.** `pymtevt → CDC → Pymt Queue` is two hops for one job: "turn a committed DB row into a published event." There's no independent value in materializing a separate `CDC` stream before `Pymt Queue` — `OutboxDispatcher` can publish the final `PaymentProcessed`/`PaymentFailed` event directly onto the same bus the `Pymt Queue` consumers read from. This removes one queue and one consumer class (`CdcProjector`) entirely, with no observable behavior change for the client.
2. **Replace 4 queues with 1 Service Bus Topic + 3 subscriptions.** Tag every outbound message with a `MessageType` application property (`ApprovalRequested`, `PaymentValidated`, `PaymentProcessed`, `PaymentFailed`, ...) and publish them all onto one topic, `ap-events`. Each consumer subscribes with a SQL filter on `MessageType` instead of each getting its own queue — topic subscriptions are the cheap way to fan one stream out to multiple independent consumers, versus provisioning/monitoring/paying for N separate queues:

   | Subscription | Filter | Replaces local channel(s) |
   |---|---|---|
   | `notifications-sub` | `MessageType = 'ApprovalRequested' OR MessageType = 'PaymentNotification'` | `notifications` |
   | `payment-processing-sub` | `MessageType = 'PaymentValidated'` | *(unlabeled)* / `validated-payments` |
   | `payment-rails-sub` | `MessageType = 'PaymentProcessed' OR MessageType = 'PaymentFailed'` | `CDC` + `Pymt Queue` (merged per point 1) |

   Net result: **1 namespace, 1 topic, 3 subscriptions**, instead of 4 separate queues and a 5-hop local pipeline. `IEventPublisher` absorbs this with no producer-side change — a `ServiceBusEventPublisher` implementation maps a local `queueName` to a `MessageType` property on one shared topic client instead of opening a connection per queue.
3. **Run every consumer inside one compute host.** The bigger cost lever isn't the bus at all — it's compute. Four `BackgroundService`s each deployed as their own Azure Container App/Function is 4 always-on billing units; running all of them as hosted services inside the single `Plooto.Ap.Api` process is 1. §4 covers how "each service is its own VS project" is reconciled with "single deployable."

This is the design a `ServiceBusEventPublisher : IEventPublisher` would target when the local `Channels` implementation is swapped out — see §3.3.3 for why that swap, not a literal `Channel<T>`-to-cloud lift, is the right move.

### 3.3.3 Can `System.Threading.Channels` cross process or Web API boundaries? (No)

Direct answer: **no.** `Channel<T>` is an in-memory, in-process producer/consumer queue — backed by managed heap objects and synchronization primitives inside one .NET runtime instance, with no wire protocol and no serialization story. A `Channel<T>` created in one process is unreachable from any other process — including a second instance of the same assembly running as a separate `dotnet run`/container, and including a separate ASP.NET Core Web API project even if it's in the same solution and on the same machine. There is no configuration that makes a `Channel<T>` cross a process boundary; that requires an actual IPC or network mechanism (named pipes, gRPC, HTTP, or a real message broker).

**What this means for "each service as its own VS project":**

- A **`.csproj` project boundary is a compile-time/assembly boundary, not a runtime/process boundary.** `Plooto.Ap.Services.Validation`, `Plooto.Ap.Services.Payment`, and `Plooto.Ap.Services.Notification` can be three distinct class-library projects — each with its own folder, namespace, and unit tests, making the service boundary unambiguous in Solution Explorer — while all three are still loaded into and executed by **one** host process (`Plooto.Ap.Api`). In this shape, `Channel<T>` works exactly as designed in §3.3/§3.3.1, because "Validation Service" and "Payment Service" are two different assemblies' code running on the same CLR, not two different operating-system processes.
- If instead each service becomes its own **independently runnable Web API** (its own `Program.cs`, its own listening port, deployed/scaled separately), it is, by definition, a separate process — at that point `Channel<T>` cannot be the transport between them under any circumstances, full stop. The messaging layer would have to become something that actually crosses a process/network boundary: real HTTP calls between the services, or a real broker (Azure Service Bus, §3.3.2). That also reintroduces exactly the operational overhead (N deployables, N things to provision/monitor/pay for) this round of revisions is trying to reduce.

**Recommendation:** adopt the first shape — separate class-library projects per service for code-organization clarity, composed into a single deployable host — as the default (§4). Treat "split Validation/Payment/Notification into independently deployable Web APIs" as an explicit future option, exercised only for a service that demonstrably needs to scale independently of the others — and only alongside the Service Bus swap from §3.3.2, never with `Channel<T>` across that boundary.

### 3.4 CDC-specific note

True Change Data Capture (e.g. Debezium tailing a database's write-ahead log, or SQL Server CDC/CT) is **not practical to run locally** for this exercise — Debezium needs Kafka Connect plus a broker, and SQLite has no CDC tooling ecosystem at all. Rather than stub this out or skip it, this plan treats the **outbox table on `pymtevt` + the `OutboxDispatcher`/`CdcProjector` pair as the deliberate local approximation of CDC**: the outbox row *is* the captured change event, and polling it and republishing to the `CDC` channel *is* the propagation step a real CDC tool would perform by tailing the log. The implementing agent should **not** attempt to wire up Debezium, SQL triggers, or SQLite WAL tailing — that would be solving a problem this plan explicitly scopes out. Document this substitution explicitly in `DESIGN.md` as a scoped-out item with the stated reasoning. (§3.3.2 goes one step further for the cost-conscious cloud target and removes the separate `CDC` hop altogether — locally, keep it, since it costs nothing and keeps the 1:1 diagram mapping legible.)

### 3.5 Labeled component list for the refined diagram

One line per component — shape category and concrete technology label — rendered in [`docs/design-refined.svg`](docs/design-refined.svg):

- **LB** — *infra* — conceptual load balancer; not implemented (single Kestrel instance locally), noted as a diagram artifact only.
- **App Server** — *infra/service* — the .NET 8 Minimal API host (`Program.cs` endpoints), single process.
- **tb (transaction router)** — *service (internal)* — in-process routing logic inside the Minimal API endpoint handlers; not a separate process or queue.
- **Validation Service** — *service* — `ValidationService` class/module, invoked synchronously from the API, calling business-rule checks (duplicate detection, isolation, invoice status).
- **notifications** — *queue* — `"notifications"` → `System.Threading.Channels.Channel<ApprovalRequested>`, fed by `IEventPublisher` via the outbox.
- **notixn srv (Notification Service)** — *service* — `NotificationDispatcher : BackgroundService`, consumes `"notifications"`, writes to an `ApproverInbox`/notification read table and logs to console (per README's "log to console for simplicity").
- **Approver Service/UI** — *external/future* — out of scope for implementation; represented by an endpoint (`POST /api/approvals/{id}/decision`) that a human or script calls; **assumption/scope-out**, document as such.
- **tb company prefs** — *store* — `CompanyPreferences` table (EF Core entity / SQLite table), read by Payment Service.
- **(unlabeled validation↔payment rectangle)** — *queue* — `"validated-payments"` → `Channel<PaymentValidated>`, consumed by `PaymentProcessor : BackgroundService`.
- **Payment Service** — *service* — `PaymentProcessor : BackgroundService` (consumer side) + `PaymentService` application-layer class (synchronous API-facing operations like idempotency-key lookup); calls `IPaymentRailClient`.
- **pmt rails** — *external (simulated)* — `IPaymentRailClient` / `SimulatedPaymentRailClient`, fake async success/failure after a delay, no real network call.
- **pymtevt** — *store* — `OutboxMessage`/`PaymentEvent` table in SQLite, written in the same transaction as payment status updates; doubles as the CDC source (see §3.4).
- **CDC** — *queue* — `"payment-events-cdc"` → `Channel<PaymentEvent>`, fed by `OutboxDispatcher : BackgroundService` polling the outbox table.
- **Pymt Queue** — *queue* — `"pymt-queue"` → `Channel<PaymentQueued>`/`Channel<PaymentStatusChanged>`, consumed by `PaymentRailsDispatcher : BackgroundService` and `StatementProjector : BackgroundService`.
- **Pymt Queue → App Server (status callback)** — *flow, not a new component* — `StatementProjector` updates a read model the Minimal API queries (`GET /payments/{id}`); represented as a polling read, not a push callback, to avoid adding SignalR/webhooks to scope.

Queues/buses render as rectangles (as in the original sketch) labeled with their channel name plus consumer class; stores (`tb company prefs`, `pymtevt`) render as cylinders to disambiguate from the routing diamond (`tb`), since the original diagram overloads diamond shape for two different meanings (router vs. data store) — a deliberate clarity improvement over the source sketch, not a reinterpretation of its content.

---

## 4. Service Boundaries & Project Structure (supersedes §1.2's project list)

Revises §1.2 so each logical service from the diagram is its own class-library project — visible as a distinct unit in Solution Explorer — while staying inside a single deployable host process, per §3.3.3's recommendation.

```
Plooto.Ap.sln
/src
  Plooto.Ap.Domain/                    # unchanged — §1.1 entities, enums, invariants
  Plooto.Ap.Infrastructure/            # unchanged — §1.4 EF Core, SQLite, repositories, DevSeeder
  Plooto.Ap.Api.Contracts/             # NEW — request/response DTOs, shared by Api and Client.Sdk (§8.1)

  Plooto.Ap.Messaging/                 # NEW — the messaging backbone, owned by no single "service"
    IEventPublisher.cs
    OutboxDispatcher.cs                # BackgroundService — polls PaymentEvent/outbox, publishes, retries/DLQs (§5.4)
    Channels/
      ChannelRegistry.cs               # named Channel<T> instances — local-dev IEventPublisher impl
    ServiceBus/                        # NOT implemented locally — interface/stub only, see §3.3.2
      ServiceBusEventPublisher.cs      # swap-in target for a future cloud deployment

  Plooto.Ap.Services.Validation/       # NEW — "Validation Service" from the diagram
    ValidationService.cs               # synchronous business-rule checks (§3.2)

  Plooto.Ap.Services.Payment/          # NEW — "Payment Service" from the diagram
    PaymentService.cs                  # synchronous, API-facing orchestration (moved here from Infrastructure/Services)
    PaymentProcessor.cs                # BackgroundService — consumes validated-payments
    PaymentRailsDispatcher.cs          # BackgroundService — consumes pymt-queue / payment-rails-sub
    StatementProjector.cs              # BackgroundService — consumes pymt-queue, updates statement read model
    IPaymentRailClient.cs / SimulatedPaymentRailClient.cs

  Plooto.Ap.Services.Notification/     # NEW — "Notification Service" from the diagram
    NotificationDispatcher.cs          # BackgroundService — consumes notifications

  Plooto.Ap.Api/                       # the single host — references every project above
    Program.cs                        # wires DI, maps endpoints (§2.2), registers all 4 BackgroundServices
    Endpoints/ Auth/ Middleware/
    data/

  Plooto.Ap.Client.Sdk/                # NEW — see §8.1
  Plooto.Ap.Client.UseCases/           # NEW — see §8.1
  Plooto.Ap.Client.Console/            # NEW — see §6

/tests
  Plooto.Ap.Tests/                     # Domain + Infrastructure, unchanged (§1.5)
  Plooto.Ap.Services.Tests/            # NEW — ValidationService/PaymentService/consumer tests, see §5
  Plooto.Ap.Api.Tests/                 # unchanged (§2.5)
  Plooto.Ap.Client.Tests/              # NEW — CSV parser + use-case tests, see §7
```

**Reference direction (no cycles):** `Domain` ← `Infrastructure`; `Messaging` ← `Services.*` ← `Api`. `Services.*` projects depend on `Domain` + `Infrastructure` (for repositories) + `Messaging` (for `IEventPublisher`) + `Api.Contracts`, but **never on each other directly, and never on `Api`** — cross-service communication only happens through `IEventPublisher`/the channel it publishes to, never a direct method call from `Services.Payment` into `Services.Validation`, even though they share a process. This is deliberate: it's what keeps "single process today, separate deployables tomorrow" an option — if `Services.Payment` called `Services.Validation`'s classes directly, splitting them into separate processes later would require an API redesign, not just the `IEventPublisher` swap from §3.3.2.

**Deployment shape:** `Plooto.Ap.Api` is the only executable; it's the only thing in the `Dockerfile`/`docker-compose.yml` already in the repo, and the only Azure compute unit (single App Service/Container App) in the cost-conscious default. See §3.3.2 and §3.3.3 for why this is the recommended default over one-process-per-service.

---

## 5. Error Handling & Failure Modes

§1–§3 concentrate on the happy path; this section is the authoritative cross-cutting reference for what happens when things go wrong. Implement these state transitions and tests alongside the endpoints/consumers they belong to — don't treat this as an afterthought pass.

### 5.1 New domain state transitions (amends §1.1)

Add two methods to `Payment`:
```csharp
public void Approve() { ... }                     // Pending -> Approved, else throws InvalidOperationException
public void MarkCancelled(string reason) { ... }   // Pending -> Cancelled, else throws InvalidOperationException
```
And one small enum for the approval decision endpoint (§5.3) — `ApprovalRequest` itself already exists in §1.1, this just specifies how it's resolved:
```csharp
public enum ApprovalDecision { Approve, Reject }
```

### 5.2 Consolidated failure-mode table

| Scenario | Where it's caught | Outcome | Reference |
|---|---|---|---|
| Invoice not found / wrong company / already paid | Validation Service (synchronous, in the request) | `404`/`403`/`409`, no `Payment` row created, nothing published | §2.2, §3.2 |
| Missing/invalid auth token | `ApiKeyAuthenticationHandler` | `401` | §2.3 |
| Token valid, wrong company route | `CompanyScopeFilter` | `403` | §2.3 |
| Idempotency-Key missing on payment endpoints | `IdempotencyFilter` | `400` | §2.4 |
| Idempotency-Key reused, same body | `IdempotencyFilter` | cached response replayed, no new `Payment` row | §2.4 |
| Idempotency-Key reused, different body | `IdempotencyFilter` | `409` | §2.4 |
| Concurrent duplicate requests, same key | DB unique constraint `(CompanyId, IdempotencyKey)` | one wins (`201`), others `409`/cached | §2.4, §1.4 |
| Approval required, rejected by approver | `POST /approvals/{id}/decision` | `Payment.MarkCancelled`, `ApprovalRequest.Status=Rejected`, `PaymentNotification` published, Invoice stays `Submitted` (resubmittable with a new payment + new idempotency key) | §5.3 |
| Approval required, approver never responds | — (no automatic timeout) | Payment stays `Pending` indefinitely; **explicitly scoped out** — a real system would add an approval SLA/timeout job; document as a gap in `DESIGN.md`, don't build it | — |
| Payment rail call fails (`SimulatedPaymentRailClient` returns failure) | `PaymentProcessor` | `Payment.MarkFailed(reason)`, `PaymentFailed` published, Invoice stays unpaid; client retries with a **new** `Idempotency-Key` — retrying the same key correctly replays the cached failure, by design | §3.2, §2.4 |
| Payment rail call times out / throws | `PaymentProcessor` | treated as a failure (above), not left `Processing` forever — wrap the rail call in a fixed timeout (`CancellationTokenSource`) so a hung simulated call can't strand a payment | §3.2 |
| Outbox row fails to publish (dispatcher-side exception) | `OutboxDispatcher` | retried with backoff up to a max attempt count, then dead-lettered | §5.4 |
| Consumer crashes mid-processing after reading from a `Channel<T>` | n/a — the message is already gone from the channel | mitigated structurally, not retried at the channel level | §5.4, §3.3.3 |
| CSV row malformed / unknown contractor / duplicate invoice number | `InvoiceCsvService` / import use case | that row skipped and reported with a line number + reason; valid rows in the same file still import | §7 |
| Console/Blazor client gets a transient network error or `5xx` | `ApiClient` in `Client.Sdk` | bounded retry with backoff (e.g. 3 attempts, exponential); `4xx` is **never** retried automatically | §8 |
| DB migration fails at startup | `Program.cs` | app fails fast, exits with the EF Core exception logged to console — no silent partial-schema state | §1.4 |

### 5.3 Approval decision endpoint (new — fills a gap in §2.2)

```
POST /api/v1/companies/{companyId}/approvals/{approvalId}/decision
Body: { "decision": "Approve" | "Reject", "approverEmail": "..." }
```
- `200` with the updated `PaymentResponse` on success.
- `404` if the `ApprovalRequest` doesn't belong to the route's company (same `CompanyScopeFilter` protection as every other endpoint).
- `409` if the `ApprovalRequest` is not `Pending` (decisions are one-shot, not revisable).
- `Approve` → `ApprovalRequest.Status=Approved`, `Payment.Approve()`, publish `PaymentValidated`.
- `Reject` → `ApprovalRequest.Status=Rejected`, `Payment.MarkCancelled("Rejected by approver")`, publish a `PaymentNotification` so the UI reflects the outcome — nothing goes onto `validated-payments-queue`.

This endpoint is the stand-in for the diagram's "Approver Service/UI," already scoped out as external in §3.5 — a human or script calls it directly; no approval UI is built.

### 5.4 Dispatcher & consumer failure handling (retry + dead-letter, local approximation)

A real broker (Azure Service Bus included) gives you redelivery and dead-lettering for free. The hand-rolled `OutboxDispatcher`/`Channel<T>` combination (§3.3) does not — reading from a `Channel<T>` removes the item permanently, so a consumer that throws mid-processing loses that message with no built-in way to get it back. Two changes close this gap without adding infrastructure:

1. **Extend the outbox row, not just the channel message.** Add `Status` (`Pending`, `Dispatched`, `Processed`, `Failed`, `DeadLettered`), `AttemptCount`, `NextAttemptAt`, and `LastError` columns to the outbox/`PaymentEvent` table. `OutboxDispatcher` only publishes rows that are `Pending` and due; on a publish exception it increments `AttemptCount`, sets `NextAttemptAt = now + 2^AttemptCount seconds` (capped), and leaves the row `Pending` for retry — once `AttemptCount` exceeds a max (e.g. 5), the row is marked `DeadLettered` and logged to console (`LastError` captured) instead of retried forever.
2. **Treat the channel message as a doorbell, not the work item.** Keep each `Channel<T>` payload minimal (essentially the outbox row's id / `PaymentId`), and have the consumer re-read current state from the database and mark the outbox row `Processed` only once its work genuinely completes. If the consumer crashes before that, the row is `Dispatched`-but-not-`Processed`; a periodic sweep in `OutboxDispatcher` (e.g. every 30s) re-queues anything stuck in `Dispatched` past a timeout back to `Pending`. This makes the database row — not the transient channel message — the real source of truth for whether work happened, which is what makes "at-least-once" an honest claim rather than only true in the no-crash case.

Document this explicitly in `DESIGN.md` as a known trade-off: a reasonable local approximation of broker-grade delivery guarantees, not a full implementation of them (no true exactly-once, no cross-retry ordering guarantee) — acceptable for a local demo, and the first thing that gets *simpler*, not more complex, when swapped for real Service Bus per §3.3.2.

---

## 6. Local Testing Client — Console App

A console app that drives the API through its real HTTP surface is both the fastest way to exercise every use case by hand and, per §8, the first of two interchangeable front-ends over the same use-case layer.

### 6.1 Project: `Plooto.Ap.Client.Console`

An executable (`dotnet run --project src/Plooto.Ap.Client.Console`) that only does I/O — reading menu choices and prompts from `Console.ReadLine()`, printing results — and delegates every operation to `Plooto.Ap.Client.UseCases` (§8.1). It talks to whichever `Plooto.Ap.Api` instance is configured (local `dotnet run` or Docker — see `RUN-INSTRUCTIONS.md`) over plain HTTP, so no special container wiring is needed.

### 6.2 Menu — one entry per high-level use case

```
=== Plooto AP — Local Test Console ===
 1. Configure API base URL & API key
 2. Create a company (bootstrap — returns an API key)
 3. Create a contractor
 4. Create a single invoice
 5. Import invoices from CSV
 6. Download a blank CSV invoice template
 7. Create a single payment
 8. Create a batch payment (pick invoice IDs, or import a CSV first)
 9. Resolve a pending approval (approve/reject)
10. View payment details
11. View company statement
12. Exit
```

Each option maps 1:1 to a class in `Plooto.Ap.Client.UseCases` — e.g. option 5 calls `ImportInvoicesFromCsvUseCase.ExecuteAsync(filePath)`, option 6 calls `DownloadCsvTemplateUseCase.ExecuteAsync(outputPath)`. `Program.cs` stays thin: a `switch` on the menu choice, a few prompts to gather arguments, and `Console.WriteLine()` to print the result (including per-row CSV errors from §7, printed with line numbers).

Option 1 stores the base URL/API key in-memory for the session (optionally persisted to a local `appsettings.local.json`) — not a credential manager, just enough to avoid retyping the key every run.

### 6.3 Why a console app specifically

- Zero UI framework dependency — `dotnet run` and you're testing the full pipeline, including the messaging backbone (§3), idempotency (§2.4), and approvals (§5.3), end-to-end.
- Forces the use-case layer (§8) to be genuinely UI-agnostic from day one, instead of retrofitting that boundary later when a Blazor front-end is wanted.
- Scriptable later: because every use case is just a method call, `Program.cs` could gain a non-interactive mode (e.g. `dotnet run -- import-csv ./invoices.csv`) with no new business logic — out of scope to build now, but the layering doesn't block it.

---

## 7. CSV Invoice Import Format

### 7.1 Schema

One row = one invoice to create, scoped to whichever company the caller is authenticated as (the file does **not** carry a `CompanyId` column — that would let a pasted/edited file silently target the wrong tenant; the company always comes from the console/Blazor session's configured API key, never from file content).

| Column | Required | Type | Notes |
|---|---|---|---|
| `InvoiceNumber` | yes | string | Human/external reference; must be unique within the company (§1.4's unique index) |
| `ContractorEmail` | yes | string | Looked up against existing, active `Contractor`s for the company — **not** auto-created; an unknown email fails that row (§5.2) |
| `Amount` | yes | decimal | Must parse and be `> 0` |
| `Currency` | no | string (ISO 4217) | Defaults to `CompanyPreferences.DefaultCurrency` (§1.1) if blank |
| `DueDate` | yes | date, `yyyy-MM-dd` | Must parse; a past date is accepted but flagged as a warning, not a hard failure |
| `Description` | no | string | Free text; quote it if it contains commas — use a real CSV library, not manual string-splitting |

### 7.2 Template

`DownloadCsvTemplateUseCase` (§6.2 option 6) writes:

```csv
InvoiceNumber,ContractorEmail,Amount,Currency,DueDate,Description
# Example row below — delete or overwrite before importing
INV-1001,jane.doe@example.com,2500.00,USD,2026-11-15,October consulting services
```

The importer (`InvoiceCsvService.ParseAsync`) skips any line starting with `#` before handing rows to the CSV parser, so the example/comment row is safe to leave in place by mistake — it's never imported as real data. Use `CsvHelper` (NuGet) rather than hand-rolled splitting, specifically because `Description` can legitimately contain commas and quoted newlines that naive `string.Split(',')` would mis-parse.

### 7.3 Import behavior

`ImportInvoicesFromCsvUseCase.ExecuteAsync(path)`:
1. Parses the file; a row that fails to parse (wrong column count, unparseable `Amount`/`DueDate`) is recorded as a `CsvRowError { LineNumber, RawLine, Reason }` and excluded from further processing — a parse failure never aborts the whole file.
2. For each parsed row, resolves `ContractorEmail` to a `ContractorId` via the API (extend the contractor list endpoint with an `?email=` filter if one doesn't already exist); unresolvable emails become a `CsvRowError` too.
3. Calls `POST .../invoices` once per valid row — not a dedicated bulk-invoice endpoint, and no idempotency key, since invoices don't carry the same duplicate-payment risk payments do (§2.2/§2.4).
4. Returns `{ Created: [...], Errors: [CsvRowError...] }`, which the console app (§6) prints as a per-row report — successes with their new `InvoiceId`, failures with line number and reason.

This mirrors the batch-payment philosophy from §2.2/§2.4 (partial success, per-item reporting) without reusing payment-specific idempotency machinery that doesn't apply to invoice creation.

---

## 8. Pluggable Client Architecture (Console today, Blazor tomorrow)

The goal: swapping the console front-end for a Blazor app (Server or WASM) later should mean writing a new UI project, not touching any business logic.

### 8.1 The shared layer: `Plooto.Ap.Client.Sdk` + `Plooto.Ap.Client.UseCases`

- **`Plooto.Ap.Client.Sdk`** — the HTTP/IO layer, UI-agnostic:
  - `ApiClient` — thin `HttpClient` wrapper with one method per API endpoint from §2.2 (`CreateCompanyAsync`, `CreateInvoiceAsync`, `CreatePaymentAsync`, `GetStatementAsync`, `ResolveApprovalAsync`, ...), injecting `Authorization`/`Idempotency-Key` headers and the bounded-retry policy from §5.2.
  - `InvoiceCsvService` — `ParseAsync`/`WriteTemplateAsync` from §7, with no dependency on `Console` or any UI type; `ParseAsync` takes a `Stream`, not just a file path, specifically so a future Blazor `<InputFile>` can reuse it unchanged (§8.2).
  - Request/response DTOs — reused from `Plooto.Ap.Api.Contracts` (§4), shared with `Api` to avoid drift between server and client shapes.
- **`Plooto.Ap.Client.UseCases`** — one class per console menu option (§6.2), each with an `ExecuteAsync(request) -> Result` shape, e.g.:
  ```csharp
  public interface IImportInvoicesFromCsvUseCase
  {
      Task<CsvImportResult> ExecuteAsync(Stream csvStream, CancellationToken ct);
  }
  ```
  These classes call only `Plooto.Ap.Client.Sdk` — never `System.Console`, never anything UI-specific. They're what both a console menu handler and a Blazor button's `OnClick` call identically.

### 8.2 What changes if Blazor replaces (or joins) the console app

Nothing in `Domain`, `Infrastructure`, `Services.*`, `Messaging`, `Api`, `Api.Contracts`, `Client.Sdk`, or `Client.UseCases` changes. A new `Plooto.Ap.Client.Blazor` project would:
- Reference `Plooto.Ap.Client.UseCases` (and transitively `Client.Sdk`) exactly like `Client.Console` does.
- Replace `Console.ReadLine()`/`Console.WriteLine()` with Blazor forms/components — e.g. a `<CreateInvoiceForm>` component binds its fields to the same `CreateInvoiceRequest` DTO and calls the same `ICreateInvoiceUseCase.ExecuteAsync(...)`.
- Replace the CSV file-path prompt with a Blazor `<InputFile>` component feeding its stream straight into `IImportInvoicesFromCsvUseCase.ExecuteAsync(stream, ct)` — the stream-based signature from §8.1 means no retrofit is needed.
- Reuse `DownloadCsvTemplateUseCase`'s output as a Blazor file-download response instead of a local file write — same use case, different sink.

This is the practical payoff of keeping `Client.UseCases` free of `Console`/file-path assumptions: both front-ends are consumers of the same application layer, not reimplementations of it — the same architectural move as §4's "services never call each other directly, only through `IEventPublisher`" rule, applied on the client side instead of the service side.

---

## Required artifacts checklist (per README)

- [ ] `DESIGN.md` — key decisions, trade-offs, scoped-out items. Pull directly from this plan's assumptions (§3.1), rejected alternatives (§1.4's global-query-filter note, §3.3's MassTransit alternative), and explicit scope-outs (§1.3's no-hard-delete, §3.4's CDC substitution, §5.4's retry/DLQ approximation, §5.2's no-approval-timeout gap).
- [x] `RUN-INSTRUCTIONS.md` — drafted for Options A/B (source, Docker). **Needs a follow-up edit once §6's console app exists**, adding a "run the console client" step (`dotnet run --project src/Plooto.Ap.Client.Console`) pointed at whichever API instance (A or B) is running.
- [ ] `AI-TRANSCRIPT.md` — full conversation transcript of AI usage, including this planning session and the Round 2 revisions.
- [x] `docs/design-refined.svg` — already generated; still accurate as-is (it documents the local-dev mapping from §3.3.1; the cost-consolidated cloud mapping is §3.3.2's separate topic/subscription table, not a diagram change).
- [ ] Console client (§6) — not a README-required artifact by name, but the requested way to exercise every use case without a UI; worth calling out in `DESIGN.md` as the project's manual test tool.
- [x] CSV template (§7.2) — static copy at [`docs/invoice-template.csv`](docs/invoice-template.csv), in addition to the console app's on-demand generator, so a reader can grab it without running anything.
