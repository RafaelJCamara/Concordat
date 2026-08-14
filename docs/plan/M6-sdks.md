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
- [x] Expand the payload-validation corpus — **4 fixtures to 12, and every one of the 8 new
      cases failed on first run.** See below
- [x] Publish the five normative artifacts as a coherent set — [`docs/protocol/`](../protocol/README.md),
      ~1,400 lines. See below
- [ ] Surface portability findings in `concordat lint` — the offline path, and the one a
      pre-commit hook can afford. **Deferred to M6.2**, where the first non-.NET SDK makes the
      offline path matter; nothing about it is blocking

### Writing the protocol down found what reading it never did

The five artifacts existed; they were not a *set*. Two were unreadable to the audience ADR-019
names — the `concordatCode` catalogue was a C# file, and the envelope and canonicalisation
rules were prose scattered through DESIGN.md. An SDK author had nowhere to start.

`docs/protocol/` is now that entry point, and it states the authority order plainly: **prose
explains, the corpus decides.** The catalogue is *generated* from `ConcordatCodes.cs` and gated
in CI, joining the OpenAPI document — both were quietly wrong at some point precisely because
nothing compared the artifact to the thing it described.

**The exercise found a schema-id divergence that would have broken content addressing across
languages.** Canonicalisation escapes characters outside the Basic Multilingual Plane as
UTF-16 surrogate pairs with uppercase hex, while accented Latin and CJK pass through raw. A Go
or Python implementation emits the supplementary character raw as UTF-8, computes different
canonical bytes, and therefore a **different schema id for the same schema**. ADR-015 claims an
id is reproducible offline in any implementation; nothing pinned this, and every other fixture
in the corpus would still have passed. Two fixtures now pin it.

Three further gaps are recorded rather than papered over: payload framing that ADR-010 describes
and no code implements ([#19](../DECISIONS-PENDING.md)), Mode A and Mode B disagreeing about
whitespace ([#20](../DECISIONS-PENDING.md)), and `envelope_format_mismatch` published but never
emitted.

### The payload corpus found four real divergences, all in our own validator

The brief said the corpus is "the only thing that turns disagreement into a CI failure rather
than a support ticket". It did that immediately: eight new fixtures, **four failures**, all of
them NJsonSchema disagreeing with draft 2020-12. Every one would have shipped as a payload that
passes in .NET and is quarantined elsewhere, or the reverse — with no bug on any SDK author's
part.

| Divergence | NJsonSchema | The specification | Consequence |
|---|---|---|---|
| **Boolean subschemas** (`{"a": true}`) | fails to compile | `true` accepts all, `false` rejects all | **Severe.** An uncompilable schema is reported invalid, so a registerable schema quarantines *all its own traffic* |
| `maxLength`/`minLength` | counts UTF-16 code units | counts characters | An emoji fails `maxLength: 1` in .NET and passes in Python and Go |
| `enum` / `const` | coerces `"1"` to match `1` | JSON equality is type-strict | .NET accepts a payload every other SDK rejects |
| `uniqueItems` | compares serialised text | compares JSON values | `[{"a":1,"b":2},{"b":2,"a":1}]` is two spellings of one value and must violate uniqueness |

All four are corrected in `Draft202012Corrections`, following the precedent M2 set for
`integer`: **the corpus is normative and the library is not.** The corrections never touch the
canonical text or its hash — a correction that changed a schema id would be a worse bug than
the one it fixed.

The boolean-subschema fix rewrites `true` → `{}` and `false` → `{"not":{}}` before compiling,
and only in genuine schema positions. `{"uniqueItems": true}` is a boolean *keyword*, not a
subschema, and rewriting it would corrupt the schema — which is why the keyword sets are
enumerated rather than inferred.

**The two over-permissive cases could not be fixed by filtering**, unlike `integer` and the
length bounds: there is no error to drop, so the violation has to be found. That walk covers
`properties`, `items` and `prefixItems` and stops at the applicator keywords — exactly where
`JsonSchemaPortabilityChecker` starts warning, so the boundary is one line rather than two.

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
