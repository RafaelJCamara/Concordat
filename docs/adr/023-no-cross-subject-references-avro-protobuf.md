# ADR-023: No cross-subject references for Avro and Protobuf in v1

- **Status:** Accepted
- **Date:** 2026-08-13
- **Deciders:** Rafael Camara

## Context

[DESIGN §4](../DESIGN.md#4-domain-model-ddd) specifies reference resolution for all three
formats: "JSON Schema `$ref` → `concordat://<env>/<subject>/<version>`; Protobuf `import`
filename → subject; Avro named-type FQN → subject."

Building M5 showed that only the first of those is a complete specification. **JSON Schema was
the easy case and set a misleading precedent**: its `$ref` takes a URI, so the environment,
subject and version all fit inside the reference itself. Neither other format has anywhere to
put a version.

- **Avro** — a cross-subject reference is a bare named-type fullname, e.g.
  `"acme.common.Address"`. In the common case, a reference inside a union or a field's
  `"type"`, it is a plain JSON *string*: there is no room to attach anything at all, let alone
  a version.
- **Protobuf** — `import "acme/common.proto";` names a file. Which subject that maps to is
  answerable; which *version* is not.

A reference with no version has to bind to something at resolution time, and the only available
answer is "whatever that subject holds now". That is precisely the failure
[ADR-017](017-gated-latest-pointer.md) exists to prevent — a third party changing what your
schema means with no deploy on your side — reintroduced one layer down, in the reference graph
rather than on the subject.

## Decision

**Concordat v1 refuses to register an Avro or Protobuf schema that depends on a definition it
does not contain.** Registration fails with `schema_references_unsupported` and names the
type or import that could not be followed.

Two things are explicitly *not* refused, because neither is a cross-subject reference:

- **Definitions inside the same document**, including a type that references itself. Recursive
  Avro records and nested Protobuf messages register normally.
- **Protobuf `google/protobuf/*` imports.** The well-known types ship with every Protobuf
  runtime and are resolved by the compiler, not by a registry. An import of `timestamp.proto`
  is a dependency on the language, closer to a JSON Schema `$ref` to a local fragment than to a
  reference to another subject.

JSON Schema is unaffected and keeps full reference support.

## Alternatives considered

- **Resolve to whichever version is currently `latest`.** Rejected: it works for both formats
  and invents no syntax, but it is exactly the silent-behaviour-change ADR-017 was written to
  eliminate. Having built a gated `latest` pointer for subjects, reintroducing an ungated one
  for references would be incoherent.
- **Pin versions out of band, in a reference manifest supplied at registration.** Not rejected
  on the merits — this is the favourite if references are added later, because it is the only
  mechanism that works identically for Avro and Protobuf. Deferred because it breaks M1.4's
  rule that *edges are derived from the document, never supplied alongside it*, and that rule
  deserves to be revisited deliberately rather than as a side effect of shipping M5. Note that
  the rule was written when JSON Schema was the only format, and its URI-shaped `$ref` made
  keeping it free.
- **Invent a per-format convention** — an object-wrapped Avro reference carrying a Concordat
  property, a version encoded in the Protobuf import filename. Rejected: each convention is a
  rule five SDKs must reproduce character for character
  ([ADR-021](021-tier-2-sdk-set.md)), and neither has been validated against a second
  independent implementation of its own format. This is the same reasoning that stopped M2.3
  inventing a spelling for generic message type names.
- **Refuse `google/protobuf/*` imports too, for consistency.** Rejected: `google.protobuf.Timestamp`
  is close to universal in real Protobuf, so this would make the format's support decorative,
  and it buys no correctness — those definitions do not live in the registry and cannot drift.

## Consequences

- **Positive:** Avro and Protobuf schemas register end to end today, with correct
  content-addressed identity and correct compatibility verdicts. Self-contained schemas are the
  common shape for both formats.
- **Positive:** nothing is guessed. A team whose schemas do span files gets a clear refusal
  naming the import or type, not a verdict computed against a version nobody chose.
- **Negative:** teams that share types across `.proto` files — a normal Protobuf practice —
  must inline them to register. This is the real cost of the decision and should be stated
  plainly in the format documentation, not discovered at first registration.
- **Negative:** the "splitting a `.proto` across files is compatible" case from
  [DESIGN §12](../DESIGN.md#12-verification) cannot be added to the conformance corpus, because
  the split definition lives behind an import the engine will not follow. That case exists to
  demonstrate a Confluent defect Concordat fixes, so it is a real loss from the v1 story.
- **Neutral:** the refusal is additive to remove. Supporting references later means
  implementing `ISchemaReferenceExtractor` for real and relaxing the check; no schema
  registered under this ADR becomes invalid, because a self-contained schema stays valid under
  any reference scheme.

## References

- [DESIGN §4](../DESIGN.md#4-domain-model-ddd) — the reference resolution this narrows
- [ADR-017](017-gated-latest-pointer.md) — the silent-behaviour-change failure being avoided
- [ADR-019](019-language-neutral-protocol.md) — why an invented convention is expensive
- [M5.2 and M5.3](../plan/M5-formats.md) — where this is implemented
