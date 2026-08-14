# The Concordat envelope

**Normative.** One of the five artifacts [ADR-019](../adr/019-language-neutral-protocol.md) names
as the protocol. It specifies what a publisher stamps on a message and what a consumer must do
with it, in no particular language.

**When this document and the conformance corpus disagree, the corpus wins, because the corpus is
executable.** `tests/Concordat.Conformance/corpus/envelope-encode/`, `envelope-decode/` and
`subject-resolution/` are the arbiter; every non-obvious rule below cites the fixture that fails
if you get it wrong. Prose is written by hand and can be wrong. A rule with no fixture behind it
is flagged as such in [Where this document is not yet pinned](#where-this-document-is-not-yet-pinned)
— treat those as the registry's behaviour rather than as settled protocol, and say so if you
depend on one.

---

## 1. What the envelope is

The envelope is schema identity travelling **beside** the payload, never inside it. Kafka had no
message headers until 0.11, which is *why* Confluent invented magic-byte payload framing;
AMQP 0-9-1 has carried `type`, `content-type` and an arbitrary `headers` field table since 2008
([ADR-010](../adr/010-header-envelope.md)). Concordat does not have to mutate payloads, so it does
not.

Three ways a message can arrive, and a reader must distinguish all three:

| Outcome | Corpus token | Meaning |
| --- | --- | --- |
| **Mode A** | `HEADERS` | Identity in AMQP headers. The default. |
| **Mode B** | `CONTENT_TYPE` | Identity in the `content-type` token, for paths where headers do not survive. |
| **None** | `NONE` | No Concordat identity. **Not an error.** |

`NONE` being a first-class outcome rather than a failure is the whole adoption argument: a
consumer with no Concordat client still reads plain JSON, and an SDK that reports an un-enveloped
message as an error breaks every brownfield estate on day one
(`envelope-decode/no-headers-is-not-an-error.json`).

There is a fourth, distinct outcome — **malformed** — where identity was clearly present and
unusable. §6 defines exactly which conditions reach it.

---

## 2. The Mode A headers

Six header names, and the set is closed for envelope version `1`.

| Header | Required | Value |
| --- | --- | --- |
| `concordat-v` | Yes | The envelope version. Currently the literal `1` — not `v1`, not `1.0`. |
| `concordat-schema-id` | Yes, whenever `concordat-v` is present | 32 lowercase hexadecimal characters, `^[0-9a-f]{32}$`. |
| `concordat-subject` | No | A subject name in the canonical grammar (§7). At most 512 characters. |
| `concordat-version` | No | The version ordinal, invariant base-10, at least 1. Advisory. |
| `concordat-semver` | No | `MAJOR.MINOR.PATCH`. Advisory. |
| `concordat-format` | No | One of `json`, `avro`, `protobuf`. |

`envelope-encode/full.json` pins every name and every value spelling at once. If your writer
produces `v1`, or a semver with a `v` prefix, or `JSON` in upper case, that fixture fails.

The names are lowercase ASCII and are matched **byte for byte** (§5). Header names other than
these six are not part of the envelope; a reader ignores them. A writer must not invent
`concordat-*` names — the prefix is reserved (§9).

### Why there is no `x-` prefix

RabbitMQ converts AMQP 0-9-1 headers beginning with `x-` into AMQP **1.0 message-annotations**,
while every other header becomes an **application-property** — which is where application metadata
belongs, and where CloudEvents puts it. `x-` is also reserved by RabbitMQ itself: `x-death`,
`x-delay`, `x-delivery-count`, `x-stream-filter-value`.

This is the single constraint that makes [ADR-013](../adr/013-amqp-091-only.md)'s "designed to
survive 1.0 conversion" claim mean anything, and it is now measured rather than asserted:
`tests/Concordat.HeaderSurvival` sends an `x-`-prefixed control header alongside the envelope on
the same message and confirms that the control header is demoted to a message-annotation while
`concordat-*` arrives as application-properties.

The prefix rule earns its keep at every hop, not only at 1.0 conversion. After dead-lettering the
header table holds both the broker's bookkeeping and ours, and the prefix alone tells them apart.

### Why every value is a UTF-8 string

Not a convenience. Three independent constraints force it:

1. **NServiceBus and Rebus expose headers as `Dictionary<string,string>`** and physically cannot
   carry an integer. Typed values would rule those adapters out without an envelope version bump.
2. **A string-only field table is the lowest common denominator** every AMQP client in every
   language can write. There is no int64 ambiguity and no numeric coercion to specify twice.
3. **RabbitMQ.Client writes a `string` with field-table tag `S` and reads it back as `byte[]`** —
   by design, permanently. A reader that accepts only strings reads nothing at all
   (`envelope-decode/byte-array-values-are-decoded.json`).

Three value shapes to avoid for related reasons, all documented in ADR-010: `ulong` (unsupported
by the field table), `bool false` (silently dropped by MassTransit's `SetHeaders`), and values
over 64 KiB (Rebus truncates).

---

## 3. Writing Mode A

Given a schema id, and optionally a subject, an ordinal, a semver label and a format, a writer
emits:

- `concordat-v` = `1`, always.
- `concordat-schema-id` = the id, always.
- each optional header **only when the caller supplied a value**.

**An absent optional is omitted entirely, never written as an empty string.** The reader treats a
present-but-empty header as malformed, so a writer that emits empty strings quarantines its own
valid messages (`envelope-encode/absent-optionals-are-omitted.json`,
`envelope-encode/minimal.json`).

**The ordinal is formatted with the invariant culture.** A host running under a locale with digit
grouping would otherwise emit `1,234,567` and no other SDK could read it
(`envelope-encode/large-ordinal-has-no-group-separators.json`). The same applies to any numeric
text this protocol carries.

**Values are written exactly as given, with no trimming, case folding or normalisation.** A writer
that emits a subject it has not already canonicalised is writing a value the reader will refuse;
canonicalisation happens during subject *resolution*, before the writer is called (§7).

The values are strings. A caller writing them into an AMQP 0-9-1 field table writes them as
strings — RabbitMQ.Client emits field-table tag `S` — and must expect to read `byte[]` back.

The writer does not touch the payload, `properties.type` or `properties.content-type`. Mode A
leaves the message otherwise exactly as the application built it, which is what makes adoption
incremental and reversible.

---

## 4. Mode B: the content-type token

For paths where headers may not survive. The payload is still untouched, so the body stays
readable by any tool — this is Azure's approach with its version defect fixed.

```
application/json+concordat.v1.7f3a9c2ea1b84d5c9e07f2b3c4d5e6b4
```

Grammar: `<media-type>` `+concordat.v1.` `<32 lowercase hex characters>`.

| Format | Media type |
| --- | --- |
| `json` | `application/json` |
| `avro` | `application/avro` |
| `protobuf` | `application/x-protobuf` |

AMQP 0-9-1's `content_type` is a `shortstr` capped at 255 bytes; a version token plus a
32-character hex id fits with room to spare.

The `v1` token is the point. Azure's unversioned `avro/binary+{id}` scheme cannot evolve, and is
the documented dead end both this token and `concordat-v` exist to avoid.

**Parsing.** A reader finds the literal `+concordat.v1.` in the content-type, takes everything
after it as the id, and validates it.

- Marker absent → not Mode B. A plain `application/json` is not an envelope
  (`envelope-decode/ordinary-content-type-is-not-mode-b.json`).
- Marker present, id invalid → **not Mode B**, not malformed. From here, an unparseable token is
  indistinguishable from an ordinary content-type that happens to contain the marker text.
- Marker present, id valid → Mode B. Everything before the marker is matched against the table
  above to recover the format; a media type not in the table yields a **valid envelope with no
  format**, not a rejection.

A Mode B envelope carries no ordinal, no semver, and never any warnings. Its subject, if any,
comes from `properties.type` (`envelope-decode/mode-b-content-type.json`).

**Payload framing is not implemented in v1.** ADR-010 and DESIGN §2 describe two additional Mode B
shapes — `0x01 | <16-byte id> | payload`, matching Confluent CP 8.1+, and read-only support for
the legacy `0x00 | <int32 BE>` layout from a Kafka bridge. Neither exists in the registry, the
client or the corpus. Do not implement them from the prose; they are unspecified until a fixture
says otherwise.

---

## 5. Reading: the rules that look fussy and are not

Four decisions govern every lookup below. Each of them exists because the obvious alternative
produces a silent cross-language split.

**Lookup is ordinal and case-sensitive.** An implementer reaching for HTTP-style header
canonicalisation would accept `Concordat-V` from one SDK and not another. `Concordat-V` is not
`concordat-v` and must not be found (`envelope-decode/case-sensitive-lookup.json`).

**Decoding is strict UTF-8.** The lenient default in both .NET and Go substitutes U+FFFD for
invalid bytes, which is precisely how a corrupted schema id becomes a valid-looking wrong one. An
ill-formed byte sequence in a required header is a rejection, never a substitution
(`envelope-decode/invalid-utf8-on-required-header-rejects.json`). A leading U+FEFF byte order mark
inside a header value counts as bad encoding and is **rejected rather than stripped**: a BOM there
means something upstream is encoding wrongly, and silently accepting it hides that.

**Values are not trimmed.** `" acme.A"` and `"acme.A"` must not become two spellings of one wire
value, and an SDK that trims would disagree with one that does not
(`envelope-decode/padded-subject-warns-and-is-ignored.json`).

**A key present with a null or empty value is malformed, not absent.** Pinning this is what stops
a writer emitting empty strings for absent optionals
(`envelope-decode/present-but-empty-is-malformed.json`).

A header value arrives in one of five states, and every rule in §6 is written against them:

| State | How it arises |
| --- | --- |
| **Absent** | Key missing, or present with a null value. |
| **Present** | A `string`, or a byte array that decodes as strict UTF-8 to a non-empty value with no leading BOM. |
| **WrongType** | Anything else — an integer, a timestamp, a boolean. Never call `ToString()` on it: an integer rendered as text looks plausible and is silently wrong (`envelope-decode/wrong-type-rejects.json`). |
| **BadEncoding** | A byte array that is not well-formed UTF-8, or a value starting with a BOM. |
| **Empty** | Decoded to the empty string. |

---

## 6. Reading: the algorithm

In order. Mode A is attempted first and **short-circuits**: if Mode A is present and malformed,
the reader returns that failure without looking at the content-type. Two SDKs that disagreed about
precedence would resolve different schemas for the same message
(`envelope-decode/mode-a-wins-over-mode-b.json`).

1. **No headers, or an empty header table** → `NONE`, then try Mode B.
2. **`concordat-v` absent** → `NONE`, then try Mode B. Another library's headers are not an
   envelope: an SDK that keys off "any header present" misclassifies every MassTransit or Rebus
   message (`envelope-decode/foreign-headers-alone.json`).
3. **`concordat-v` present but not `1`** → reject with `envelope_version_unsupported`, and **do
   not interpret any other `concordat-*` header.** A later envelope version may have redefined
   them, and guessing is worse than declining
   (`envelope-decode/unsupported-version-stops-interpretation.json`).
4. **`concordat-schema-id` absent** → reject with `envelope_schema_id_missing`. Declaring a
   version without an id is a producer bug (`envelope-decode/version-without-schema-id-rejects.json`).
5. **`concordat-schema-id` present but not 32 lowercase hexadecimal characters** → reject with
   `schema_id_malformed`.
6. **`concordat-format` present and unknown** → reject with `envelope_format_unknown`
   (`envelope-decode/unknown-format-rejects.json`).
7. Otherwise the envelope is read, with zero or more warnings. Only now are the advisory fields —
   subject, ordinal, semver — interpreted, and every problem with them is a warning.

If Mode A yielded `NONE`, parse the content-type per §4. If that also yields nothing, the message
carries no Concordat identity.

### Which failures reject and which only warn

This split is the load-bearing judgement in envelope reading, and the corpus pins both sides of
it. **The schema id already pins the exact schema.** A mistyped version ordinal or a malformed
semver label therefore tells a reader nothing it did not already know, and quarantining a
structurally valid payload over a human label is a self-inflicted outage. An unknown *format*, by
contrast, means the reader cannot know how to validate the payload at all, so proceeding would be
a guess.

| Header | State | Outcome | `concordatCode` |
| --- | --- | --- | --- |
| `concordat-v` | Absent | Not enveloped; try Mode B | — |
| `concordat-v` | WrongType | **Reject** | `envelope_header_type_invalid` |
| `concordat-v` | BadEncoding | **Reject** | `envelope_header_encoding_invalid` |
| `concordat-v` | Empty | **Reject** | `envelope_malformed` |
| `concordat-v` | Present, ≠ `1` | **Reject** | `envelope_version_unsupported` |
| `concordat-schema-id` | Absent | **Reject** | `envelope_schema_id_missing` |
| `concordat-schema-id` | WrongType | **Reject** | `envelope_header_type_invalid` |
| `concordat-schema-id` | BadEncoding | **Reject** | `envelope_header_encoding_invalid` |
| `concordat-schema-id` | Empty | **Reject** | `envelope_malformed` |
| `concordat-schema-id` | Not 32 lowercase hex | **Reject** | `schema_id_malformed` |
| `concordat-format` | Absent | Format is null | — |
| `concordat-format` | WrongType | **Reject** | `envelope_header_type_invalid` |
| `concordat-format` | BadEncoding | **Reject** | `envelope_header_encoding_invalid` |
| `concordat-format` | Empty | **Reject** | `envelope_malformed` |
| `concordat-format` | Unknown token | **Reject** | `envelope_format_unknown` |
| `concordat-subject` | Absent | Fall back to `properties.type` | — |
| `concordat-subject` | WrongType | Warn, fall back to `properties.type` | `envelope_header_type_invalid` |
| `concordat-subject` | BadEncoding | Warn, fall back to `properties.type` | `envelope_header_encoding_invalid` |
| `concordat-subject` | Empty | Warn, fall back to `properties.type` | `envelope_malformed` |
| `concordat-subject` | Padded with whitespace | Warn, subject is null | `subject_name_invalid` |
| `concordat-subject` | Fails the grammar | Warn, subject is null | `subject_name_invalid` |
| `concordat-subject` | Disagrees with `properties.type` | Warn, **the header wins** | `envelope_subject_type_mismatch` |
| `concordat-version` | Absent | Ordinal is null | — |
| `concordat-version` | Not a base-10 integer ≥ 1, or unreadable | Warn, ordinal is null | `envelope_ordinal_malformed` |
| `concordat-semver` | Absent | Semver is null | — |
| `concordat-semver` | Not `MAJOR.MINOR.PATCH` | Warn, semver is null | `semver_invalid` |
| `concordat-semver` | Carries a pre-release or build suffix | Warn, semver is null | `semver_prerelease_unsupported` |
| `concordat-semver` | WrongType, BadEncoding or Empty | Warn, semver is null | `envelope_malformed` |

Notes on four rows that are easy to get subtly wrong:

- **The ordinal is parsed strictly**: base-10 digits only, no sign, no leading or trailing
  whitespace, no group separators, and the result must be at least 1. Anything else warns
  (`envelope-decode/malformed-ordinal-warns.json`).
- **A pre-release semver still delivers the message.** v1 rejects pre-release labels *at
  registration*, but a message carrying one must not be quarantined for a human label
  (`envelope-decode/prerelease-semver-warns.json`).
- **An unreadable subject warns rather than passing silently**, or an unreadable subject looks
  identical to an absent one. It then falls back exactly as an absent one would.
- **The subject rules apply to the fallback too.** A `properties.type` used because the header was
  absent or unreadable is validated by the same rules: padded or ungrammatical, it warns and the
  envelope carries no subject.

Warnings must be reported, not swallowed. They are how an operator sees that two libraries are
both setting identity on the same message.

### Recovering the subject

The subject is optional in the envelope, and a reader recovers it in this order:

1. `concordat-subject`, validated exactly as it arrived.
2. `properties.type`, when the header is absent or unreadable
   (`envelope-decode/subject-falls-back-to-properties-type.json`).
3. Neither — the subject is null. This is not a failure; the subject is recoverable from the
   registry with `GET /v1/schemas/{id}/subjects`.

When both are present and disagree, **the header wins**, because it is the one Concordat wrote —
and the disagreement is reported, because it usually means two libraries are both stamping
identity (`envelope-decode/header-wins-over-properties-type.json`).

---

## 7. Subject resolution, on the publish side only

The resolver runs **only when publishing** ([ADR-011](../adr/011-subject-is-message-type.md)). The
envelope then carries the subject, so a consumer never re-derives anything. This is not an
implementation detail: in RabbitMQ a publisher knows `(exchange, routing key)` and a consumer
knows `(queue)`, and any scheme requiring both to compute the same answer from what each can see
cannot work.

The default strategy reads `properties.type`, applies two rewrites, and validates:

1. **Everything from the first comma is dropped.** A .NET publisher reaching for
   `typeof(T).AssemblyQualifiedName` supplies assembly, version, culture and public-key-token, all
   of which sit after that comma — so one rule covers the list and there is nothing for another SDK
   to get subtly wrong. Without the strip, a routine assembly version bump silently registers a new
   subject and orphans the old contract
   (`subject-resolution/assembly-qualification-is-stripped.json`,
   `subject-resolution/trailing-comma-leaves-a-usable-name.json`).
2. **`+` and `:` become `.`** — the nested-type separator in .NET and the scope separator several
   brokers and code generators use (`subject-resolution/nested-type-separator-becomes-a-dot.json`,
   `subject-resolution/colon-separator-becomes-a-dot.json`). The cost is real and accepted:
   `Acme.Orders+OrderCreated` and a top-level `Acme.Orders.OrderCreated` collapse to the **same**
   subject and become indistinguishable.

**The separator list is closed.** `/` does not normalise, however much it looks like a scope
separator; without that being pinned, one SDK adds it and the same publisher name yields two
subjects depending on which language sent the message
(`subject-resolution/slash-is-not-a-separator.json`).

**Surrounding whitespace is trimmed here** — deliberately the opposite of the envelope reader's
rule, and the two are not in conflict. Resolution *produces* the canonical value, so trimming here
means the wire only ever carries clean text; decoding must not trim, or a padded and an unpadded
value become two spellings different SDKs disagree about. Produce canonically, read literally
(`subject-resolution/surrounding-whitespace-is-trimmed.json`).

**Case is preserved, never folded.** `Acme.Orders` and `acme.orders` are two subjects. Folding
sounds helpful until it is a second lossy rewrite every SDK must reproduce identically, and until
it mangles names meant to be read (`subject-resolution/case-is-preserved-not-folded.json`). The
consequence is accepted: two teams spelling the same type differently get two subjects, which the
registry's subject list makes visible.

The result is validated against the canonical grammar:

```
^[A-Za-z0-9_]+(\.[A-Za-z0-9_]+)*$      at most 512 characters
```

Resolution has three outcomes, and collapsing any two of them is a real bug:

| Outcome | When | What an SDK does |
| --- | --- | --- |
| **Resolved** | The rewritten name matches the grammar. | Publish with the subject. |
| **Absent** | `properties.type` is null, empty or whitespace. | Publish unenforced. **Not an error** — an un-instrumented publisher is the ordinary brownfield state, and an SDK that nags on every message is an SDK whose enforcement gets switched off wholesale (`subject-resolution/no-type-is-absent-not-invalid.json`, `subject-resolution/blank-type-is-absent.json`). |
| **Unusable** | A type was set and cannot become a subject. | Report `subject_name_invalid`. |

Two refusals that implementers are tempted to soften:

- **A generic type name is refused, not mangled.** Any spelling invented for a CLR generic —
  ``List`1[[Acme.Order]]`` — would have to be reproduced character for character by every SDK, and
  a Go or Python SDK has no CLR generic syntax to reproduce it from. A name containing a backtick
  or a bracket is therefore reported as unusable, with a message saying to publish a named type
  (`subject-resolution/generic-type-is-refused-not-mangled.json`).
- **A hyphen is refused, not rewritten.** Everyone arrives from routing keys where `order-created`
  is idiomatic. Rewriting it to an underscore is another invention every SDK would have to share,
  and a subject silently differing from what the publisher wrote is worse than a clear refusal at
  publish time (`subject-resolution/hyphen-is-not-a-subject-character.json`). A leading dot — what
  a naive concatenation of an empty namespace with a type name produces — is refused for the same
  reason (`subject-resolution/leading-dot-is-refused.json`).

**Routing data is never a fallback.** With no type but a perfectly good exchange and routing key
available, the temptation is obvious and ADR-011 rejects it: routing keys are high-cardinality and
dynamic, alternate and dead-letter bindings rewrite them in flight, and the consumer cannot see
them. An SDK that quietly falls back produces subjects no other SDK produces
(`subject-resolution/routing-data-is-never-a-fallback.json`).

---

## 8. Which mode to use, measured

Every row below was measured against `rabbitmq:4.1-management` by `tests/Concordat.HeaderSurvival`,
which raises real brokers and asserts each finding. They are assertions rather than a written-up
report on purpose: a broker upgrade that changes any of this breaks the build instead of quietly
turning this section into fiction.

| Hop | Mode A survives | Notes |
| --- | --- | --- |
| Dead-letter — nack, TTL expiry, overflow | Yes, all three | broker adds `x-death`, `x-first-death-*` |
| Shovel, with and without `dest-add-forward-headers` | Yes, both | adds `x-shovelled*` when on |
| Federation, two brokers over a real link | Yes | adds `x-received-from` |
| STOMP subscriber | Yes | frame headers, unprefixed and unmangled |
| MQTT 5.0 subscriber | Yes | arrives as user properties |
| MQTT 3.1.1 subscriber | **No — impossible** | the protocol has no user properties at all |
| AMQP 1.0 client | Yes | **application-properties**, as ADR-013 requires |

**Mode A is the default and is safe across every hop RabbitMQ itself performs.** Reach for Mode B
in exactly two cases: MQTT 3.1.1 consumers, and any path that strips headers outright.

Two findings that change the guidance, and that a client author needs:

- **`properties.type` does not survive AMQP 1.0 conversion.** It does *not* become the AMQP 1.0
  `subject`, which is where anyone would look — it is demoted to the message-annotation
  `x-basic-type`. So for an estate with 1.0 consumers the header envelope is not an optimisation,
  it is the only thing that works. `content-type` does survive into the standard properties
  section, so Mode B's token is unaffected.
- **Every envelope header arrives from RabbitMQ.Client as `Byte[]`, never `string`.** The one
  exception is `properties.type`, because it is a frame field rather than a field-table entry.

---

## 9. The `concordat-` namespace

The `concordat-` prefix is reserved. Do not write names into it that this document does not
define, and expect future envelope versions to define more.

Concordat must never write into another library's namespace either. These prefixes are owned
elsewhere, and a collision would be silent — it would corrupt another library's routing rather
than merely failing validation:

`x-`, `MT-`, `NServiceBus.`, `rbs2-`, `rabbitmq-`, `ce-`, `cloudEvents`

Beyond the six envelope headers, v1's .NET consumer writes five more when it republishes a
message to the quarantine exchange (default `concordat.quarantine`). They are listed for
completeness and are **not pinned by the corpus**; an SDK that quarantines differently is not
non-conformant today.

| Header | Value |
| --- | --- |
| `concordat-quarantine-reason` | The `concordatCode`, defaulting to `payload_invalid`. |
| `concordat-quarantine-detail` | The explanation, truncated to 4096 characters plus `… (truncated)`. |
| `concordat-quarantine-exchange` | The exchange the message was originally published to. |
| `concordat-quarantine-routing-key` | The original routing key, also reused as the routing key on the quarantine publish so operators can bind selectively. |
| `concordat-quarantine-at` | UTC timestamp, ISO 8601 round-trip (`O`) format, invariant culture. |

**CloudEvents interop is read-only and not implemented in v1.** DESIGN §2 describes reading both
the official `cloudEvents_`/`cloudEvents:` convention (AMQP 1.0 only) and Knative's `ce-` working
draft (AMQP 0-9-1). Neither is in the registry, the client or the corpus. The prefixes appear
above only so that Concordat never writes into them.

---

## 10. Fixture map

Every fixture carries a `why` — a test asserts that none is missing, because a fixture whose
purpose nobody recorded is one nobody dares change when it fails.

| Fixture | Pins |
| --- | --- |
| `envelope-encode/minimal.json` | Only `concordat-v` and `concordat-schema-id` on a minimal envelope |
| `envelope-encode/full.json` | Every header name and value spelling |
| `envelope-encode/absent-optionals-are-omitted.json` | Absent optionals omitted, never empty |
| `envelope-encode/large-ordinal-has-no-group-separators.json` | Invariant number formatting |
| `envelope-decode/no-headers-is-not-an-error.json` | `NONE` is an outcome, not a failure |
| `envelope-decode/foreign-headers-alone.json` | Another library's headers are not an envelope |
| `envelope-decode/case-sensitive-lookup.json` | Ordinal, case-sensitive lookup |
| `envelope-decode/byte-array-values-are-decoded.json` | `byte[]` values must decode |
| `envelope-decode/invalid-utf8-on-required-header-rejects.json` | Strict UTF-8, no U+FFFD substitution |
| `envelope-decode/wrong-type-rejects.json` | No `ToString()` on a non-string value |
| `envelope-decode/present-but-empty-is-malformed.json` | Empty ≠ absent |
| `envelope-decode/version-without-schema-id-rejects.json` | An id is required once a version is declared |
| `envelope-decode/unsupported-version-stops-interpretation.json` | A future version stops all interpretation |
| `envelope-decode/unknown-format-rejects.json` | Unknown format rejects, unlike advisory fields |
| `envelope-decode/malformed-ordinal-warns.json` | Advisory fields warn |
| `envelope-decode/prerelease-semver-warns.json` | A human label never quarantines a payload |
| `envelope-decode/padded-subject-warns-and-is-ignored.json` | The reader does not trim |
| `envelope-decode/subject-falls-back-to-properties-type.json` | Fallback to `properties.type` |
| `envelope-decode/header-wins-over-properties-type.json` | Header precedence, with a warning |
| `envelope-decode/mode-b-content-type.json` | The Mode B token |
| `envelope-decode/ordinary-content-type-is-not-mode-b.json` | A plain content-type is not an envelope |
| `envelope-decode/mode-a-wins-over-mode-b.json` | Mode precedence |
| `subject-resolution/*.json` | Every rule in §7 |

---

## Where this document is not yet pinned

Stated explicitly rather than left for an implementer to discover. Each of these is behaviour of
the reference implementation that **no fixture asserts**, so a second implementation could diverge
without failing the corpus. Treat each as a gap worth closing rather than as settled protocol.

- **A `concordat-schema-id` that decodes cleanly but is not 32 lowercase hex characters.** The
  reference implementation rejects with `schema_id_malformed` — a non-`envelope_*` code, which is
  itself worth confirming. No fixture covers it.
- **A Mode B token whose id is malformed** (`application/json+concordat.v1.zzz`). It is treated as
  *no envelope*, not as malformed. No fixture covers it.
- **A Mode B token on an unrecognised media type** (`text/plain+concordat.v1.<id>`). The envelope
  is read and the format left null. No fixture covers it.
- **Padded `properties.type` under Mode B.** Mode A refuses a padded subject with a warning; the
  Mode B path validates `properties.type` through a routine that *trims*, so the same padded value
  is accepted there and refused under Mode A. This looks like an inconsistency in the
  implementation rather than an intended rule, and no fixture covers either half.
- **An invalid `properties.type` under Mode B** is dropped silently, with no warning, where Mode A
  would warn.
- **A padded `concordat-semver`** (`" 2.1.0 "`) is accepted, because semver parsing trims — again
  inconsistent with the no-trim rule for subjects, and uncovered.
- **`envelope_format_mismatch`** is in the code catalogue — "the declared format disagrees with the
  format the registry holds for that id" — but nothing in the registry or client emits it today.
- **Ordinal values above `int32`** warn rather than being carried; the ceiling is an
  implementation limit, not a stated protocol bound.
