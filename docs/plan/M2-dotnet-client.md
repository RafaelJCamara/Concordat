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

**Done 2026-08-13 · DESIGN §3 · ADR-011 · 14 corpus fixtures + 10 tests**

- [x] `ISubjectResolver` seam, with `PublishContext` and a three-outcome `SubjectResolution`
- [x] `MessageTypeSubjectResolver` — `properties.type`, as-is
- [x] Canonical-form validation `^[A-Za-z0-9_]+(\.[A-Za-z0-9_]+)*$`, via `SubjectNormalizer`
- [x] `corpus/subject-resolution` — 14 normative fixtures
- [ ] Binding it to `IReadOnlyBasicProperties` — **M2.4**, where RabbitMQ.Client is a
      dependency anyway

### Transport-neutral, following the M2.2 precedent

The seam takes a `PublishContext` of plain strings rather than RabbitMQ's types, for the same
reason `EnvelopeReader` takes a plain header dictionary: testable without a broker, shared by
every transport adapter. So it lives in the Domain and M2.3 takes no broker dependency at all.

There is deliberately **no `System.Type` on the context.** The moment a CLR type reaches the
seam, .NET's type system starts leaking into subject names that four other SDKs have to
reproduce. Turning `typeof(T).FullName` into a string is the caller's job, one layer up, where
it is obviously a .NET concern.

### "As-is" and normalisation are not in conflict

DESIGN §3 says the subject is `properties.type` **as-is**, and also that separators normalise
and assembly qualification is stripped. *As-is* means the subject is not **derived** from the
exchange or routing key — the things ADR-011 rejects. The declared type still needs rewriting,
or a publisher using `typeof(T).AssemblyQualifiedName` would register a new subject on every
assembly version bump.

Two rules, both mechanical:

- **Everything from the first comma is dropped.** DESIGN §3 enumerates assembly, version,
  culture and public-key-token; all four sit after that comma, so one rule covers the list and
  leaves nothing for another SDK to get subtly wrong.
- **`+` and `:` become `.`** — and the list is **closed**. A fixture pins that `/` does not
  normalise, because the natural reading of §3 is that its examples were illustrative, and one
  SDK adding `/` would split the same publisher's name across two subjects.

### Every normalisation rule is a cross-language liability

That is the whole reason this is corpus-pinned rather than merely tested. A rule here is a rule
five SDKs must apply identically or one message type becomes two subjects, so the set is small
and refusals are preferred to inventions:

- **Generic type names are refused, not mangled.** Any spelling for
  `` List`1[[Acme.Order]] `` would be an invention every SDK must reproduce character for
  character — and Go and Python have no CLR generic syntax to reproduce it *from*. Refusing is
  honest and actionable.
- **Hyphens are refused, not rewritten to underscores.** Everyone arrives from routing keys
  where `order-created` is idiomatic. Rewriting would be another shared invention, and a
  subject silently differing from what the publisher wrote is worse than a clear refusal.
- **Case is preserved, not folded.** Folding sounds helpful until it is a second lossy rewrite
  four SDKs must agree on. Consequence accepted: two teams spelling a type differently get two
  subjects, which the registry's subject list makes visible.

### One accepted collision, recorded so it is not a surprise

`+` → `.` means `Acme.Orders+OrderCreated` and a top-level `Acme.Orders.OrderCreated` become
**the same subject**, indistinguishable. Refusing nested types outright was the alternative;
DESIGN §3 chose the rewrite. The fixture says so in its `why` rather than leaving it to be
discovered.

### Absent is not invalid

Three outcomes again, for the same reason the envelope reader has three. `properties.type` is
optional in AMQP and ADR-011 records the consequence plainly: a publisher that sets no type
gets no subject. That is the ordinary state of an un-instrumented brownfield publisher. An SDK
reporting it as an error would nag on every message of every legacy publisher — which is how
enforcement gets switched off wholesale.

The most valuable fixture in the set is a negative: with no type but a perfectly good exchange
and routing key present, the resolver still returns **absent**. An implementer will be tempted
to fall back, and ADR-011 rejects exactly that.

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

**Done 2026-08-13 · DESIGN §2 · ADR-013 · 14 experiments against `rabbitmq:4.1-management`**

- [x] Dead-lettering — nack, TTL expiry, and queue overflow, separately
- [x] Shovel — with `dest-add-forward-headers` both on and off
- [x] Federation — two brokers on a container network, over a real link
- [x] STOMP adapter
- [x] MQTT adapter — 5.0 **and** 3.1.1, because "MQTT" alone is not an actionable answer
- [x] **AMQP 1.0 conversion (ADR-013)** — plus the `x-`-prefixed counterfactual
- [x] Findings written into [DESIGN §2](../DESIGN.md#2-the-concordat-envelope-adr-010)

### The findings are assertions, not a report

The brief said "empirical, no code deliverable". It got one anyway, and the reason is the
failure mode of the alternative: a written-up report is true on the day it is written and
silently becomes fiction at the next broker upgrade. `tests/Concordat.HeaderSurvival` raises
real brokers and **asserts** each finding, so a change in RabbitMQ's behaviour breaks the build
rather than invalidating a paragraph nobody re-reads.

Everything survives every hop RabbitMQ itself performs. The two exceptions are below.

### ADR-013 holds — and so does its counterfactual

`concordat-*` headers reach an AMQP 1.0 client as **application-properties**. That alone would
only show our headers arrive; it would not show the `x-` prefix was ever a real hazard. So the
same message carries a deliberately `x-`-prefixed control header, and that one **is** demoted
to a message-annotation. The prefix rule is now measured to be load-bearing rather than
precautionary.

`byte[]`-on-read is confirmed too. M2.2 took it from documentation and built the reader around
it; it is the one assumption whose failure would have broken every SDK silently.

### Mode A alone does not survive AMQP 1.0 — the finding that changes advice

`properties.type` does **not** become the AMQP 1.0 `subject`. It is demoted to the
message-annotation `x-basic-type`, exactly the fate the envelope avoids.

So for an estate containing AMQP 1.0 consumers the envelope is **mandatory, not an
optimisation**: a Mode A message whose subject lives only in `properties.type` arrives with its
subject in a section an ordinary 1.0 client need not surface.

### MQTT 3.1.1 cannot be reached by any header scheme

The protocol has no user properties. Not a defect to fix — a limit to publish. The payload
arrives untouched, so Mode B works; otherwise those consumers are unvalidated. MQTT 5.0 carries
the whole envelope as user properties.

### Cost

Four new test-only dependencies, all licence-checked before use: RabbitMQ.Client
(Apache-2.0 OR MPL-2.0), Testcontainers (MIT), AMQPNetLite (Apache-2.0), MQTTnet (MIT —
declared as a licence *file* rather than an SPDX expression, the same shape that concealed
`JsonSchema.Net`'s maintenance fee, so the file was read rather than the metadata trusted).

The suite takes ~15 s, dominated by federation's two brokers.

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
