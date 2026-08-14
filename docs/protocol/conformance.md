# The conformance corpus

**Normative, and the ultimate arbiter.** Where this corpus and any prose disagree — including
every other document in this directory — the corpus is right. It is executable; prose is not.

The corpus is 89 JSON fixtures in
[`tests/Concordat.Conformance/corpus/`](../../tests/Concordat.Conformance/corpus). They are
plain files on disk rather than embedded resources or test-framework fixtures, for exactly one
reason: **another language's test runner has to be able to read the same bytes.** A corpus that
only .NET can load is a .NET test suite wearing a specification's clothes.

## Why it exists

[ADR-021](../adr/021-tier-2-sdk-set.md) commits to five independent implementations —
`ajv`, `jsonschema`, `santhosh-tekuri`, `networknt` and .NET's — of specifications with
genuine edge-case disagreement. Two SDKs can each be correct by their own reading and still
quarantine different messages.

That failure is invisible from inside any one implementation. It shows up as a support ticket
from someone whose Node producer and Go consumer disagree, weeks later, with no bug on either
side. The corpus turns that into a CI failure on the day the divergence is introduced.

It works. Every category below has caught something real, and most of it in the *first*
implementation:

| Category | What it caught |
| --- | --- |
| `payload-validation` | Four places the .NET validator disagreed with draft 2020-12 — boolean subschemas failing to compile, string length counted in UTF-16 units, `enum` coercing `"1"` to `1`, `uniqueItems` comparing text |
| `canonicalisation` | That Avro's Parsing Canonical Form discards `default` and `aliases`, which the compatibility engine needs |
| `compatibility` | That `integer` → `number` must pass the default policy and fail a `SOURCE` one |
| `schema-id` | Nothing yet — every fixture matched hand-written expectations first run, which is the point of writing them by hand |

## Running it in another language

You need a JSON parser and a test runner. Nothing else.

1. **Enumerate** `corpus/<category>/*.json`, sorted by filename, so failures are reported in a
   stable order.
2. **Deserialise** each file. Every fixture carries `name` and `why`; the rest is
   category-specific and described below.
3. **Assert** the expectation. On failure, print the fixture's `why` — it explains what the
   case is defending against, which is usually more useful than the diff.

The .NET runner is [`CorpusTests.cs`](../../tests/Concordat.Conformance/CorpusTests.cs) and is
worth reading once as a reference implementation, but you are not required to mirror its
structure.

> **Every fixture must have a non-empty `why`**, and a test enforces it. A fixture whose purpose
> nobody recorded is one nobody dares change when it fails, so it gets suppressed or deleted —
> both worse than the failure.

## The categories

Fields common to every fixture: `name` (matches the filename) and `why` (the rationale).

### `canonicalisation` — 11 fixtures

| Field | Meaning |
| --- | --- |
| `format` | `json`, `avro` or `protobuf` — **read it**; the categories are mixed |
| `input` | The document as authored |
| `canonical` | The exact expected canonical text, when valid |
| `error` | The expected `concordatCode`, when the input must be refused |

Assert the canonical text **byte-for-byte**, then assert **idempotence** — canonicalising the
output again must be a no-op, or ids are not stable under re-registration.

### `schema-id` — 4 fixtures

| Field | Meaning |
| --- | --- |
| `format`, `canonicalBody`, `references` | The inputs |
| `preimage` | The exact bytes hashed, with `\n` separators |
| `schemaId` | The resulting 32-character lowercase hex id |

**Assert the preimage, not only the id.** An implementation that produces the right hash from
the wrong framing agrees on every fixture here and diverges the moment a reference set changes.

### `compatibility` — 21 fixtures

| Field | Meaning |
| --- | --- |
| `format`, `contentModel` | Inputs |
| `policy` | `{ mode, surface }` — the two axes of ADR-016 |
| `previous` | Prior versions as `{ ordinal, schema }` |
| `proposed` | The proposal |
| `expected` | `compatible`, `suggestedBump`, `breakingChanges[]`, optional `allDivergences[]` |

Findings are compared as an **unordered set** of `(path, kind, direction, surface)`. Messages
are deliberately excluded — they must stay free to improve.

Note the paired fixtures: the same two schemas under two different surfaces with opposite
verdicts. If a pair ever agrees, the second axis has stopped working.

### `payload-validation` — 12 fixtures

| Field | Meaning |
| --- | --- |
| `schema` | The schema |
| `mustAccept` | Documents that must validate |
| `mustReject` | Documents that must not |

**The category where SDKs diverge without anyone writing a bug.** Use your language's mature
validator, then correct it where it disagrees with these fixtures — do not relax a fixture to
match your library. That is what the .NET implementation does, in
`Draft202012Corrections.cs`, and the header comment there explains each correction.

### `envelope-encode` — 4 fixtures · `envelope-decode` — 18 fixtures

Encode: given identity, produce exactly this header set — **set equality**, since an extra
header is as much a divergence as a missing one.

Decode: given headers plus `properties.type` and `content-type`, produce this outcome. The
load-bearing distinction is between failures that **reject** a message and ones that only
**warn**: quarantining a structurally valid payload because a human mistyped a semver label
would be a self-inflicted outage.

### `subject-resolution` — 14 fixtures

`messageType` (and optionally `exchange`, `routingKey`) → one of three outcomes:

- `resolved` with a `subject`,
- `absent` — no subject and **no error**,
- `unusable` with an `error` code.

**`absent` and `unusable` are asserted separately on purpose.** An SDK that reports absent as an
error passes a looser check and then fails every brownfield estate, which is full of message
types nobody has registered yet.

### `references` — 5 fixtures

`canonicalBody` → either `references[]` (the edges derived from the document) or an `error`.
Pins [ADR-023](../adr/023-no-cross-subject-references-avro-protobuf.md): Avro and Protobuf
cross-subject references are refused in v1, while `google/protobuf/*` imports are allowed
because the runtime resolves them rather than a registry.

## Adding a fixture

Add one when you find a divergence, and **write the expected value by hand before running it**.
A fixture generated from the implementation's own output cannot catch that implementation being
wrong — it only pins today's behaviour, which is a regression test, not a specification.

Every fixture in `schema-id` was written that way and all four matched on the first run. Every
new fixture in `payload-validation` was written that way and four of eight failed — which is
the whole argument for the practice.
