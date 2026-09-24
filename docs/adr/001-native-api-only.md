# ADR-001: Native API only, no Confluent wire compatibility

- **Status:** Accepted
- **Date:** 2026-08-13
- **Deciders:** Rafael Camara

## Context

Every existing open-source schema registry — Redpanda, Apicurio, Karapace — clones
Confluent's REST surface. The reason is specific, not fashion: Confluent's Apache-2.0
serializers are compiled into Connect, ksqlDB, Flink, Spark and Debezium, and they
hardcode both the `/subjects/…` paths and the 5-byte magic-byte prefix. A Kafka estate can
therefore only be migrated by a registry that matches that API exactly.

Concordat targets RabbitMQ, where no such installed base exists. No RabbitMQ client
anywhere hardcodes Confluent's paths, because there has never been a reason to.

## Decision

Concordat exposes a native REST API at `/v1`, shaped around RabbitMQ's semantics. It does
not implement a Confluent-compatible endpoint surface, and does not accept the 5-byte
prefix as a first-class wire format.

## Alternatives considered

- **Clone the Confluent REST surface.** Rejected: it buys migration compatibility from an
  installed base that does not exist on RabbitMQ, while permanently constraining the API
  to topic-shaped concepts. Confluent's three subject strategies all presuppose a topic;
  RabbitMQ has exchanges, routing keys and queues, and the publisher and consumer name
  different things (see [ADR-011](011-subject-is-message-type.md)).
- **Dual surface — native plus Confluent-compatible.** Rejected for v1: two API surfaces
  means two sets of semantics to keep consistent, and the compatibility surface would be
  the one constraining every future change.

## Consequences

- **Positive:** the API can express `(vhost, exchange, routing key)` publish bindings and
  `(vhost, queue)` consume bindings, which have no Confluent equivalent. Error responses
  can use RFC 9457 with actionable JSON-Pointer paths instead of opaque numeric codes.
- **Negative:** no drop-in migration for a team moving from Kafka. ~~Reading messages from a
  Kafka bridge is supported only as a read-only legacy prefix format
  ([ADR-010](010-header-envelope.md)).~~ **Amended 2026-08-14** — see below.
- **Neutral:** cross-language reach comes from a committed OpenAPI document rather than
  from an existing serializer ecosystem ([ADR-019](019-language-neutral-protocol.md)).

## Amendment, 2026-08-14: there is no legacy-prefix read path either

**The Kafka-bridge consequence above promised a capability the product deliberately does not
have.** [ADR-010's amendment of the same date](010-header-envelope.md#amendment-2026-08-14-binary-framing-is-out-of-v1)
scoped both binary forms out of v1 — including read-only `0x00 | <int32 BE>` — because M2.5
measured `concordat-*` headers surviving every transport it could raise, so framing had nothing
left to carry. No magic-byte prefix is parsed anywhere in `src/`. (Carried across here
2026-09-24; the decision itself is ADR-010's.)

The decision this ADR records is untouched: it already refused the 5-byte prefix as a
first-class wire format, and the negative only got sharper. A team leaving Kafka re-publishes
through a Concordat client rather than bridging framed messages in. If ADR-010's reversal
condition is ever met — a transport that drops the header table *and* `content-type` — the
legacy read path returns with it.

## References

- [DESIGN §5](../DESIGN.md#5-api-surface-and-cross-language-strategy)
- [ADR-011](011-subject-is-message-type.md), [ADR-019](019-language-neutral-protocol.md)
