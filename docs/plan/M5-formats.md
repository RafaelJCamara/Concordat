# M5 — Avro + Protobuf

**Depends on:** [M1](M1-registry-core.md) · **Unlocks:** [M6](M6-sdks.md) · **Design refs:** [§7](../DESIGN.md#7-contract-checks--cli-and-build-time), decisions 002, 015, 016

---

## M5.1 Format abstraction

**Done, ahead of schedule** — built during M1/M2 so JSON Schema (the only format M1 shipped)
never had a format-specific shortcut baked into the Application layer. See PLAN.md's "Two
things built early on purpose" for the pattern; this is a third.

- [x] `Concordat.Formats.Abstractions` — `ISchemaCanonicalizer`, `ICompatibilityChecker`,
      `ISchemaReferenceExtractor`, `ISchemaBundler`, `IPayloadValidator`, `SchemaIdComputer`
- [x] Format projects depend only on the abstraction + their parser library —
      `Concordat.Formats.Json` proved the shape; `Concordat.Formats.Avro` (below) follows it
- [x] `ISchemaFormatRegistry` / `ISchemaBundlerRegistry` resolve services by `SchemaFormat` at
      the Application layer, throwing loudly for an unregistered format rather than falling
      back to JSON — this is what lets a format be wired in one interface at a time (see M5.2)

## M5.2 Avro

**Done 2026-08-13 · `Concordat.Formats.Avro` · 93 tests**

- [x] Canonical form and content-addressed identity
- [x] Resolution rules: defaults, aliases, union widening, enum symbols
- [x] Two-axis mapping
- [x] Named-type FQN → subject reference resolution — **settled as a refusal
      ([ADR-023](../adr/023-no-cross-subject-references-avro-protobuf.md)).** An Avro fullname
      has nowhere to pin a version, so a schema referencing a type it does not define is
      rejected with `schema_references_unsupported` naming the type. Self-references and
      same-document types resolve normally, so recursive schemas still work

### The canonical form is PCF with one deviation, and finding out why was the milestone

Avro's Parsing Canonical Form is what DESIGN §4 names, and it was implemented exactly — until
building the checker on top of it showed it could not work. **PCF's `[STRIP]` rule discards
`default` and `aliases`**, and the architecture, settled in M1 when JSON Schema was the only
format, *stores and compares the canonical form*. JSON Schema hid this for four milestones
because its canonicalisation is lossless; **Avro is the first format where canonicalisation
throws information away**, and it throws away precisely what schema resolution runs on.

Two consequences, the second worse than the first: the checker cannot tell an added field with
a default from one without (the difference between the ordinary Avro change and a breaking one),
and **the registry would serve consumers a schema stripped of the defaults that let it read
older data at all**.

So the canonical form here is **lossless normalisation**: sort, resolve fullnames, drop
whitespace, and strip only `doc`. `default`, `aliases`, `logicalType` and everything else
survive. For a schema using none of the attributes PCF would have stripped the output is
byte-identical to PCF, and a test pins that. Recorded as
[DECISIONS-PENDING #17](../DECISIONS-PENDING.md#17-avros-parsing-canonical-form-is-lossy-and-the-architecture-stores-the-canonical-form)
— **still yours to overturn, and free to overturn until the first Avro schema is stored.**

### Compatibility is an implementation of a specification, not a design

The opposite of M1.3. JSON Schema has no compatibility spec, so that engine had to be designed
and its acceptance criteria argued. Avro specifies resolution exactly, so a disagreement here is
a bug rather than a judgement call — and the tests read as the spec's rules restated.

**Direction is applied by swapping the roles, not by inspecting the finding.** Avro resolution
is asymmetric (`int` promotes to `long`, never the reverse), so `backward` runs
`(writer: previous, reader: proposed)` and `forward` runs the pair the other way round. The same
edit legitimately produces different findings in each direction, which a narrower/wider
heuristic cannot express at all.

**Surfaces come out inverted from JSON Schema, and that is the point of the second axis.** A
JSON document is self-describing, so almost every divergence is `WireJson` — validation fails,
bytes still parse. Avro binary is positional, so a resolution failure means **the bytes do not
decode**: most findings are `Wire`. The two exceptions are what make the axis earn its keep:

| Change | Surface | Why |
|---|---|---|
| `int` → `long` | `Source` | Avro promotes on read, so it decodes; the generated type changes. **Avro's `int32 → int64`** — permitted under the default `Backward × WireJson`, blocked under `× Source` |
| Enum symbol absorbed by the reader's `default` | `WireJson` | Decodes, but the value read is not the value written |

Four tokens were added to `BreakingChangeKinds` — `name_changed`, `fixed_size_changed`,
`type_promoted`, `enum_value_defaulted`. Additive, but normative under ADR-019 once published.

### Two things that fall out of the rules and are worth stating

- **A rename is recoverable backward and not forward.** Aliases are declared on the *reader*, so
  a proposal can name what it used to be called; a schema written before the new name existed
  cannot. The error message says so rather than leaving it to be discovered.
- **`ContentModel` is not consulted.** It exists for JSON Schema's `additionalProperties`. Avro
  records are closed by construction — the wire format is positional and there is no way to
  encode an undeclared field — so honouring the setting would imply it does something.

## M5.3 Protobuf

**Done 2026-08-13 · `Concordat.Formats.Protobuf` · 69 tests**

- [x] Normalised canonical form, import ordering normalised
- [x] Field numbers as identity — break on number or wire-type change, and on removal without `reserved`
- [x] Two-axis mapping — a rename is `WIRE`-safe but `WIRE_JSON`- and `SOURCE`-breaking
- [x] `import` filename → subject reference resolution — **settled as a refusal
      ([ADR-023](../adr/023-no-cross-subject-references-avro-protobuf.md)).** An import names a
      file and carries no version. `google/protobuf/*` imports are allowed — the runtime
      resolves those, not a registry — and every other import is rejected by name

### A hand-written parser, and the second reason is the deciding one

ADR-019 needs canonicalisation reproduced byte-for-byte in every SDK, which is the same
argument that kept the JSON and Avro canonicalisers hand-written. The stronger reason is
narrower: **the mature .NET `.proto` parsers are reflection-heavy, and M3.3 established that
reflection in the CLI's NativeAOT binary fails *silently*** — it compiles, passes every JIT
test, and misbehaves once trimmed. The CLI links this assembly.

It **refuses what it does not understand** rather than guessing: proto2, groups, `extend`,
`service` and aggregate option values are rejected with a reason. A parser that silently
mis-reads a construct produces a confidently wrong schema id *and* a confidently wrong verdict,
which is worse than not supporting the format.

### The canonical form is `.proto` source, applying M5.2's lesson before it cost anything

DESIGN §4 says "normalised `FileDescriptorProto`". A descriptor is the right *model* — it is
what the compatibility engine reasons over — but the canonical text is also what the registry
**stores and serves**, and a consumer fetching a Protobuf schema wants source it can hand to
`protoc`. Serving a descriptor blob would be [#17](../DECISIONS-PENDING.md#17-avros-parsing-canonical-form-is-lossy-and-the-architecture-stores-the-canonical-form)
again: technically sufficient, practically useless. That was cheap to get right here only
because M5.2 had just paid for the lesson.

### This is where the second axis stops being theoretical

DESIGN §4 names a documented Confluent defect: its Protobuf checker is *stricter than
Protobuf's actual wire compatibility*, rejecting changes that produce byte-identical output.
With one axis there is nowhere to record "the bytes are fine, the JSON mapping is not", so the
only available answer is to reject. Every case below is a test:

| Change | `× WIRE` | `× WIRE_JSON` | `× SOURCE` | Why |
|---|---|---|---|---|
| Rename a message, tags unchanged | ✅ | ❌ | ❌ | Names are not transmitted; proto3 JSON, `Any` type URLs and generated code all use them |
| Rename a field, number unchanged | ✅ | ❌ | ❌ | Same |
| `int32` → `int64` | ✅ | ❌ | ❌ | Identical varint bytes — but proto3 JSON quotes 64-bit integers and leaves 32-bit bare |
| `string` → `bytes` | ✅ | ❌ | ❌ | Both length-delimited; JSON base64-encodes bytes |
| Add explicit `optional` | ✅ | ✅ | ❌ | Presence tracking only; encoding identical |
| `int32` → `fixed32` | ❌ | ❌ | ❌ | Different wire type; misaligns everything after it |
| `int32` → `sint32` | ❌ | ❌ | ❌ | Both varints, but zigzag means the value read is not the value written |
| Remove a field without `reserved` | ❌ | ❌ | ❌ | See below |
| Add a field with a fresh number | ✅ | ✅ | ✅ | proto3 has no required fields; readers skip unknown tags |

**Removing a field without `reserved` is reported at `Wire` even though nothing breaks that
day.** Readers skip unknown tags, so today's traffic is fine; the hazard is that the number is
now free for a later version to reuse, at which point old data decodes into the new field
silently. Flagging it at removal is the only moment it is cheap to fix, and the message says
`reserved <n>;` outright.

## M5.4 Corpus extension

- [x] `int32 → int64` passes `WIRE`, fails `SOURCE` — covered for **both** Protobuf (M5.3) and
      Avro (M5.2, where the promotion is a `Source` finding rather than `WireJson`)
- [x] Protobuf message rename with stable field tags passes `WIRE`, fails `WIRE_JSON`
- [ ] ~~Splitting a `.proto` across files is compatible (Confluent rejects this)~~ — **cannot be
      written under [ADR-023](../adr/023-no-cross-subject-references-avro-protobuf.md)**, which
      refuses the import the split definition lives behind. Recorded in that ADR's consequences
      as a real loss from the v1 story: the case exists to demonstrate a Confluent defect
      Concordat fixes. It returns with references
- [x] Promote these from per-format test suites into `tests/Concordat.Conformance` — **18
      fixtures added; the corpus runs 82 cases, up from 64**

### The corpus runner was quietly JSON-only

Every fixture already carried a `format` field and **nothing read it**. With one format that was
invisible; the moment an Avro fixture landed it would have been canonicalised by the JSON
implementation and passed or failed for the wrong reason. The runner now resolves the
canonicaliser, checker and extractor by format.

A new **`references/`** category pins ADR-023 itself. The refusal is protocol, not a .NET
choice: an SDK that resolves what this one rejects would accept schemas the registry will not,
and the disagreement surfaces as a failed registration nobody can explain. The category also
carries the JSON contrast case, so the refusals read as a consequence of `$ref` being the only
reference form that can carry a version rather than as an arbitrary restriction.

Every expected value in the 18 new fixtures was written by hand before running them, and all 18
matched first time — the same check M1.7 applied to the schema-id preimages.

---

## Exit

All three formats round-trip through canonicalisation, identity and both compatibility
axes, with the corpus green.

**Met.** JSON Schema, Avro and Protobuf each canonicalise deterministically and idempotently,
produce content-addressed ids, and are checked on both axes; the corpus runs all three formats
and is green at 82 cases. The one documented shortfall is
[ADR-023](../adr/023-no-cross-subject-references-avro-protobuf.md): Avro and Protobuf accept
only self-contained schemas, which costs the "splitting a `.proto` across files" case that
DESIGN §12 wanted.

---

← [M4 — Web app](M4-web-app.md) · [Plan index](../PLAN.md) · [M6 — Tier 2 SDKs →](M6-sdks.md)
