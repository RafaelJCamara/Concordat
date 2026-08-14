# ADR-024: v1 ships the .NET SDK only; Tier 2 SDKs are deferred

- **Status:** Accepted
- **Date:** 2026-08-14
- **Deciders:** Rafael Camara

## Context

[ADR-021](021-tier-2-sdk-set.md) commits to four Tier 2 SDKs — TypeScript/JavaScript, Python,
Go and Java — and [ADR-019](019-language-neutral-protocol.md) states an acceptance test written
against that commitment: *a team writes a complete Go client from the normative documents
without reading a line of C#*.

[M6.1](../plan/M6-sdks.md) is done. The protocol is now published as a coherent set in
[`docs/protocol/`](../protocol/README.md), the `concordatCode` catalogue is generated and gated,
and the conformance corpus covers all three formats. The prerequisites for writing a second SDK
exist.

What does not yet exist is **evidence from use**. The .NET SDK has never been run against a
real workload by anyone. Every design decision it embodies — enforcement defaulting to
`Monitor`, fail-open resolution, the envelope's optional headers, quarantine behaviour — is
reasoned but unvalidated.

Writing four more SDKs against an unvalidated design multiplies whatever is wrong with it by
five, and each one is a public package with its own release cadence and its own users to
migrate when the answer changes.

## Decision

**v1 ships the .NET SDK only.** M6.2 (TypeScript/JavaScript), M6.3 (Python), M6.4 (Go) and
M6.5 (Java) are **deferred, not cancelled** — they resume once the .NET SDK has been tested
against a real workload.

ADR-021 is **not superseded**. Its decision — *which* four languages, and in what order — still
stands and is still the plan. This ADR changes only when they are built.

**Nothing built for interoperability is removed or re-argued.** The protocol documents, the
conformance corpus, the `concordatCode` catalogue, the portability checker's warnings about
constructs that behave differently in other languages — all of it stays, and all of it stays
justified. It is the groundwork that makes resuming cheap, and most of it has already paid for
itself against the single implementation:

- The corpus found four places the .NET validator disagreed with draft 2020-12.
- Publishing the protocol found a canonicalisation divergence that would have given the same
  schema a different id in Go or Python — caught with no Go or Python client in existence.

## Alternatives considered

- **Drop the Tier 2 SDKs permanently and call Concordat a .NET product.** Rejected. It would
  mean re-arguing the portability warnings, the corpus's cross-validator framing and much of
  ADR-019, and it forecloses the thing that makes a schema registry worth having in a mixed
  estate. Nothing about the current state requires that decision now.
- **Build one second SDK anyway**, as the only real proof the protocol is language-neutral —
  ADR-019's own argument. Rejected for v1 on timing, not merit: the proof is worth having, but
  it is worth more against a design that has survived contact with a real workload. This is the
  first thing to reconsider when the SDKs resume.
- **Build them in parallel with validating .NET.** Rejected: it is precisely the multiplication
  this ADR exists to avoid, and a protocol change discovered during validation would land on
  five codebases instead of one.

## Consequences

- **Positive:** one implementation to change while the design is still moving. A protocol
  correction — and M6.1 alone produced several — costs one edit rather than five.
- **Positive:** the deferral is cheap to reverse. The prerequisites are done, the corpus is
  executable, and the protocol is documented to the standard an outside implementer needs.
- **Negative, and the real cost:** ADR-019's acceptance test stays **unverified**. A protocol
  is only demonstrably language-neutral when someone implements it in another language, and
  until then "no .NET assumptions leaked" remains an assertion. The corpus and the protocol
  documents reduce that risk; they do not discharge it.
- **Negative:** Concordat is not usable outside .NET in v1, which excludes exactly the mixed
  estates a schema registry is most valuable in.
- **Neutral:** [M6](../plan/M6-sdks.md) closes for v1 with M6.1 complete. Its remaining item —
  surfacing portability findings in `concordat lint` — moves with the SDKs.

## References

- [ADR-019](019-language-neutral-protocol.md) — the acceptance test this leaves unverified
- [ADR-021](021-tier-2-sdk-set.md) — the SDK set, unchanged and still the plan
- [`docs/protocol/`](../protocol/README.md) — the prerequisites, complete
- [M6](../plan/M6-sdks.md) — where the deferred packages live
