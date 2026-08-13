# ADR-010: Header envelope — no `x-` prefix, all values UTF-8 strings

- **Status:** Accepted
- **Date:** 2026-08-13
- **Deciders:** Rafael Camara

## Context

Kafka had no message headers until 0.11, which is *why* Confluent invented magic-byte
payload framing. AMQP 0-9-1 has carried `type`, `content-type`, `app-id` and an arbitrary
`headers` field table since 2008. Concordat does not have to mutate payloads.

Three constraints then force the exact shape:

1. RabbitMQ converts AMQP 0-9-1 headers beginning with `x-` into AMQP **1.0
   message-annotations**, while all others become application-properties — which is where
   application metadata belongs and where CloudEvents puts it. `x-` is also reserved by
   RabbitMQ itself: `x-death`, `x-delay`, `x-delivery-count`, `x-stream-filter-value`.
2. NServiceBus and Rebus expose headers as `Dictionary<string,string>` and physically
   cannot carry an integer.
3. RabbitMQ.Client writes a `string` with field-table tag `S` and reads it back as
   `byte[]` — by design, permanently.

## Decision

**Mode A (default):** identity travels in headers, payload untouched — `concordat-v`
(required), `concordat-schema-id` (required), `concordat-subject`, `concordat-version`,
`concordat-semver`, `concordat-format`. No `x-` prefix. All values UTF-8 strings, and
consumers must UTF-8-decode on read.

**Mode B (opt-in):** payload framing for paths where headers may not survive — preferred
form is a `content-type` token, `application/json+concordat.v1.<hex-id>`.

## Alternatives considered

- **Magic-byte prefix as the default**, as Confluent does. Rejected: it mutates the
  payload, so every existing consumer breaks on day one and incremental adoption is
  impossible. Retained as opt-in Mode B, plus read-only support for the legacy
  `0x00 | <int32 BE>` layout so messages from a Kafka bridge can be ingested.
- **Unversioned framing**, as Azure's `avro/binary+{id}` does. Rejected: it cannot evolve.
  `concordat-v` and the `v1` token exist precisely so the envelope can change later.
- **Typed header values.** Rejected: see constraint 2. Also avoid `ulong` (unsupported),
  `bool false` (silently dropped by MassTransit's `SetHeaders`) and values over 64 KiB
  (Rebus truncates).

## Consequences

- **Positive:** a consumer with no Concordat client still reads plain JSON, so adoption is
  incremental and reversible.
- **Positive:** avoiding `x-` is what makes [ADR-013](013-amqp-091-only.md)'s 1.0-safety
  claim real rather than aspirational.
- **Negative:** headers may not survive every hop. Whether they survive dead-lettering,
  shovel, federation and the STOMP/MQTT adapters is **unverified** and is empirical work
  in M2.5; the result determines the documented Mode A versus Mode B guidance.
- **Negative:** string-only values mean version numbers are parsed on read.

## References

- [DESIGN §2](../DESIGN.md#2-the-concordat-envelope-adr-010)
- [M2.5 — Header survival experiments](../plan/M2-dotnet-client.md#m25-header-survival-experiments)
