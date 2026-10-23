# Run Instructions

Two ways to run this project locally. Both read/write the same SQLite file at `data/ap.db` and require no external services — no Postgres/Redis/RabbitMQ/Kafka to stand up, since the messaging layer is entirely in-process (see `PLAN_1.md` §3 for why).

These instructions describe the project once it's built per `PLAN_1.md`. If you're starting from the plan rather than a finished implementation, follow `PLAN_1.md`'s "Suggested implementation order" first — these steps assume `src/Plooto.Ap.Api`, `src/Plooto.Ap.Infrastructure`, `src/Plooto.Ap.Domain`, and `tests/` exist as laid out in §1.2.

---

## Option A — Run from source (.NET 8 SDK)

**Prerequisites:** [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). No database server, no Docker.

```bash
# 1. Clone and restore
git clone <repo-url>
cd BE-take-home-oluwakayode-osagbemi
dotnet restore

# 2. Build
dotnet build

# 3. Run the test suite (optional but recommended)
dotnet test

# 4. Run the API
dotnet run --project src/Plooto.Ap.Api
```

- On first run, the app creates `src/Plooto.Ap.Api/data/` and `ap.db` automatically and applies EF Core migrations at startup (`Database.Migrate()`) — there is no separate `dotnet ef database update` step to run manually.
- In the `Development` environment, seed data (a couple of companies with contractors/invoices) loads automatically so there's something to query immediately — see `PLAN_1.md` §1.4.
- The console output prints the listening URL (e.g. `http://localhost:5080`); use that in place of `http://localhost:5000` below if it differs, or pin it with `dotnet run --project src/Plooto.Ap.Api --urls http://localhost:5000`.

**Try it:**

```bash
# Create a company — response includes a one-time ApiKey
curl -X POST http://localhost:5000/api/v1/companies \
  -H "Content-Type: application/json" \
  -d '{"name":"Acme Inc"}'

# List invoices for that company (replace <companyId> / <api-key>)
curl http://localhost:5000/api/v1/companies/<companyId>/invoices \
  -H "Authorization: Bearer <api-key>"

# Create a payment — Idempotency-Key is required on payment creation
curl -X POST http://localhost:5000/api/v1/companies/<companyId>/payments \
  -H "Authorization: Bearer <api-key>" \
  -H "Idempotency-Key: $(uuidgen)" \
  -H "Content-Type: application/json" \
  -d '{"invoiceId":"<invoiceId>"}'
```

**Reset local state:** stop the app and delete `src/Plooto.Ap.Api/data/ap.db`.

---

## Option B — Run via Docker (no .NET SDK required)

**Prerequisites:** Docker Desktop, or Docker Engine + the Compose plugin. Nothing else.

Repo root includes `Dockerfile`, `.dockerignore`, and `docker-compose.yml` — a multi-stage build (SDK image to build/publish, slim ASP.NET runtime image to run) with the SQLite data directory bind-mounted to the host.

```bash
# 1. Build the image and start the container
docker compose up --build

# 2. API is reachable at:
#    http://localhost:8080
```

- The SQLite database is bind-mounted to `./data/ap.db` on the host (the same `data/` convention as Option A), so it persists across `docker compose down` / `docker compose up` cycles and can be opened with any SQLite client even while the container is stopped.
- Run the same `curl` examples as Option A, substituting port `8080` for `5000`.

```bash
# Stop the container
docker compose down
```

**Reset local state:** `docker compose down`, then delete `./data/ap.db` on the host.

**Rebuilding after code changes:**

```bash
docker compose up --build
```

---

## Running tests only

```bash
dotnet test
```

Works identically in either environment (SDK installed or Docker-only — tests run inside the SDK, not the container). Tests use an isolated in-memory SQLite connection per test run (see `PLAN_1.md` §1.5) and never touch `data/ap.db`, so running the suite is always safe and doesn't require the API to be running.
