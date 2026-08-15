# M7 — Environments, brokers, governance

**Depends on:** [M1](M1-registry-core.md) · **Unlocks:** [M8](M8-identity.md) · **Design refs:** [§4](../DESIGN.md#4-domain-model), decisions 012, 017

Contexts B, C and D of the domain model — the contract layer Kafka has no equivalent for.

---

## M7.1 Environments and brokers

**ADR-012**

- [x] `Environment` aggregate — `Name`, `Description`, `Brokers[]`, `DefaultCompatibilityPolicy`
- [x] `BrokerConnection` entity — `Uri`, `VirtualHost`, `CredentialRef`, `TlsSettings`, `Status`
- [x] Connection health check
- [x] `GET|PUT …/registration-policy`, deferred from [M1.6](M1-registry-core.md)
- [x] **`RegistrationPolicy` enforced**, on subject creation and version registration alike
- [x] `Scope.Ci` — the marker that separates a build pipeline from a producer

### The policy was stored for a whole milestone without being read

`Environment.RegistrationPolicy` shipped in M7.1 with a clever default — anything named `prod`,
`production` or `live` starts at `CiOnly` — and three separate comments saying it is *"enforced
server-side, which is the whole point"*, the stated advantage over Confluent's client-side-only
equivalent. **Nothing consulted it.** A production registry created on the strength of that
sentence was exactly as open as one with no policy at all.

Building the two routes without fixing that would have shipped a control panel wired to nothing.

**`CiOnly` needs to tell a pipeline from a producer, and nothing else in the system could.**
Both arrive with an API key and `subject:write`; `ApiKeyKind` distinguishes *how* a credential
was issued, not *what holds it*. So `ci` is a marker scope: it grants nothing, is implied by
nothing, and is read by exactly one rule. **No role grants it** — not even Owner — because it
belongs on a key issued to a pipeline rather than on a human's role, and an administrator who
inherited it could walk through the control that keeps production clean.

**It gates subject creation too, not just registration.** Gating only versions would let a
misconfigured producer fill a closed environment with empty subjects: permanent clutter,
arriving through the exact door the policy exists to shut, reported as success every time.

**An environment with no row has no policy and is allowed.** Routes accept an environment name
before an `Environment` aggregate exists — the id is derived from the name — so refusing there
would refuse every registration on a registry nobody had explicitly configured. Stated because
it is a real gap, not a corner case.

**Refused with 403 and `registration_policy_forbids`, never 400.** The first run answered 400,
because an unmapped code falls through to the default — telling a pipeline author their payload
was malformed, which is the one thing it was not.

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
- [x] **The SDK actually calls it** — `ContractCache` (60 s TTL), batch resolve at warm-up from
      the declared topology, on-demand resolve for anything undeclared, stale-on-failure
- [x] Contracts decide enforcement per route, overriding the client's `Mode` in both directions
- [x] A single-subject binding supplies the subject an un-instrumented publisher omitted
- [x] Route conformance: `contract_subject_not_permitted`, `contract_version_not_permitted`
- [x] `ConcordatChannel.BasicConsumeAsync` wraps consumers, so consume is enforced like publish

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

### A governing contract wins outright; `Mode` is the default for what it does not cover

The obvious alternative — take the stricter of the contract and the client's own `Mode` — is
wrong, and wrong in the direction that matters. Under it an operator could switch enforcement
**on** centrally but never **off** again: any service configured locally to `Enforce` would keep
refusing traffic after the contract had been set to `OFF`. An off switch that does not switch
anything off is worse than no off switch, because it is believed.

So a contract that matches decides, and the client's `Mode` governs only routes nothing covers.
This is the whole reason `resolve` distinguishes a null contract from an `OFF` one: **"nobody has
written a contract for this" has to mean something different from "a contract covers this and
says do nothing"**, or the central control is unreachable from the client.

The one exception is local `Mode = Off`, which no contract can override. It means "Concordat does
nothing in this process", and honouring that must not depend on the registry being reachable — it
is also what lets `Off` cost nothing, since the channel short-circuits before resolving anything.

### What contracts let the SDK do that schemas alone cannot

- **Enforce an un-instrumented publisher.** No `properties.type` used to be the end of the line.
  A binding that permits exactly one subject supplies the one the publisher omitted, so a
  brownfield service becomes enforceable without its code being touched. Two or more subjects and
  the SDK refuses to guess — `contract_subject_ambiguous` — because choosing would silently
  validate against the wrong schema.
- **Catch the right message on the wrong route.** A payload can be perfectly valid against the
  schema it claims and still not belong on that exchange. Schema validation never sees the
  topology, so nothing but the contract can tell.
- **Catch a version the route has not agreed to.** A binding pinned to `orders.created@3` against
  a subject whose latest is 7 means the publisher is stamping identity the route does not accept.

**A route violation carries no envelope; a payload violation does.** An envelope asserts "this is
schema X, sent here deliberately" — stamping one on a message the contract does not accept would
put a claim on the wire the registry itself contradicts. A bad payload on the *right* route has no
such problem, and stamping it is what lets consumers start reading schema ids before every
publisher is clean.

**Consume-side conformance is best-effort by construction.** Mode B carries only the schema id in
the content type, so a message that arrives that way has no subject to compare against the
contract. Stated here rather than silently skipped.

### Two defects this found

- **A successful resolve could be thrown away.** The per-route lookup wrote the cache and then
  read it back demanding a `Fresh` entry, so a resolve was discarded whenever the clock moved past
  the TTL in between — on a short TTL, every time. The route was then reported *ungoverned*, which
  falls back to the client's `Mode`: enforcement switching itself off with nothing reporting an
  error. Found by an integration test with an aggressive TTL, fixed by using the resolved value
  rather than re-reading.
- **Consumers were opt-in while publishes were not.** `ConcordatChannel` decorated
  `BasicPublishAsync` on the stated grounds that "enforcement you can bypass by forgetting is the
  failure mode this product exists to prevent", and then passed `BasicConsumeAsync` straight
  through. A codebase could publish under enforcement and consume without it, and nothing said so.
  It is also the only place the queue name is known — RabbitMQ hands a delivery its exchange and
  routing key, never the queue it arrived on — so consume-side contracts could not have worked
  without capturing it there.

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

- [x] Outbox for domain events
- [x] `INotificationChannel`: Email (SMTP, MailKit) and Webhook
- [x] Events: version registered, breaking change submitted/approved/rejected, version
      promoted, subject deprecated
- [x] Per-environment subscriptions, `GET|POST|DELETE /v1/environments/{env}/notifications`
- [x] `ENFORCEMENT_VIOLATION` — **closed 2026-08-14** by [decision 25](../DECISIONS-PENDING.md).
      The violation happens in the SDK, on the publisher's machine, so the registry cannot
      observe it and the client has to say so: `POST /v1/environments/{env}/violations`, with
      `ViolationReporter` counting by fingerprint on the delivery path and `ConcordatClient
      .FlushViolationsAsync` posting the batch off it. Upserted by fingerprint, and the
      notification fires on first sight only — a broken publisher emits thousands a second and
      an alert per message is an outage of its own. **Nothing schedules the flush**: a host
      opts in by wrapping its observer and calling `FlushViolationsAsync` on a timer, which is
      the one part of this a user still has to write themselves

**The outbox exists because of the transaction, not because of the queue.** Sending an
email or a webhook inside a request handler means a change can commit and its notification
vanish, or a notification can go out for a change that then rolls back — "your breaking
change was approved" about an approval that did not happen. A row written alongside the
change makes the two atomic; a separate pump makes delivery someone else's problem. A
*refused* request stages nothing, for the same reason it audits nothing.

**Delivery is at-least-once, and that is a promise to the receiver rather than an
apology.** A crash between sending and marking sent re-sends: a duplicate "breaking change
awaiting approval" is noise, a missing one is the outage this exists to prevent. Every
notification carries an id, in the body and in an `X-Concordat-Message-Id` header, to
deduplicate on.

**A message nobody subscribed to is marked delivered, not left pending.** Nobody
subscribing is a legitimate configuration — the commonest one — and treating it as
undelivered would grow the table forever.

**Partial success counts as success.** When two subscribers want a message and one endpoint
is down, retrying re-delivers to the healthy one, so the choice is between duplicates for
the working subscriber and silence for the broken one. Duplicates win. The failure is still
recorded on the message and the retry still happens.

**Failed messages are parked after five attempts, never deleted.** A message nobody could
deliver is evidence about the channel; discarding it makes a misconfigured webhook
indistinguishable from a quiet week. `GET /v1/notifications/outbox` reports the parked
count, and nothing else will say so.

**An empty event set means every event**, and an unknown event token is refused rather than
ignored. The failure this avoids is a subscription that is configured, enabled, and
silently delivers nothing — which looks correct from every screen.

**`http://` webhooks are refused, not warned about.** A webhook body names subjects,
versions and reviewers: the shape of an organisation's message contracts.

**The pump is in-process and every instance polls**, so two instances can deliver the same
message twice. That is acceptable precisely because the contract is already at-least-once;
a leader election would add a protocol to buy a guarantee this design has already declined
to make. It never lets an exception escape — a `BackgroundService` that throws is stopped by
the host and never runs again, and the registry would keep accepting changes while quietly
telling nobody.

---

## Exit

Registering a version in staging reports exactly which registered consumers break,
notifies the subscribed channel, and can be promoted to prod with a fresh check.

**Met.** Each clause is covered by a test that exercises it over real PostgreSQL, and the
first one end to end over a real broker as well: an SDK declares `subject@1` at startup
(`ServiceDeclarationTests`), impact analysis names that service as broken by a type change,
the registration stages a notification the pump delivers, and promotion re-checks against
the target's own history.

Two gaps are stated rather than hidden: **approval reviewers** need M8's identity, and
**`ENFORCEMENT_VIOLATION`** cannot be raised by the registry because the violation happens
in the SDK. Both are in [decisions-pending](../DECISIONS-PENDING.md).

---

← [M6 — Tier 2 SDKs](M6-sdks.md) · [Plan index](../PLAN.md) · [M8 — Identity →](M8-identity.md)
