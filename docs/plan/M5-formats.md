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

**Done 2026-08-13 except references · `Concordat.Formats.Avro` · 83 tests**

- [x] Canonical form and content-addressed identity
- [x] Resolution rules: defaults, aliases, union widening, enum symbols
- [x] Two-axis mapping
- [ ] Named-type FQN → subject reference resolution — **blocked on
      [DECISIONS-PENDING #16](../DECISIONS-PENDING.md#16-avro-cross-subject-references-carry-no-version).**
      Avro references are bare FQN strings with no syntactic room to pin a version. Until it is
      settled, `ISchemaReferenceExtractor` and `ISchemaBundler` are unimplemented for Avro, so
      `ISchemaFormatRegistry` throws `NotSupportedException` for them and **an Avro schema still
      cannot be registered end to end** — canonicalise, identify and check all work today

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

- [ ] Normalised `FileDescriptorProto`, import ordering normalised
- [ ] Field numbers as identity — break on number or wire-type change, and on removal without `reserved`
- [ ] `import` filename → subject reference resolution
- [ ] Two-axis mapping — a rename is `WIRE`-safe but `WIRE_JSON`- and `SOURCE`-breaking

## M5.4 Corpus extension

- [ ] `int32 → int64` passes `WIRE`, fails `SOURCE`
- [ ] Protobuf message rename with stable field tags passes `WIRE`, fails `WIRE_JSON`
- [ ] Splitting a `.proto` across files is compatible (Confluent rejects this)

---

## Exit

All three formats round-trip through canonicalisation, identity and both compatibility
axes, with the corpus green.

---

← [M4 — Web app](M4-web-app.md) · [Plan index](../PLAN.md) · [M6 — Tier 2 SDKs →](M6-sdks.md)
