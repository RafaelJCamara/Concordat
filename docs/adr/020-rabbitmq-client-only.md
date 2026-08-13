# ADR-020: v1 ships one .NET SDK, over RabbitMQ.Client only

- **Status:** Accepted
- **Date:** 2026-08-13
- **Deciders:** Rafael Camara

## Context

.NET RabbitMQ traffic is spread across a raw client and five service buses: MassTransit,
EasyNetQ, Wolverine, NServiceBus and Rebus. All five were researched against current
library source — publish hook, whether a throw blocks, consume hook, and raw AMQP property
access — and all five have a usable integration point. That research is preserved in
[DESIGN Appendix A](../DESIGN.md#appendix-a--framework-adapter-research-deferred-adr-020).

Supporting all six at once means absorbing six sets of reject-path semantics, six header
conventions and six subject-resolution rules before the envelope has been validated
against a single real deployment.

## Decision

v1 ships `Concordat.Client` plus `Concordat.Messaging.RabbitMq` — raw RabbitMQ.Client
only. Service-bus adapters are deferred, with their research retained rather than
discarded. The same rule applies in every language: each SDK binds its language's raw AMQP
client, which puts **Spring AMQP** in the deferred set alongside MassTransit.

## Alternatives considered

- **Ship MassTransit support in v1** — it has the largest .NET installed base. Rejected on
  sequencing: the envelope should be proven against the substrate before being adapted to
  a framework that mediates it. MassTransit remains first in line when adapters resume.
- **Ship all six.** Rejected: six reject-path behaviours to get right, and any accident
  baked into the wire format then has six consumers before a second language exists —
  precisely the risk [ADR-019](019-language-neutral-protocol.md) exists to avoid.

## Consequences

- **Positive:** raw RabbitMQ.Client has unrestricted AMQP access in both directions, so it
  exercises the full envelope with nothing mediating it. It is also the substrate all five
  service buses sit on, so nothing learned is wasted.
- **Positive:** drops the MassTransit licensing constraint out of the v1 dependency
  surface. v9 is commercially licensed (Massient); v8.5.x is the last Apache-2.0 release,
  and the adapter would need to target `MassTransit.Abstractions` 8.5.x to satisfy
  [ADR-009](009-apache-2-including-cloud.md).
- **Negative:** teams on a service bus — the majority of .NET RabbitMQ users — get nothing
  in v1 beyond the CLI and CI gate.
- **Negative, and sharpest in Java:** Spring AMQP is a large share of Java's RabbitMQ
  estate, so the Java SDK reaches less of its language than the other three. Spring AMQP
  therefore outranks all the .NET adapters if the deferred set is resumed.

## References

- [DESIGN §6](../DESIGN.md#6-client-sdk-design--rabbitmqclient-only-adr-020)
- [Appendix A](../DESIGN.md#appendix-a--framework-adapter-research-deferred-adr-020) — the preserved hook table
