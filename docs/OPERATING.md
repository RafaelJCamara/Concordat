# Operating Concordat

For somebody running the registry for other people. [RUNNING.md](RUNNING.md) is the developer loop and
[QUICKSTART.md](QUICKSTART.md) is the first ten minutes; this is the page for the day after.

**What you run:** the registry (`concordat-api`, an ASP.NET container), its **migrator** (the same image, a
different entry point, run to completion), the **web app** (`concordat-web`, nginx serving the Angular bundle and
forwarding `/v1` and `/health` to the registry) and **PostgreSQL**. RabbitMQ is yours and is not touched by the
registry: it never sees message traffic ([BROKER-PERMISSIONS.md](BROKER-PERMISSIONS.md) is about what the SDK needs).
**PostgreSQL is the only state.**

## First run, and the checklist before it is reachable

1. **Set `CONCORDAT_BOOTSTRAP_TOKEN`** (any long random string, `openssl rand -hex 32`) before the registry is
   reachable by anybody but you. A new instance answers `401` to everything except the claim
   ([ADR-047](adr/047-a-new-instance-is-closed-until-it-is-claimed.md)), and without a token the claim is open to the
   first request that reaches it.
2. **Open the web app and create the owner** (the token goes in the *Setup token* box), or `POST /v1/auth/bootstrap`
   with `{"email","password","token"}`. The password is at least 12 characters. This happens once.
3. **Put TLS in front** (below), **change the database password**, and decide who else is a member: *Settings → Members*
   (roles: reader, admin, owner), and *Settings → API keys* for pipelines.
4. **Do not enable `AllowAnonymousUntilClaimed`** on anything but a machine only you can reach.

## Configuration

Configuration is environment variables, with `__` for `:` (`Concordat__Smtp__Host`). Anything not listed is
not meant to be changed.

### The registry (`concordat-api`) and the migrator

| Variable | Meaning | Default |
|---|---|---|
| `ConnectionStrings__Concordat` | Npgsql connection string. **Required** outside Development: the registry refuses to guess one. The migrator reads the same variable. | none |
| `Concordat__Profile` | `SelfHosted` (one organisation) or `Cloud` (multi-tenant; refuses to start without a Key Vault key). | `SelfHosted` |
| `Concordat__Authentication__BootstrapToken` | The secret `POST /v1/auth/bootstrap` must carry. Compose reads `CONCORDAT_BOOTSTRAP_TOKEN`. | unset |
| `Concordat__Authentication__AllowAnonymousUntilClaimed` | Opt in to answering an *unclaimed* instance as an owner. Compose: `CONCORDAT_ALLOW_ANONYMOUS_UNTIL_CLAIMED`. | `false` |
| `Concordat__Authentication__SessionLifetime` | How long a sign-in lasts (a .NET time span). | 12 hours |
| `Concordat__WebOrigin` | The web app's origin, **only if it is served from a different origin than the API**. Same-origin deployments (the compose file, the web container) need nothing. | unset: cross-origin requests are refused |
| `Concordat__Smtp__Host`, `__Port`, `__UseStartTls`, `__Username`, `__Password`, `__From` | Outbound email for notifications. With no host, an email subscription's deliveries fail and are parked with the reason after five attempts. | port 587, STARTTLS on |
| `Concordat__KeyProtection__KeyUri` | An Azure Key Vault *key* URI that wraps the Data Protection key ring. Required for `Cloud`, optional otherwise. | unset |

### The web container (`concordat-web`)

| Variable | Meaning | Default |
|---|---|---|
| `CONCORDAT_API_UPSTREAM` | Where nginx forwards `/v1` and `/health`. | `http://registry:8080` |
| `CONCORDAT_API_BASE_URL` | Where the *browser* sends API requests. | empty: the same origin |
| `CONCORDAT_PROFILE` | `self-hosted` or `cloud`. | `self-hosted` |
| `CONCORDAT_DEFAULT_ENVIRONMENT` | The environment selected on a cold start. | `dev` |

A value the image will not use stops the container with one sentence naming it ([ADR-029](adr/029-web-container.md)).

## TLS and the network

The registry speaks plain HTTP on port 8080 and **expects TLS to be terminated in front of it**: a reverse proxy,
an ingress, or a platform that does it (the Azure template does). It trusts `X-Forwarded-For` and
`X-Forwarded-Proto` **from anywhere**, because a platform-managed ingress has no address to name; so do not
expose the registry's own port except through your proxy, or a client can claim to be somebody else's address.
In production the registry also sends `Strict-Transport-Security`. The health endpoints
(`/health/live`, `/health/ready`) answer without a credential and over plain HTTP, for your orchestrator.

The compose file binds every port to `127.0.0.1` on purpose: it is an evaluation shortcut, with the well-known
development passwords for PostgreSQL and RabbitMQ.

## Upgrading

1. **Back up first** (below).
2. Pull the new images, **run the migrator to completion** (`concordat-migrate`: the API image with the entry point
   `dotnet Concordat.Migrator.dll`, with the same connection string), **then start the new registry**. The compose file
   does this in order (`migrate` must exit 0 before `registry` starts); on any other platform it is a job that
   runs before the rollout.
3. The migrator only applies migrations forward. **A rollback is a restore of the backup you took in step 1**, not an older image on
   a newer schema. The registry reads rows an *older* build wrote and, where it matters, rows a *newer* one wrote
   ([ADR-036](adr/036-the-audit-read-tolerates-an-action-its-build-does-not-know.md)), but a schema change is
   not undone by starting an older binary.
4. The web and registry images are versioned together. Upgrade them in the same change.

## Backups

**Back up the PostgreSQL database and nothing else**, with `pg_dump` or your platform's snapshots. It holds the
subjects and versions, the audit trail, accounts and API-key hashes, **the Data Protection key ring that decrypts
the broker credentials Concordat stores**, and the outbox. A restore into an empty database plus the migrator is a
full recovery; there is no separate store to keep in step.

Because the key ring is in the same database as the ciphertext it protects, a database dump can read stored
broker credentials unless the keys are wrapped (`Concordat__KeyProtection__KeyUri`). Treat a dump as a secret.

## Health and logs

- `GET /health/live` is liveness and **ignores the database on purpose**: restarting a registry whose database is
  down helps nobody. `GET /health/ready` includes the database.
- Logs go to standard output. While an instance started with the anonymous opt-in is unclaimed it logs a warning
  hourly; a closed instance logs nothing about it.
- `GET /v1/audit` (and *Audit log* in the web app) is the record of every change, as the authenticated caller. A
  refused request writes nothing, by design.

## What this page does not cover

Sizing and load figures (the dashboard's version counts measure about 100 ms at 200,000 versions; nothing else
has been load tested), running several registry instances (the key ring is shared through PostgreSQL so that it
works, but it has not been exercised under load), high-availability PostgreSQL, and the hosted Cloud edition.
