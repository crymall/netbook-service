---
name: verify
description: Build, run, and drive netbook-service end-to-end to verify a change against the live API.
---

# Verifying netbook-service changes

## Build & run

```bash
dotnet test                                 # endpoint suite first — it boots the real pipeline
docker run -d --name netbook-pg -e POSTGRES_USER=netbook -e POSTGRES_PASSWORD=netbook -e POSTGRES_DB=netbook_db -p 5432:5432 postgres:15-alpine
dotnet run --urls http://localhost:5099     # run in background; ready when it logs "Now listening"
```

- Schema comes from EF Core migrations, applied automatically at startup.
  After model changes, add a migration (`dotnet ef migrations add <Name>`) — a stale model with no migration fails loudly at the first query, not silently.
- Inspect state directly with `docker exec netbook-pg psql -U netbook -d netbook_db` (note: quoted CamelCase identifiers, e.g. `SELECT * FROM "Notes";`).
- `curl http://localhost:5099/healthz` should return `Healthy` — it exercises the DB connection.

## Getting an authenticated handle

Every notes endpoint requires a JWT signed with `Jwt:Secret` (stored in `dotnet user-secrets`; same HS256 secret as iam-service's `JWT_SECRET`).
Mint one locally — no need to run iam-service:

```bash
SECRET=$(dotnet user-secrets list | sed -n 's/^Jwt:Secret = //p')
# HS256 JWT with iam-service's claim shape: id (IAM UUID), username
# (a ~15-line python script with hmac/base64 suffices; exp required)
curl -H "Authorization: Bearer $TOKEN" http://localhost:5099/notes
```

The token is also accepted from a `token` cookie.

**There is no JIT provisioning**: a token whose `id` has no `Users` row gets 403 everywhere.
Create the user first, the way iam-service does:

```bash
curl -X POST http://localhost:5099/users -H 'Content-Type: application/json' \
  -H 'x-api-key: dev_api_key' -d '{"username":"tester","iam_id":"<the UUID in your token>"}'
```

(`x-api-key` must match the `MiddenApiKey` config value; it falls back to `dev_api_key` locally.
The sync endpoints take api-key only — no JWT needed.)

## Flows worth driving

- `GET/POST/PUT/DELETE /notes` with a synced user — ownership scoping (cross-user access must 404, not 403).
- A token with an unsynced IAM UUID — every notes endpoint must 403.
- `POST /notes` with client-supplied `id`/`userId`/`createdAt` — all must be overridden server-side.
- `POST /users` twice with the same `iam_id` — second call must return 200 with the same row (idempotent), not 500.
- `DELETE /users/sync/{iamId}` — must remove the user's notes too (no FK cascade exists).

## Full-stack option

For changes touching the iam contract, run iam-service against a throwaway Postgres with `USER_SYNC_API_URLS=http://localhost:5099` and `SKIP_EMAIL_VERIFICATION=true`, then register/login for a real cookie.
Gotcha: iam's `verification_codes.expires_at` is a naive timestamp written in the Node process's local time — if the throwaway Postgres runs in UTC on a non-UTC machine, 2FA codes look instantly expired; align with `ALTER DATABASE iam_db SET timezone='<your local tz>';`.

## Gotchas

- No HTTPS redirect anymore; use http://localhost:5099 directly.
- No seed data; every user/note you need must be created through the API.
- Local user ids are autoincrement ints starting at 1 on a fresh database.
