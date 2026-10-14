# ADR-039: A document that is not a valid schema is refused, unless the registry already holds it

- **Status:** Accepted
- **Date:** 2026-10-14
- **Deciders:** Rafael Camara (the brief called it a product decision and asked for it to be probed and recorded as
  reversible). The choices under "Decision" that the brief left open were made in implementation;
  [DECISIONS-PENDING.md](../DECISIONS-PENDING.md) lists the ones to confirm and [STATUS.md](../STATUS.md) says how to undo each.

## Context

[STATUS.md](../STATUS.md) recorded one hole since the schema editor shipped: **`{"type":"bogus"}` registers.** The registry's
JSON Schema canonicaliser checks that the text is JSON with one meaning (well-formed, no duplicate key, no lone surrogate,
under 512 KiB, under 64 levels) and the portability checker refuses a dialect it does not implement. Nothing reads the
document *as a schema*, so it is stored, given an id, offered to consumers, and the row said it then "fails every message".
The row also said that refusing it "changes what existing users can register", which is why it was never fixed in passing.

Three things were measured before deciding, because the row's prose had never been checked.

**1. The other two formats already refuse a document that is not one of theirs.** Registered through the real API on
2026-10-14: Avro `{"type":"bogus"}` is `400 schema_malformed` ("Unknown Avro type 'bogus'"), Avro `[1,2]` is
`400 schema_malformed` ("A schema must be a string, an array or an object, not Number"), and a `.proto` that is not Protobuf
is `400 schema_malformed` ("Unexpected 'this' at the top level of the file"). So the registry already takes the position that a document which is not a schema of its declared
format is not registered, for two of the three formats; JSON Schema is the exception only because it has no parser to fail
and nobody wrote the step. That inconsistency is the argument, and it is stated here as measured rather than assumed.

**2. What an invalid schema does downstream is not "fails every message".** Run through `NJsonSchemaPayloadValidator`,
the registry's own .NET validator, and compared with `ajv` 8:

| Document | NJsonSchema | `ajv` 8 (2020-12) |
|---|---|---|
| `{"type":"bogus"}` | compiles, **0 errors: accepts every message** | `validateSchema` fails |
| `{"minimum":"5"}` | compiles, **reads `"5"` as 5** (payload `3` is `NumberTooSmall`) | fails |
| `{"type":"string","pattern":"("}` | compiles; a message then fails as `malformed_json` ("Invalid pattern") | a format, not asserted; see below |
| `{"items":[{"type":"string"}]}` | compiles, reads it as a draft-07 tuple | fails |
| `{"properties":{"a":{"required":true}}}` | **throws** while compiling | fails |
| `{"$ref":"#/$defs/nope"}` | **throws** (cannot resolve) | compile throws `MissingRefError` |

So the same stored schema quarantines every message on one SDK (it cannot be compiled: `schema_uncompilable`, the worst
failure shape available, per `Draft202012Corrections`) and **silently validates nothing on the .NET one**, which is worse
than failing. Both are the cross-implementation divergence this registry exists to warn about, and neither is something an
author meant.

**3. The package question has a real answer.** NJsonSchema, the only validator already a dependency, is draft-04-shaped:
it does not validate a document against the 2020-12 metaschema, and (above) it will not even notice `type: "bogus"`. The one
.NET library that does (`LateApexEarlySpeed.Json.Schema` 4.2.1, BSD-3-Clause) was probed with the eight metaschema files
loaded from disk: it works, and reports `/type` and `/properties/a/required` correctly. It is not used, for the reasons under
"Alternatives considered"; the deciding ones are what it drags in and what it can do.

## Decision

**The registry refuses a document that is not a valid draft 2020-12 schema, with `400 schema_invalid`, at registration and in
the dry-run check, unless it already holds that exact document.** Four parts.

**1. What "invalid" means, exactly.** Two kinds of defect, and nothing else.

- *`metaschema_violation`*: the document does not satisfy the **assertions** of the 2020-12 meta-schema and its seven
  vocabularies. That is the specification's own definition: a `type` that names no type, a `minimum` that is a string, a
  `required` that is `true` or has a repeat, a `properties` member that is not a schema, `items` as an array, `$anchor`
  that is not a name, `$id` with a non-empty fragment, a root that is not an object or a boolean. A boolean is a schema,
  at the root and everywhere a schema goes.
- *`reference_unresolved`*: a `$ref` whose value is a JSON Pointer into **this document** (`#`, `#/a/b`) and points at nothing.
  The meta-schema checks the *shape* of a reference, not whether it leads anywhere, and it is the commonest way a schema that
  passes the first check still cannot be compiled.

**2. What it deliberately does not mean.** Each of these would refuse a schema somebody was entitled to register.

- **`format` is an annotation.** The published 2020-12 meta-schema itself includes the format *annotation* vocabulary, not the
  assertion one, so `format: regex` on `pattern` and `format: uri-reference` on `$ref` are not enforced. **A `pattern` that is
  not a regular expression is therefore not refused**; ECMA-262, RE2 and .NET disagree about what one is, and picking one
  would make the registry's answer depend on whose syntax it applied. This is the largest thing still open, and it is
  recorded as such in STATUS.
- **An unknown keyword or vocabulary is accepted**, as the meta-schema allows: `x-owner`, a keyword a later draft adds.
- **Another `$schema` is not this check's business.** A draft-07 declaration is `schema_dialect_unsupported`, decided first,
  because "this is draft-07" is the answer that tells its author what to do and "your `items` is an array" is its symptom.
  With no `$schema`, the document is 2020-12 by assumption (as it already was), which is the one behaviour change that
  will surprise somebody: **a draft-07 tuple-form `items` with no `$schema` is now refused**, where it used to register and
  quietly validate as a tuple on .NET and fail on every other SDK.
- **A reference to another document, a `concordat://` address and a plain-name anchor (`#node`) are never judged and never
  fetched.** The document under test is *data* to the checker, not a schema it compiles, so nothing in it, a `$ref` or an
  `$id`, is an address to visit. Once any resource inside the document declares its own `$id`, `#/…` is relative to that
  resource and the check stops looking at references at all rather than guess.
- **Data is not schema.** A `$ref` or a `type: "bogus"` inside `default`, `const`, `enum` or `examples` is a value, and a
  property named `type` or `$ref` is a property; neither is read as a keyword.
- **A pointer with an escape RFC 6901 does not define (`~2`) is not judged**, because it cannot be told from a pointer to a
  member that has one.

**3. A document the registry already holds keeps registering.** The registry accepted every such document until today, so
refusing one that is stored would turn an idempotent retry, a second subject taking the same content, or a promotion into
a failure nothing the caller did could have caused. So the refusal is for a document the **caller's tenant cannot already
reach** (`ISchemaRepository.IsReachableByCurrentTenantAsync`, the predicate `GET /v1/schemas/{id}` and `lookup` already
use), and for no other reason:

- *Stored documents are not rewritten, hidden or marked.* Reading one (`GET /v1/schemas/{id}`, its subject's versions, its
  bundle) answers exactly as before; no read runs the check.
- *Re-registering identical content* is the same idempotent `200` no-op; registering it into **another subject of the same
  tenant** works; **promotion** copies a version the registry already holds and is excused the same way.
- *Reachable, not merely stored.* Schemas are stored once across tenants (ADR-015). Excusing a document because *anyone* has
  it would make "may I register this?" depend on what other tenants registered, which is the oracle `lookup` was built not
  to be. A document only another tenant holds is refused as if nobody did, and a test pins it.
- *One more byte is a new document.* The exemption is the content address, nothing near it.

**4. The shape of the refusal.** `400`, `concordatCode: "schema_invalid"`, the standard Problem Details. The detail names
the **first** defect in path order, as a JSON Pointer with the rule it broke, and counts the rest:
`The document is not a valid schema. At #/type: matches none of the permitted forms: must be one of array, boolean, integer,
null, number, object, string, not "bogus"; or must be an array, not a string. (2 more.)` One precise location is what an
author acts on, and a response that listed every defect of a 500 KiB document would be that document back. There is no
structured `errors` member in v1; the pointer is in the text. **400, not 422**, matching `schema_malformed` and
`schema_dialect_unsupported`, so a client that branches on status sees one family.

### How it is built

- **No new dependency.** `MetaschemaValidator` interprets the specification's **own eight files** (`schema`, `core`,
  `applicator`, `unevaluated`, `validation`, `meta-data`, `format-annotation`, `content`), embedded verbatim and pinned by a
  SHA-256 per file. It implements the sixteen keywords those files use to decide validity (and nine that only annotate) and **throws when built** on any other, in any schema
  position, including inside `$defs`, so a meta-schema that used a keyword it skipped cannot silently pass a rule that was
  never run. Two simplifications are exact for these files and checked when they load: `$dynamicRef: "#meta"` is the root,
  and `uniqueItems` compares strings only (every array it governs also requires strings), which keeps it linear.
- **Where it runs.** `ISchemaValidityChecker` (JSON only, `null` for the formats that parse their input) is asked by the
  evaluator, which **reports** defects in `EvaluatedProposal.Defects` and does not refuse; `SchemaValidityGate` is the one
  place that decides, called by `RegisterVersionHandler` and `CheckCompatibilityHandler`, so **the dry run answers exactly
  what registration answers** (same status, code and words; a test compares them). Promotion and impact analysis do not call
  it: a promotion carries a version the registry already holds, and an impact analysis writes nothing.
- **The extra read is paid only by a document with a defect.** A valid schema never asks whether the registry holds it.
- **No migration.** Nothing about what is stored changes.

## Alternatives considered

- **Accept and warn** (a `portability`-style finding on the response). Rejected for the `{"type":"bogus"}` case in particular:
  the measured downstream behaviour is "validates nothing" on one SDK and "will not compile" on the others, and a warning
  the pipeline can ignore leaves exactly that schema registered and offered. It is what the registry does for *legal* schemas
  that diverge between SDKs (a lookaround `pattern`), which is the right treatment there and the wrong one for a document that
  is not a schema. A reader who disagrees can have it in one line: have the evaluator turn each defect into a
  `PortabilityFinding` of `Warning` severity and drop the two gate calls.
- **Refuse everything `ajv` strict mode would.** Rejected: strict mode refuses unknown keywords and unknown formats, which the
  meta-schema allows and every vendor extension uses. The line is the specification's assertions, no stricter.
- **Assert `format`** (so a bad `pattern` is refused). Rejected for the reason under "What it deliberately does not mean". The
  cost is real, a schema with `"pattern":"("` still registers and then throws when a message is validated, and it is listed as
  open rather than described as handled.
- **A third-party validator, `LateApexEarlySpeed.Json.Schema`.** Rejected, narrowly. It works, and a general validator is
  the textbook answer. But it is an additional **single-maintainer** package bringing eleven more (`Nullability.Generic` and a
  `Microsoft.Extensions.*` 3.1.0 tree of ten, including `Http`, which central pinning would have to raise), into a project the CLI and the quickstart sample also reference; it ships
  an HTTP document loader (`AddHttpDocumentAsync`) on a code path whose whole safety argument is "nothing here is ever
  fetched"; it asserts `format` by default (it refused `{"pattern":"("}`); and the job is reading eight fixed files. NuGet
  audit is live in this repository and an advisory in an EOL-era transitive tree is an error. `JsonSchema.Net` is excluded for
  the licence reason already recorded on `NJsonSchemaPayloadValidator` (ADR-009). NJsonSchema cannot do it (above).
- **A hand-written list of rules** (`required` is an array of unique strings, …). Rejected: it is a transcription of the
  meta-schema, and a transcription is where a refusal gets one rule slightly wrong and turns away a schema that was entitled
  to register. Interpreting the files cannot drift from them, and an `ajv` cross-check (below) found no disagreement.
- **Refuse in the canonicaliser.** Rejected: canonicalisation is normative across the SDKs and feeds the content-addressed id
  (ADR-015, ADR-019); making it fail for validity would change what an offline SDK computes, and could not tell a held
  document from a new one, so it would break the idempotent retry this decision exists to keep.
- **Excuse a document because it is stored, anywhere.** Rejected: the cross-tenant oracle above.
- **A setting (`Concordat:Registry:RefuseInvalidSchemas`).** Rejected for now. The decision is reversible without one (below)
  and a flag that is on by default and off in the field is two products to test and document. If a deployment needs the old
  behaviour, that is the day to add it.
- **Make the editor block on it.** Rejected, ADR-028's rule stands: a client finding is a refusal only when it is a port of
  what the registry certainly refuses, and `ajv` is a different engine. The editor now says the same thing as advice for the
  same two kinds of defect, and a browser test holds the two to agreeing on the ordinary cases.

## Consequences

- **Positive:** `{"type":"bogus"}`, `{"required":true}`, `{"minimum":"5"}`, a tuple `items` and a `$ref` typo no longer get an id,
  are no longer offered to consumers, and are refused with the place and the rule instead of a quarantine at 3 a.m. The three
  formats now treat "not a schema of this format" the same way.
- **Positive, measured:** the check costs little. In a Release build, warm, the checker alone takes a median of **7.6 ms for a
  6 KiB schema, 46 ms for 172 KiB, 65 ms for 344 KiB** (76 ms with a defect to report) and **17 ms for a 458 KiB document whose
  `required` lists 60,000 names**; through the whole dry-run request on the private stack the same 344 KiB schema answered in
  370 ms, valid, and 328 ms refused. Before the uniqueness check was hashed, a pairwise scan of that list took 1.9 to 5.6 s.
  It runs once per registration of a document the registry has not seen; reads never run it.
- **Positive:** the answer is checked three ways that agree. The corpus of 104 documents behind the unit tests was run through
  `ajv`'s 2020-12 meta-schema, an engine that shares no code with this one: **0 verdict disagreements and no path
  disagreement**. The dry run and registration are compared in a test. And the interpreter checks the published meta-schema
  files themselves as schemas (they are valid), which is the broadest valid document there is.
- **Negative, and the one to read first:** it changes what a **new** registration can be. A draft-07 or draft-04 document with no
  `$schema` that uses `items: [...]`, boolean `exclusiveMinimum`, `required: true`, or a draft-07 `$id` with a `#fragment` as
  an anchor was accepted and is refused now. Every such document is one that fails to compile in `ajv`, the Python
  `jsonschema` library, and Go, and (for the first two) validates something else on .NET, so the refusal is an existing
  failure surfacing earlier; but it is the behaviour change, and the message says which keyword. Stored documents are untouched.
- **Negative:** `pattern` syntax is not checked (see above), and a `$ref` to an anchor, another document or a `concordat://`
  address is not judged here. `reference_unresolved` is silent once any resource inside the document declares its own `$id`.
- **Negative:** one more thing to maintain. The interpreter is about 420 lines of code (630 with its comments) and a second reading of JSON Schema in the
  codebase. The pins and the throw-on-unknown-keyword rule are what keep it honest; neither catches a meta-schema whose
  *semantics* we misread, which only a cross-check does.
- **Neutral, and the way back:** reversible without a migration. Delete the two `SchemaValidityGate.RefuseAsync` calls
  (`RegisterVersion.cs`, `SubjectQueries.cs`) and the registry accepts everything it accepted before; nothing persisted
  depends on the rule, and everything registered under it is valid and stays valid. To demote it to a warning, see the first
  alternative. The code `schema_invalid` is catalogued and would stay.
- **Neutral:** `concordat lint` and `concordat check` offline do not report it; they ask the registry, whose dry run does.

## References

- [ADR-015](015-content-addressed-ids.md), [ADR-019](019-language-neutral-protocol.md), [ADR-021](021-tier-2-sdk-set.md),
  [ADR-028](028-schema-editor.md), [ADR-031](031-unpaired-surrogates-are-refused.md)
- [STATUS.md](../STATUS.md) (the holes row, closed), [DECISIONS-PENDING.md](../DECISIONS-PENDING.md) #40
- [Protocol: canonicalisation §3.7 and the code catalogue](../protocol/concordat-codes.md)
- `src/formats/Concordat.Formats.Json/` (`MetaschemaValidator.cs`, `JsonSchemaValidityChecker.cs`, `Metaschema/`),
  `src/core/Concordat.Application/Registry/SchemaValidityGate.cs`
- JSON Schema draft 2020-12, [Core §8.1](https://json-schema.org/draft/2020-12/json-schema-core) and
  [Validation](https://json-schema.org/draft/2020-12/json-schema-validation)
