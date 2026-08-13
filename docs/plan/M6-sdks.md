# M6 — Tier 2 SDKs

**Depends on:** [M5](M5-formats.md) · **Design refs:** [§5](../DESIGN.md#5-api-surface-and-cross-language-strategy), decisions 019, 020, 021

**A gated sequence, not a batch.** Ship one SDK properly, look at adoption, then start the
next. Java at the back is the natural stopping point if reality intervenes — no decision
to revise, just a queue that stops advancing.

---

## M6.1 Protocol freeze and interop prerequisites 🔴

Do this before the first SDK, not during it.

- [ ] Publish the five normative artifacts as a coherent set (OpenAPI, envelope spec, canonicalisation rules, `indentureCode` catalogue, conformance corpus)
- [ ] **Pin JSON Schema draft 2020-12** as the only supported dialect
- [ ] Define the **interoperable keyword subset**; warn at registration when a schema strays outside it
- [ ] Expand the payload-validation corpus — five independent validators (`ajv`, `jsonschema`, `santhosh-tekuri`, `networknt`, .NET) disagree at the edges, and this is the only thing that turns that into a CI failure rather than a support ticket
- [ ] Audit the API for CLR-shaped leakage: no assembly-qualified names, no `System.*` type strings

## M6.2 TypeScript / JavaScript

- [ ] `@indenture/client` — isomorphic REST + cache, browser-safe
- [ ] `@indenture/amqp` — Node-only middleware over `amqplib` (**separate package**, so `amqplib` never enters a browser bundle)
- [ ] `ajv` validation, shared behaviour with the Angular app
- [ ] ESM + CJS builds + `.d.ts`; a plain-JS consumer needs no TypeScript toolchain
- [ ] Conformance corpus in CI; publish to npm

## M6.3 Python

- [ ] `indenture-client`, Python 3.11+
- [ ] `pika` adapter (sync)
- [ ] `aio-pika` adapter (async) — **a separate programming model, not a flag**
- [ ] `jsonschema` validation
- [ ] Conformance corpus in CI; publish to PyPI

## M6.4 Go

- [ ] `indenture-go` over `rabbitmq/amqp091-go`
- [ ] `santhosh-tekuri/jsonschema`
- [ ] Fail-open/closed and reject paths as **error returns** — expect this to surface places where the corpus specified .NET control flow rather than behaviour; fix the corpus, not just the client
- [ ] Conformance corpus in CI; publish module

## M6.5 Java

- [ ] `io.github.rafaeljcamara:indenture-client`, Java 21 LTS, over `com.rabbitmq:amqp-client`
- [ ] `networknt/json-schema-validator`
- [ ] Conformance corpus in CI; publish to Maven Central
- [ ] **Known gap:** Spring AMQP is deferred ([Appendix A](../DESIGN.md#appendix-a--framework-adapter-research-deferred-adr-020)), so this reaches a minority of Java's RabbitMQ estate. Budget the Spring adapter alongside it or expect the SDK to under-deliver.

---

## Exit — per SDK, not per milestone

The conformance corpus passes unmodified, and a quickstart written from the published docs
alone — no reading of Indenture's C# — works end to end.

---

← [M5 — Formats](M5-formats.md) · [Plan index](../PLAN.md) · [M7 — Governance →](M7-governance.md)
