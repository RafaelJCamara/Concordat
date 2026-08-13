# Concordat — A Schema Registry for RabbitMQ

> **Status: design, not yet implemented.** This document records the architecture and the
> decisions behind it. No code exists yet; see [Milestones](#11-milestones) for the
> intended build order.

## Context

**The problem.** Kafka teams get schema governance free via Confluent Schema Registry:
producers and consumers agree on a versioned contract, and incompatible changes are
rejected before production. RabbitMQ has no equivalent. Teams ship untyped JSON,
discover breaking changes at runtime, and have no central answer to *"what flows through
this broker, and who owns it?"*

**The goal.** Concordat: a schema registry and contract-enforcement platform for RabbitMQ,
built in .NET but usable from any language.

**Two flavours, one codebase:** self-hosted and Concordat Cloud (multi-tenant SaaS).

A React prototype (Vite + shadcn/ui, maintained separately) sketches the intended UX and
serves as the design reference for the Angular port described in §9.

**Toolchain.** .NET SDK 10 (`net10.0`), Node 22+, Angular CLI 21+ (Angular 22 target),
Docker with Compose v2+.

**Market check.** The category is empty. No product does runtime payload enforcement on
RabbitMQ AMQP 0-9-1. Searching NuGet for `schema registry rabbitmq` returns one obscure
193-download package. The two vendors owning commercial RabbitMQ — Broadcom/Tanzu and
CloudAMQP — ship *zero* payload governance. Everyone doing real enforcement (Kong Event
Gateway, the late Bufstream, Confluent's own broker validation) is Kafka-protocol-only.
The one AsyncAPI broker-side validator that ever existed (`asyncapi/event-gateway`) was
Kafka-only and was archived in 2024.

---

## Decisions

Each of these should be expanded into an ADR under `docs/adr/`.

| # | Decision | Rationale |
|---|---|---|
| 001 | **Native API only** — no Confluent wire-compat layer | RabbitMQ semantics don't fit Kafka's topic-shaped API. Redpanda, Apicurio and Karapace all clone Confluent's REST surface because Confluent's **Apache-2.0** serializers are compiled into Connect, ksqlDB, Flink, Spark and Debezium and hardcode both the `/subjects/…` paths and the 5-byte prefix — so a Kafka estate can only be migrated by matching that API. **No such installed base exists on RabbitMQ**, so that lock-in doesn't transfer. |
| 002 | **Three formats in v1**: JSON Schema, Avro, Protobuf | JSON Schema is the RabbitMQ norm; Avro has the best-specified compatibility rules; Protobuf carries the polyglot story. |
| 003 | **Monorepo** — backend, web, CLI, SDKs, deploy assets, docs | Atomic cross-cutting changes, one CI pipeline, one version. |
| 004 | **Version identity = integer ordinal + optional semver label** | The integer is canonical, immutable, totally ordered. The semver label carries intent and Concordat *verifies* it — a breaking change cannot be labelled MINOR. |
| 005 | **Enforcement = client SDK middleware + CI-time checks** | The only viable path — see §1. |
| 006 | **Angular port keeps the prototype's design system, rebuilds the code** | Its `index.css` token set is framework-agnostic and ports verbatim; the component and state layer is rebuilt with real boundaries. |
| 007 | **PostgreSQL + EF Core** | Deliberately *not* the Confluent/Karapace/Redpanda pattern of storing schemas in a broker log. Apicurio's PostgreSQL backend is the precedent. |
| 008 | **Built-in identity + scoped API keys, OIDC optional** | No third-party dependency required to run Concordat. |
| 009 | **Fully open source (Apache-2.0)**, including Cloud | **Accepted risk:** a third party could host the same code; Cloud competes on operations and iteration speed. *Upside:* Confluent's server is under the Confluent Community License, and **all authorization — RBAC and subject ACLs — is separately Enterprise-licensed**, so free-tier Confluent SR has no authz at all; anyone with credentials can mutate or delete any subject. Concordat shipping RBAC, API-key scopes and audit under Apache-2.0 is a concrete differentiator. |
| 010 | **Header envelope: no `x-` prefix, all values UTF-8 strings** | Forced by AMQP conversion rules and .NET client behaviour — see §2. |
| 011 | **Subject = message type, via a pluggable `ISubjectResolver`** | The only identifier both sides share. See §3. |
| 012 | **Environment is a logical label over registered brokers** | Handles "prod is its own cluster" and "prod is a vhost" without forcing either. See §4. |
| 013 | **AMQP 0-9-1 only in v1, designed to survive 1.0 conversion** | Streams and 1.0 become additive, not a rewrite. |
| 014 | **`concordat infer` for brownfield onboarding** | Turns a 200-message-type estate from weeks of authoring into an afternoon. |
| 015 | **Schema IDs are content-addressed**, not sequential | Same schema ⇒ same ID in every environment and install, so promotion never invalidates an in-flight envelope. Registration becomes idempotent via a unique constraint; no single-writer counter. Confluent's own `IncrementalIdGenerator` needs a collision-retry loop that can hard-fail, and **CP 8.1+ retrofitted a content-derived GUID carried in a header**. Glue, Azure and Buf converged here independently. |
| 016 | **Two-axis compatibility**: *who breaks* × *what breaks* | BACKWARD/FORWARD/FULL answers who; WIRE / WIRE_JSON / SOURCE answers what. Confluent cannot express "`int32→int64` is wire-safe but source-breaking" at all. |
| 017 | **Breaking changes register but gate the `latest` label** | Buf's pattern. CI never wedges, the proposed schema is a reviewable artifact, and consumers pinned to `latest` are unaffected until approval. |
| 018 | **Schema editing in the web app is admin-only** | Registering a version is a governance action with blast radius across every registered consumer, not a routine edit. Non-admins get the full read surface — browse, diff, impact, export — and no write affordance. **The UI is not the boundary:** it reflects a server-side `subject:write` scope check (§5, Context E), because the same endpoints are reachable from the CLI, the SDKs and curl. See §9. |
| 019 | **The registry is a language-neutral HTTP service; every SDK is an ordinary client of the same public protocol** | The server happens to be .NET. Nothing about the protocol may assume it. No capability exists that isn't reachable over documented REST — the .NET SDK gets no privileged endpoint, no private header, no serialisation shortcut, no behaviour that isn't written down. The **normative artifacts** are the OpenAPI document, the envelope spec (§2), the canonicalisation rules (§4), the `concordatCode` catalogue and the conformance corpus (§12) — all language-neutral and versioned with the API. Acceptance test for the principle: a team writes a complete Go client from those five documents without reading a line of C#, and hits no surprises. See §5. |
| 020 | **v1 ships one .NET SDK, over RabbitMQ.Client only** | Service-bus adapters (MassTransit, EasyNetQ, Wolverine, NServiceBus, Rebus) are deferred; the hook research is preserved in **Appendix A**. Raw RabbitMQ.Client has unrestricted AMQP access both directions, so it exercises the envelope with nothing mediating it, and it is the substrate all five sit on. Absorbing five libraries' reject-path and header quirks *before* a second-language client exists would risk hardening .NET accidents into the wire format — the opposite of ADR-019. |
| 021 | **Tier 2 SDKs: TypeScript/JavaScript, Python, Go, Java** | TS and JS are **one npm package**, not two — written in TypeScript, published with ESM + CJS builds and `.d.ts`, so a plain-JS consumer needs no TypeScript toolchain and gets types for free if they want them. Every SDK binds its language's **raw** AMQP client only; framework adapters are deferred uniformly for ADR-020's reason, which puts **Spring AMQP** in Appendix A alongside MassTransit. Order, bindings and the validator-divergence hazard: §5. |
| 022 | **Named Concordat. Signet, Hutch, Syngraph and Stipula were rejected first** | A concordat *is* a formal agreement between two parties — no metaphor to unpack — and it is unclaimed on NuGet, PyPI, the `@concordat` npm scope and `.dev`/`.io`/`.sh`. The highest-starred GitHub repo bearing the name has **zero** stars. `crates.io/concordat` is taken and irrelevant: ADR-021 ships no Rust SDK. Maven uses `io.github.rafaeljcamara`, so no domain sits on the critical path. **Rejected, recorded so they don't resurface:** *Signet* — `Signet.Client` (NuGet) and `signet-client` (PyPI) are published by an **active** project, `bytepunx/signet-proto`, PyPI upload 2026-08-08 — the exact two package names ADR-021 depends on, in the same registries, for the same polyglot-client shape; NuGet's `signet` id is separately held by **SigNET** (7,452 downloads; ids are case-insensitive) and `signet.dev` is registered. *Hutch* — `ruby-amqp/hutch` (878 stars) is "a system for processing messages from RabbitMQ": same ecosystem, strictly worse than Signet. *Syngraph / Chirograph* — clean on every registry, but `-graph` reads as graph database or GraphQL to this audience. *Stipula* — the best metaphor of the lot (the straw broken in two to seal a bargain), but `stipula-language/stipula` is a DSL for legal contracts. **The transferable lesson: registry availability is not the test — ecosystem collision is.** Signet and Hutch were both free on NuGet. |

### Non-goals for v1
- **No inline AMQP proxy** — universal enforcement, but a new availability-critical hop.
- **No Erlang broker plugin** — see §1; the hook that can reject is crippled.
- **No passive firehose observer** — revisit after v1.
- **No RabbitMQ Streams, MQTT or STOMP support** — Streams use a separate client and are not touched by any broker interceptor.
- **No service-bus framework adapters** (ADR-020) — RabbitMQ.Client only. Appendix A holds the research for when they return.
- **No test-time contract-testing framework.** Microcks (free, OSS, real AMQP 0-9-1 support), Specmatic and PactNet 5.x cover this. Don't rebuild it.

---

## 1. Why RabbitMQ is harder than Kafka — and where enforcement can live

**Harder:**

1. **Publisher and consumer name different things.** A producer knows `(exchange, routing key)`; a consumer knows `(queue)`. The binding is declared by whoever owns the queue and can change with neither side redeploying. Confluent's three subject strategies all presuppose a topic; AsyncAPI's AMQP binding has no field for it; xRegistry has no `AMQP/0.9.1` protocol value. **Resolved in ADR-011 — see §3.**
2. **One queue receives many message types.** A binding `orders.#` delivers `orders.created`, `orders.cancelled` and `orders.shipped` to one queue. "One schema per queue" is wrong.
3. **Routing keys are high-cardinality and dynamic.** They cannot each be a subject.
4. **Alternate/dead-letter/exchange-to-exchange bindings** change the effective routing key in flight.

**Easier — the design lever:** AMQP 0-9-1 has carried rich per-message metadata since 2008 (`type`, `content-type`, `app-id`, plus an arbitrary `headers` field table). Kafka had no headers until 0.11, which is *why* Confluent invented magic-byte payload framing. **Concordat does not have to mutate payloads.**

### Where enforcement can live

| Option | Verdict |
|---|---|
| **Client SDK middleware** | ✅ **Chosen.** Every major .NET library has a usable hook (§6). Voluntary, but portable and zero-infrastructure. |
| **CI-time checks** | ✅ **Chosen.** Catches breakage before deploy. Language-agnostic via CLI. |
| `rabbit_msg_interceptor` (4.x, all protocols) | ❌ **Cannot reject.** Signature is `intercept(mc:state(), context(), stage(), config()) -> mc:state()` — no error tuple exists. Observe-and-annotate only. |
| `rabbit_channel_interceptor` (0-9-1 only) | ❌ Can reject, but `check_no_overlap/1` allows **one interceptor per AMQP method broker-wide** — Concordat would conflict with any other `basic.publish` plugin. Only new channels pick it up, and rejection **kills the channel** rather than nacking. Plus shipping Erlang from a .NET product. |
| Inline AMQP proxy | ❌ Deferred. Genuinely enforcing (the Kong pattern), but requires a 0-9-1 frame codec and becomes availability-critical. |

> **Honest limitation, to be documented publicly:** client-side enforcement is opt-in.
> Concordat cannot stop a publisher that doesn't use an SDK. The mitigation is CI-time checks
> plus registered-consumer impact analysis, not a broker gate. Note **Confluent's own
> broker-side validation doesn't introspect data either** — it only checks that the ID in
> the prefix is registered. That's the bar to beat, and it's low.

---

## 2. The Concordat Envelope (ADR-010)

**Mode A — header binding (default).** Payload untouched, so a consumer with no Concordat
client still reads plain JSON and adoption is incremental.

```
properties.type          = "acme.orders.OrderCreated"
properties.content-type  = "application/json"
headers["concordat-v"]         = "1"      # REQUIRED — envelope version
headers["concordat-schema-id"] = "7f3a9c2ea1b84d5c9e07f2b3c4d5e6b4"   # REQUIRED
headers["concordat-subject"]   = "acme.orders.OrderCreated"
headers["concordat-version"]   = "3"
headers["concordat-semver"]    = "2.0.0"  # optional
headers["concordat-format"]    = "json" | "avro" | "protobuf"
```

`concordat-schema-id` is the **content-addressed ID (ADR-015)**: the canonical-form hash
truncated to 128 bits, lowercase hex. Identical schemas produce identical IDs across
every environment and every Concordat install, so a message published in `staging` stays
valid after the subject is promoted to `prod`. `concordat-v` exists so the envelope can
evolve — Azure's unversioned `avro/binary+{id}` scheme is a documented dead end for
exactly this reason.

Three constraints force this exact shape:

1. **No `x-` prefix.** RabbitMQ converts AMQP 0-9-1 headers beginning with `x-` into AMQP **1.0 message-annotations**, while all others become application-properties — which is where app metadata belongs and where CloudEvents puts it. `x-` is also reserved by RabbitMQ itself (`x-death`, `x-delay`, `x-delivery-count`, `x-stream-filter-value`). This is what makes ADR-013's "1.0-safe" claim real.
2. **All values are strings.** The envelope is a cross-language wire contract (ADR-019), and a string-only field table is the lowest common denominator every AMQP client in every language can carry — no int64 ambiguity, no per-language numeric coercion, nothing to spec twice. It also keeps the deferred adapters admissible without an envelope version bump: NServiceBus and Rebus expose headers as `Dictionary<string,string>` and physically cannot carry an int.
3. **Consumers must UTF-8 decode.** RabbitMQ.Client writes a `string` with field-table tag `S` and reads it back as **`byte[]`** — by design, permanently. Also avoid `ulong` (unsupported), `bool false` (silently dropped by MassTransit's `SetHeaders`), and values >64 KiB (Rebus truncates).

**Mode B — payload framing (opt-in)**, for paths where headers may not survive.
**Every framing carries an explicit version byte or version token** — Glue ships a
version byte plus a `secondaryDeserializer` hook and can evolve; Azure's unversioned
`avro/binary+{id}` cannot, and is the cautionary example.

- `content-type: application/json+concordat.v1.<hex-id>` — the Azure approach *with the
  version defect fixed*. No payload mutation, so the body stays readable by any tool.
  AMQP 0-9-1 `content_type` is a `shortstr` (255 bytes); a version token plus a 32-char
  hex id fits easily. **The better default of the two.**
- `0x01 | <16-byte content-addressed id> | payload` — matches the shape Confluent
  adopted in CP 8.1+. Opt-in interop only; it mutates the payload and so breaks every
  brownfield consumer. The legacy `0x00 | <int32 BE>` layout is **read-only** support,
  for ingesting messages from a Kafka bridge.

**CloudEvents interop (read-only, v1).** Two incompatible conventions exist and Concordat
must read both: `cloudEvents_` / `cloudEvents:` (official CNCF, **AMQP 1.0 only**,
`datacontenttype` mapping to `content-type` as the sole exception) and `ce-` (Knative's
`eventing-rabbitmq` working draft, **AMQP 0-9-1**, shipping in real clusters, never
merged upstream). `cloudEvents_dataschema` is typed `URI` (absolute) and the primer is
explicit that it's **informational** — the word "validat" appears nowhere in the core
spec. *That gap is the product.* Filing an AMQP 0-9-1 binding with the CloudEvents WG is
a credible post-v1 standardisation play.

> **Verify empirically in M2:** whether custom headers survive dead-lettering, shovel,
> federation and the STOMP/MQTT adapters. That sets the documented Mode A vs Mode B guidance.
> **Also verify the AMQP 1.0 conversion itself** — that `concordat-*` headers surface as
> application-properties rather than message-annotations to a 1.0 client. ADR-013's
> "designed to survive 1.0 conversion" rests entirely on that behaviour and is otherwise
> an untested assertion.

---

## 3. Subject naming (ADR-011)

**Default strategy `MessageType`:** subject = the fully-qualified message type name.
The stable contract in RabbitMQ is *what a message is*, not where it went — and it is the
only identifier a publisher and a consumer both possess.

**Resolution in v1: RabbitMQ.Client only** (ADR-020). Subject = `properties.type`, as-is.
`ISubjectResolver` stays an extension point rather than collapsing to a constant, because
no two frameworks agree on where the type lives and because each non-.NET SDK will have
its own convention. Per-framework resolution research: **Appendix A**.

Canonical form: `^[A-Za-z0-9_]+(\.[A-Za-z0-9_]+)*$` — dot-separated segments,
deliberately **not** a CLR type name. Separators (`:`, `+`) normalise to `.`; assembly,
version, culture and public-key-token are always stripped.

> **The subject grammar carries no language's type system (ADR-019).** A Python publisher
> writing `acme.orders.OrderCreated` into `properties.type` is a first-class citizen, not
> a translation of a .NET concept. Resolution is entirely a *client-side* concern — the
> registry only ever sees a string matching the grammar above, and must never be able to
> tell which language produced it.

**Alternatives, configurable per contract:** `ExchangeRoutingKey`, `Queue`, `Explicit`.

**How this resolves the publisher/consumer asymmetry — the key insight:**
the resolver runs **only on the publish side**. The envelope then carries
`concordat-subject` and `concordat-schema-id`, so a consumer never re-derives anything; it
reads the subject off the message and validates against its `ConsumeBinding`. The
`Contract` (§4) is what bridges the two naming worlds:

```
PublishBinding  (vhost /, exchange orders, routingKey order.*)
    -> [ acme.orders.OrderCreated @latest,
         acme.orders.OrderCancelled @>=2 ]
ConsumeBinding  (vhost /, queue order-events)
    -> [ acme.orders.OrderCreated @latest ]
```

---

## 4. Domain model

Hierarchy: `Tenant → Environment → Subject → Version`. Subject names unique per
`(Tenant, Environment)`. Environments are a **first-class isolation boundary** — a
common complaint about Confluent is that "contexts" bolted this on late.

### Context A — Registry (core)

- **`Schema`** — immutable, content-addressed aggregate. `SchemaId` = SHA-256 of the
  canonical form truncated to 128 bits; `Format`, `Body` (canonical text), `References[]`.
  Registering an identical schema returns the existing id (idempotent via a unique constraint).
  > **The hash covers the whole envelope** — canonical body **plus references plus any
  > rules/metadata, not just the body.** This is what Confluent's CP 8.1 GUID does, and
  > getting it wrong means two schemas with different reference sets collide.
  >
  > **IDs are never reallocated.** Confluent's hard delete frees an ID for reuse, and
  > issue #4277 documents the result: a new schema reusing a soft-deleted ID can tombstone
  > referenced versions, leaving content fetchable by ID while reference resolution breaks.
  > Content-addressing eliminates that failure mode structurally.
  >
  > **Canonicalisation is day-one work.** Confluent's `normalize.schemas` still defaults
  > to `false`, which is how registries accumulate thousands of near-duplicate schemas
  > and blow past quota. Avro → Parsing Canonical Form; JSON Schema → key-sorted,
  > whitespace-normalised, `$id` resolved; Protobuf → normalised `FileDescriptorProto`
  > with import ordering normalised.
  >
  > **Size limit** — reject above a documented ceiling (Confluent has `42209
  > SCHEMA_TOO_LARGE`; Redpanda warns <128 KB; Glue caps at 170 KB).
- **`Subject`** — aggregate root. `SubjectName`, `Format`, `CompatibilityPolicy`,
  `Owner`, `Lifecycle` (Active|Deprecated|Retired), `Versions[]`.
  *Invariants:* one format across all versions; a new version satisfies the policy;
  ordinals contiguous and monotonic.
  - **`SchemaVersion`** — `Ordinal`, `SemanticVersion` (optional), `SchemaId`,
    `Changelog`, `RegisteredAt/By`, `Deprecated`, **`Status`** (`Active | AwaitingApproval`).
- **`LatestPointer`** (ADR-017) — the `latest` label is an explicit, gated pointer on the
  Subject, not "whatever has the highest ordinal". A breaking registration succeeds with
  `Status = AwaitingApproval` and does **not** advance it.
  > Confluent's `latest` is a mutable, globally-shared pointer, so with
  > `use.latest.version=true` a *third party* registering a version silently changes what
  > your producer serializes with, at runtime, with no deploy. Making the pointer
  > explicit and gated removes that. Borrowing Buf's refinement: an approval is
  > **automatically dismissed if the change is reverted**.
  >
  > **Guidance to document:** production contracts should `Pin` or `Range`, not track `latest`.
- **`CompatibilityPolicy`** — two orthogonal axes (ADR-016):
  - *Who breaks*: `Backward | BackwardTransitive | Forward | ForwardTransitive | Full | FullTransitive | None`
  - *What breaks*: `Wire | WireJson | Source`

  Per environment, overridable per subject. A policy is a pair, e.g. `Backward × Wire`
  permits `int32 → int64`, while `Backward × Source` blocks it.
  > This directly fixes a documented Confluent defect: its Protobuf checker is *stricter
  > than protobuf's actual wire compatibility* and rejects changes that produce
  > byte-identical output (splitting a `.proto` across files, renaming a message while
  > keeping field tags). With a single axis there is no way to express the distinction.
- **`RegistrationPolicy`** (per environment) — `Open | CiOnly | Closed`. Whether an SDK
  may auto-register on publish.
  > Confluent's `auto.register.schemas` is **client-side only with no server-side kill
  > switch** (open issue #2761). Combined with its Community edition having no
  > authorization at all, one misconfigured producer in any language permanently pollutes
  > the registry. Concordat enforces this server-side: `prod` defaults to `CiOnly`.

**Schema references.** `Reference = (name, subject, version)`. Resolution per format:
JSON Schema `$ref` → `concordat://<env>/<subject>/<version>`; Protobuf `import` filename →
subject; Avro named-type FQN → subject. Registration resolves references into a bundled
canonical form *and* retains the edges. Cycle detection is required, and **compatibility
must be evaluated transitively** — a breaking change inside a referenced schema breaks
every referrer, so referrers are re-checked on the referenced subject's new version.

**Deletion semantics.** Schemas are content-addressed and **never deleted**. Subjects
soft-delete by moving to `Retired`; hard delete requires no registered consumers, an
explicit force flag, and writes an audit entry. A version cannot be hard-deleted while
referenced by a contract, another schema, or a registered consumer.

### Context B — Topology / Contracts (the differentiator)

- **`Contract`** — aggregate root, environment-scoped.
  - `TopologyScope` (VO): `(BrokerId, VirtualHost)`
  - `PublishBinding`: `TopologyScope + Exchange + RoutingKeyPattern` → `SubjectRef[]`
  - `ConsumeBinding`: `TopologyScope + Queue` → `SubjectRef[]`
  - `SubjectRef`: subject name + `VersionSelector` (`Latest | Pinned(n) | Range(">=2")`)
  - `EnforcementMode`: `Off | Monitor | Enforce` — Monitor validates and reports without
    blocking, which is how a team safely trials a contract before turning it on.
  - *Invariant:* valid AMQP topic patterns; overlapping patterns on one exchange may not
    bind conflicting subjects without explicit precedence.

### Context C — Environments & Brokers (ADR-012)

- **`Environment`** — aggregate root. `Name`, `Description`, `Brokers[]`,
  `DefaultCompatibilityPolicy`.
  - **`BrokerConnection`** (entity): `Id`, `DisplayName`, `Uri` (amqp/amqps host:port),
    `VirtualHost`, `CredentialRef`, `TlsSettings`, `Status`.

```
Environment "prod"
  broker eu-1   amqps://rabbit-eu:5671  vhost /orders
  broker eu-1   amqps://rabbit-eu:5671  vhost /billing
  broker us-1   amqps://rabbit-us:5671  vhost /
Environment "dev"
  broker local  amqp://localhost:5672   vhost /
```

**Credential storage:** encrypted at rest via ASP.NET Core Data Protection — a disk/DB
key ring self-hosted, a KMS-backed key in Cloud. Credentials are **write-only over the
API**; reads return a `hasCredentials` boolean, never the secret.

### Context D — Governance & Lineage
- **`ServiceRegistration`** — services declare producer/consumer intent (SDK at startup, CLI in CI).
- **Impact Analysis** — *"if I register this version, which registered consumers break?"* The capability Confluent most conspicuously lacks.
- **Promotion** — `dev → staging → prod`, re-checking compatibility in the target. Also a Confluent gap.

### Context E — Identity & Access
`Tenant`, `User`, `Membership`, `Role`, `ApiKey` (hashed at rest).
Scopes: `subject:read|write|admin`, `contract:*`, `env:*`, `broker:*`, `org:admin`.

**Schema writes are admin-scoped (ADR-018).** `subject:write` and `subject:admin` are
granted to admin roles only; a non-admin membership carries `subject:read`. Every
mutating subject/version endpoint — create subject, register version, approve/reject,
patch, delete, promote — checks the scope server-side and returns `403` with
`concordatCode: "insufficient_scope"`. This is the enforcement point; §9's UI gating is a
presentation of it, not a substitute. Approve/reject (ADR-017) is admin-only for the same
reason, which keeps the author of a breaking change from waving it through themselves
once reviewers land in M7.

### Context F — Notifications
Outbox-driven `INotificationChannel`; **Email (SMTP) and Webhook in v1**, Slack later.
Events: breaking change attempted/blocked, version registered, enforcement violation,
subject deprecated. Subscriptions configured per environment.

### Context G — Billing (Cloud only)
`Subscription`, `Plan`, `UsageMeter`, `Invoice` — Stripe-backed.

---

## 5. API surface and cross-language strategy

Native REST at `/v1`, JSON, **OpenAPI 3.1 generated from the minimal-API endpoints and
committed** to `docs/api/openapi.v1.json`. Non-.NET clients generated from it.

```
GET  /v1/schemas/{id}  |  GET /v1/schemas/{id}/subjects
POST /v1/schemas/lookup                            { format, body } -> id | 404

GET|POST         /v1/environments/{env}/subjects
GET|PATCH|DELETE /v1/environments/{env}/subjects/{subject}
GET|POST         /v1/environments/{env}/subjects/{subject}/versions
GET              /v1/environments/{env}/subjects/{subject}/versions/{ordinal|latest}
POST             /v1/environments/{env}/subjects/{s}/versions/{n}/approve   # ADR-017
POST             /v1/environments/{env}/subjects/{s}/versions/{n}/reject
GET|PUT          /v1/environments/{env}/registration-policy   # Open | CiOnly | Closed

POST /v1/environments/{env}/bootstrap    # one call -> every schema a client needs

POST /v1/environments/{env}/subjects/{subject}/compatibility     # dry run, never writes
     -> { compatible, breakingChanges[], suggestedSemver, impactedConsumers[] }
GET|PUT /v1/environments/{env}/subjects/{subject}/compatibility-policy
GET  /v1/environments/{env}/subjects/{s}/versions/{a}/diff/{b}

GET|PUT /v1/environments/{env}/contracts
POST    /v1/environments/{env}/contracts/resolve    # SDKs call once at startup, cache

GET|POST         /v1/environments  |  GET|POST /v1/environments/{env}/brokers
POST /v1/environments/{env}/services               # producer/consumer intent
GET  /v1/environments/{env}/subjects/{s}/impact    # who breaks
POST /v1/environments/{env}/subjects/{s}/promote   { toEnvironment }

GET /v1/audit | POST /v1/api-keys | GET|PUT /v1/environments/{env}/notifications
GET /health/live /health/ready /openapi/v1.json
```

**Errors: RFC 9457 Problem Details + a stable string `concordatCode`** (not Confluent's
opaque numerics). The `breakingChanges[]` array with JSON-Pointer paths is the biggest
usability win over Confluent, whose messages are notoriously hard to act on:

```json
{ "type": "https://concordat.dev/errors/incompatible-schema", "status": 409,
  "concordatCode": "incompatible_schema", "subject": "acme.orders.OrderCreated",
  "policy": "BACKWARD_TRANSITIVE",
  "breakingChanges": [{ "path": "#/properties/legacyId", "kind": "required_field_removed",
    "message": "Required field 'legacyId' removed; consumers on v1 will fail",
    "conflictsWithVersion": 1 }],
  "suggestedSemver": "2.0.0" }
```

### The protocol is the product (ADR-019)

The server is written in .NET; the contract is not. Everything needed to build a client
is normative, language-neutral and versioned with the API:

| Artifact | Where | Specifies |
|---|---|---|
| OpenAPI 3.1 | `docs/api/openapi.v1.json` (generated, committed) | the REST surface |
| Envelope spec | §2 — a header table and prose, not a C# type | what goes on the wire |
| Canonicalisation rules | §4, per format, deferring to each format's own spec | how a schema id is derived |
| `concordatCode` catalogue | with the Problem Details shapes | every error a client must handle |
| Conformance corpus | `tests/Concordat.Conformance` | required client *behaviour* |

**No endpoint, header, error code or shortcut is reserved for the .NET SDK.** Two
concrete rules that follow, worth stating because they are the ones quietly violated
first: the schema id is a hash of canonical text defined by each format's own
specification, never of a .NET serialisation; and no API response may contain a
CLR-shaped identifier — no assembly-qualified names, no `System.*` type strings.

The .NET SDK is the first client, not a reference implementation with special access. Its
client-side behaviours — cache key discipline and TTLs, warm-up jitter,
`fail-open|fail-closed`, quarantine routing, the resolver seam — are specified in the
conformance corpus in language-neutral terms, so Python and Go implement the same
contract against the same fixtures rather than reverse-engineering C#.

**Client tiers.** Tier 1 .NET — first only because it is where the server is written.
Tier 2 **TypeScript/JavaScript, Python, Go, Java** (ADR-021): a generated REST client plus a
hand-written cache and AMQP middleware. **`concordat` CLI** is the universal escape
hatch: one NativeAOT binary for win-x64/linux-x64/linux-arm64/osx-arm64, plus a Docker
image and GitHub Action — a Python or Go shop needs zero .NET installed. (.NET is
genuinely underserved here: Apicurio ships Java/TS/Python/Go and no .NET SDK; only Azure
and AWS Glue have first-party .NET, both cloud-locked.)

### Tier 2 SDK bindings (ADR-021)

| SDK | Packages | AMQP binding | JSON Schema validator |
|---|---|---|---|
| **TypeScript / JavaScript** | `@concordat/client` (isomorphic REST + cache) and `@concordat/amqp` (Node-only middleware) | `amqplib` | `ajv` |
| **Python** (3.11+) | `concordat-client` | `pika` (sync) **and** `aio-pika` (async) | `jsonschema` |
| **Go** | `concordat-go` | `rabbitmq/amqp091-go` | `santhosh-tekuri/jsonschema` |
| **Java** (21 LTS) | `io.github.rafaeljcamara:concordat-client` | `com.rabbitmq:amqp-client` | `networknt/json-schema-validator` |

Four things that fall out of the specific choices, each affecting the package layout:

- **The TS package splits in two.** The REST client and cache are isomorphic and run in a
  browser; AMQP middleware cannot. Keeping them in one package would drag `amqplib` into
  every browser bundle. `ajv` is already the Angular app's validator (§9), so the web UI
  and the Node SDK agree on validation behaviour by construction.
- **Python needs two adapters, not one.** `pika` and `aio-pika` are different programming
  models, not a sync/async flag over one API. Budget for both; `pika` is the larger
  installed base, `aio-pika` the one new services pick.
- **Go has no exceptions.** Fail-open/fail-closed and the reject path are error returns,
  not a thrown-and-caught `SchemaValidationException`. This is the client most likely to
  expose places where the conformance corpus specified .NET control flow rather than
  behaviour — which is exactly its value under ADR-019.
- **Java binds the raw client, and Spring AMQP is deferred.** `spring-rabbit` is the Java
  analogue of MassTransit — the dominant framework layer — so ADR-020 applies to it
  unchanged. Be honest that **this deferral bites hardest in Java**: a large share of
  Java RabbitMQ users are on Spring AMQP rather than `amqp-client` directly, so the Java
  SDK reaches a smaller fraction of its language's estate than the other three do.
  Spring AMQP is therefore **first in line when adapters resume** (Appendix A).

> **The real cross-language hazard: payload validation is not Concordat's code.** The
> compatibility engine is server-side and has one implementation, so its verdicts are
> identical everywhere. **Payload validation is client-side and uses a different
> third-party library in every language** — `ajv`, `jsonschema`, `santhosh-tekuri`, `networknt`,
> and .NET's own. Draft coverage and edge-case behaviour differ between them, so the same
> message can pass in one language and fail in another with no bug on Concordat's part.
> Mitigations, all required before the first Tier 2 SDK ships: **pin draft 2020-12** as
> the only supported dialect; define the **interoperable keyword subset** and warn at
> registration when a schema uses anything outside it; and carry a payload-validation
> fixture corpus (§12) with expected accept/reject per case that every SDK's CI runs.

**Client caching contract** — `schema-id → schema` and `subject+version → schema-id` are
immutable (content-addressed, so this is guaranteed rather than assumed) and cached
forever; `subject → latest` 30 s TTL; contract resolution 60 s TTL.
**Hard rule: after warm-up the registry is never in the critical path of message
delivery.** Unreachable ⇒ serve from cache, fail open or closed by configuration.

**Cold start is the real load pattern, and it must be designed for.** A fleet-wide
rolling restart means every instance has an empty cache simultaneously and stampedes the
registry — which sits on the *deserialize* path, so a registry that buckles takes
consumption down with it. Confluent Cloud caps Schema Registry at **75 reads/sec on
every tier, Essentials and Advanced alike**, which is a low ceiling for exactly this
burst. Concordat's mitigations: `POST /bootstrap` returns every schema reachable from a
client's contracts in **one** request instead of N; SDKs jitter their warm-up; and
negative lookups are cached so a missing subject doesn't retry-storm.

---

## 6. Client SDK design — RabbitMQ.Client only (ADR-020)

| Library | Publish hook (throw blocks?) | Consume hook | Raw AMQP? |
|---|---|---|---|
| **RabbitMQ.Client 7.2.2** | decorator over `IChannel.BasicPublishAsync` — ✅ | `IAsyncBasicConsumer` decorator — **nack, don't throw** (throws surface via `CallbackExceptionAsync`) | ✅ both ways |

The raw client is the right and only v1 target: unrestricted AMQP access in both
directions exercises the envelope with nothing mediating it, and it is the substrate all
five service buses sit on, so nothing learned here is wasted when they land.

**Three rules the SDK is built around:**

1. **A schema violation must never retry.** It is deterministic — redelivery cannot change
   the verdict, and under a default retry policy one bad message becomes dozens of failed
   deliveries. Nack without requeue and route to a `concordat.quarantine` exchange with
   failure-reason headers, so bad messages are inspectable rather than lost or looping.
2. **The registry is never in the delivery path after warm-up** (§5). Unreachable ⇒ serve
   from cache; fail open or closed by configuration.
3. **Every behaviour here is protocol, not implementation** (ADR-019). Cache keys and
   TTLs, warm-up jitter, the fail-open/closed switch, quarantine routing and the resolver
   seam are written into the conformance corpus in language-neutral terms *as they are
   built*, not documented afterwards. The Python and Go clients implement that spec; they
   do not port this code.

**Deferred: service-bus adapters.** MassTransit, EasyNetQ, Wolverine, NServiceBus and
Rebus are out of scope. **Appendix A** preserves the hook research verbatim so the work
resumes from evidence rather than a re-read of five codebases. One near-term benefit
worth noting: it also drops the MassTransit licensing constraint (v9 is commercial;
v8.5.x is the last Apache-2.0 release) out of the v1 dependency surface entirely.

---

## 7. Contract checks — CLI and build-time

**CI-time — the `concordat` CLI (the primary gate)**
```
concordat check   --env staging --dir ./contracts   # dry-run compat; exit 1 on break
concordat push | promote | diff | impact | lint | export
concordat infer   --dir ./samples --out ./contracts            # ADR-014
concordat infer   --queue order-events --broker prod/eu-1 --max 500 --out ./contracts
```

**`concordat infer` (ADR-014)** reads sample payloads from files, or drains a queue
**read-only** — `basic.get` with requeue, or an exclusive consumer that nacks with
requeue; document that this can reorder a live queue and default to file mode. It infers
JSON Schema from the corpus: types, required-by-presence across samples, `format`
detection (uuid, date-time, email), enums at low cardinality, nullability. Output is a
**draft plus a confidence/ambiguity report for human review** — it never auto-registers.

**Build/test-time NuGet packages**
- **`Concordat.Client`** — HTTP + caching.
- **`Concordat.Contracts`** — `[ConcordatContract("acme.orders.OrderCreated")]` on C# records.
- **`Concordat.Contracts.MSBuild`** — MSBuild task + Roslyn analyzer generating a schema
  from every attributed type at build time, diffing against checked-in `contracts/`,
  **erroring on drift**. The C# type is the source of truth; breaking it breaks the build.
- **`Concordat.Contracts.Testing`** — `await Concordat.Assert.CompatibleAsync<OrderCreated>(env: "prod")`.

### Compatibility semantics (ADR-016)

**Axis 1 — who breaks.** `BACKWARD` — the *new* schema reads data written by the
*previous* one. `FORWARD` — the *previous* reads data written by the *new*. `FULL` —
both. `*_TRANSITIVE` — checked against *all* prior versions. `NONE` — no check.
Upgrade order matters: BACKWARD ⇒ consumers first, FORWARD ⇒ producers first. Default is
`BACKWARD`, and like Confluent it is **non-transitive** — worth documenting loudly,
since a chain of individually-backward-compatible changes can still leave you unable to
read the oldest data.

**Axis 2 — what breaks.** `WIRE` (bytes still decode) ⊂ `WIRE_JSON` (JSON mapping holds)
⊂ `SOURCE` (generated code still compiles). Every finding in `breakingChanges[]` is
tagged with the narrowest axis it violates, so the same change can be reported as
allowed under one policy and blocked under another.

Per format: **Avro** follows specified resolution rules (defaults, aliases, union
widening, enum symbols). **Protobuf** treats field numbers as identity — breaking on
number/wire-type change or removal without `reserved`; a rename is `WIRE`-safe but
`WIRE_JSON`- and `SOURCE`-breaking, which the two-axis model states precisely instead of
guessing.

**JSON Schema needs its own design, not a port of Confluent's.** There is no
compatibility spec for JSON Schema, and Confluent's attempt is widely judged unusable:
because it treats open (`additionalProperties: true`, the default) and closed content
models under mutually exclusive rules, **adding an optional field is not backward
compatible under the defaults** — you hit `PROPERTY_ADDED_TO_OPEN_CONTENT_MODEL` or
`PROPERTY_REMOVED_FROM_CLOSED_CONTENT_MODEL`. Independent analysis concludes all three
content models fail, and the workaround teams actually ship is setting compatibility to
`NONE` — a registry with its central value proposition switched off.

Concordat's requirements, stated as acceptance criteria for M1:
- **Adding and removing an optional property MUST be fully compatible.** This is the
  single most common schema change; if it is blocked, the product is unusable.
- **The content model is explicit subject config**, not inferred per-schema, so it cannot
  silently flip between versions.
- Narrowing `type`/`enum`/`maximum`, adding to `required`, and
  `additionalProperties: true → false` are backward-breaking; widening is forward-breaking.
- **Payload validation is on by default.** Confluent's `json.fail.invalid.schema`
  defaults to `false`, so its JSON deserializer does not check the payload against the
  schema at all unless you opt in.

Every finding carries an exact JSON-Pointer path. **This is where Confluent is weakest
and where Concordat should be unambiguously better.**

---

## 8. Backend architecture (DDD + Clean Architecture)

```
Concordat/
  Concordat.slnx  Directory.Build.props  Directory.Packages.props  global.json
  docs/adr/  docs/api/openapi.v1.json
  src/
    core/      Concordat.Domain/  Concordat.Application/  Concordat.Infrastructure/
    formats/   Concordat.Formats.Abstractions/ .Json/ .Avro/ .Protobuf/
    hosts/     Concordat.Api/  Concordat.Migrator/
    cloud/     Concordat.Cloud.Tenancy/  Concordat.Cloud.Billing/
    clients/   Concordat.Client/  Concordat.Contracts{,.MSBuild,.Testing}/
               Concordat.Messaging.RabbitMq/     # service-bus adapters deferred — Appendix A
    tools/     Concordat.Cli/          # NativeAOT
  clients/     typescript/ python/ go/ java/          # ADR-021
  web/         # Angular
  deploy/      docker/ compose/ helm/
  tests/       Concordat.Domain.Tests/ Concordat.Application.Tests/ Concordat.Formats.*.Tests/
               Concordat.Api.IntegrationTests/ Concordat.Messaging.Tests/ Concordat.Conformance/
```

**Dependency rule:** Domain ← Application ← Infrastructure/Api. Format projects depend
only on `Formats.Abstractions` + their parser lib; Domain references interfaces only.

**Patterns.** CQRS via a hand-rolled dispatcher (`ICommandHandler<,>`/`IQueryHandler<,>`
with DI scanning) — deliberately **not MediatR**, now commercially licensed and in
conflict with ADR-009. `Result<T>` for domain failures → Problem Details. Outbox for
domain events → notifications and webhooks. **Tenancy is one code path:**
`ITenantContext` from API key or session; self-hosted binds a fixed tenant, Cloud
resolves per request; EF Core global query filters enforce isolation.
`ConcordatProfile.SelfHosted | Cloud` swaps `ITenantResolver`/`IBillingGate`/
`IIdentityProvider` at the composition root — no `if (cloud)` scattered around.

---

## 9. Frontend architecture (Angular)

Angular 22 standalone + signals, no NgModules. `@ngrx/signals` SignalStore per feature.
**Spartan UI** `@spartan-ng/brain` (headless, on Angular CDK) with `helm` components
generated into the repo as source — the direct analog of shadcn, so the prototype's
Tailwind token contract and `index.css` port **verbatim**. Monaco replaces the regex
highlighter; `ajv` for client-side validation and sample-payload checking.

```
web/src/app/
  core/     http/ (auth, tenant, problem-details interceptors)  auth/  config/
  shared/   ui/ directives/ pipes/
  domain/   registry/ topology/ identity/ billing/     # pure TS, no Angular
  features/<ctx>/
      data-access/   # the ONLY place HttpClient appears; DTO<->domain mappers
      application/   # SignalStore facade
      ui/            # presentational only
      feature/       # routed smart components
```

Boundaries enforced by an ESLint boundaries rule: `domain/` imports nothing from Angular
or `features/`; `ui/` never touches a store or HTTP; `feature/` talks only to its own store.

**Schema editing is admin-only (ADR-018).** For a non-admin the registry is read-only:
`NewVersionPage` and `SubjectDetailPage`'s edit actions are not routable, the "New
version" / "Edit" / "Deprecate" / "Delete" affordances are absent rather than disabled
(a disabled button invites a support ticket; an absent one doesn't), and `ApprovalsPage`
renders the pending diff and impact without approve/reject controls. Everything
read-only stays open to all members — subject list, version detail, diff, impact
analysis, audit, export — since hiding the contract from the people who have to honour
it defeats the product.

Mechanically: a single `canWriteSchemas` computed on the session SignalStore, derived
from the scopes the API returns at login, consumed by a `*cdIfScope` structural directive
for affordances and a `scopeGuard` on the write routes. One source of truth, so a new
write screen can't quietly ship ungated. A direct navigation to a write route by a
non-admin redirects to the read view; a `403` from the API surfaces through the existing
problem-details interceptor rather than a bespoke path.

> **Milestone ordering caveat:** the Angular port is M4 but identity and RBAC are M8, so
> the UI ships before real roles exist. Build the gate against `canWriteSchemas` from day
> one, backed by a stub that returns admin in the single-user self-hosted profile. The
> alternative — retrofitting the check across finished screens in M8 — is how write paths
> get missed.

| Prototype screen | Becomes |
|---|---|
| Dashboard | Environment-scoped overview: subjects, versions, contracts, recent breaking changes, enforcement coverage |
| Schemas (871-line god component) | **Split four ways** — `SubjectListPage`, `SubjectDetailPage` (`/subjects/:name`, real routes not query params), `VersionDetailPage`, `NewVersionPage` |
| ValidationRules | `ContractsPage` — bindings + enforcement mode + version selectors |
| Notifications / Settings | Reactive forms that persist; Settings splits into `EnvironmentSettings`, `Brokers`, `ApiKeys`, `Members` |
| *(new)* | `CompatibilityDiffPage`, `ImpactAnalysisPage`, **`ApprovalsPage`** (pending breaking changes with their impact and diff, approve/reject), `AuditLogPage`, `LoginPage`, `BillingPage` (Cloud-only) |

**Preserve from the prototype:** immutable-ID confirmation, auto-slugged id with a
"touched" flag, semver auto-increment seeded from latest, cloning the previous version's
JSON, compatibility tooltips, "No versions yet" empty state, Format/Validate buttons.

**Fix while porting:** the prototype's `dangerouslySetInnerHTML` regex highlighting is an
**XSS hole** — replaced by Monaco, not ported. Its two competing HTTP paths (a hardcoded
absolute `fetch` and an `axios.post` to a different, unproxied path that always 404s and
silently swallows the create) collapse into one typed data-access layer. Uncontrolled
`defaultValue` forms → reactive forms. Dark-only tokens gain a light theme. Drop the
unused dependency surface (React Query, react-hook-form, zod, recharts, next-themes,
cmdk, vaul, embla, input-otp — all installed, none used).

---

## 10. Deployment flavours

**Self-hosted** — one image serving API + embedded SPA. `docker compose up` brings
Concordat + Postgres + optional RabbitMQ. Helm chart. `CONCORDAT__*` env vars. Auto-migrate on
startup (toggleable). Single implicit tenant, local accounts + optional OIDC.

**Concordat Cloud** — same image, `CONCORDAT__PROFILE=Cloud`. Multi-tenant, row-level
isolation. Org signup, Google/GitHub SSO, SAML on the top tier. Stripe metered on
subjects, versions/month, API requests, environments, seats. Free (1 env, 10 subjects) →
Team → Business → Enterprise. Per ADR-009 everything is Apache-2.0, so Cloud competes on
managed upgrades, backups, HA, SLA and support.

---

## 11. Milestones

> Summary only. The work breakdown — numbered packages, checklists and per-milestone exit
> criteria — lives in **[PLAN.md](PLAN.md)**, which changes often; this table should not.

| M | Deliverable |
|---|---|
| **M0** | Solution skeleton, `Directory.*.props`, `global.json`, CI, ADRs 001–022. Name availability **done** — the project was renamed from Signet to Concordat (ADR-022); `concordat.dev` still to buy. |
| **M1** | Registry core, **JSON Schema only**: subjects, versions, canonicalisation, **content-addressed IDs**, **two-axis compatibility engine**, references, **gated `latest` pointer + registration policy**, REST API + `/bootstrap`, Postgres, OpenAPI |
| **M2** | `Concordat.Client` + `Concordat.Messaging.RabbitMq` + `ISubjectResolver`; Testcontainers tests; **verify header survival** (§2) |
| **M3** | `concordat` CLI incl. `infer` + GitHub Action + `Concordat.Contracts{,.MSBuild,.Testing}` |
| **M4** | Angular app port |
| **M5** | Avro + Protobuf formats |
| **M6** | **Tier 2 SDKs** (ADR-021) — TypeScript/JavaScript → Python → Go → Java — plus the cross-language conformance suite running in every SDK's CI |
| **M7** | Environments + brokers + credential encryption; governance: service registration, impact analysis, promotion, audit, notifications/webhooks, **approval reviewers + auto-dismiss-on-revert** |
| **M8** | Identity, RBAC, API keys |
| **M9** | Cloud: tenancy, Stripe, signup, metering |

M1–M3 is the smallest genuinely useful product: register a JSON Schema, block a breaking
change in CI, enforce it at runtime from .NET.

**Polyglot SDKs moved from M10 to M6** (ADR-019). A second-language client is the only
real proof that the protocol is language-neutral, and every milestone it waits behind is
another chance for a .NET assumption to set unnoticed. Shipping it right after M5 means
all three formats exist and the protocol has stopped moving, but governance, identity and
Cloud are all still ahead of it — so anything the second SDK exposes as .NET-shaped gets
fixed while those surfaces are still being designed, not retrofitted across them.

**SDK order within M6: TypeScript/JavaScript → Python → Go → Java** (ADR-021). Ship each
one properly before starting the next; four finished clients beat five half-built ones.

TS/JS first for reach — two audiences in one package, and `ajv` and the DTO shapes are
already exercised by the Angular app, so it is the cheapest of the four to get right.
Python second: wide RabbitMQ audience, and the first client sharing no lineage with the
.NET one. Go third, carrying a distinct job — no exceptions, no reflection-driven
serialisation, least in common with .NET — making it the sharpest audit of whether the
protocol is genuinely language-neutral. Java fourth: the largest single-language
investment of the four and the one that gains most from a corpus three independent
clients have already hardened.

> **Two tensions, stated so they can be re-decided:**
>
> 1. The *first* non-.NET SDK is the one that finds the protocol leaks, and Go is the
>    client most likely to find them. This order optimises reach first, audit third. If
>    proving ADR-019 matters more than early adoption, move Go to the front.
> 2. **Java last is an audience call, not a difficulty call.** Spring AMQP is a large
>    share of Java's RabbitMQ estate and is deferred under ADR-020, so the Java SDK
>    reaches less of its language than the other three (§5). If enterprise Java demand
>    shows up, the right response is probably to pull *Spring AMQP* forward out of
>    Appendix A rather than to reorder M6.

---

## 12. Verification

- **Compatibility matrix golden tests** — table-driven corpus of
  `(old schema, new schema, who-axis, what-axis) → (verdict, expected breaking-change
  paths)` per format. The correctness heart of the product; a wrong verdict either blocks
  safe changes or waves breaking ones through. Heaviest test investment. **Must include
  the cases Confluent gets wrong**: adding/removing an optional JSON Schema property is
  fully compatible; `int32 → int64` passes `WIRE` and fails `SOURCE`; a Protobuf message
  rename with stable field tags passes `WIRE` and fails `WIRE_JSON`.
- **Canonicalisation and identity tests** — semantically identical schemas differing in
  whitespace, key order, `$id` form and Protobuf import order must yield one schema id;
  schemas differing *only* in their reference set or metadata must yield **different**
  ids; the same schema registered in two environments must yield the **same** id, and a
  promoted version must keep its id so an in-flight envelope stays valid.
- **Authorization tests (ADR-018)** — every mutating subject/version endpoint returns
  `403 insufficient_scope` for a `subject:read` principal, and does so for API keys and
  session cookies alike; the read surface stays fully available. Plus an E2E pass as a
  non-admin asserting no write affordance renders and a direct URL to a write route
  redirects.
- **Approval-gate tests** — a breaking registration returns 201 with
  `Status = AwaitingApproval` and leaves `latest` unmoved; approval advances it;
  reverting the change auto-dismisses the pending approval; `RegistrationPolicy = CiOnly`
  rejects SDK auto-registration server-side regardless of client config.
- **Reference tests** — transitive breakage: changing a referenced schema must fail its
  referrers; cycles must be rejected at registration.
- **Header round-trip tests** — the `string` → `byte[]` decode holds on the
  RabbitMQ.Client path, and no Concordat header collides with `MT-`, `NServiceBus.`,
  `rbs2-`, `rabbitmq-` or `x-`. The collision check stays despite ADR-020: those headers
  ride on messages Concordat reads today, and the namespace must still be clear when the
  adapters land.
- **Cross-language conformance suite** (`tests/Concordat.Conformance`) — a language-neutral
  corpus of envelope fixtures, canonicalisation cases and expected verdicts, executed by
  every SDK's CI so the Python and .NET clients cannot silently diverge.
  **Includes a payload-validation corpus** (§5): documents that must accept and must
  reject under a given schema, run through each language's third-party JSON Schema
  validator. This is the one place SDKs can diverge without anyone writing a bug —
  `ajv`, `jsonschema`, `santhosh-tekuri`, `networknt` and .NET's validator are five independent
  implementations of a spec with real edge-case disagreement, and the corpus is what
  turns that from a support ticket into a CI failure.
  > **Normative, not merely a test (ADR-019):** where the corpus and an implementation
  > disagree, the corpus is right and the implementation is a bug — including when the
  > implementation is .NET. It therefore exists **from M1**, populated alongside the
  > compatibility engine, years before a second SDK consumes it. A corpus written when
  > the second SDK arrives only ratifies whatever .NET already did.
- **Integration** — Testcontainers Postgres for the API; Testcontainers RabbitMQ for
  messaging (publish a conforming and a non-conforming message; assert the latter is
  rejected into `concordat.quarantine` with reason headers).
- **Contract** — CI fails if generated OpenAPI drifts from the committed spec.
- **E2E** — Playwright against the Angular app and a real API.
- **Manual smoke** — `docker compose up`; create a subject in the UI; run a sample .NET
  producer publishing one valid and one invalid message; watch enforcement and the
  quarantine queue; attempt a breaking version and confirm `concordat check` exits 1 with
  the offending JSON-Pointer path.

---

## 13. Deliberately deferred — decide during implementation

Recorded so nothing is silently dropped: registry HA and leader election, backup/restore
procedure, API rate limiting, Concordat's own observability (metrics/traces it emits), SLO
targets for the validate path, the versioning and deprecation policy for Concordat's own
REST API, and community scaffolding (CONTRIBUTING, code of conduct, issue templates,
docs site).

---

## Appendix A — Framework adapter research (deferred, ADR-020)

Not in v1, in any language. Every SDK binds its language's raw AMQP client only.
Researched against current library source and preserved verbatim so the work resumes from
evidence. **When these land, this table is the implementation spec.**

**Non-.NET frameworks in the same queue.** Not yet researched to the depth below:
**Spring AMQP (`spring-rabbit`)** — the Java analogue of MassTransit, and the highest
-priority adapter of all of them, since it is a large share of Java's RabbitMQ estate
(§5); **Celery** (Python), which uses RabbitMQ as a broker but frames its own task
envelope; **NestJS microservices** (TS). Each needs the same four-column analysis:
publish hook, whether a throw blocks, consume hook, and raw AMQP property access.

### .NET service buses

| Library | Publish hook (throw blocks?) | Consume hook | Raw AMQP? |
|---|---|---|---|
| **MassTransit 8.5.x** | `IFilter<PublishContext<T>>` via `UsePublishFilter` — ✅ reaches the `Publish()` caller | `IFilter<ConsumeContext<T>>` via `UseConsumeFilter` → `_error` | ✅ `RabbitMqSendContext.BasicProperties` |
| **EasyNetQ 8.1.5** | `ProducePipelineBuilder` — ✅ no try/catch on the produce path | `ConsumePipelineBuilder` → `AckStrategies.NackWithoutRequeueAsync` | ⚠️ `MessageProperties`, not the AMQP object |
| **NServiceBus 10.2 / RabbitMQ 11.2** | `Behavior<IOutgoingPhysicalMessageContext>` — ✅ | `Behavior<IIncomingPhysicalMessageContext>` | ❌ out (needs `OutgoingNativeMessageCustomization`); ✅ in via `context.Extensions.Get<BasicDeliverEventArgs>()` |
| **Rebus 8.9 / RabbitMq 10.1** | `IOutgoingStep` before `SendOutgoingMessageStep` — ✅ nothing commits | `IIncomingStep` before `DeserializeIncomingMessageStep` + `FailFastOn<T>` | ❌ string→string; write `rabbitmq-*` to influence |
| **Wolverine 6.26** | `IEnvelopeRule.Modify` — ✅ synchronous on `PublishAsync` | `IMessageSerializer.ReadFromData` → `MoveToErrorQueue` | ✅ only in `IRabbitMqEnvelopeMapper` |

**Subject resolution** — `ISubjectResolver` implementations (§3):

| Framework | Source | Normalisation |
|---|---|---|
| MassTransit | first non-interface entry of the envelope `messageType[]` URN array | strip `urn:message:`, `Ns:Type` → `Ns.Type` |
| EasyNetQ | AMQP `type` = `"Ns.Type, Asm"` | strip assembly |
| NServiceBus | `NServiceBus.EnclosedMessageTypes`, first entry | strip assembly qualification |
| Rebus | `rbs2-msg-type` (**not** AMQP `type`, which comes from `rabbitmq-type`) | strip assembly |
| Wolverine | `message-type` header | alias-aware |

**Two findings to carry forward:**

1. **Reject paths differ sharply, and schema violations must never retry.** MassTransit:
   a throw in `ConfigureReceive` is *upstream* of `UseRescue`, so it nacks rather than
   reaching `_error` — reject at the **consume-filter** level. Rebus burns
   `maxDeliveryAttempts` unless you register `FailFastOn<SchemaValidationException>()`.
   NServiceBus burns ~24 attempts by default. Wolverine's serializer path goes straight
   to `MoveToErrorQueue`. → Ship explicit per-framework poison config.
2. **MassTransit v9 is commercially licensed (Massient); v8.5.x is the last Apache-2.0
   release**, and every interface Concordat touches is byte-identical between them. →
   **Target `MassTransit.Abstractions` 8.5.x**, which runs unchanged on v9. Matters given
   ADR-009. Deferring the adapters keeps this out of the v1 dependency surface.

**Original build order**, if resumed as a block: MassTransit → EasyNetQ → Wolverine →
NServiceBus → Rebus (roughly by adoption). Re-verify every version number first; all six
libraries move. Across languages, **Spring AMQP outranks all of them** on estate share.

> **Re-entry criterion.** These are a .NET-depth investment, and ADR-019 makes
> cross-language breadth the higher priority. Resume when a second-language SDK has shipped
> and demand is demonstrated — a MassTransit adapter is worth building for users who exist,
> not for a hypothetical estate.
