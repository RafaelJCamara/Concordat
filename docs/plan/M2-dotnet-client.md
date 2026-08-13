# M2 — .NET client + RabbitMQ.Client middleware

**Depends on:** [M1](M1-registry-core.md) · **Unlocks:** M3 runtime bits · **Design refs:** [§2](../DESIGN.md#2-the-signet-envelope-adr-010), [§3](../DESIGN.md#3-subject-naming-adr-011), [§5](../DESIGN.md#5-api-surface-and-cross-language-strategy), [§6](../DESIGN.md#6-client-sdk-design--rabbitmqclient-only-adr-020), decisions 010, 011, 020

Scope is RabbitMQ.Client only (ADR-020). Service-bus adapters are [Appendix A](../DESIGN.md#appendix-a--framework-adapter-research-deferred-adr-020).

---

## M2.1 `Signet.Client`

- [ ] Typed HTTP client over the `/v1` surface; API-key auth
- [ ] Problem Details → typed exceptions, `signetCode` preserved
- [ ] Cache: `schema-id → schema` and `subject+version → schema-id` **immutable, cached forever** (guaranteed by content-addressing, not assumed)
- [ ] Cache: `subject → latest` 30 s TTL; contract resolution 60 s TTL; negative lookups cached
- [ ] Warm-up via `POST /bootstrap`, **with jitter** — a fleet-wide rolling restart must not stampede
- [ ] `fail-open | fail-closed` configuration
- [ ] **Hard rule enforced by test: after warm-up the registry is never in the delivery path**

## M2.2 Envelope (DESIGN §2)

- [ ] Mode A writer/reader — `signet-v`, `signet-schema-id`, `signet-subject`, `signet-version`, `signet-semver`, `signet-format`
- [ ] All values UTF-8 strings; **decode `byte[]` on read** (RabbitMQ.Client writes `S`, reads back `byte[]`)
- [ ] No `x-` prefix (ADR-013 — RabbitMQ turns `x-` headers into AMQP 1.0 message-annotations; keeping clear of it is what makes the "1.0-safe" claim real); avoid `ulong`, `bool false`, values > 64 KiB
- [ ] Mode B: `content-type: application/json+signet.v1.<hex-id>`
- [ ] Mode B: `0x01 | <16-byte id> | payload`, opt-in
- [ ] Legacy `0x00 | <int32 BE>` — **read-only**, for Kafka-bridge ingestion
- [ ] CloudEvents read-only interop: both `cloudEvents_`/`cloudEvents:` and `ce-`

## M2.3 Subject resolution

- [ ] `ISubjectResolver` seam
- [ ] RabbitMQ.Client resolver — `properties.type`, as-is
- [ ] Canonical-form validation `^[A-Za-z0-9_]+(\.[A-Za-z0-9_]+)*$`

## M2.4 Middleware

- [ ] Publish: decorator over `IChannel.BasicPublishAsync`; a throw blocks the publish
- [ ] Consume: `IAsyncBasicConsumer` decorator — **nack, never throw** (throws surface via `CallbackExceptionAsync`)
- [ ] Payload validation **on by default**
- [ ] Reject to a `signet.quarantine` exchange with failure-reason headers
- [ ] **No retry on schema violation** — deterministic, so redelivery is pure waste
- [ ] `EnforcementMode` honoured: `Off | Monitor | Enforce`

## M2.5 Header survival experiments 🔴 (DESIGN §2)

Empirical work with no code deliverable; the result **is** the documented Mode A vs Mode B guidance.

- [ ] Dead-lettering
- [ ] Shovel
- [ ] Federation
- [ ] STOMP and MQTT adapters
- [ ] **AMQP 1.0 conversion (ADR-013)** — confirm `signet-*` headers arrive as application-properties, not message-annotations, when the same message is read by a 1.0 client. This is the only check that turns ADR-013's "designed to survive 1.0 conversion" from an assertion into a verified property.
- [ ] Write findings back into DESIGN §2

## M2.6 Tests

- [ ] Testcontainers RabbitMQ: publish conforming + non-conforming; assert the latter lands in `signet.quarantine` with reason headers
- [ ] Header round-trip: `string → byte[]` holds; no collision with `MT-`, `NServiceBus.`, `rbs2-`, `rabbitmq-`, `x-`
- [ ] Conformance corpus runs against the client

---

## Exit

A .NET producer publishes a valid and an invalid message; the invalid one is rejected to
quarantine with a readable reason and is not retried; the registry can be killed after
warm-up without affecting delivery.

---

← [M1 — Registry core](M1-registry-core.md) · [Plan index](../PLAN.md) · [M3 — CLI →](M3-cli.md)
