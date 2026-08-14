# M7 — Environments, brokers, governance

**Depends on:** [M1](M1-registry-core.md) · **Unlocks:** [M8](M8-identity.md) · **Design refs:** [§4](../DESIGN.md#4-domain-model), decisions 012, 017

Contexts B, C and D of the domain model — the contract layer Kafka has no equivalent for.

---

## M7.1 Environments and brokers

**ADR-012**

- [x] `Environment` aggregate — `Name`, `Description`, `Brokers[]`, `DefaultCompatibilityPolicy`
- [x] `BrokerConnection` entity — `Uri`, `VirtualHost`, `CredentialRef`, `TlsSettings`, `Status`
- [x] Connection health check

## M7.2 Credential handling

- [x] Encryption at rest via ASP.NET Core Data Protection; disk/DB key ring self-hosted
- [x] **Write-only over the API** — reads return `hasCredentials`, never the secret

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

- [x] `ServiceRegistration` — producer/consumer intent, `POST /v1/environments/{env}/services`,
      reported by the .NET SDK at warm-up (`ConcordatClientOptions.ServiceName`)
- [x] **Impact analysis** — "who breaks if I change this?" (`GET|POST …/subjects/{s}/impact`, `concordat impact`)
- [x] **Promotion** `dev → staging → prod`, re-checking compatibility in the target
- [x] Audit log + `GET /v1/audit`
- [x] **Auto-dismiss a pending approval when the change is reverted**
- [ ] Approval **reviewers** — deferred to [M8](M8-identity.md). A reviewer set is a statement
      about *who may approve*, and there is no `User`, `Membership` or `Role` to name until
      identity exists. Approve/reject works today and records who decided; what is missing is
      the rule about who is allowed to.

**Impact runs FORWARD, and getting the direction wrong would invert every answer.**
Registration asks BACKWARD by default — can the new schema read what the old one wrote?
Impact asks the opposite question about a different party: a consumer still holding
version K, faced with data written under the candidate. Concretely, **adding a required
field is breaking to register and harmless to consume**, because data written under it
always carries the field. Reporting that as consumer-breaking would cry wolf on the single
most common schema change there is.

**A consumer on `latest` is reported as following, not guessed at.** The registry knows
what such a consumer fetches, not what its code was built against. Calling it safe would
be a guess; calling it broken would make every report useless. A range is judged at its
**floor** — `>=1` claims to handle version 1 onward, so version 1 is the reader that has
to survive.

**Declaring is opt-in and never blocks startup.** The SDK reports nothing unless
`ServiceName` is set — a machine name or process id would fill the service table with rows
nobody recognises, which is worse for impact analysis than an empty table that at least
says "nobody has declared themselves". The report runs *after* the bootstrap payload is
ingested and its failures are swallowed, because a service that would not start over a
governance nicety has put the registry back on the critical path.

**`LastSeenAt` is what keeps impact analysis honest.** A registration is a claim made once
and then true until contradicted; without a timestamp a service decommissioned a year ago
blocks a change forever. Stale entries are reported and marked, never dropped.

**The audit trail is written in the same transaction as the change it records**, which is
why there is no `IAuditLog.Update` and no delete. It follows that a *refused* request
leaves no entry — a refusal never reaches a commit, so there is no transaction to ride
along with. A breaking registration is not a refusal: it succeeds, and lands as
`VERSION_SUBMITTED`.

**Promotion is an ordinary registration in the target, not a copy of a row.** A version
compatible in `dev` says nothing about `prod`, whose history is older and whose policy may
be stricter. A breaking promotion lands as a proposal rather than being refused — ADR-017
applied consistently, since making promotion the one exception would mean the
safest-sounding operation is the only one that cannot be reviewed. The content-addressed
id (ADR-015) is asserted to be unchanged, so an in-flight envelope stays resolvable.

**Reverting is detected as "the submitted schema is already the approved tip".** That path
previously returned early on the idempotency branch without touching anything, leaving a
reviewer holding a proposal no repository contained. The wider rule — dismiss whenever any
other schema registers — was deliberately not taken: a second, unrelated change is not a
withdrawal of the first.

**`Subject.Revision` closed a latent concurrency hole.** `xmin` only changes when the
subject *row* is updated, so `Reject` — which touches only a child row — slipped past the
optimistic-concurrency guard entirely, and M7.4's dismissal would have too. Every mutator
now advances a counter on the root.

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
