# ADR-003: Monorepo

- **Status:** Accepted
- **Date:** 2026-08-13
- **Deciders:** Rafael Camara

## Context

Concordat ships a backend, a web application, a CLI, five or more client SDKs in four
languages, deployment assets and documentation. A change to the envelope specification or
the REST surface touches several of those at once.

## Decision

Everything lives in one repository: `src/`, `clients/`, `web/`, `tools/`, `deploy/`,
`docs/`, `tests/`.

> **The directory list drifted; the decision did not.** `tools/` ended up one level down as
> `src/tools/`, and `samples/`, `scripts/` and `docker/` were added at the top level as the
> milestones that needed them landed. `src/clients/` holds the .NET client and the RabbitMQ
> middleware; the top-level `clients/` this list named is still the planned home of the
> [ADR-021](021-tier-2-sdk-set.md) SDKs and is empty, because
> [ADR-024](024-v1-ships-dotnet-only.md) deferred them past v1.
> [`src/README.md`](../../src/README.md) carries the project-by-project ledger.
> *(Noted 2026-09-24.)*

## Alternatives considered

- **Repository per SDK.** Rejected: an envelope change would need coordinated pull
  requests across five repositories with no atomic commit and no single CI run that
  proves they agree. The cross-language conformance suite
  ([ADR-019](019-language-neutral-protocol.md)) is exactly the thing that must run against
  all of them together.
- **Server repo plus one polyglot clients repo.** Rejected as an unstable middle: the
  protocol artifacts the clients depend on live server-side, so the split lands directly
  on the seam that changes most.

## Consequences

- **Positive:** an envelope or API change and every client update land in one commit. One
  CI pipeline, one version number, one issue tracker.
- **Negative:** CI must learn to build .NET, TypeScript, Python, Go and Java in one
  workflow, with path filters so a docs change does not rebuild every SDK. ~~Not yet
  addressed — CI is .NET-only until M6.~~ **Corrected 2026-09-24:** CI already builds two
  stacks — `.github/workflows/ci.yml` has a `web app` job for the Angular application and a
  `browser end-to-end` job that raises both, six jobs in total. The remaining languages arrive
  with their SDKs, deferred by [ADR-024](024-v1-ships-dotnet-only.md), and no path filters
  exist yet, so a docs-only change still runs every job.
- **Negative:** language-specific tooling that assumes a repository root (Go modules,
  Maven layout conventions) needs explicit configuration.

## References

- [DESIGN §8](../DESIGN.md#8-backend-architecture-ddd--clean-architecture)
- [`src/README.md`](../../src/README.md) — which projects exist when
