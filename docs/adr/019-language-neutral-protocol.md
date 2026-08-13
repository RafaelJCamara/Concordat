# ADR-019: The registry is a language-neutral HTTP protocol; every SDK is an ordinary client

- **Status:** Accepted
- **Date:** 2026-08-13
- **Deciders:** Rafael Camara

## Context

The server is written in .NET, and the first SDK is .NET. The failure mode this invites is
well known: the reference client quietly depends on server behaviour that is never written
down, and the second-language client discovers it the hard way. By then the "protocol" is
whatever C# happens to do.

Concordat's reach argument depends on being usable from any language — .NET is genuinely
underserved in this space, but it is a minority of RabbitMQ traffic.

## Decision

The protocol is the product. **No capability exists that is not reachable over documented
REST.** The .NET SDK gets no privileged endpoint, no private header, no serialisation
shortcut, and no behaviour that is not written down.

Five artifacts are normative and versioned with the API:

| Artifact | Specifies |
|---|---|
| `docs/api/openapi.v1.json` (generated, committed) | the REST surface |
| Envelope spec, DESIGN §2 — prose and a header table, not a C# type | what goes on the wire |
| Canonicalisation rules, DESIGN §4, deferring to each format's own spec | how a schema ID is derived |
| The `concordatCode` catalogue | every error a client must handle |
| `tests/Concordat.Conformance` | required client *behaviour* |

**Acceptance test for the principle:** a team writes a complete Go client from those five
documents, without reading a line of C#, and hits no surprises.

Two rules that follow, stated because they are the ones violated first: the schema ID is a
hash of canonical text defined by each format's own specification, never of a .NET
serialisation; and no API response may contain a CLR-shaped identifier — no
assembly-qualified names, no `System.*` type strings.

## Alternatives considered

- **Treat the .NET client as the reference implementation.** Rejected: that makes C# the
  specification, and every other language a reverse-engineering exercise.
- **Write the conformance corpus when the second SDK arrives.** Rejected, and this is the
  subtle one: a corpus written then can only ratify whatever .NET already does. It must
  exist from M1, before anything can quietly depend on .NET behaviour.

## Consequences

- **Positive:** adding a language is additive and costs the server nothing, which is why
  dropping or adding an SDK ([ADR-021](021-tier-2-sdk-set.md)) is a cheap, reversible
  decision.
- **Positive:** forces the error catalogue and envelope to be written down properly, which
  benefits the .NET client too.
- **Negative:** slower than letting the first client define reality. The corpus must be
  maintained from M1 with no second consumer to justify it for several milestones.
- **Negative:** payload validation is client-side and uses a different third-party library
  per language, so verdicts can diverge with no bug on Concordat's part. Mitigated by
  pinning draft 2020-12, defining an interoperable keyword subset, and carrying a
  payload-validation corpus.

## References

- [DESIGN §5](../DESIGN.md#5-api-surface-and-cross-language-strategy)
- [M1.7](../plan/M1-registry-core.md#m17-conformance-corpus-v0--adr-019), [M6.1](../plan/M6-sdks.md#m61-protocol-freeze-and-interop-prerequisites-)
