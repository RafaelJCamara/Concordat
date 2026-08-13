# M6 — Tier 2 SDKs

**Depends on:** [M5](M5-formats.md) · **Design refs:** [§5](../DESIGN.md#5-api-surface-and-cross-language-strategy), decisions 019, 020, 021

**A gated sequence, not a batch.** Ship one SDK properly, look at adoption, then start the
next. Java at the back is the natural stopping point if reality intervenes — no decision
to revise, just a queue that stops advancing.

---

## M6.1 Protocol freeze and interop prerequisites

**🔴 Heavy · do this before the first SDK · in progress**

Do this before the first SDK, not during it.

- [x] **Pin JSON Schema draft 2020-12** as the only supported dialect — refused with
      `schema_dialect_unsupported`, the one error-severity portability finding
- [x] Define the **interoperable keyword subset**; warn at registration when a schema strays
      outside it — `ISchemaPortabilityChecker` + `JsonSchemaPortabilityChecker`, 25 tests.
      Settles what was [DECISIONS-PENDING #9](../DECISIONS-PENDING.md#settled)
- [x] **Audit the API for CLR-shaped leakage** — done, and it found something worse than
      leakage. See below
- [ ] Publish the five normative artifacts as a coherent set (OpenAPI, envelope spec, canonicalisation rules, `concordatCode` catalogue, conformance corpus)
- [ ] Expand the payload-validation corpus — five independent validators (`ajv`, `jsonschema`, `santhosh-tekuri`, `networknt`, .NET) disagree at the edges, and this is the only thing that turns that into a CI failure rather than a support ticket
- [ ] Surface portability findings in `concordat lint` — the offline path, and the one a
      pre-commit hook can afford

### The OpenAPI document described no responses at all

The CLR-leakage audit found no assembly-qualified names and no `System.*` strings. It found
this instead, which is worse: **`docs/api/openapi.v1.json` contained seven schemas, all of them
requests.** Not one response shape, and no `ProblemDetails`. Every handler returns `IResult`,
which is opaque to the generator, and no endpoint carried `.Produces<T>()`.

ADR-019's acceptance test is *"a team writes a complete Go client from those five documents
without reading a line of C#, and hits no surprises."* That was not achievable. You could learn
what to send and nothing about what comes back — including which `concordatCode` values an
endpoint can return, which is the thing clients are supposed to branch on.

It had been invisible because the drift gate compares the generated document against the
committed one, and both were equally empty. **A gate that verifies an artifact is unchanged
cannot notice that the artifact was never complete.**

All 19 endpoints now declare their response types and status codes. The document went from
**7 schemas to 23**. The 200-vs-201 split on registration is spelled out, because they are both
success and mean different things — 200 is the idempotent re-registration of the tip, where no
ordinal was allocated, and a client treating them alike double-counts versions.

### Portability is warnings, with one exception

The schemas this flags are legal and usually intentional, so refusing them would be the
Confluent mistake in the other direction. Three findings:

| Kind | Severity | Why it exists |
|---|---|---|
| `dialect_unsupported` | **Error** | Keywords changed meaning between drafts; Concordat would be applying rules the author never wrote against |
| `keyword_not_compared` | Warning | The keyword validates but the compatibility engine cannot see it, so a change confined to it reads as compatible |
| `regex_not_portable` | Warning | Go's RE2 has no lookaround or backreferences **at all**, so a Go consumer fails to build the validator rather than disagreeing with it |

The regex one is the sharpest: it is not "behaves differently in Go", it is "the payload check
is lost entirely on that SDK", and no corpus can fix it because the schema never compiles there.

## M6.2 TypeScript / JavaScript

- [ ] `@concordat/client` — isomorphic REST + cache, browser-safe
- [ ] `@concordat/amqp` — Node-only middleware over `amqplib` (**separate package**, so `amqplib` never enters a browser bundle)
- [ ] `ajv` validation, shared behaviour with the Angular app
- [ ] ESM + CJS builds + `.d.ts`; a plain-JS consumer needs no TypeScript toolchain
- [ ] Conformance corpus in CI; publish to npm

## M6.3 Python

- [ ] `concordat-client`, Python 3.11+
- [ ] `pika` adapter (sync)
- [ ] `aio-pika` adapter (async) — **a separate programming model, not a flag**
- [ ] `jsonschema` validation
- [ ] Conformance corpus in CI; publish to PyPI

## M6.4 Go

- [ ] `concordat-go` over `rabbitmq/amqp091-go`
- [ ] `santhosh-tekuri/jsonschema`
- [ ] Fail-open/closed and reject paths as **error returns** — expect this to surface places where the corpus specified .NET control flow rather than behaviour; fix the corpus, not just the client
- [ ] Conformance corpus in CI; publish module

## M6.5 Java

- [ ] `io.github.rafaeljcamara:concordat-client`, Java 21 LTS, over `com.rabbitmq:amqp-client`
- [ ] `networknt/json-schema-validator`
- [ ] Conformance corpus in CI; publish to Maven Central
- [ ] **Known gap:** Spring AMQP is deferred ([Appendix A](../DESIGN.md#appendix-a--framework-adapter-research-deferred-adr-020)), so this reaches a minority of Java's RabbitMQ estate. Budget the Spring adapter alongside it or expect the SDK to under-deliver.

---

## Exit — per SDK, not per milestone

The conformance corpus passes unmodified, and a quickstart written from the published docs
alone — no reading of Concordat's C# — works end to end.

---

← [M5 — Formats](M5-formats.md) · [Plan index](../PLAN.md) · [M7 — Governance →](M7-governance.md)
