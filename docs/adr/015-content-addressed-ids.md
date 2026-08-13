# ADR-015: Schema IDs are content-addressed, not sequential

- **Status:** Accepted
- **Date:** 2026-08-13
- **Deciders:** Rafael Camara

## Context

Confluent allocates schema IDs from a global sequential counter. Three problems follow.
The allocator needs a collision-retry loop that can hard-fail. IDs differ between
installations, so an ID means nothing across environments. And hard-deleting a schema frees
its ID for reuse — Confluent issue #4277 documents the result: a new schema reusing a
soft-deleted ID can tombstone referenced versions, leaving content fetchable by ID while
reference resolution breaks.

Confluent themselves retrofitted a content-derived GUID carried in a header in CP 8.1+.
AWS Glue, Azure and Buf converged on content addressing independently.

## Decision

`SchemaId` is the SHA-256 of the canonical form, truncated to 128 bits, lowercase hex.
**The hash covers the whole envelope — canonical body plus references plus any rules and
metadata, not just the body.** Registering an identical schema returns the existing ID.
Schema content is never deleted and IDs are never reallocated.

## Alternatives considered

- **Sequential integer IDs.** Rejected for the three reasons above.
- **Hash the body only.** Rejected: two schemas with identical bodies but different
  reference sets would collide, which is exactly what CP 8.1's GUID computation avoids.
- **Full 256-bit hash.** Rejected: 128 bits is ample against collision for this population
  and halves the bytes on every message header.

## Storage: the schema table is global

**Schemas are stored once, globally, keyed by `SchemaId` alone — not per tenant** (decided
during M1.1, before M1.5 writes a migration).

This is the storage-layer expression of the decision itself. "Same content ⇒ same ID
everywhere" is only literally true if the same content is also *one row* everywhere;
tenant-scoping the table would make `SchemaId` a partial key and reintroduce, per tenant, the
duplication content addressing exists to remove.

The cost is an authorisation obligation on M1.6: `GET /schemas/{id}` must be authorised by
**reachability** — the caller may fetch a schema only if some subject in their tenant
references it — rather than by a tenant column on the row. That work is real and must not be
forgotten, because the naive implementation leaks any schema to anyone who can guess a hash.

Subjects and versions remain tenant-scoped as normal; only the immutable content is shared.

## Consequences

- **Positive:** the same schema yields the same ID in every environment and every
  installation, so promotion never invalidates an in-flight envelope — the single most
  useful property for [ADR-012](012-environment-over-brokers.md)'s promotion flow.
- **Positive:** registration becomes idempotent via a unique constraint. No single-writer
  counter, no retry loop, no coordination.
- **Positive:** `schema-id → schema` is immutable, so clients can cache it forever. This is
  *guaranteed* rather than assumed, which is what lets the SDK keep the registry off the
  delivery path after warm-up.
- **Negative, and load-bearing:** canonicalisation becomes day-one work rather than an
  optimisation. Get it wrong and semantically identical schemas produce different IDs.
  Confluent's `normalize.schemas` still defaults to `false`, which is how registries
  accumulate thousands of near-duplicate schemas.
- **Negative:** IDs are not human-friendly. A 32-character hex string is harder to discuss
  than "schema 41", so the UI and CLI must show subject and version alongside.

## References

- [DESIGN §4, Context A](../DESIGN.md#context-a--registry-core)
- [M1.2 — Canonicalisation and identity](../plan/M1-registry-core.md#m12-canonicalisation-and-identity--adr-015)
