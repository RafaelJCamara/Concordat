# ADR-031: A schema containing an unpaired surrogate escape is refused, not repaired

- **Status:** Accepted
- **Date:** 2026-10-13
- **Deciders:** Rafael Camara

## Context

The schema editor's parity test (ADR-028) found a document the registry crashed on and the editor
did not mention: `{"a":"\ud800"}` — a JSON string holding a lone UTF-16 surrogate escape. It answered
**500**, from registration, from the dry run and from the lookup. The first account of it, written
into [STATUS.md](../STATUS.md) and the parity spec, was *"a bare 500 with no Problem Details body,
from the canonical writer"*. Reproducing it against the real registry corrected that three ways:

- **It does have a body.** `application/problem+json`, the framework's generic one — a title and a
  trace id and no `concordatCode`, which for a client branching on the code is the same as none.
- **It is not the writer.** `JsonDocument.Parse` accepts the escape, because the grammar allows any
  four hex digits. What throws is *reading the string back*: `JsonElement.GetString()` and
  `JsonProperty.Name` raise `InvalidOperationException: Cannot read invalid UTF-16 JSON text as
  string`, so the first walk of the tree dies — the duplicate-key check, before the writer is reached.
- **It is wider than one string.** Any string or *key* at any depth, in JSON Schema and in Avro
  (`doc`, `default`, any custom attribute), and a *reversed* pair (`\ude00\ud83d`) is two unpaired
  halves and fails the same way. The duplicate-key refusal quotes the key it finds, so a document
  with a lone-surrogate key written twice would have failed *building the refusal*.

The string such an escape stands for has no UTF-8 spelling, and parsers disagree about it. Measured
on this machine: Go replaces it with U+FFFD while parsing; JavaScript and Python keep it as a string
they cannot then encode (JavaScript writes U+FFFD, Python raises); .NET's reader throws when the string
is read. RFC 8259 §8.2 only warns that the behaviour is unpredictable; RFC 7493 (I-JSON) forbids it.

## Decision

**The registry refuses it, with the existing `schema_malformed`.** A canonicaliser in any language
must refuse a JSON document containing an unpaired surrogate escape — a high half (`D800`–`DBFF`) not
immediately followed by an escaped low half (`DC00`–`DFFF`), or a low half not immediately preceded
by its high half — in a value or a key, at any depth, in every JSON-bodied format. It is in the
normative [canonicalisation spec](../protocol/canonicalisation.md) (§3.1, §4.2) and pinned by five
conformance fixtures, including the mirror case that a hand-written scanner gets wrong: an escaped
backslash followed by the letters `ud800` is ordinary text and is accepted.

**The check reads the escapes as written and never decodes the string** (`UnpairedSurrogates`, in
`Formats.Abstractions`, used by both canonicalisers). Decoding is the thing that throws. It runs
right after the document parses and **before** the duplicate-key check, and its message names the
JSON Pointer and the escape as written — never the string, because a Problem Details body carrying an
unpaired surrogate cannot itself be serialised, which would turn the refusal back into the 500.

**No new code.** `schema_malformed` already means "not well-formed in its declared format", the
duplicate-key refusal is its precedent, and every client already maps it; a new code would give a
caller nothing it could do differently. So `ConcordatCodes.cs` and the generated TypeScript catalogue
are unchanged, and the editor reports the same code with the same grammar (`json-document.ts`), held
against the registry by the parity spec in both directions.

## Alternatives considered

- **Canonicalise it faithfully.** There is no faithful spelling: the canonical form is UTF-8 text that
  is hashed, and a lone surrogate is not UTF-8. Emitting the escape would give the document an id and
  hand every consumer a string their parser reads three ways — the argument that refuses a duplicate
  key, one layer down.
- **Replace it with U+FFFD, as Go does.** Rejected because it is lossy in the worst place: two
  different schemas (`"\ud800"` and `"\udc00"`) would canonicalise identically and so share one id.
- **A new code, such as `schema_string_invalid`.** Rejected above; it can be added later without
  breaking anything, which is not true of the reverse.
- **Catch the exception where it is thrown.** It would turn the 500 into a 400 and leave the second
  fault (the refusal quoting its key) and the Avro and key cases to be found one at a time.

## Consequences

- **Positive:** a document with an unpaired surrogate is refused with a code and a location, from
  registration, the dry run and the lookup alike (all three share one choke point, and a test per
  route notices the day one stops).
- **Positive:** no stored data is affected. These documents never got past canonicalisation, so none
  was ever registered; this changes what a user *can* register only in the sense that it used to crash.
- **Positive:** the parity test could not see this before. `dryRun` reported a crash as `code: null`,
  the same as an acceptance, so a registry that crashed on a document the editor was quiet about passed.
  It now fails on any 5xx.
- **Negative:** every SDK that canonicalises must implement the rule and, if it scans escapes by hand,
  get the escaped backslash right. That is why it is five fixtures rather than one.
- **Neutral, and not changed:** a *raw* lone surrogate character (as opposed to an escape) cannot reach
  the schema at all — `JSON.stringify` writes it as an escape in the request envelope, which the registry's
  request reader refuses with a bare 400 before any rule runs. That is a refusal and not a crash, and it
  carries no `concordatCode`, so the editor does not claim to know it. Protobuf is not JSON and is
  unaffected.

## References

- [ADR-015](015-content-addressed-ids.md), [ADR-019](019-language-neutral-protocol.md),
  [ADR-028](028-schema-editor.md)
- [canonicalisation.md §3.1](../protocol/canonicalisation.md), `tests/Concordat.Conformance/corpus/canonicalisation/unpaired-surrogate-*.json`
- `src/formats/Concordat.Formats.Abstractions/UnpairedSurrogates.cs`, `web/src/app/domain/registry/json-document.ts`
