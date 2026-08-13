# ADR-002: Three schema formats in v1 — JSON Schema, Avro, Protobuf

- **Status:** Accepted
- **Date:** 2026-08-13
- **Deciders:** Rafael Camara

## Context

A schema registry must decide which schema languages it understands. Each format carries a
different cost: a parser, a canonical form, a compatibility rule set, and a payload
validator — multiplied across every SDK language ([ADR-021](021-tier-2-sdk-set.md)).

The three candidates serve different populations. JSON Schema is what RabbitMQ teams
actually ship today. Avro has the best-specified compatibility rules of any format, with
schema resolution defined normatively rather than by implementation. Protobuf is the
polyglot lingua franca and the format most likely to be already in use where gRPC is.

## Decision

Concordat supports JSON Schema, Avro and Protobuf. JSON Schema ships first and alone in
M1; Avro and Protobuf land together in M5 behind a shared `Formats.Abstractions` contract.

## Alternatives considered

- **JSON Schema only.** Rejected: it would exclude teams already standardised on Protobuf
  and give up the one format (Avro) with unambiguous compatibility semantics to validate
  the two-axis model against.
- **Avro first, as Confluent does.** Rejected: Avro is not the RabbitMQ norm. Leading with
  it would mean the first release fits almost nobody's existing estate.
- **Add AsyncAPI as a fourth.** Rejected: AsyncAPI describes an API, not a message payload
  schema; it embeds one of these three for the payload itself.

## Consequences

- **Positive:** covers the practical majority of RabbitMQ payloads. Avro's specified
  resolution rules act as a correctness reference for the compatibility engine.
- **Negative:** three canonicalisation implementations and three compatibility rule sets,
  each of which must be reimplemented or bound per SDK language. This is the single
  largest multiplier on M6's cost.
- **Negative:** JSON Schema has no compatibility specification at all, so its rules must
  be designed rather than implemented — see [ADR-016](016-two-axis-compatibility.md).

## References

- [DESIGN §7](../DESIGN.md#7-contract-checks--cli-and-build-time)
- [M5 — Avro + Protobuf](../plan/M5-formats.md)
