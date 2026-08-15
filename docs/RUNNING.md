# Running and testing Concordat locally

Everything needed to get the registry, the web app and the test suites running on a developer
machine, in the order you need it, with the traps named where you would hit them.

[QUICKSTART.md](QUICKSTART.md) is the ten-minute tour that ends with a message refused for
breaking its contract. This document is the wider one: every process, every test suite, both
shells, and what to do when a step does not do what it says.

> Commands are given for **PowerShell** and for **bash** where the two differ. The difference is
> not cosmetic — `VAR=value command` is bash syntax that PowerShell parses as a command name, so
> the bash form of the migrator step fails on Windows with no useful message.

## What you need

| | Version | Check with |
|---|---|---|
| .NET SDK | 10.x — `global.json` asks for `10.0.100` and rolls forward | `dotnet --version` |
| Node.js | `^22.22.3 \|\| ^24.15.0 \|\| >=26.0.0` — the floor the Angular 22 CLI enforces | `node --version` |
| Docker | Any current version, **and the daemon running** | `docker info` |

Docker is not optional even if you only want to run the tests: the .NET suite starts its own
PostgreSQL and RabbitMQ containers through Testcontainers, so `dotnet test` fails without a
daemon.

## The ports

Worth reading once, because two of them are deliberately not the number you would guess.

| Port | What | Note |
|---|---|---|
| `55432` | PostgreSQL | **Not 5432.** A developer machine very often already has a Postgres on the default port |
| `5672` | RabbitMQ | |
| `15672` | RabbitMQ management UI | `guest` / `guest` |
| `5062` | The registry | Fixed by `launchSettings.json`; the web app's proxy hard-codes it |
| `4200` | The web app | `npm start` |
| `4300` | The web app, for end-to-end tests | `npm start -- --port 4300` — the e2e suite expects this one |

## 1. Start the dependencies

```bash
docker compose -f deploy/compose/docker-compose.yml up -d
```

PostgreSQL and RabbitMQ, nothing else. The registry is deliberately left out so you can run it
with `dotnet run` and get a restart instead of an image rebuild on every change.

Confirm both are healthy before moving on — the migrator fails unhelpfully against a database
that is listening but not yet ready:

```bash
docker compose -f deploy/compose/docker-compose.yml ps
```

## 2. Create the database schema

Migrations run as their own process rather than on API startup, so two API instances starting
together cannot race each other into the same migration.

**PowerShell**

```powershell
$env:ConnectionStrings__Concordat = "Host=localhost;Port=55432;Database=concordat;Username=postgres;Password=postgres"
dotnet run --project src/hosts/Concordat.Migrator
```

**bash**

```bash
ConnectionStrings__Concordat="Host=localhost;Port=55432;Database=concordat;Username=postgres;Password=postgres" \
  dotnet run --project src/hosts/Concordat.Migrator
```

It is idempotent. On a database that is already current it says `Database is up to date; no
migrations to apply.` and exits 0.

## 3. Start the registry

Same environment variable, same shell, in the session you started it in:

```powershell
$env:ConnectionStrings__Concordat = "Host=localhost;Port=55432;Database=concordat;Username=postgres;Password=postgres"
dotnet run --project src/hosts/Concordat.Api
```

It listens on **<http://localhost:5062>**, from `launchSettings.json`, which takes precedence
over `ASPNETCORE_URLS` under `dotnet run`. To choose the port yourself:

```bash
dotnet run --project src/hosts/Concordat.Api --no-launch-profile --urls http://localhost:5065
```

Check it:

```bash
curl http://localhost:5062/health/ready
```

`/health/live` and `/health/ready` are separate, and liveness deliberately ignores the database:
a registry that cannot reach Postgres is not ready, but restarting it will not help.

## 4. Claim the instance — read this before anything writes

A registry with no accounts is **unclaimed**, and an unclaimed instance answers every request as
an owner. That is what makes `docker compose up` produce something you can immediately try
(ADR-008), and it is why the first run of anything that writes just works.

Ask which state you are in:

```bash
curl http://localhost:5062/v1/auth/status
```

```json
{ "claimed": false, "authenticated": true, "actor": "unclaimed-instance", "scopes": ["..."] }
```

- **`"claimed": false`** — every write succeeds unauthenticated. The quickstart sample runs as
  written. The registry logs a warning on a loop telling you this, which is the point.
- **`"claimed": true`** — writes need a credential, and anything unauthenticated gets **401**.

Claiming happens exactly once and cannot be repeated:

```bash
curl -X POST http://localhost:5062/v1/auth/bootstrap \
  -H "Content-Type: application/json" \
  -d '{"email":"you@example.com","password":"a password you will remember"}'
```

Then sign in at <http://localhost:4200> with those details.

> **If you have ever run the end-to-end suite against this database, it is already claimed** —
> `global-setup.ts` bootstraps `e2e-owner@concordat.test` with the password
> `correct horse battery staple`. Sign in as that, or reset the database (see
> [Starting over](#starting-over)).

## 5. Start the web app

```bash
cd web
npm install      # first time only
npm start
```

<http://localhost:4200>, proxying `/v1` and `/health` to the registry on `:5062` via
`proxy.conf.json`. **The proxy target is hard-coded**, so a registry on any other port will
leave the app loading against nothing.

## 6. Run the sample

```bash
dotnet run --project samples/Quickstart
```

It registers a contract, publishes a valid order, then publishes an invalid one and shows it
refused before it reaches the broker — with the exact JSON-Pointer path that failed.

**This needs an unclaimed instance.** On a claimed one it stops at
`401 (Unauthorized)` on its first write, because it authenticates nowhere — registration
belongs to CI and the CLI under ADR-005, and the sample is deliberately a plain REST client.

`samples/Quickstart/Program.cs` is meant to be edited; [QUICKSTART.md
§5](QUICKSTART.md#5-now-change-something) lists the changes worth making and what each teaches.

## Running the tests

### The .NET suite — 1,493 tests

```bash
dotnet test Concordat.slnx
```

Needs Docker running. Five of the thirteen assemblies start real PostgreSQL and RabbitMQ
containers through Testcontainers rather than mocking them, so a cold run pulls images and
takes several minutes. The other eight are pure unit tests and finish in seconds:

```bash
dotnet test tests/Concordat.Domain.Tests        # 517 tests, no Docker, ~1s
```

### The web suite — 352 tests

```bash
cd web
npm test                # vitest
npm run lint            # architectural boundaries, not just style — see web/README.md
npm run format:check    # prettier
npm run codes:check     # fails if the error-code union has drifted from the .NET catalogue
```

### The browser end-to-end suite — 43 tests

Three processes, none of them started by Playwright — a config that quietly started its own copy
of either half would produce a suite that passes against the wrong thing.

```bash
# 1. the registry on :5062        (either dotnet run, or the compose registry profile)
# 2. the web app on :4300
cd web && npm start -- --port 4300

# 3. the tests
cd web && npm run e2e
```

`npm run e2e:headed` watches it happen; `npm run e2e:ui` opens Playwright's runner. Override
either endpoint with `CONCORDAT_REGISTRY` and `CONCORDAT_WEB_URL`.

The suite claims the instance and creates its own fixtures, all idempotently. See
[web/e2e/README.md](../web/e2e/README.md) for what it assumes and why it exists.

### What CI runs

`.github/workflows/ci.yml` is the authority. Six jobs: `build & test` (format, build, the
OpenAPI drift gate, the .NET suite), `web app`, `browser end-to-end`, `protocol docs gate`,
`contract drift gate` and `CLI container`. To reproduce the formatting gate locally:

```bash
dotnet format Concordat.slnx --verify-no-changes
```

## Troubleshooting

**`401 (Unauthorized)` from the sample, or from any write.** The instance is claimed. See
[step 4](#4-claim-the-instance--read-this-before-anything-writes).

**The registry will not start: address already in use on 5062.** Something else is already
serving it — most often a `concordat-api` container left behind by the compose `registry`
profile or by an earlier e2e session:

```bash
docker ps --filter name=concordat-api
docker stop concordat-api
```

**`Port 4300 is already in use` when starting the app for e2e.** A dev server from an earlier
session is still on it. That one will serve the suite perfectly well — but it is running whatever
code it started with, so if you have changed the app since, stop it and start a fresh one rather
than testing yesterday's bundle.

**The web app loads but every panel is empty or errors.** The registry is not on `:5062`, or the
`dev` environment does not exist. Check `curl http://localhost:5062/health/ready`, then
`curl http://localhost:5062/v1/environments`.

**`dotnet test` fails immediately in the integration assemblies.** The Docker daemon is not
running. `docker info` should print without error.

**The migrator or the API cannot reach the database.** The compose Postgres is on **55432**, not
5432. A connection string pointing at 5432 will find whatever else you have there and fail on
credentials rather than on connectivity, which reads as the wrong problem.

**PowerShell says `ConnectionStrings__Concordat=... : The term ... is not recognized`.** That is
the bash form. Use `$env:ConnectionStrings__Concordat = "..."` on its own line first.

## Starting over

```bash
docker compose -f deploy/compose/docker-compose.yml down        # keeps the data
docker compose -f deploy/compose/docker-compose.yml down -v     # discards it
```

`down -v` drops the volume, so the next `up` gives you an empty database and therefore an
**unclaimed** registry again — which is what you want before running the quickstart sample, and
what you must not do casually if you have data you care about. Re-run [step 2](#2-create-the-database-schema)
afterwards.
