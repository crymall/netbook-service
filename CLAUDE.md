# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Code style

Prefer semantically-named identifiers and readable logic over comments.
Add a comment only when it is critically necessary — to flag an unusual pattern, a non-obvious constraint, or a deliberate choice that would otherwise read as a bug (e.g. why `IsStale` compares strictly-newer rather than exact-match).
Do not write comments that restate what the code, its names, or an adjacent assertion already make clear.

## Project overview

`netbook-service` is the backend API for a notes app, written in C# / ASP.NET Core (.NET 10).
It is a REST API only — the React frontend lives in the midden-hub monorepo (`../../midden-hub/apps/Netbook`).
Production runs on the Midden k3s cluster behind the nginx ingress at `netbook.reedgaines.com/netbook/*`.

## Commands

```bash
dotnet build              # Build everything via netbook-service.sln
dotnet test               # Run the xUnit endpoint test suite
dotnet run                # Run the API (http://localhost:5099, https://localhost:7119)
dotnet watch run          # Run with hot reload
dotnet format             # Apply code style/formatting
dotnet ef migrations add <Name>   # Add a schema migration (needs the dotnet-ef global tool)
```

Local runs need a Postgres reachable via the default connection string (`Host=localhost;Port=5432;Username=netbook;Password=netbook;Database=netbook_db`), e.g.:

```bash
docker run -d --name netbook-pg -e POSTGRES_USER=netbook -e POSTGRES_PASSWORD=netbook -e POSTGRES_DB=netbook_db -p 5432:5432 postgres:15-alpine
```

Override via a `ConnectionStrings:Netbook` user secret, or via the `DB_HOST`/`DB_PORT`/`DB_USER`/`DB_PASSWORD`/`DB_NAME` env vars the cluster uses.

## Architecture

This is a thin Web API plus a test project, tied together by `netbook-service.sln`.
Request flow: `Program.cs` → `Controllers/*` → `NetbookDbContext` → PostgreSQL (Npgsql provider).

- **`Program.cs`** — composition root.
  Configures JWT bearer auth, builds the connection string (`DB_*` env vars first, `ConnectionStrings:Netbook` fallback), and applies pending EF Core migrations on startup — the deployment's equivalent of the Node services' `npm run db:init && npm start`.
  The `Testing` environment skips migration (the test factory swaps in its own database).
  Also maps `/healthz` (health check that pings the DbContext; used by the k8s probes) and `/metrics` (prometheus-net; scraped via the Grafana pod annotations).
  There is no `UseHttpsRedirection` — TLS terminates at the nginx ingress, and an in-app redirect would loop.
  Ends with `public partial class Program` so the test project's `WebApplicationFactory<Program>` can see the implicit Program class.
- **`Migrations/`** — EF Core migrations (Npgsql provider).
  Schema changes go through `dotnet ef migrations add`, never through manual edits to a live database.
  There is no `HasData` seeding; test data is created through the API in tests.
- **`Controllers/`** — `NotesController` (`Controllers/Note.cs`) and `UsersController` (`Controllers/User.cs`), talking to EF Core directly (no service/repository layer).
  Both bind write DTOs, never entities: clients cannot supply server-owned fields (`Id`, `UserId`, `CreatedAt`) at all, and request-level length limits (`MaxLength` on the DTOs) mirror the database constraints — SQLite in tests doesn't enforce varchar lengths, so the DTO validation is what the test suite exercises.
- **`Models/`** — EF Core entities (`Note`, `User`).
  `Note.UserId` is an `int` foreign key to `Users.Id` with `ON DELETE CASCADE` (configured in `NetbookDbContext`, no navigation properties): the database guarantees no orphaned notes and deletes a user's notes when the user row goes.
  Column limits: `Title` varchar(200), `Content` varchar(100000), `Username` varchar(50) (matching iam's column).
- **`NetbookDbContext`** — the two `DbSet`s, unique indexes on `User.IamId` and `User.Username`, and the `Note`→`User` cascade FK.
- **`Attributes/ApiKeyAttribute.cs`** — `[ApiKey]` action filter checking the `x-api-key` header against the `MiddenApiKey` config value.
  Fails closed: outside Development a missing config value returns 500 (and `Program.cs` refuses to start without it); the `dev_api_key` fallback exists only in Development.
- **`netbook-service.Tests/`** — xUnit + `WebApplicationFactory` endpoint tests running the real pipeline against SQLite in-memory.
  `TestWebApplicationFactory` uses `builder.UseSetting` (not `ConfigureAppConfiguration`, which applies too late for config read in top-level statements) and removes both `DbContextOptions<NetbookDbContext>` and `IDbContextOptionsConfiguration<NetbookDbContext>` when swapping providers (EF Core 9+ keeps provider config in the latter).
  The main csproj excludes `netbook-service.Tests/**` from its compile globs because it sits at the repo root.

## Auth — this service does not own accounts

`netbook-service` has no login/registration/password logic of its own.
All real account management (registration, password hashing, 2FA, roles/permissions) lives in **`iam-service`**, a sibling Node/Express + Postgres project at `../iam-service` (part of the same "Midden" family of services).
`netbook-service` only *verifies* the JWT that `iam-service` already issued — it is a pure downstream consumer.

- All controllers are `[Authorize]` by default, expecting a JWT bearer token.
- The JWT handler also accepts the token from a `token` cookie (see `OnMessageReceived` in `Program.cs`) — this matches `iam-service`'s `routes/auth.js`, which sets an httpOnly cookie named `token` on `/verify-2fa` login.
- **Shared secret, not a shared library**: the signing key comes from `Jwt:Secret` in configuration; `iam-service` signs tokens with `process.env.JWT_SECRET`.
  There is **no fallback secret** — this service throws at startup if `Jwt:Secret` is unset.
  In the cluster both come from the `midden-global-secrets` secret (`jwt_secret` key), so they cannot drift.
  If auth ever breaks between the two, check for a secret mismatch first.
- **Local dev config**: the Node services read `JWT_SECRET` from their gitignored `.env` files; this service stores `Jwt:Secret` in `dotnet user-secrets` (the `UserSecretsId` is in the csproj).
  To (re)set it: `dotnet user-secrets set "Jwt:Secret" "<same value as iam-service's .env JWT_SECRET>"`.
  Never commit the secret to `appsettings*.json`.
- `issuer`/`audience` validation are both disabled, so trust is entirely a function of secret possession.
- **Secret length matters on this side**: .NET's JWT validator requires the HS256 key to be ≥128 bits — a shorter `Jwt:Secret` is silently excluded and every token fails with `WWW-Authenticate: ... "The signature key was not found"`.
  Node signs with short keys without complaint, so a too-short shared secret makes `iam-service` appear to work while `netbook-service` rejects everything.
  Use 32+ bytes.

## User sync — push from iam, no local provisioning

The local `Users` table is a mirror keyed by `IamId`, maintained entirely by `iam-service` pushes; it is not a source of truth.
There is **no just-in-time provisioning**: a validly signed JWT whose `id` claim has no local `Users` row gets `403` from every notes endpoint.

- `iam-service` pushes on registration (`POST /users` with `{ username, iam_id }`) and deletion (`DELETE /users/sync/:iamId`) to every URL in its `USER_SYNC_API_URLS` env var (comma-separated; this service is `http://netbook-service:8080` in the cluster).
- The whole `UsersController` is a machine surface: `[ApiKey]` at class level, no JWT, no browser-facing endpoints.
  The surface is exactly `GET /users/{id}`, `POST /users`, and `DELETE /users/sync/{iamId}` — there is deliberately no list endpoint, no PUT, and no by-local-id delete.
- `POST /users` binds `UserSyncDto` (`[JsonPropertyName("iam_id")]` — default binding would silently drop the snake_case key) and is **idempotent**: an already-synced `IamId` returns `200` with the existing row.
  A taken username under a *different* `IamId` returns `409` — that means the mirror is stale (missed deletion sync); reconcile with the sync script.
- User deletion cascades to the user's notes at the database level (FK `ON DELETE CASCADE`).
- Backfill/reconciliation: `midden-infra/scripts/sync-users.js` pulls the user list from iam's api-key-guarded `GET /users/sync` and pushes each user here; safe to re-run.

## Note permissions

Every `NotesController` action is scoped to the caller's own notes.
The ownership model:

- If a note is yours (`Note.UserId` matches the caller), you're effectively its editor — no separate roles/permissions within a note.
- If a note isn't yours, it's completely inaccessible.
  (If note sharing is ever built, "not yours" could become read-only instead — but sharing is **not on the roadmap right now**.)
- This is unrelated to the `role`/`permissions` claims `iam-service` puts on the JWT — those describe IAM-level roles, not note-level ownership, and don't factor into note access.

How the caller is resolved (`GetCurrentUserAsync` in `Controllers/Note.cs`): the JWT's `id` claim (the IAM user's UUID) → local `Users` row via `User.IamId` → `User.Id` compared against `Note.UserId`.
No row means `403` (see the user-sync section above).

Enforcement conventions:

- `GET`/`PUT`/`DELETE` on someone else's note returns **404, not 403**, so callers can't probe which note ids exist.
- `POST /notes` binds only `title`/`content` (`NoteCreateDto`) — `Id`, `UserId`, `CreatedAt`, and `UpdatedAt` are stamped server-side and extra JSON keys are ignored.
- **`UpdatedAt` lifecycle**: create stamps `UpdatedAt` equal to `CreatedAt`; `PUT /notes/{id}` refreshes it.
  The column is nullable only for rows that predate it (the migration leaves existing rows null) — the API never writes null.
  A successful PUT returns `200` with the updated note (not `204`), so a syncing client can base its next conditional write on the fresh `UpdatedAt` without a GET.
- **Optimistic concurrency for offline sync**: `PUT /notes/{id}` and `DELETE /notes/{id}` accept an optional `updatedAt` in the JSON body — the timestamp of the copy the client based its change on.
  Absent means unconditional (last writer wins, the pre-existing behavior); present means the write is rejected with `409` — body carrying the current server row for conflict resolution — when the stored `UpdatedAt` is *strictly newer* than the supplied value.
  Strictly-newer rather than exact-match is deliberate: Postgres stores microseconds while JSON responses carry .NET's 100ns ticks, so exact equality would produce phantom conflicts when a client echoes a POST/PUT response value.
  A null stored `UpdatedAt` (legacy row) is never newer, so conditional writes against legacy rows proceed.
  DELETE's body is optional (`EmptyBodyBehavior.Allow`) — a bare DELETE with no body still works.
- `GET /notes` returns only the caller's notes, **paginated**: query params `page` (default 1, clamped `>= 1`) and `pageSize` (default 10, clamped to `[1, 50]`), most recently updated first, null-`UpdatedAt` legacy rows last (explicitly bucketed — Postgres would otherwise put nulls first on `DESC`), newest-created first as the tiebreak. The response is a `PagedResult<Note>` — `{ items, page, pageSize, total, totalPages }` — not a bare array. Ordering is server-side so pages stay stable; an out-of-range page returns empty `items` with the real `total`.
  (The old `GET /notes/user/{userId}` was redundant and removed; the frontend never used it.)

## Deployment

- **CI** (`.github/workflows/ci.yml`): `dotnet test` on PRs to main.
- **Deploy** (`.github/workflows/deploy.yml`): on merge to main — test, build/push `crymall/netbook-server:<sha>` to Docker Hub, `kubectl apply -f k8s/`, wait for rollout.
- **k8s/**: `server-deployment.yaml` (port 8080, `/healthz` probes, Grafana scrape annotations, env from `netbook-secrets` + `midden-global-secrets`), `postgres.yaml` (PVC-backed postgres:15-alpine), `server-ingress.yaml` (`netbook.reedgaines.com`, `/netbook(/|$)(.*)` rewrite, own `netbook-tls-secret`), `netbook-backup-cronjob.yaml` (nightly 02:45 pg_dump to Linode Object Storage with healthchecks.io heartbeat).
- Cluster-scoped concerns (ClusterIssuer, secrets inventory, backup alerting) live in `midden-infra`, not here.

## Frontend integration notes

The React frontend (midden-hub `apps/Netbook`) consumes this API through the `/netbook` path prefix — the Vite dev proxy and the production ingress both strip it.
Auth is cookie-based via iam-service on the same host, so no CORS policy is configured; requests arrive same-origin.
