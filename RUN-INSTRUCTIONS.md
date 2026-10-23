# Run Instructions

Two ways to run the API locally, plus a console client to drive it. Neither needs an external service: no Postgres, Redis, RabbitMQ or Kafka, because the messaging layer is in-process (see `DESIGN.md` for why). Data lives in one SQLite file at `data/ap.db`.

---

## Option A: Run from source (.NET 8 SDK)

**Prerequisites:** [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (a newer SDK also works; the projects target `net8.0`, so the .NET 8 runtime must be installed).

```bash
git clone <repo-url>
cd BE-take-home-oluwakayode-osagbemi

dotnet restore
dotnet build
dotnet test                                   # optional: ~250 tests, no external services needed

dotnet run --project src/Plooto.Ap.Api        # API on http://localhost:5080
```

- On first run the app creates `src/Plooto.Ap.Api/data/ap.db` and applies EF Core migrations automatically (`Database.Migrate()`); there is no manual `dotnet ef database update` step. If a migration fails, the app exits with the error rather than running on a partial schema.
- In the `Development` environment (the default for `dotnet run`) two demo companies with contractors and invoices are seeded, with fixed, obviously-fake API keys:

  | Company | API key |
  |---|---|
  | Acme Inc (payments above 5,000 need human approval) | `dev-acme-api-key` |
  | Globex Corp (no approval required) | `dev-globex-api-key` |

  Set `ASPNETCORE_ENVIRONMENT=Production` to disable seeding (and those keys).
- To change the port: `dotnet run --project src/Plooto.Ap.Api --urls http://localhost:5090` (and point the console at it). Port 5080 is used rather than 5000 because 5000 is reserved on many Windows machines and taken by AirPlay on macOS.
- `GET /health` returns `{"status":"ok"}`.

**Reset local state:** stop the app and delete `src/Plooto.Ap.Api/data/ap.db` (and the `-wal`/`-shm` files next to it).

### Try it with curl

```bash
# Create a company: the response contains a one-time ApiKey (no auth needed for this call)
curl -X POST http://localhost:5080/api/v1/companies \
  -H "Content-Type: application/json" -d '{"name":"My Co"}'

# Who am I? (finds your company id from your key)
curl http://localhost:5080/api/v1/me -H "Authorization: Bearer <api-key>"

# Everything else is under /api/v1/companies/<companyId>/...
curl http://localhost:5080/api/v1/companies/<companyId>/invoices \
  -H "Authorization: Bearer <api-key>"

# Pay an invoice. The Idempotency-Key header is required; reuse the key to retry safely
curl -X POST http://localhost:5080/api/v1/companies/<companyId>/payments \
  -H "Authorization: Bearer <api-key>" \
  -H "Idempotency-Key: $(uuidgen)" \
  -H "Content-Type: application/json" \
  -d '{"invoiceId":"<invoiceId>"}'
```

Payments are processed asynchronously: the POST returns `Pending` or `Approved`, then `GET .../payments/<paymentId>` shows `Completed` a moment later. Simulated rail behaviour: an amount whose cents are `.13` is declined, one with `.66` never answers (times out), anything else succeeds.

---

## Option B: Run via Docker (no .NET SDK required)

**Prerequisites:** Docker Desktop, or Docker Engine plus the Compose plugin.

```bash
docker compose up --build        # API on http://localhost:8080
docker compose down              # stop
```

- The SQLite file is bind-mounted to `./data/ap.db`, so it survives restarts and can be opened with any SQLite client.
- Use the same curl examples with port `8080`, and the same seeded dev keys (the image runs in `Development`).
- Reset state: `docker compose down`, then delete `./data/ap.db`.
- Note: the `Dockerfile` follows the current project layout, but the image was not built in the environment this was developed in (Docker was not available there). If the build fails, Option A is the verified path.

---

## Console client (manual test tool)

A menu-driven console app that drives the API over HTTP, exercising every use case: companies, contractors, invoices, CSV import, single and batch payments, approvals, payment details and the statement. It works against Option A or Option B.

```bash
# in a second terminal, with the API already running
dotnet run --project src/Plooto.Ap.Client.Console
```

Defaults to `http://localhost:5080`. Point it elsewhere with the menu (option 1) or `PLOOTO_API_URL`. To skip typing credentials, set `PLOOTO_API_KEY` (and optionally `PLOOTO_COMPANY_ID`; if omitted the company is looked up from the key).

A quick tour:

1. **Option 1**: press Enter for the default URL, leave the company id blank and enter `dev-acme-api-key` (a seeded demo company), or
   **option 2** to create your own company (this signs you in automatically and prints a one-time key).
2. **Option 14 / 13**: list the contractors and invoices.
3. **Option 7**: pay an invoice, then **option 10** to watch it move to `Completed`.
4. **Option 15** switches on the approval policy; payments above the threshold then wait in `Pending`. **Option 9** lists and resolves them.
5. **Option 6** writes a blank CSV template; fill it in and use **option 5** to import. Bad rows are reported with their line number and reason while good rows still import. Example row (remove the leading `#` to use it):

   ```csv
   InvoiceNumber,ContractorEmail,Amount,Currency,DueDate,Description
   INV-1001,jane.doe@example.com,2500.00,USD,2026-11-15,October consulting services
   ```

   A static copy of the template is in `docs/invoice-template.csv`. The contractor must already exist (the file never creates contractors and never names a company: the company always comes from your API key).
6. **Option 8** pays several invoices at once (blank input means "all Submitted invoices") and reports a result per item.
7. **Option 11**: the company statement with a running balance.

---

## Running tests only

```bash
dotnet test
```

Tests use real SQLite (an in-memory connection, or a throwaway temp file when background services run concurrently) and never touch `data/ap.db`, so running them is always safe and does not need the API to be running.
