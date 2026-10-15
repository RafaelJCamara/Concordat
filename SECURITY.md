# Security policy

## Reporting a vulnerability

Please **do not open a public issue** for a security problem. Use GitHub's private vulnerability reporting
(**Security → Report a vulnerability** on this repository), or email the maintainer at the address on the
GitHub profile of the repository owner. Include what you found, how to reproduce it, and which version or
commit you were running.

You can expect an acknowledgement within **three working days** and a first assessment within **ten**. A
confirmed vulnerability is fixed on `main` and released as a patch; the advisory credits you unless you
prefer otherwise.

## What is supported

The latest release, and `main`. Concordat is young: there is no long-term-support branch.

## What the registry does to protect itself

So that a report starts from what is already known ([docs/STATUS.md](docs/STATUS.md) has the verified detail):

- **A new instance answers nobody until it is claimed** ([ADR-047](docs/adr/047-a-new-instance-is-closed-until-it-is-claimed.md)),
  and every read as well as every write needs a credential once it is
  ([ADR-027](docs/adr/027-read-requires-authentication.md)). **Set `CONCORDAT_BOOTSTRAP_TOKEN` on any host
  other than your own machine**: without it, whoever reaches `POST /v1/auth/bootstrap` first owns the instance.
- Passwords are hashed with PBKDF2-HMAC-SHA512 and API keys by SHA-256, never stored; webhooks are signed; the web
  app holds its credential in memory only, with an `httpOnly`, `SameSite=Strict` cookie that does nothing but hand it
  back after a reload ([ADR-006](docs/adr/006-angular-port-strategy.md) records the decision).
- Sign-in and sign-up attempts are rate limited.
- Tenant isolation is enforced by a query filter on every tenant-owned table, tested through real HTTP against
  PostgreSQL in the multi-tenant profile.
- Dependencies are audited nightly ([`dependency-audit.yml`](.github/workflows/dependency-audit.yml)), and
  `nuget.config` maps each package to its source.

## Known limits

Listed so that nobody has to find them: the registry never sees message traffic, so "coverage" is what contracts
declare and not proof that a service does it; the bootstrap claim is open until it happens; the rate limit covers
sign-in and sign-up attempts only and is not configurable; and the compose file's Postgres and RabbitMQ credentials are the well-known
development ones and are safe only because compose binds them to `127.0.0.1`.