# ADR-011: The subject is the message type, resolved by a pluggable `ISubjectResolver`

- **Status:** Accepted
- **Date:** 2026-08-13
- **Deciders:** Rafael Camara

## Context

This is the hardest problem in the product. In RabbitMQ, **the publisher and the consumer
name different things.** A producer knows `(exchange, routing key)`; a consumer knows
`(queue)`. The binding between them is declared by whoever owns the queue and can change
with neither side redeploying.

Worse: one queue receives many message types — a binding `orders.#` delivers
`orders.created`, `orders.cancelled` and `orders.shipped` to the same queue, so "one schema
per queue" is simply wrong. Routing keys are high-cardinality and dynamic, so they cannot
each be a subject. Alternate, dead-letter and exchange-to-exchange bindings change the
effective routing key in flight.

No existing standard solves this. Confluent's three subject strategies all presuppose a
topic. AsyncAPI's AMQP binding has no field for it. xRegistry has no `AMQP/0.9.1` protocol
value at all.

## Decision

The subject is the **fully-qualified message type name** — the only identifier both sides
possess. It is obtained through a pluggable `ISubjectResolver`, because no two client
libraries put the type in the same place.

**The resolver runs on the publish side only.** The envelope then carries
`concordat-subject` and `concordat-schema-id`, so a consumer never re-derives anything: it
reads the subject off the message and validates against its `ConsumeBinding`.

Canonical form is `^[A-Za-z0-9_]+(\.[A-Za-z0-9_]+)*$` — dot-separated segments,
deliberately not a CLR type name. Assembly, version, culture and public-key-token are
always stripped.

## Alternatives considered

- **Subject = queue.** Rejected: one queue carries many types.
- **Subject = exchange + routing key.** Rejected: high-cardinality and dynamic, and the
  consumer does not know it. Retained as an opt-in strategy for topologies where it fits.
- **Resolve on both sides.** Rejected: it requires publisher and consumer to agree on a
  derivation they cannot both compute. Publish-side-only resolution is what makes the
  asymmetry disappear.

## Consequences

- **Positive:** the asymmetry is solved without requiring either side to learn the other's
  vocabulary. The `Contract` aggregate bridges the two naming worlds explicitly.
- **Positive:** the grammar carries no language's type system, so a Python publisher
  writing `acme.orders.OrderCreated` into `properties.type` is a first-class citizen
  ([ADR-019](019-language-neutral-protocol.md)).
- **Negative:** a publisher that sets no type gets no subject. `properties.type` is
  optional in AMQP, so brownfield estates may need `concordat infer`
  ([ADR-014](014-infer-for-brownfield.md)) and explicit configuration.
- **Negative:** each new client library needs a resolver written against its conventions.

## References

- [DESIGN §3](../DESIGN.md#3-subject-naming-adr-011)
- [Appendix A](../DESIGN.md#appendix-a--framework-adapter-research-deferred-adr-020) — per-framework resolution research
