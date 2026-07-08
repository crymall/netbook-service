# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project overview

`netbook-service` is the backend API for a notes app, written in C# / ASP.NET Core (.NET 10). It is a REST API only — the React frontend is a separate project not contained in this repo.

## Commands

```bash
dotnet build              # Build the project
dotnet run                # Run the API (http://localhost:5099, https://localhost:7119)
dotnet watch run          # Run with hot reload
dotnet format             # Apply code style/formatting
```

There is no test project in this repo yet.

## Architecture

This is a thin, single-project Web API (no separate class libraries or solution file). Request flow: `Program.cs` → `Controllers/*` → `NetbookDbContext` → SQLite.

- **`Program.cs`** — composition root. Configures JWT bearer auth, wires up the SQLite `NetbookDbContext`, and calls `db.Database.EnsureCreated()` on startup instead of using EF Core migrations. There is no `Migrations/` folder; schema changes are made by editing the `Models/`/`NetbookDbContext` and relying on `EnsureCreated`, which only creates a database if one doesn't exist yet (it will not apply changes to an existing `.db/Netbook.db`, so delete that file locally after model changes).
- **`Controllers/`** — one controller per entity (`NotesController`, `UsersController`), following the standard scaffolded ASP.NET Core CRUD pattern (GET/GET by id/POST/PUT/DELETE) directly against `NetbookDbContext`. There is no service/repository layer — controllers talk to EF Core directly.
- **`Models/`** — EF Core entities (`Note`, `User`). `Note.UserId` is a plain `string` foreign key (not a navigation property), matched against `User.Id` (an `int`) — there's no enforced relational constraint between them.
- **`NetbookDbContext`** — defines the two `DbSet`s and seeds fixed dev data via `HasData` in `OnModelCreating` (two users, two notes).
- **`Attributes/ApiKeyAttribute.cs`** — a custom `IAsyncActionFilter` used as `[ApiKey]` on top of `[Authorize]` for extra-sensitive endpoints (currently `POST`/`DELETE` on `UsersController`). It checks an `x-api-key` header against the `MiddenApiKey` config value (falls back to `dev_api_key` if unset).

## Auth — this service does not own accounts

`netbook-service` has no login/registration/password logic of its own. All real account management (registration, password hashing, 2FA, roles/permissions) lives in **`iam-service`**, a sibling Node/Express + Postgres project at `../midden-services/iam-service` (part of the same "Midden" family of services). `netbook-service` only *verifies* the JWT that `iam-service` already issued — it is a pure downstream consumer.

- All controllers are `[Authorize]` by default, expecting a JWT bearer token.
- The JWT handler also accepts the token from a `token` cookie (see `OnMessageReceived` in `Program.cs`) — this matches `iam-service`'s `routes/auth.js`, which sets an httpOnly cookie named `token` on `/verify-2fa` login.
- **Shared secret, not a shared library**: the signing key comes from `Jwt:Secret` in configuration; `iam-service` signs tokens with `process.env.JWT_SECRET`. There is **no fallback secret** — this service throws at startup if `Jwt:Secret` is unset, and `iam-service`/`canteen-service` exit at startup (`bin/www`) if `JWT_SECRET` is unset. The two services aren't calling each other at request time to validate tokens — they just both need to be configured with the *same* secret. If auth ever breaks between the two, check for a secret mismatch first.
- **Local dev config**: the Node services read `JWT_SECRET` from their gitignored `.env` files; this service stores `Jwt:Secret` in `dotnet user-secrets` (the `UserSecretsId` is in the csproj). To (re)set it: `dotnet user-secrets set "Jwt:Secret" "<same value as iam-service's .env JWT_SECRET>"`. Never commit the secret to `appsettings*.json`.
- `issuer`/`audience` validation are both disabled, so trust is entirely a function of secret possession.
- `iam-service` embeds `role` and a `permissions` array in the JWT payload (see its `authorizePermissions` middleware). `netbook-service` currently does not read or enforce either claim — it only checks that the token is valid, not what the caller is allowed to do.
- **User sync, not shared DB**: `iam-service`'s `/register` and user-deletion routes call out to sub-app APIs with `POST/DELETE .../users` plus an `x-api-key: MIDDEN_API_KEY` header, passing `{ username, iam_id }`. This is the same shape as `netbook-service`'s `ApiKeyAttribute` (`x-api-key` header, `MiddenApiKey` config) guarding `POST/DELETE /users`, and matches `User.IamId`/`User.Username` in `Models/User.cs`. In other words, `netbook-service`'s local `Users` table is a thin mirror keyed by `IamId`, kept in sync by `iam-service` pushing changes — not a source of truth and not queried by `iam-service` directly.
- The `dev_api_key` fallback for `MiddenApiKey` (and `MIDDEN_API_KEY` in the Node services) still exists and is not safe for production — override it via configuration/secrets before deploying, matching whatever `iam-service` is configured with.
- **Secret length matters on this side**: .NET's JWT validator requires the HS256 key to be ≥128 bits — a shorter `Jwt:Secret` is silently excluded and every token fails with `WWW-Authenticate: ... "The signature key was not found"`. Node signs with short keys without complaint, so a too-short shared secret makes `iam-service` appear to work while `netbook-service` rejects everything. Use 32+ bytes.

## Note permissions

Every `NotesController` action is scoped to the caller's own notes. The ownership model:
- If a note is yours (`Note.UserId` matches the caller), you're effectively its editor — no separate roles/permissions within a note.
- If a note isn't yours, it's completely inaccessible. (If note sharing is ever built, "not yours" could become read-only instead — but sharing is **not on the roadmap right now**.)
- This is unrelated to the `role`/`permissions` claims `iam-service` puts on the JWT (see above) — those describe IAM-level roles, not note-level ownership, and don't factor into note access.

How the caller is resolved (`GetCurrentUserAsync` in `NotesController`): the JWT's `id` claim (the IAM user's UUID, set by `iam-service`) → local `Users` row via `User.IamId` → `User.Id.ToString()` compared against `Note.UserId`. A valid JWT whose IAM id has no local `Users` row gets `403` — the user hasn't been synced (see user-sync above).

Enforcement conventions:
- `GET`/`PUT`/`DELETE` on someone else's note returns **404, not 403**, so callers can't probe which note ids exist.
- `POST /notes` ignores any client-supplied `UserId` and stamps the caller's — same for `Id` and `CreatedAt`.
- `GET /notes` returns only the caller's notes; `GET /notes/user/{userId}` returns 403 unless `userId` is the caller's own.

## Frontend integration notes

The upcoming React frontend will consume this API as a separate client. Keep in mind when changing endpoints:
- Auth is cookie- or bearer-token-based (see above) — CORS and cookie settings (`SameSite`, `Secure`) will need attention once a separate frontend origin is introduced, since no CORS policy is currently configured in `Program.cs`.
- `POST /users` and `DELETE /users/{id}` require the `x-api-key` header in addition to a valid JWT.
