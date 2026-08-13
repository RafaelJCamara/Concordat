# M5 — Avro + Protobuf

**Depends on:** [M1](M1-registry-core.md) · **Unlocks:** [M6](M6-sdks.md) · **Design refs:** [§7](../DESIGN.md#7-contract-checks--cli-and-build-time), decisions 002, 015, 016

---

## M5.1 Format abstraction

- [ ] `Concordat.Formats.Abstractions` — canonicalisation, validation, compatibility per format
- [ ] Format projects depend only on the abstraction + their parser library

## M5.2 Avro

- [ ] Parsing Canonical Form
- [ ] Resolution rules: defaults, aliases, union widening, enum symbols
- [ ] Named-type FQN → subject reference resolution
- [ ] Two-axis mapping

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
