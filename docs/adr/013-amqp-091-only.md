# ADR-013: AMQP 0-9-1 only in v1, designed to survive 1.0 conversion

- **Status:** Accepted
- **Date:** 2026-08-13
- **Deciders:** Rafael Camara

## Context

RabbitMQ speaks AMQP 0-9-1, AMQP 1.0, MQTT and STOMP, and additionally offers Streams
through a separate protocol and client. Supporting all of them in v1 would multiply the
envelope, the middleware and the test matrix before any of it is proven once.

AMQP 0-9-1 is where the overwhelming majority of RabbitMQ traffic and tooling lives.

## Decision

v1 supports AMQP 0-9-1 only. The envelope is designed so that 1.0 support is additive
rather than a rewrite: **no header uses the `x-` prefix**, because RabbitMQ converts
`x-`-prefixed 0-9-1 headers into AMQP 1.0 *message-annotations*, while all others become
*application-properties* — which is where application metadata belongs.

## Alternatives considered

- **Support 1.0 in v1 too.** Rejected: doubles the middleware surface before the envelope
  has been validated against a single real deployment.
- **Support Streams.** Rejected: Streams use a separate client and are not touched by any
  broker interceptor, so it is a genuinely separate integration rather than another hook.
- **Ignore 1.0 entirely and use whatever headers are convenient.** Rejected: that is
  precisely the choice that would make 1.0 support a breaking envelope change later.

## Consequences

- **Positive:** a much smaller v1, and the 1.0 path stays open at essentially no cost —
  the only price is a naming constraint.
- **Negative, and important:** the 1.0-safety claim is currently **an assertion, not a
  verified property.** It rests entirely on RabbitMQ's documented conversion behaviour.
  M2.5 tests it directly by reading a Concordat-published message with a 1.0 client and
  confirming the headers arrive as application-properties.
- **Negative:** MQTT and STOMP users get nothing in v1, and Streams users get nothing at
  all until the separate client is addressed.

## References

- [DESIGN §2](../DESIGN.md#2-the-concordat-envelope-adr-010)
- [ADR-010](010-header-envelope.md) — the `x-` constraint this depends on
- [M2.5](../plan/M2-dotnet-client.md#m25-header-survival-experiments--design-2) — where the claim gets verified
