# ADR-021: Tier 2 SDKs are TypeScript/JavaScript, Python, Go and Java

- **Status:** Accepted
- **Date:** 2026-08-13
- **Deciders:** Rafael Camara

## Context

[ADR-019](019-language-neutral-protocol.md) makes an SDK a pure client of a documented
protocol, so adding or dropping a language costs the server nothing. What it does cost is
permanent maintenance: CI, releases, docs, dependency bumps and issue triage, forever.

The .NET SDK is Tier 1 only because that is where the server is written, not because it is
the largest audience.

## Decision

Tier 2 is TypeScript/JavaScript, Python, Go and Java, shipped in that order as a **gated
sequence** — one finished properly before the next begins, not a batch.

**TypeScript and JavaScript are one npm package, not two:** written in TypeScript,
published with ESM and CJS builds plus `.d.ts`, so a plain-JS consumer needs no TypeScript
toolchain and gets types free if they want them.

Bindings, all raw AMQP clients per [ADR-020](020-rabbitmq-client-only.md):

| SDK | AMQP | Validator |
|---|---|---|
| TS/JS | `amqplib` | `ajv` |
| Python 3.11+ | `pika` **and** `aio-pika` | `jsonschema` |
| Go | `rabbitmq/amqp091-go` | `santhosh-tekuri/jsonschema` |
| Java 21 | `com.rabbitmq:amqp-client` | `networknt/json-schema-validator` |

## Alternatives considered

- **Count TS and JS as two SDKs.** Rejected: it inflates the plan by a milestone of work
  that does not exist.
- **Drop Java.** Considered and initially taken, then reversed. The JVM RabbitMQ estate is
  large enough to matter, and under ADR-019 adding it is cheap.
- **Rust.** Not included. `crates.io/concordat` is taken, and no evidence of demand.
- **A single sync/async Python client.** Rejected: `pika` and `aio-pika` are different
  programming models, not a flag over one API.

## Consequences

- **Positive:** covers the practical majority of RabbitMQ traffic outside .NET. The gated
  sequence means Java at the back is a natural stopping point if reality intervenes, with
  no decision to revise.
- **Negative:** four more release pipelines and four more dependency surfaces, permanently.
- **Negative — the real hazard:** payload validation uses a *different third-party library
  in each language*. Draft coverage and edge-case behaviour differ, so the same message can
  pass in one language and fail in another with no bug on Concordat's part. Mitigations are
  mandatory before the first Tier 2 SDK ships: pin JSON Schema draft 2020-12, define the
  interoperable keyword subset and warn at registration outside it, and run a shared
  payload-validation corpus in every SDK's CI.
- **Negative:** the TS package must split in two — `@concordat/client` is isomorphic and
  browser-safe, `@concordat/amqp` is Node-only — or `amqplib` lands in browser bundles.

## References

- [DESIGN §5](../DESIGN.md#5-api-surface-and-cross-language-strategy)
- [M6 — Tier 2 SDKs](../plan/M6-sdks.md)
