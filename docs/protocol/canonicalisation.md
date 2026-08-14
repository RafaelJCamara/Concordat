# Canonicalisation and schema identity

**Normative.** One of the five artifacts [ADR-019](../adr/019-language-neutral-protocol.md) names
as the protocol. It specifies the canonical form of each of the three schema languages and the
exact bytes hashed to derive a schema id.

**When this document and the conformance corpus disagree, the corpus wins, because the corpus is
executable.** `tests/Concordat.Conformance/corpus/canonicalisation/` and `corpus/schema-id/` are
the arbiter; every non-obvious rule below cites the fixture that fails if you get it wrong. A rule
with no fixture behind it is flagged in
[Where this document is not yet pinned](#where-this-document-is-not-yet-pinned).

Get this right before anything else. Schema ids are content-addressed, so an implementation that
canonicalises differently computes different ids and **cannot share a registry with anyone**.

---

## 1. Why this exists, and where it sits

Two failures, both observable in shipping products.

**Near-duplicate accumulation.** Confluent's `normalize.schemas` still defaults to `false`, which
is how registries end up holding thousands of schemas that differ only in whitespace, key order or
a comment, and blow past quota. Canonicalisation is day-one work under
[ADR-015](../adr/015-content-addressed-ids.md), not an optimisation.

**Colliding identities.** Hashing the body alone collides two schemas that have identical text and
different reference sets. That is the specific mistake Confluent's CP 8.1 GUID computation exists
to avoid, and §6 is shaped around it.

The registration pipeline runs in this order, and the order is protocol because each step consumes
the previous step's output:

1. **Canonicalise** the authored body. A failure here rejects the registration.
2. **Check portability** (JSON Schema only, §3.7). An unsupported dialect rejects, because the
   rules the schema was written against are not the rules that would be applied to it.
3. **Extract references** from the *canonical* body (§7). Edges come from the document, never from
   the caller.
4. **Compute the schema id** from the format, the canonical body and the extracted references (§6).

**The canonical text is what the registry stores and serves.** The authored body is never
persisted. Two consequences an implementer must internalise:

- Anything canonicalisation discards is discarded for everyone, forever. A consumer fetching that
  schema gets the canonical text, not what the author typed.
- The canonical text is also what the compatibility engine compares. A rule that strips something
  the engine needs makes every verdict from that engine wrong.

Both are why Avro's canonical form deliberately is **not** the Avro specification's Parsing
Canonical Form (§4).

---

## 2. Rules common to all three formats

**Canonicalisation is idempotent.** Canonicalising an already-canonical document must be a
byte-for-byte no-op, or the id is not stable under re-registration. The corpus runner re-canonicalises
its own output on every `canonicalisation/` fixture, not only the ones that mention it.

**The canonical form is text, and it is hashed as UTF-8.** No byte order mark.

**An empty or whitespace-only body is rejected** with `schema_body_empty`.

**A body that is not well-formed in its declared format is rejected** with `schema_malformed`. It
is never repaired, and never partially parsed.

**A canonical body may be at most 512 KiB, measured in UTF-8 bytes**, rejected with
`schema_too_large` above that. A documented ceiling is required: without one a registry accumulates
schemas large enough to make every read expensive, and the failure arrives as a timeout rather than
a clear rejection. AWS Glue caps at 170 KB and Redpanda warns above 128 KB, so 512 KiB is generous.

**Sorting is ordinal by UTF-16 code unit**, wherever this document says keys or names are sorted.
This is the rule RFC 8785 uses, and it is deliberately *not* UTF-8 byte order: the two orders
disagree for any name containing a supplementary-plane character, because a surrogate pair sorts
before U+E000 in UTF-16 and after it in UTF-8. It is also not a culture-aware comparison, or the
canonical form would depend on the server's locale (`canonicalisation/key-order.json`).

**All three canonicalisers are hand-written against their specifications rather than delegated to a
parser library.** ADR-019 requires every SDK to reproduce these bytes exactly, and a third-party
library's own notion of "canonical" is a dependency that cannot be audited for cross-language
agreement. Reuse a *parser* if you like; do not reuse someone else's *canonicaliser*.

---

## 3. JSON Schema

The canonical form is the document reserialised with:

- object members sorted by key (§2);
- no insignificant whitespace (`canonicalisation/whitespace.json`);
- minimal, deterministic string escaping (§3.4);
- **array order preserved** (§3.2);
- **number literals preserved verbatim** (§3.3);
- `$id` and `$ref` normalised when they hold an absolute URI (§3.5).

### 3.1 Parsing

Strict JSON. Comments are rejected, trailing commas are rejected, single-quoted strings are
rejected — accepting any of them would make the canonical form depend on which parser's leniency
was in play (`canonicalisation/malformed-rejected.json`).

**Duplicate object keys are rejected outright** with `schema_malformed`, at any depth. Parsers
disagree about which value wins, so the document has no single meaning and must not be given an id
at all. Picking a winner would make the same bytes mean different things to producer and consumer
(`canonicalisation/duplicate-keys-rejected.json`).

### 3.2 Array order is preserved

Never sort an array. Order is semantic in JSON Schema: `prefixItems` is positional, and `enum`
order is observable (`canonicalisation/array-order-preserved.json`).

### 3.3 Number literals are preserved verbatim

`1.0` and `1` produce different ids. `1e2` stays `1e2`; `1E2` stays `1E2`; `-0` stays `-0`;
`1.50` keeps its trailing zero.

This is a **deliberate deviation from RFC 8785**, which routes every number through an ECMAScript
double and therefore loses precision on large integers. Corrupting a `maximum` or a `multipleOf` is
strictly worse than missing a deduplication, and verbatim preservation is markedly easier to
reimplement identically in Python, Go and Java — which ADR-019 requires
(`canonicalisation/numbers-verbatim.json`).

### 3.4 String escaping

Escaping is normalised by round-tripping through the writer, so `\u0041` becomes `A` and two
spellings of one string collapse. The output escapes the minimum, and must not depend on where the
text might later be displayed — in particular `<`, `>`, `&`, `'` and `/` are **not** escaped, and
ordinary non-ASCII text such as `é` or `中` is emitted as raw UTF-8.

What is escaped, as the registry implements it today:

| Input | Output |
| --- | --- |
| `"` (U+0022) and `\` (U+005C) | `\"` and `\\` |
| U+0008, U+0009, U+000A, U+000C, U+000D | the short escapes `\b`, `\t`, `\n`, `\f`, `\r` |
| Every other control (Cc): U+0000–U+001F, U+007F–U+009F | a six-character `\u` escape, **upper-case hex digits** |
| Every space separator (Zs) except U+0020: U+00A0, U+1680, U+2000–U+200A, U+202F, U+205F, U+3000 | a six-character `\u` escape |
| U+2028 (Zl) and U+2029 (Zp) | a six-character `\u` escape |
| U+FEFF | a six-character `\u` escape |
| Private-use characters (Co), U+E000–U+F8FF | a six-character `\u` escape |
| Unassigned BMP code points (Cn) | a six-character `\u` escape |
| **Every supplementary character**, above U+FFFF | **two** six-character `\u` escapes — the UTF-16 surrogate pair, upper-case hex |
| Everything else, including `<`, `>`, `&`, `'`, `/` and ordinary non-ASCII text such as `é` or `中` | raw UTF-8 |

The same escaping applies to object keys, and to the Avro canonical form in §4, which uses the same
writer.

**This table is measured from the implementation and is pinned by no fixture.** It is the sharpest
cross-language hazard in this document: the natural implementation in Go or Python emits raw UTF-8
for everything except `"`, `\` and C0 controls, which produces a different canonical body — and so
a different schema id — for any schema containing an emoji, a non-breaking space or a private-use
character anywhere in a description, a `const` or an `enum`. See
[Where this document is not yet pinned](#where-this-document-is-not-yet-pinned).

### 3.5 `$id` and `$ref` URI normalisation

**Only those two keywords, and only when the value parses as an absolute URI.** Normalising any
string that happens to look like a URI would rewrite ordinary schema content such as a `const` or a
`description` (`canonicalisation/uri-normalisation.json`).

Normalisation is RFC 3986 syntax-based normalisation, as .NET's `Uri.AbsoluteUri` performs it:

| Input | Output |
| --- | --- |
| `HTTPS://Example.COM:443/a/../schema` | `https://example.com/schema` |
| `CONCORDAT://Prod/acme.Common/1` | `concordat://prod/acme.Common/1` |
| `http://example.com:80/x` | `http://example.com/x` |
| `https://example.com` | `https://example.com/` |
| `https://example.com/./a/./b/` | `https://example.com/a/b/` |
| `https://example.com/%7euser` | `https://example.com/~user` |
| `https://example.com/a b` | `https://example.com/a%20b` |
| `urn:uuid:1234…` | unchanged |

So: scheme and host lower-cased, the default port for the scheme dropped, dot segments resolved, an
empty path filled in as `/`, percent-encoding of unreserved characters decoded and remaining
escapes normalised. Query and fragment are preserved as written.

**Relative and fragment-only references are left exactly as written.** `#/$defs/Address` cannot be
normalised without a base document the registry does not have.

### 3.6 Booleans, nulls and boolean schemas

`true`, `false` and `null` are written as themselves. A boolean schema (`true` or `false` in place
of an object) is a legal document and canonicalises to itself.

### 3.7 Dialect

Concordat implements **JSON Schema draft 2020-12 and nothing else**. A document whose `$schema`
declares another dialect is rejected with `schema_dialect_unsupported`, because keywords changed
meaning between drafts — `items` most visibly — so validating it under 2020-12 would apply rules
its author did not write it against.

Both `https://json-schema.org/draft/2020-12/schema` and the same URI with a trailing `#` are
accepted: the specification publishes one and the wild uses the other, and refusing a document over
a URI detail its author never chose would be absurd.

**An absent `$schema` means 2020-12 by assumption**, with no warning. A warning that fires on
almost every schema anyone writes is noise.

Rejection happens *after* canonicalisation, on the canonical text. The dialect check is not part of
the canonical form.

---

## 4. Avro

**Concordat's Avro canonical form is deliberately not the specification's Parsing Canonical Form.
Read this section before writing any code.**

PCF is defined by the specification's `[STRIP]` rule: keep only `type`, `name`, `fields`,
`symbols`, `items`, `values` and `size`, and drop everything else — which discards `default`,
`aliases`, `logicalType` and `doc`. DESIGN §4 names PCF, and an early implementation did exactly
that. It was wrong, and the reasons matter because an implementer who "corrects" this back to PCF
will ship two serious defects:

1. **The registry could not serve a usable Avro schema.** The canonical text is what is stored and
   served, so a consumer fetching a schema by id would receive a body with no defaults — and would
   therefore be unable to read data written under an older version, which is the one job an Avro
   reader schema exists to do. That is data loss at registration time, not a reporting gap.
2. **The compatibility checker could not be built correctly.** Avro's schema resolution runs on
   `default` and `aliases`. Without them, a checker has to call every added field breaking,
   including the ones carrying a default — which is the ordinary Avro change, and precisely the
   unusable-under-its-own-defaults behaviour Concordat exists to beat Confluent at.

Underneath both sits an identity problem: under PCF, a schema **with** a field default and the same
schema **without** one hash to the same id, yet they resolve differently against the same bytes.
Content addressing is supposed to mean *same id ⇒ same meaning*, and under PCF for Avro it does not.

So the rule is **lossless normalisation**: every attribute that can change how data is read or
resolved survives, and `doc` — the one purely presentational attribute — is dropped so that a
comment edit does not mint a new schema id
(`canonicalisation/avro-doc-stripped-defaults-kept.json`).

**For a schema that uses none of the attributes PCF would have stripped, the output is
byte-identical to PCF.** The form only diverges where PCF loses information, which is what keeps it
recognisable to anyone who knows Avro.

This decision is recorded as [DECISIONS-PENDING #17](../DECISIONS-PENDING.md) and is open: it is
reversible until the first Avro schema is stored, and reversing it costs a preimage version bump
(§6) plus a migration. Concordat ids were never Avro fingerprints anyway — 128-bit truncated
SHA-256 over a versioned preimage, against Avro's 64-bit CRC — so nothing that interoperates today
depends on the two forms matching.

### 4.1 The transformation

**FULLNAMES.** Every named type is rewritten to its fully-qualified name, resolved against the
nearest enclosing namespace, and the now-redundant `namespace` attribute is dropped. A name that
already contains a dot is a fullname; any `namespace` given alongside it is ignored. Nested types
inherit the enclosing record's namespace, and a self-reference resolves to the type's own fullname —
the linked-list case (`canonicalisation/avro-fullnames-resolved.json`).

**ALIASES ON A NAMED TYPE ARE FULLNAMES TOO**, resolved against the namespace the type itself
resolved in, or `Old` and `acme.Old` would canonicalise differently while naming the same type.
**Aliases on a *field* are left unqualified**, because a field alias is a plain field name and
fields do not live in a namespace.

**PRIMITIVES.** A primitive in object form with nothing else surviving reduces to the bare string:
`{"type":"long","doc":"…"}` becomes `"long"`. One carrying any other attribute keeps its object
form — `{"type":"long","logicalType":"timestamp-millis"}` stays, because `logicalType` decides the
type a generator emits and how a reader interprets the bytes. PCF always reduces here; this does
not. `precision` and `scale` on a `decimal` survive for the same reason.

**KEY ORDER.** Structural keys first, in the specification's fixed order; every surviving attribute
then follows, sorted ordinally, so that arbitrary and future attributes still canonicalise
deterministically.

| Node | Keys, in order |
| --- | --- |
| `record` or `error` | `name`, `type`, `fields`, then sorted extras |
| `enum` | `name`, `type`, `symbols`, then sorted extras |
| `fixed` | `name`, `type`, `size`, then sorted extras |
| `array` | `type`, `items`, then sorted extras |
| `map` | `type`, `values`, then sorted extras |
| A primitive in object form | `type`, then sorted extras |
| A record field | `name`, `type`, then sorted extras |

So a `fixed` written `{"size":16,"namespace":"acme","name":"Fx","type":"fixed"}` canonicalises to
`{"name":"acme.Fx","type":"fixed","size":16}`, and a field written
`{"name":"a","type":"string","doc":"…","default":"d","order":"ascending","aliases":["oldA"]}`
canonicalises to `{"name":"a","type":"string","aliases":["oldA"],"default":"d","order":"ascending"}`.
`size` is re-emitted as a base-10 integer.

**SEQUENCE ORDER IS PRESERVED** in `fields`, `symbols` and union branches. All three are semantic:
a union branch's position is its index on the wire, and a symbol's position is its ordinal.

**`error` canonicalises as `record`.** The two differ only in whether the type may be used as a
protocol error; nothing about the data changes.

**WHITESPACE AND NUMBERS.** No whitespace outside string literals. Number literals inside attribute
values are preserved verbatim, the same deviation from RFC 8785 the JSON canonicaliser makes.
String escaping is the table in §3.4.

**ARBITRARY ATTRIBUTE VALUES.** A `default`, a `logicalType`, or anything a future Avro revision
adds is written deterministically: object keys sorted, **array order preserved** — a `default` for
an array field is data and its order is meaningful — and numbers verbatim.

**`doc` is the only attribute dropped**, at every level.

### 4.2 What is rejected

With `schema_malformed`: a schema that is not a string, array or object; an object schema with no
string `type`; an unknown type name; a named type with no non-empty `name`; a non-string
`namespace`; a record with no `fields` array; a field that is not an object with a non-empty `name`;
a non-string enum symbol; a non-string alias; a `fixed` whose `size` is missing, non-numeric or
negative. Duplicate object keys are rejected exactly as in §3.1.

---

## 5. Protobuf

**The canonical form is normalised `.proto` source, not a serialised `FileDescriptorProto`.**
DESIGN §4 names the descriptor, and a descriptor is the right *model* — it is what the
compatibility engine reasons over. But the canonical text is also what the registry stores and
serves, and a consumer fetching a Protobuf schema wants source it can hand to `protoc`. Serving a
descriptor blob would repeat exactly the mistake corrected for Avro in §4: technically sufficient,
practically useless.

**The output is indented rather than minified**, unlike the JSON and Avro forms. Those are consumed
by machines; this one is compiled by people. Canonicalisation requires determinism and idempotence,
and neither of those needs the whitespace removed.

### 5.1 Layout

Two-space indent per level, `\n` line endings, and a trailing newline at end of file. Emitted in
this order, with nothing else:

```
syntax = "proto3";
package <package>;                 // omitted when the file declares none
import "<file>";                   // sorted ordinally, duplicates removed
option <name> = <value>;           // sorted by name
<enums>                            // sorted by fully-qualified name
<messages>                         // sorted by fully-qualified name
```

A message body, in order:

```
  reserved <n>[ to <m>];           // ranges merged and sorted, one statement each
  reserved "<name>";               // sorted ordinally, duplicates removed
  option <name> = <value>;         // sorted by name
  [label ]<type> <name> = <number>[ [<opt> = <value>, …]];   // fields sorted by number
  oneof <name> {                   // oneofs sorted by name; their fields by number
    …
  }
  <nested enums>                   // sorted by fully-qualified name
  <nested messages>                // sorted by fully-qualified name
```

An enum body is `reserved`, then options, then values sorted by **number**, each written
`<NAME> = <number>[ [<opt> = <value>, …]];`.

Details that decide byte-equality: the label is `repeated ` or `optional ` or nothing; a map type is
written `map<KEY, VALUE>` with a space after the comma; inline options are a space, then `[`, then
`name = value` pairs joined with `, `, then `]`; a message or enum is written with its **local**
name, not its fullname, even though the sort key is the fullname.

### 5.2 The normalisations

**Comments are dropped.** Line and block comments alike; they are the only purely presentational
content (`canonicalisation/protobuf-import-order-normalised.json` carries both).

**Declaration order carries no meaning in Protobuf, so it is normalised away.** Definitions sort by
name, fields sort by **number** — the number is the field's identity, not its position — enum values
sort by number, and options sort by name.

**Imports sort.** Imports are a set, not a sequence, and DESIGN §12 requires import order not to
affect the schema id (`canonicalisation/protobuf-import-order-normalised.json`). `public` and `weak`
modifiers change visibility rather than content and are dropped.

**Reserved ranges are merged and sorted.** Ranges that overlap *or touch* coalesce, so
`reserved 3, 2;` and `reserved 2 to 3;` become one statement, `reserved 2 to 3;`. Leaving them
distinct would give one schema two ids
(`canonicalisation/protobuf-reserved-ranges-merged.json`). `to max` is the literal 536870911, the
largest legal field number.

**Type references resolve to leading-dot fullnames wherever the document defines them**, using
protobuf's innermost-first scoping: try the enclosing scope, then each enclosing scope outward, then
the file's package root. So `Item`, `Order.Item` and `.acme.Order.Item` canonicalise identically,
and a forward reference to a sibling declared later resolves correctly — names are collected in a
first pass precisely so that a single pass in declaration order cannot fail on one.

**A reference the document does not define is left exactly as written.** Anything reached through an
`import` stays as the author spelled it, because resolving it requires the imported file — which the
registry does not fetch, and which §7 refuses outright for anything but the well-known types.
Scalar types are left as written.

**Option values are preserved as written**, with string values re-emitted in double quotes, escaping
only `\` and `"`. An `option` declared inside a `oneof` block is parsed and discarded.

### 5.3 What is refused, and why refusing is right

A parser that silently mis-reads a construct produces a confidently wrong schema id *and* a
confidently wrong compatibility verdict, which is worse than not supporting the format. All of the
following are `schema_malformed`:

| Construct | Reason |
| --- | --- |
| `syntax = "proto2"` | proto2 adds required fields, groups and default values, none of which the engine reasons about. |
| A missing `syntax` line | Without the declaration `protoc` assumes proto2, so this is not proto3 that forgot to say so. |
| `required` | proto2 only. |
| `group`, `extensions`, `extend` | proto2 constructs, and extensions change how unknown fields are interpreted. |
| `service` | Concordat registers message schemas; an RPC surface is not one. |
| Aggregate option values (`option x = { … }`) | They would have to be normalised to compare reliably, and no compatibility rule reads one. |
| A field number below 1 | Field numbers start at 1. |
| A `repeated` map field | Not legal proto3. |
| Unterminated block comment or string literal | — |

---

## 6. The schema id

```
SchemaId = lowercase-hex( first 16 bytes of SHA-256( UTF-8( preimage ) ) )
```

32 lowercase hexadecimal characters. **Upper case is rejected rather than normalised** wherever an
id is parsed: two spellings of one id would defeat the point of content addressing.

Truncation to 128 bits is deliberate — ample against collision for this population, and it halves
the bytes carried in every message header.

### 6.1 The preimage

**The preimage format is itself normative protocol**, and it carries an explicit version tag for
the same reason the envelope carries `concordat-v`: changing the framing changes every id in every
installation, and Azure's unversioned `avro/binary+{id}` scheme is the documented dead end. Bump
`concordat-schema-id/v1` only alongside a migration plan.

The preimage is this exact text. Every `\n` below is a real newline byte and is the only line break
the preimage contains; nothing follows the final one:

```
concordat-schema-id/v1\n
format:<format-token>\n
body:<byte-length>:<canonical body>\n
refs:<count>\n
```

followed, for each reference in ascending order of its name compared ordinally, by:

```
ref-name:<byte-length>:<name>\n
ref-subject:<byte-length>:<subject>\n
ref-version:<ordinal>\n
```

- `<format-token>` is `json`, `avro` or `protobuf` — the same tokens the `concordat-format` header
  uses. Deliberately not a language's enum name: renaming a member must not silently change the
  wire format or invalidate every stored id.
- `<count>` and `<ordinal>` are invariant base-10 integers.
- References are **sorted by name, ordinally**, before hashing. Their input order is not
  significant.

### 6.2 Every length prefix is a count of UTF-8 **bytes**

Not characters, not UTF-16 code units. The preimage is hashed as UTF-8, so a character count would
not actually delimit the encoded field, and an implementation using string length diverges on the
first non-ASCII schema. `é` is one UTF-16 char and two UTF-8 bytes: the body `{"t":"é"}` is nine
characters and **ten bytes**, and the preimage says `body:10:`
(`schema-id/length-prefixes-are-utf8-byte-counts.json`).

The length prefix is also what makes the framing unambiguous rather than decorative. The value that
follows may itself contain `\n` — every Protobuf canonical body does — so a reader of this format
must use the byte count and must not scan for the next newline.

### 6.3 Why the format and the references are in the hash

**The format.** The same bytes under a different schema language are a different schema. Without
the format in the preimage, a JSON and an Avro document with identical text collide
(`schema-id/format-is-part-of-the-identity.json`).

**The references.** Two schemas with identical bodies and different reference sets are different
schemas. Hashing the body alone collides them — the mistake Confluent's CP 8.1 GUID computation
exists to avoid (`schema-id/references-are-covered-by-the-hash.json`).

**The length prefixes on each field.** Without them, a reference named `a:b` and a pair of
references named `a` and `b` could serialise identically and collide.

### 6.4 Worked examples, straight from the corpus

Fixtures write `\n` literally; the preimage contains real newline bytes.

```
concordat-schema-id/v1\nformat:json\nbody:17:{"type":"object"}\nrefs:0\n
  -> 696e1e6b82db0848e5c59eaa7a89f7d0        schema-id/no-references.json

concordat-schema-id/v1\nformat:avro\nbody:17:{"type":"object"}\nrefs:0\n
  -> 548725813d68316619b8894e5f830eca        schema-id/format-is-part-of-the-identity.json

concordat-schema-id/v1\nformat:json\nbody:10:{"t":"é"}\nrefs:0\n
  -> 8298cedf683163e4d9a915dd36d4a674        schema-id/length-prefixes-are-utf8-byte-counts.json

concordat-schema-id/v1\nformat:json\nbody:17:{"type":"object"}\nrefs:1\n
ref-name:25:concordat://prod/acme.A/1\nref-subject:6:acme.A\nref-version:1\n
  -> 0617b836e433e81284ebfa2a16da9aaa        schema-id/references-are-covered-by-the-hash.json
```

The corpus checks the **preimage bytes directly**, not only the resulting id. An implementation that
produces the right hash from the wrong framing diverges the moment a reference set changes, and the
id alone would not have caught it.

### 6.5 Consequences worth relying on

Registering an identical schema returns the existing id — registration is idempotent through a
unique constraint, with no counter, no retry loop and no coordination. The same schema yields the
same id in every environment and every installation, so promoting a subject from `staging` to `prod`
never invalidates an in-flight envelope. Ids are never reallocated and content is never deleted, so
`schema-id → schema` is immutable and a client may cache it forever — which is what lets an SDK keep
the registry off the delivery path after warm-up.

---

## 7. The reference set that feeds the id

References are extracted from the canonical body, never taken from the caller. A reference is
`(name, subject, version)`.

**JSON Schema** is the one format where a reference carries its own version, because `$ref` takes a
URI. Every `$ref` whose value begins `concordat://` becomes an edge; the `name` is the normalised
URI `concordat://<environment>/<subject>/<version>`
(`references/json-refs-are-extracted.json`). Local refs such as `#/$defs/Address` and ordinary HTTP
refs are the validator's business, not the registry's, and are ignored. A `$ref` that is clearly
addressed to Concordat but malformed is **reported** with `reference_invalid` rather than skipped,
or a typo in the scheme becomes a schema that registers with no edges and fails to resolve later.
The same target referenced twice is one edge.

**Avro and Protobuf refuse cross-subject references** with `schema_references_unsupported`
([ADR-023](../adr/023-no-cross-subject-references-avro-protobuf.md)). Neither an Avro fullname nor a
Protobuf `import` carries a version, so following one would bind to whatever that subject holds at
read time — the silent behaviour change ADR-017's gated `latest` pointer exists to prevent, one
layer down in the reference graph. Two carve-outs, both load-bearing:

- **An Avro record referencing itself is not an external reference.** Otherwise every recursive
  schema becomes unregisterable (`references/avro-self-reference-is-not-external.json`).
- **`google/protobuf/*` imports are allowed.** The well-known types ship with every Protobuf runtime
  and are resolved by the compiler, not by a registry, so they cannot drift and are not
  cross-subject references at all. Refusing them would rule out most real Protobuf, since
  `google.protobuf.Timestamp` is close to universal
  (`references/protobuf-well-known-import-is-allowed.json`).

An SDK that resolves what the registry refuses accepts schemas the registry will not, which is why
these refusals sit in the corpus rather than only in the .NET test suites.

Within a schema, reference names must be unique (`duplicate_reference_name`) and the set is stored
sorted by name, so the id is stable.

---

## 8. Fixture map

| Fixture | Pins |
| --- | --- |
| `canonicalisation/whitespace.json` | Insignificant whitespace removed |
| `canonicalisation/key-order.json` | Ordinal key sorting, at every depth |
| `canonicalisation/array-order-preserved.json` | Arrays are never sorted |
| `canonicalisation/numbers-verbatim.json` | The RFC 8785 deviation |
| `canonicalisation/uri-normalisation.json` | `$id`/`$ref` only, absolute only |
| `canonicalisation/duplicate-keys-rejected.json` | Duplicate keys have no single meaning |
| `canonicalisation/malformed-rejected.json` | No parser leniency |
| `canonicalisation/avro-doc-stripped-defaults-kept.json` | The PCF deviation |
| `canonicalisation/avro-fullnames-resolved.json` | Namespace inheritance and fullname folding |
| `canonicalisation/protobuf-import-order-normalised.json` | Import sorting, comment stripping, field sorting |
| `canonicalisation/protobuf-reserved-ranges-merged.json` | Reserved range merging |
| `schema-id/no-references.json` | Preimage framing baseline |
| `schema-id/format-is-part-of-the-identity.json` | Format is in the hash |
| `schema-id/length-prefixes-are-utf8-byte-counts.json` | Byte counts, not char counts |
| `schema-id/references-are-covered-by-the-hash.json` | References are in the hash |
| `references/*.json` | §7, including ADR-023's refusals |

---

## Where this document is not yet pinned

Stated explicitly rather than left for an implementer to discover. Each item is behaviour of the
registry that **no fixture asserts**, so a second implementation could diverge without failing the
corpus.

- **String escaping beyond ASCII (§3.4).** The most likely source of a silent id divergence in this
  document. No fixture contains a supplementary character, a private-use character, a non-breaking
  space or a control character, so nothing in the corpus catches an implementation that emits raw
  UTF-8 where the registry emits `\uXXXX`, or lower-case hex where it emits upper-case. Until a
  fixture exists, an implementation should reproduce the table in §3.4 and treat any schema
  containing those characters as a known interoperability risk.
- **URI normalisation beyond the cases in §3.5.** The corpus pins one case; the .NET unit tests add
  casing, default port and dot segments. Percent-encoding normalisation, an empty path gaining `/`,
  internationalised host names, and non-hierarchical schemes such as `urn:` are measured from the
  implementation and pinned nowhere. A language whose URI library differs will differ here.
- **Sort order for names containing supplementary characters.** §2 states UTF-16 code-unit order,
  which is what the implementation does and what RFC 8785 requires, but no fixture contains such a
  name, so a UTF-8-byte-order implementation passes the corpus today.
- **The Avro canonical form is an open decision.** DECISIONS-PENDING #17 records it as taken on the
  implementer's judgement and reversible until the first Avro schema is stored. If it is reversed,
  the preimage version tag in §6.1 changes with it.
- **Protobuf option value normalisation.** Escape sequences inside a string option value are decoded
  during lexing and re-escaped on output for `\` and `"` only, so a `\n` written in the source
  becomes a literal newline inside the canonical text. No fixture covers option values at all.
- **The 512 KiB ceiling is enforced against the canonical body**, after canonicalisation rather than
  on the authored text. No fixture covers the boundary.
- **Idempotence is asserted by the corpus runner, not by any fixture's data.** An implementation
  that runs the fixtures without re-canonicalising the output will not notice a non-idempotent rule.
