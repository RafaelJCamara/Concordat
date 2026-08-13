# M7 — Environments, brokers, governance

**Depends on:** [M1](M1-registry-core.md) · **Unlocks:** [M8](M8-identity.md) · **Design refs:** [§4](../DESIGN.md#4-domain-model), decisions 012, 017

Contexts B, C and D of the domain model — the contract layer Kafka has no equivalent for.

---

## M7.1 Environments and brokers

**ADR-012**

- [ ] `Environment` aggregate — `Name`, `Description`, `Brokers[]`, `DefaultCompatibilityPolicy`
- [ ] `BrokerConnection` entity — `Uri`, `VirtualHost`, `CredentialRef`, `TlsSettings`, `Status`
- [ ] Connection health check

## M7.2 Credential handling

- [ ] Encryption at rest via ASP.NET Core Data Protection; disk/DB key ring self-hosted
- [ ] **Write-only over the API** — reads return `hasCredentials`, never the secret

## M7.3 Contracts

**DESIGN §4 Context B · the differentiator**

- [ ] `Contract` aggregate, environment-scoped
- [ ] `PublishBinding` — `TopologyScope + Exchange + RoutingKeyPattern → SubjectRef[]`
- [ ] `ConsumeBinding` — `TopologyScope + Queue → SubjectRef[]`
- [ ] `VersionSelector` — `Latest | Pinned(n) | Range(">=2")`
- [ ] `EnforcementMode` — `Off | Monitor | Enforce`
- [ ] Invariant: valid AMQP topic patterns; overlapping patterns cannot bind conflicting subjects without explicit precedence
- [ ] `POST /contracts/resolve` for SDK startup

## M7.4 Governance

- [ ] `ServiceRegistration` — producer/consumer intent from the SDK at startup and the CLI in CI
- [ ] **Impact analysis** — "who breaks if I change this?"
- [ ] **Promotion** `dev → staging → prod`, re-checking compatibility in the target
- [ ] Audit log + `GET /v1/audit`
- [ ] Approval reviewers; **auto-dismiss a pending approval when the change is reverted**

## M7.5 Notifications

- [ ] Outbox for domain events
- [ ] `INotificationChannel`: Email (SMTP) and Webhook
- [ ] Events: breaking change attempted/blocked, version registered, enforcement violation, subject deprecated
- [ ] Per-environment subscriptions

---

## Exit

Registering a version in staging reports exactly which registered consumers break,
notifies the subscribed channel, and can be promoted to prod with a fresh check.

---

← [M6 — Tier 2 SDKs](M6-sdks.md) · [Plan index](../PLAN.md) · [M8 — Identity →](M8-identity.md)
