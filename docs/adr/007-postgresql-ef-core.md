# ADR-007: PostgreSQL with EF Core

- **Status:** Accepted
- **Date:** 2026-08-13
- **Deciders:** Rafael Camara

## Context

Confluent, Karapace and Redpanda all store schemas in a compacted Kafka topic. That design
follows from their context: the broker is already there, already replicated, and already
the system of record. Concordat has no such broker — RabbitMQ is the thing being governed,
not a dependency of the registry, and storing registry state in the broker it governs
creates a circular failure mode.

Apicurio's PostgreSQL backend is the direct precedent for a relational store.

## Decision

PostgreSQL as the only supported store, accessed through EF Core.

## Alternatives considered

- **Store schemas in RabbitMQ.** Rejected: circular dependency. A broker outage would take
  the registry with it, and the registry is what tells you the broker's traffic is valid.
- **SQLite for self-hosted, PostgreSQL for Cloud.** Rejected: two dialects means two
  migration sets and two sets of query behaviour to test, for a deployment story already
  served by a Postgres container in the compose file.
- **Dapper or raw SQL instead of EF Core.** Rejected: EF Core's global query filters are
  the mechanism that makes multi-tenancy one code path
  ([ADR-009](009-apache-2-including-cloud.md), M9), and migrations are needed regardless.

## Consequences

- **Positive:** one dialect, one migration set. Global query filters give row-level tenant
  isolation without an `if (cloud)` in every query — wired from M1.5 with a single
  implicit tenant so M9 is a configuration swap, not surgery.
- **Positive:** a unique constraint on the content-addressed schema id makes registration
  idempotent with no application-level locking
  ([ADR-015](015-content-addressed-ids.md)).
- **Negative:** self-hosting requires running PostgreSQL. There is no zero-dependency
  single-file mode.
- **Negative:** EF Core's abstraction can hide query cost; the compatibility and impact
  paths need explicit review.

## References

- [DESIGN §4](../DESIGN.md#4-domain-model), [DESIGN §8](../DESIGN.md#8-backend-architecture-ddd--clean-architecture)
- [M1.5 — Persistence](../plan/M1-registry-core.md#m15-persistence)
