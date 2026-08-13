# M2 — .NET client + RabbitMQ.Client middleware

**Depends on:** [M1](M1-registry-core.md) · **Unlocks:** M3 runtime bits · **Design refs:** [§2](../DESIGN.md#2-the-concordat-envelope-adr-010), [§3](../DESIGN.md#3-subject-naming-adr-011), [§5](../DESIGN.md#5-api-surface-and-cross-language-strategy), [§6](../DESIGN.md#6-client-sdk-design--rabbitmqclient-only-adr-020), decisions 010, 011, 020

Scope is RabbitMQ.Client only (ADR-020). Service-bus adapters are [Appendix A](../DESIGN.md#appendix-a--framework-adapter-research-deferred-adr-020).

---

## M2.1 `Concordat.Client`

**Done 2026-08-13 · DESIGN §5 · 22 tests**

- [x] Typed HTTP client over the `/v1` surface; API-key auth
- [x] Problem Details → typed exceptions, `concordatCode` preserved
- [x] Cache: `schema-id → schema` **immutable, cached forever** (guaranteed by content-addressing, not assumed)
- [x] Cache: `subject → latest` 30 s TTL; negative lookups cached with doubling backoff
- [x] **Negative-lookup caching, deferred from M1.6** — it belongs on the client, not as a
      server cache header. The failure it prevents is a missing subject retry-storming the
      registry during a cold start, and only the client can decline to retry
- [x] Warm-up via `POST /bootstrap`, **with jitter** — a fleet-wide rolling restart must not stampede
- [x] `fail-open | fail-closed` configuration
- [x] **Hard rule enforced by test: after warm-up the registry is never in the delivery path**
- [ ] Contract-resolution TTL — **deferred to M7**, which is where contracts first exist. There
      is no endpoint to cache

### The hard rule needs a caveat

DESIGN §5 says the registry is never in the critical path after warm-up. That is true of cache
hits, and cache hits are the overwhelming majority — but `/bootstrap` ships each subject's
**latest** schema only. A consumer handed a message pinned to an *older* schema id has never
seen it and must fetch it once, after which content addressing guarantees it never fetches it
again.

Better stated here than discovered in production. The client is honest about three states, not
two: served from cache, fetched once, unresolvable.

### A 4xx is not an outage

An expired API key answers every request with 401. The first implementation reported that as
`DEGRADED, registry unreachable` — which sends whoever is paged to go and stare at a perfectly
healthy registry while enforcement is off fleet-wide.

So `IsDegraded` now means only that the registry **failed to answer**: 5xx, 408, 429, dead
socket. A 4xx is the registry answering, with a refusal, and surfaces through `LastFailure` as
`401 auth_invalid_key`. Same mis-attribution as absent-vs-unreachable, one layer up.

### Three defects the tests caught

- **The negative cache reset its own backoff.** Expired entries were deleted on read, so the
  next miss took the add path and went back to 5 s instead of doubling. A subject missing
  because of a typo would have been asked about at a fixed rate forever. Expired entries are
  now retained; only the *answer* expires, not the accounting.
- **Degradation was invisible on a cold client.** `ToString()` reported it only inside the warm
  branch, so a client that never warmed *and* could not reach the registry printed just
  `cold` — read by any operator as "still starting up".
- **`fail-open | fail-closed` was declared and never read.** Every path fail-opened regardless
  of the setting. Worse, the negative cache short-circuited before the mode was consulted, so
  even once fixed a fail-closed consumer would have thrown on the first message and silently
  proceeded on every one after.

### `ResolutionFailures` counts operations, not causes

The number to alert on. A subject that stays unregistered keeps producing unenforced messages,
so each one counts; recording it once would report a standing enforcement hole as a single
historic blip.

## M2.2 Envelope

**Done 2026-08-13 · DESIGN §2 · ADR-010**

`EnvelopeWriter` and `EnvelopeReader` live in the Domain and operate over a plain header
dictionary, so the encoding is testable without a broker and shared by every transport
adapter. 25 tests.

### Reading has three outcomes, not two

Read, **absent**, or malformed. "No envelope" is not a failure: Mode A exists so a consumer
without a Concordat client still reads plain JSON, and treating an un-enveloped message as an
error would break the incremental adoption ADR-010 is built around.

### Four decisions that look fussy and are not

- **Lookup is ordinal and case-sensitive.** An implementer reaching for HTTP-style header
  canonicalisation would accept `Concordat-V` from one SDK and not another.
- **Decoding is strict UTF-8.** `Encoding.UTF8` substitutes U+FFFD, which would turn a
  corrupted schema id into a valid-looking wrong one. Same trap in Go, where `string(bytes)`
  does the same; Python is the exception in erroring by default.
- **Values are not trimmed.** `SubjectName.Create` trims — right for a form field, wrong on
  the wire, because `"  acme.A  "` and `"acme.A"` would become two spellings of one value and
  an SDK that does not trim would disagree with one that does. A padded subject warns and is
  ignored.
- **A present-but-empty header is malformed, not absent.** So the writer omits absent
  optionals entirely rather than writing empty strings, or it would quarantine its own
  perfectly good messages.

### The warn/reject split

Rejects: a malformed or missing schema id, an unsupported envelope version, a non-string
header value, invalid UTF-8 on a required header, an unknown format token. Warns: a
subject/`properties.type` disagreement, an unparseable ordinal, a bad semver label, an
unreadable advisory header.

The schema id pins the exact schema, so an advisory field tells us nothing we do not already
know. Quarantining a structurally valid payload because someone mistyped a version label
would be a self-inflicted outage.

An unsupported envelope **version** stops interpretation entirely rather than reading what it
can: a v2 producer may have redefined the other headers, and guessing is worse than declining.

### Two defects the tests caught

The reader trimmed subjects, via `SubjectName.Create` — exactly what the research warned
against. And invalid UTF-8 on an advisory header fell through silently, making an unreadable
subject indistinguishable from an absent one.

## M2.2 notes (original scope)

**DESIGN §2 · ADR-010**

- [ ] Mode A writer/reader — `concordat-v`, `concordat-schema-id`, `concordat-subject`, `concordat-version`, `concordat-semver`, `concordat-format`
- [ ] All values UTF-8 strings; **decode `byte[]` on read** (RabbitMQ.Client writes `S`, reads back `byte[]`)
- [ ] No `x-` prefix (ADR-013 — RabbitMQ turns `x-` headers into AMQP 1.0 message-annotations; keeping clear of it is what makes the "1.0-safe" claim real); avoid `ulong`, `bool false`, values > 64 KiB
- [ ] Mode B: `content-type: application/json+concordat.v1.<hex-id>`
- [ ] Mode B: `0x01 | <16-byte id> | payload`, opt-in
- [ ] Legacy `0x00 | <int32 BE>` — **read-only**, for Kafka-bridge ingestion
- [ ] CloudEvents read-only interop: both `cloudEvents_`/`cloudEvents:` and `ce-`

## M2.3 Subject resolution

- [ ] `ISubjectResolver` seam
- [ ] RabbitMQ.Client resolver — `properties.type`, as-is
- [ ] Canonical-form validation `^[A-Za-z0-9_]+(\.[A-Za-z0-9_]+)*$`

## M2.0 Payload validation

**Done 2026-08-13 · ADR-009, ADR-019**

- [x] `IPayloadValidator` port in `Formats.Abstractions`
- [x] `NJsonSchemaPayloadValidator` — **NJsonSchema, MIT**
- [x] The M1.7 payload-validation corpus now **executes** against it

### The licence survey mattered

`JsonSchema.Net` is the better-known choice and its source is MIT, but its published NuGet
binary ships an **Open Source Maintenance Fee** agreement charging revenue-generating users
above US$10,000 annual revenue. That obligation would propagate to Concordat's own users —
the MassTransit-v9 and MediatR pattern a third time, and precisely what ADR-009 exists to
catch. `Newtonsoft.Json.Schema` is commercially licensed outright. NJsonSchema is MIT with a
clean `.nuspec`.

The port exists so that decision stays reversible, and so a host can substitute a validator
its own compliance process has already cleared.

### The corpus earned its keep on the first run

`integer-vs-number` failed immediately: **NJsonSchema rejects `1.0` for `type: integer`**,
implementing draft-04 semantics. From draft-06 onward — including 2020-12, the dialect
ADR-021 pins — `integer` "matches any number with a zero fractional part", so `1.0` is valid
and `1.5` is not.

The fixture was **not** relaxed to match the library. The corpus is normative; an
implementation that disagrees is what bends. `CorrectToDraft202012` drops the offending error
only when the value at that path really has a zero fractional part, and a test proves the
correction does not overreach — a document with one whole and one fractional integer property
still fails, on the fractional one only.

Concretely: without this, a JavaScript producer emitting `1.0` for a whole number would be
quarantined by the .NET consumer and accepted by every other SDK. That is the exact
cross-language divergence ADR-019 predicts, found four milestones before any second SDK
exists.

## M2.4 Middleware

- [ ] Publish: decorator over `IChannel.BasicPublishAsync`; a throw blocks the publish
- [ ] Consume: `IAsyncBasicConsumer` decorator — **nack, never throw** (throws surface via `CallbackExceptionAsync`)
- [ ] Payload validation **on by default**
- [ ] Reject to a `concordat.quarantine` exchange with failure-reason headers
- [ ] **No retry on schema violation** — deterministic, so redelivery is pure waste
- [ ] `EnforcementMode` honoured: `Off | Monitor | Enforce`

## M2.5 Header survival experiments

**🔴 Heavy · DESIGN §2 · empirical, no code deliverable**

Empirical work with no code deliverable; the result **is** the documented Mode A vs Mode B guidance.

- [ ] Dead-lettering
- [ ] Shovel
- [ ] Federation
- [ ] STOMP and MQTT adapters
- [ ] **AMQP 1.0 conversion (ADR-013)** — confirm `concordat-*` headers arrive as application-properties, not message-annotations, when the same message is read by a 1.0 client. This is the only check that turns ADR-013's "designed to survive 1.0 conversion" from an assertion into a verified property.
- [ ] Write findings back into DESIGN §2

## M2.6 Tests

- [ ] Testcontainers RabbitMQ: publish conforming + non-conforming; assert the latter lands in `concordat.quarantine` with reason headers
- [ ] Header round-trip: `string → byte[]` holds; no collision with `MT-`, `NServiceBus.`, `rbs2-`, `rabbitmq-`, `x-`
- [ ] Conformance corpus runs against the client

---

## Exit

A .NET producer publishes a valid and an invalid message; the invalid one is rejected to
quarantine with a readable reason and is not retried; the registry can be killed after
warm-up without affecting delivery.

---

← [M1 — Registry core](M1-registry-core.md) · [Plan index](../PLAN.md) · [M3 — CLI →](M3-cli.md)
