# ADR-008: Built-in identity with scoped API keys, OIDC optional

- **Status:** Accepted
- **Date:** 2026-08-13
- **Deciders:** Rafael Camara

## Context

A registry that gates schema registration needs to know who is asking. The usual options
are to require an external identity provider, or to build local accounts.

Requiring an IdP means a team cannot evaluate the product without first standing up Keycloak
or configuring an OAuth application. For a tool whose adoption story is "docker compose up
and try it", that is a real barrier.

## Decision

Concordat ships its own identity: local accounts, memberships, roles, and API keys hashed
at rest with explicit scopes (`subject:read|write|admin`, `contract:*`, `env:*`,
`broker:*`, `org:admin`). OIDC is supported but optional.

## Alternatives considered

- **Require OIDC.** Rejected: blocks first-run evaluation and forces infrastructure on
  self-hosted users who may have none.
- **No authentication in self-hosted mode.** Rejected: the schema registry decides what
  reaches production. An unauthenticated write endpoint is not acceptable even internally,
  and it would make [ADR-018](018-admin-only-schema-editing.md) unenforceable.
- **API keys only, no user accounts.** Rejected: the web application needs sessions, and
  audit entries need a human identity rather than a key label.

## Consequences

- **Positive:** `docker compose up` produces a working, authenticated instance with no
  external dependency. This is a concrete differentiator: Confluent's free tier has **no
  authorization at all** — RBAC and subject ACLs are separately Enterprise-licensed — so
  anyone with credentials can mutate or delete any subject.
- **Negative:** Concordat owns password storage, session handling and key rotation, which
  are security-sensitive and must be got right rather than delegated.
- **Neutral:** enterprises will still use OIDC; the built-in path is for everyone else.

## References

- [DESIGN §4, Context E](../DESIGN.md#context-e--identity--access)
- [M8 — Identity, RBAC, API keys](../plan/M8-identity.md)
