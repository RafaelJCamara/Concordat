# ADR-016: Two-axis compatibility — *who breaks* × *what breaks*

- **Status:** Accepted
- **Date:** 2026-08-13
- **Deciders:** Rafael Camara

## Context

Every registry models compatibility on one axis: BACKWARD, FORWARD, FULL and their
transitive variants. That axis answers *who* breaks. It cannot express *what* breaks, and
the difference is not academic:

- `int32 → int64` in Protobuf is **wire-safe** — the bytes still decode — but
  **source-breaking**: generated code stops compiling.
- Renaming a Protobuf message while keeping field tags is **wire-safe** but breaks the
  JSON mapping and generated source.

With one axis, a registry must pick a side. Confluent's Protobuf checker picks strict, and
is consequently *stricter than protobuf's actual wire compatibility*: it rejects changes
that produce byte-identical output, such as splitting a `.proto` across files.

## Decision

Compatibility is a pair of orthogonal axes, configured per environment and overridable per
subject:

- **Who breaks:** `Backward | BackwardTransitive | Forward | ForwardTransitive | Full | FullTransitive | None`
- **What breaks:** `Wire ⊂ WireJson ⊂ Source`

A policy is a pair. `Backward × Wire` permits `int32 → int64`; `Backward × Source` blocks
it. Every finding in `breakingChanges[]` is tagged with the narrowest axis it violates, so
the same change is reported as allowed under one policy and blocked under another.

## Alternatives considered

- **Single axis, strict**, as Confluent does. Rejected: it blocks safe changes and gives
  no vocabulary to explain why.
- **Single axis, permissive.** Rejected: it waves through changes that break generated
  code, which is where most consumer breakage actually happens.
- **A per-change allowlist.** Rejected: unprincipled, unexplainable, and it would grow
  without bound.

## Consequences

- **Positive:** the registry can state precisely why a change is or is not allowed, and
  teams can choose a policy matching how they consume — a Go shop generating structs cares
  about `Source`; a dynamic-language consumer may only need `Wire`.
- **Positive:** it fixes the documented Confluent defect directly, which is a concrete
  demonstrable difference rather than a claim.
- **Negative:** the compatibility matrix is now two-dimensional, so the golden test corpus
  is `(old, new, who-axis, what-axis) → (verdict, expected paths)`. This is the heaviest
  test investment in the repository, and a wrong verdict either blocks safe changes or
  waves breaking ones through.
- **Negative:** more configuration surface to explain in documentation.

## References

- [DESIGN §7](../DESIGN.md#7-contract-checks--cli-and-build-time)
- [M1.3 — Compatibility engine](../plan/M1-registry-core.md#m13-compatibility-engine--adr-016-design-7)
