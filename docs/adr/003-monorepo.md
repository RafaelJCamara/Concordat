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
  workflow, with path filters so a docs change does not rebuild every SDK. Not yet
  addressed — CI is .NET-only until M6.
- **Negative:** language-specific tooling that assumes a repository root (Go modules,
  Maven layout conventions) needs explicit configuration.

## References

- [DESIGN §8](../DESIGN.md#8-backend-architecture-ddd--clean-architecture)
- [`src/README.md`](../../src/README.md) — which projects exist when
