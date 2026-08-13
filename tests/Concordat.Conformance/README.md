# Conformance corpus

**This corpus is normative.** Where it and an implementation disagree, the corpus is right and
the implementation is a bug — including when the implementation is .NET (ADR-019).

It exists from M1, long before a second SDK consumes it, precisely because a corpus written
when the second SDK arrives can only ratify whatever .NET already did.

## What is in here

| Directory | Answers |
|---|---|
| `corpus/canonicalisation/` | What is the one canonical text for this document? |
| `corpus/schema-id/` | What exact bytes get hashed, and what id results? |
| `corpus/compatibility/` | Is this change allowed, and what precisely is wrong with it? |
| `corpus/payload-validation/` | Which documents does this schema accept and reject? |
| `corpus/envelope-encode/` | What headers does a producer write? |
| `corpus/envelope-decode/` | What does a consumer read, and when does it refuse? |

Every fixture is a standalone JSON file. Nothing here is C#, and nothing may become C#: the
acceptance test for ADR-019 is that a Go team implements a client from these files plus the
prose specs, without reading a line of the reference implementation.

## Fixture formats

Every fixture carries `name` and `why`. `why` is not decoration — a fixture whose purpose
nobody recorded is a fixture nobody dares change when it fails.

### `canonicalisation/`

```json
{
  "name": "key-order-is-normalised",
  "why": "Two spellings of one schema must not become two schemas.",
  "format": "json",
  "input": "{\"z\":1,\"a\":2}",
  "canonical": "{\"a\":2,\"z\":1}"
}
```

For inputs that must be refused, replace `canonical` with `error`, naming the expected
`concordatCode`:

```json
{ "name": "duplicate-keys", "why": "...", "format": "json",
  "input": "{\"a\":1,\"a\":2}", "error": "schema_malformed" }
```

### `schema-id/`

Pins the **preimage bytes**, not only the resulting id. An implementation that produces the
right id from the wrong framing will diverge the first time a reference set changes, so the
intermediate is checked directly.

```json
{
  "name": "references-are-covered",
  "why": "Two schemas with identical bodies but different references are different schemas.",
  "format": "json",
  "canonicalBody": "{\"type\":\"object\"}",
  "references": [{ "name": "concordat://prod/acme.A/1", "subject": "acme.A", "version": 1 }],
  "preimage": "concordat-schema-id/v1\nformat:json\nbody:19:...\n",
  "schemaId": "0123456789abcdef0123456789abcdef"
}
```

`preimage` uses `\n` for line separators. It is UTF-8 when hashed; the byte lengths inside it
are **UTF-8 byte counts**, not character counts.

### `compatibility/`

```json
{
  "name": "adding-an-optional-property-is-compatible",
  "why": "The most common schema change. If it is blocked, the product is unusable.",
  "format": "json",
  "contentModel": "open",
  "policy": { "mode": "BACKWARD", "surface": "WIRE_JSON" },
  "previous": [{ "ordinal": 1, "schema": "{...}" }],
  "proposed": "{...}",
  "expected": {
    "compatible": true,
    "suggestedBump": "PATCH",
    "breakingChanges": [],
    "allDivergences": []
  }
}
```

`breakingChanges` and `allDivergences` are matched on `path`, `kind`, `direction` and
`surface`, ignoring `message` — messages are for humans and must be free to improve.

### `envelope-encode/`

Identity in, headers out. The expected header set is matched **exactly** — an extra header is
as much a divergence as a missing one, and an absent optional written as an empty string would
make a producer quarantine its own valid messages.

```json
{
  "name": "minimal",
  "why": "...",
  "schemaId": "7f3a…",
  "headers": { "concordat-v": "1", "concordat-schema-id": "7f3a…" }
}
```

### `envelope-decode/`

A message in, identity or a refusal out. Header values are **tagged**, because a plain string
map cannot express the wrong-type and invalid-UTF-8 cases — two of the behaviours most likely
to differ between SDKs. Exactly one key is set per value:

```json
"headers": {
  "concordat-v":         { "string": "1" },
  "concordat-schema-id": { "bytesBase64": "N2YzYTlj…" },
  "some-other":          { "integer": 42 }
}
```

`expected.kind` is `NONE`, `HEADERS` or `CONTENT_TYPE`; `expected.warnings` lists advisory
codes in any order. A fixture that must be refused carries `error` instead, naming the
`concordatCode`.

The distinction these fixtures exist to pin: **rejecting and warning are different**. A bad
schema id refuses the message; a mistyped semver label does not, because the schema id already
identifies the schema and quarantining a structurally valid payload over a human label would be
a self-inflicted outage.

### `payload-validation/`

```json
{
  "name": "optional-property-absent",
  "why": "...",
  "schema": "{...}",
  "mustAccept": ["{...}"],
  "mustReject": ["{...}"]
}
```

> **Not yet executed.** Concordat has no payload validator of its own — validation is
> client-side and uses a different third-party library in every language (`ajv`,
> `jsonschema`, `santhosh-tekuri`, `networknt`, and .NET's own). That is exactly why these
> fixtures exist: four independent implementations of a spec with real edge-case
> disagreement, and the same message can otherwise pass in one language and fail in another
> with no bug on Concordat's part. The .NET runner currently checks only that the fixtures
> load and are well-formed; **M2 wires the first real validator, and M6.1 makes every SDK run
> them.**

## Adding a fixture

Add the file, run the tests, and write `why` as though explaining to whoever has to decide,
two years from now, whether a failure means the code broke or the corpus was wrong.
