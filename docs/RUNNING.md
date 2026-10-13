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
| `5063` | The web app, from containers | The compose `registry` profile — [§7](#7-or-run-all-of-it-from-containers). Next to the registry's 5062, and not 4200, so `npm start` and the container can run at once |

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

- **`"claimed": false`** — every request succeeds unauthenticated, reads and writes alike. The
  quickstart sample runs as written. The registry logs a warning on a loop telling you this,
  which is the point.
- **`"claimed": true`** — every request needs a credential, and anything unauthenticated gets
  **401** ([ADR-027](adr/027-read-requires-authentication.md)) — a plain `curl` against
  `GET /v1/environments/dev/subjects` on a claimed instance now needs the same `Authorization`
  header a write does.

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

## 7. Or run all of it from containers

Everything above is the developer loop. To *evaluate* the product — the registry, its migrator, its
database and its web UI — with no .NET SDK and no Node, build the two images and let compose run them:

```bash
docker build -f docker/api.Dockerfile -t concordat/api:local .
docker build -f docker/web.Dockerfile -t concordat/web:local .
```

**bash**

```bash
CONCORDAT_IMAGE=concordat/api:local CONCORDAT_WEB_IMAGE=concordat/web:local \
  docker compose -f deploy/compose/docker-compose.yml --profile registry up -d postgres migrate registry web
```

**PowerShell**

```powershell
$env:CONCORDAT_IMAGE = "concordat/api:local"
$env:CONCORDAT_WEB_IMAGE = "concordat/web:local"
docker compose -f deploy/compose/docker-compose.yml --profile registry up -d postgres migrate registry web
```

The two variables name images you just built because the published ones
(`ghcr.io/rafaeljcamara/concordat-api` and `-web`, the defaults) do not exist until the publish
workflow has run — see "Blocked on you" in [STATUS.md](STATUS.md). Once they do, the variables
disappear and so does the build.

Naming the four services leaves RabbitMQ out, which nothing here needs. Leave the names off
(`--profile registry up -d`) to start it too, which the quickstart sample does need — and which fails
with *port is already allocated* if another project's broker owns 5672 (see
[Troubleshooting](#troubleshooting)).

```bash
docker compose -f deploy/compose/docker-compose.yml --profile registry ps -a
```

```
NAME                 IMAGE                     COMMAND                  SERVICE    CREATED          STATUS                      PORTS
concordat-api        concordat/api:local       "dotnet Concordat.Ap…"   registry   40 seconds ago   Up 16 seconds (healthy)     127.0.0.1:5062->8080/tcp
concordat-migrate    concordat/api:local       "dotnet Concordat.Mi…"   migrate    41 seconds ago   Exited (0) 16 seconds ago
concordat-postgres   postgres:17-alpine        "docker-entrypoint.s…"   postgres   41 seconds ago   Up 40 seconds (healthy)     127.0.0.1:55432->5432/tcp
concordat-web        concordat/web:local       "/docker-entrypoint.…"   web        40 seconds ago   Up 10 seconds (healthy)     127.0.0.1:5063->8080/tcp
```

`migrate` exiting 0 is the point: it ran, and the registry waited for it. Then open
**<http://localhost:5063>**. The registry is still on <http://localhost:5062>, and everything in
[step 4](#4-claim-the-instance--read-this-before-anything-writes) about claiming applies
unchanged — a fresh database is an unclaimed registry, and the page says so in a banner.

nginx forwards `/v1` and `/health` to the registry, so the browser only ever talks to port 5063, and
you can see both halves answer through it:

```bash
curl http://localhost:5063/config.json
curl http://localhost:5063/v1/auth/status
```

```json
{"apiBaseUrl":"","profile":"self-hosted","tenant":null,"defaultEnvironment":"dev"}
{"claimed":false,"authenticated":true,"actor":"unclaimed-instance","scopes":["broker:read", "..."]}
```

The first is **the web container's own answer, written from its environment when it started**; the
second is the registry's, through nginx. That one image is what every deployment gets, and
`config.json` is how it is told where its registry is: `CONCORDAT_API_UPSTREAM` (where nginx
forwards — the compose file sets it to `http://registry:8080`), `CONCORDAT_API_BASE_URL`,
`CONCORDAT_PROFILE`, `CONCORDAT_TENANT` and `CONCORDAT_DEFAULT_ENVIRONMENT`. A value that is wrong
stops the container with a sentence rather than being corrected; the header of
[`docker/web.Dockerfile`](../docker/web.Dockerfile) has the list and [ADR-029](adr/029-web-container.md)
has the reasoning.

This is not the developer loop. There is no hot reload, and a change to the app is an image rebuild —
`npm start` (§5) is still how you work on it. What it is is the production build, served the way it
ships, which is why the browser suite can be pointed at it:

```bash
cd web && CONCORDAT_WEB_URL=http://localhost:5063 npm run e2e
```

## Running the tests

### The .NET suite — 1,664 tests

```bash
dotnet test Concordat.slnx
```

Needs Docker running. Five of the thirteen assemblies start real PostgreSQL and RabbitMQ
containers through Testcontainers rather than mocking them, so a cold run pulls images and
takes several minutes. The other eight are pure unit tests and finish in seconds:

```bash
dotnet test tests/Concordat.Domain.Tests        # 541 tests, no Docker, ~1s
```

### The web suite — 1,425 tests

```bash
cd web
npm test                # vitest
npm run lint            # architectural boundaries, not just style — see web/README.md
npm run format:check    # prettier
npm run codes:check     # fails if the error-code union has drifted from the .NET catalogue
npm run monaco:check    # fails if the vendored editor stylesheet is not the installed Monaco's
```

### The browser end-to-end suite — 223 tests

Two processes Playwright deliberately does not start for you, then the tests — a config that
quietly started its own copy of either half would produce a suite that passes against the
wrong thing.

```bash
# 1. the registry on :5062        (either dotnet run, or the compose registry profile)
# 2. the web app on :4300
cd web && npm start -- --port 4300

# 3. the tests
cd web && npm run e2e
```

`npm run e2e:headed` watches it happen; `npm run e2e:ui` opens Playwright's runner. Override
either endpoint with `CONCORDAT_REGISTRY` and `CONCORDAT_WEB_URL` — the second is how the same
suite runs against the production image from [§7](#7-or-run-all-of-it-from-containers), which it
should, because the dev server is not what ships. It is also the way out when `4300` is not yours:
another project's container can be published there (on one machine Docker Desktop was forwarding it to
an unrelated dashboard, and the suite failed at sign-in looking for a form that was not this app's), so
`npm start -- --port 4301` and `CONCORDAT_WEB_URL=http://localhost:4301 npm run e2e` is the same suite on
a port nobody else has.

**The suite creates things that cannot be deleted** — a subject is permanent, and a retired one keeps its
name. A run adds nine to `dev` (two from creating one on the create screen, seven from changing owners and
policies on the subject page), one to `e2e-inherit` (an environment whose default is `NONE`, which nothing may
change), two to `e2e-follows` (the one environment whose default a spec moves, because the spec owns it and puts
it back before every run), and the first run on a database adds a fixed table of them to an environment of its own, `e2e-names`;
later runs add nothing there. See [web/e2e/README.md](../web/e2e/README.md) for which, and why `e2e-empty` must
stay empty.

The suite claims the instance and creates its own fixtures, all idempotently. See
[web/e2e/README.md](../web/e2e/README.md) for what it assumes and why it exists.

### What CI runs

`.github/workflows/ci.yml` is the authority. Seven jobs: `build & test` (format, build, the
OpenAPI drift gate, the .NET suite), `web app`, `browser end-to-end`, `protocol docs gate`,
`contract drift gate`, `Web container` and `CLI container`. To reproduce the formatting gate locally:

```bash
dotnet format Concordat.slnx --verify-no-changes
```

## Troubleshooting

**`401 (Unauthorized)` from the sample, or from any read or write.** The instance is claimed. See
[step 4](#4-claim-the-instance--read-this-before-anything-writes).

**The web app loads to a sign-in prompt where you expected the subject list.** Correct, not
broken, on a claimed instance: [ADR-027](adr/027-read-requires-authentication.md) gates reads too,
and `signedInGuard` sends a signed-out visitor to `/sign-in` before `/subjects` renders.

**The registry will not start: address already in use on 5062.** Something else is already
serving it — most often a `concordat-api` container left behind by the compose `registry`
profile or by an earlier e2e session:

```bash
docker ps --filter name=concordat-api
docker stop concordat-api
```

**Starting the dependencies fails: port 5672 or 15672 is already allocated.** Another RabbitMQ
owns it — usually a different project's compose stack. Postgres sidesteps this by publishing on
55432; RabbitMQ kept the defaults because a second broker on one machine is rarer, but it does
happen. Stop the other broker, or override the published ports with a second compose file
(`ports:` needs `!override`) — remembering the quickstart sample's `ConnectionFactory` then
needs the new port too.

**The web container's `/v1` answers 502.** It is running and cannot find the registry: nginx looks the
upstream up when a request arrives, so the UI comes up first and says so. Check
`docker compose -f deploy/compose/docker-compose.yml --profile registry ps` and that
`CONCORDAT_API_UPSTREAM` names a service on the same compose network.

**The web container exits at start with one line starting `concordat-web:`.** A `CONCORDAT_*` variable
has a value the image will not use — `docker logs concordat-web` says which and why. It refuses
rather than corrects, because the values decide where the page may connect.

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

If you started the `registry` profile (§7), add `--profile registry` to either. Without it compose
only stops the services whose profile is on, so the database goes and the registry and the web
container are left running with nothing behind them.

`down -v` drops the volume, so the next `up` gives you an empty database and therefore an
**unclaimed** registry again — which is what you want before running the quickstart sample, and
what you must not do casually if you have data you care about. Re-run [step 2](#2-create-the-database-schema)
afterwards.
