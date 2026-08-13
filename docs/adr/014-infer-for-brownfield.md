# ADR-014: `concordat infer` for brownfield onboarding

- **Status:** Accepted
- **Date:** 2026-08-13
- **Deciders:** Rafael Camara

## Context

The realistic Concordat user has an existing RabbitMQ estate with 50 to 500 untyped JSON
message types and no schemas at all. Authoring those by hand is weeks of work that must
happen *before* the product delivers any value. That gap is where adoption dies.

## Decision

The CLI can infer draft JSON Schemas from real messages, two ways:

- **File mode (the default):** from a directory of sample payloads.
- **Queue mode:** read-only drain of a live queue via `basic.get` with requeue, or an
  exclusive consumer that nacks with requeue.

Inference covers types, required-by-presence across samples, `format` detection (uuid,
date-time, email), low-cardinality enums, and nullability. Output is **a draft plus a
confidence and ambiguity report for human review. It never auto-registers.**

## Alternatives considered

- **No inference; hand-author everything.** Rejected: it makes onboarding cost proportional
  to estate size, which is exactly backwards — the bigger the estate, the more the product
  is worth and the less likely anyone adopts it.
- **Infer and auto-register.** Rejected: inference from a sample is a guess. Auto-registering
  guesses would fill the registry with wrong schemas that then gate production traffic.
- **Queue mode as the default.** Rejected: draining a live queue can reorder it. File mode
  is the safe default and queue mode carries a documented warning.

## Consequences

- **Positive:** turns a 200-message-type estate from weeks of authoring into an afternoon
  of review, which is the difference between evaluating the product and not.
- **Negative:** inference quality depends entirely on sample coverage. A field absent from
  every sample is inferred as absent; an optional field present in all samples is inferred
  as required. Hence the confidence report rather than a bare schema.
- **Negative:** queue mode touches production traffic. It is read-only by construction, but
  reordering is a real side effect and must be documented prominently.

## References

- [DESIGN §7](../DESIGN.md#7-contract-checks--cli-and-build-time)
- [M3.2 — `concordat infer`](../plan/M3-cli.md#m32-concordat-infer-adr-014)
