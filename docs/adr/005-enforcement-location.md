# ADR-005: Enforcement lives in client middleware and CI checks

- **Status:** Accepted
- **Date:** 2026-08-13
- **Deciders:** Rafael Camara

## Context

Enforcement has to happen somewhere. Four candidate locations were investigated against
RabbitMQ's actual extension points, not against how they ought to work.

- `rabbit_msg_interceptor` (4.x, all protocols): signature is
  `intercept(mc:state(), context(), stage(), config()) -> mc:state()`. **There is no error
  tuple.** It can observe and annotate; it cannot reject.
- `rabbit_channel_interceptor` (0-9-1 only): can reject, but `check_no_overlap/1` permits
  **one interceptor per AMQP method broker-wide**, so Concordat would conflict with any
  other `basic.publish` plugin. Only new channels pick it up, and rejection kills the
  channel rather than nacking. It also means shipping Erlang from a .NET product.
- Inline AMQP proxy: genuinely enforcing, but requires a 0-9-1 frame codec and inserts a
  new availability-critical hop.
- Client middleware plus CI: voluntary, but portable and needs no infrastructure.

## Decision

Enforcement is implemented as client SDK middleware on publish and consume, plus
compatibility checks at CI time via the `concordat` CLI. No broker plugin, no proxy in v1.

## Alternatives considered

All three above, rejected for the reasons stated. The proxy is deferred rather than
refused — it is the only option that is universally enforcing, and revisiting it after v1
is reasonable if demand justifies the availability cost.

## Consequences

- **Positive:** zero infrastructure. Works on managed RabbitMQ where plugins cannot be
  installed — CloudAMQP, Amazon MQ — which is a large share of the market.
- **Negative, and to be documented publicly:** enforcement is opt-in. Concordat cannot
  stop a publisher that does not use an SDK. The mitigation is CI-time checks plus
  registered-consumer impact analysis, not a broker gate.
- **Neutral:** the bar to beat is low. Confluent's own broker-side validation does not
  introspect payloads either — it only checks that the ID in the prefix is registered.

## References

- [DESIGN §1](../DESIGN.md#1-why-rabbitmq-is-harder-than-kafka--and-where-enforcement-can-live)
- [ADR-020](020-rabbitmq-client-only.md) — which client libraries carry the middleware
