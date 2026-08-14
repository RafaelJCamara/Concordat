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

- [x] `Contract` aggregate, environment-scoped
- [x] `PublishBinding` — `TopologyScope + Exchange + RoutingKeyPattern → SubjectRef[]`
- [x] `ConsumeBinding` — `TopologyScope + Queue → SubjectRef[]`
- [x] `VersionSelector` — `Latest | Pinned(n) | Range(">=2")`
- [x] `EnforcementMode` — `Off | Monitor | Enforce`
- [x] Invariant: valid AMQP topic patterns; overlapping patterns cannot bind conflicting subjects without explicit precedence
- [x] `POST /contracts/resolve` for SDK startup

**Overlap is intersection, not text equality.** `orders.*` and `*.created` share no
characters in common yet both match `orders.created`, so comparing the pattern strings
would let a publisher be governed by two contracts at once with no way to know which.
`RoutingKeyPattern.Overlaps` decides emptiness of the intersection of the two languages,
and the refusal quotes a key both patterns match — otherwise the author is left to
intersect two topic patterns by hand.

**A new contract is MONITOR, never ENFORCE.** A contract that started blocking the moment
it was written would have been authored by guessing about a live topology and then
discovered in production.

**An unmatched route is answered, not omitted.** `resolve` returns
`{contract: null, enforcement: "OFF"}` for a route no contract governs, because the SDK
has to tell "nothing governs this" (normal, brownfield) from "I forgot to ask" (a bug),
and the answers are positional.

**Subjects are one text column, not a child table** — a value-object list with no identity
that is always read and written together. See `ContractConfiguration`; the value comparer
there is load-bearing, not decoration.

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
