# ADR-034: A subject's policy change notifies subscribers, and a subscription with no event filter is one

- **Status:** Accepted
- **Date:** 2026-10-14
- **Deciders:** Rafael Camara

## Context

[ADR-033](033-inherit-means-the-environments-default.md) made a policy change an audit entry
(`SUBJECT_POLICY_CHANGED`) and held a notification back: `NotificationEvent` is a closed, deliberately small set
(an event "a team would configure an alert for"), a new member is a new subscription vocabulary, and *who is
told* was the maintainer's to decide, not a side effect of recording who did it. The confirmation on the subject
page said *Nobody is notified*, and `APolicyChangeNotifiesNobody` measured it. **The maintainer has now decided:
add it.** This is that decision, and what it costs.

The case for it is the one ADR-033 already named. A policy decides how the next registration is judged, so a
team reading a subject may find the next version judged **more laxly** (a breaking change now goes live without a
reviewer) or **more strictly** (a change that used to go straight in is now held) than the one it built against.
Nothing else on the page tells it, and the audit log is read by people who go looking.

## Decision

**A new event, `NotificationEvent.SubjectPolicyChanged`, with the wire token `SUBJECT_POLICY_CHANGED`** — the
same spelling as the audit action, so one name means one thing: "who changed it" is the audit filter, "tell me
when it changes" is the subscription, and both are that token (`WireTokenTests` pins the spelling on each side
and that they are equal). It is the only vocabulary change in this decision, and it touches every place that pins
the notification vocabulary: `NotificationEvent` and `NotificationTokens`, `WireTokenTests`, the web's
`NOTIFICATION_EVENTS` and `NOTIFICATION_EVENT_MEANING` (the subscription form's boxes, and its "n of 8"), and the
policy confirmation. It is stored as its token in `outbox_message.event` (`varchar(64)`) and in a subscription's
`events` text, so there is no migration.

**Staged from `SetCompatibilityPolicyHandler`, in the same unit of work as the audit entry and under the same
guard** (`previous != command.Policy`). So: a change stages one message and one entry in one `SaveChanges`; a
request that changes nothing (the policy the subject already has, including inherit on an inheriting subject)
stages nothing and writes nothing, still answering `200`; a refused request (half a policy, an unknown subject, a
retired one) never reaches a commit and stages nothing. This is the outbox's whole point (an alert for a change
that rolled back is worse than none) and is the rule an idempotent registration already follows.

**The message names the subject, the caller, and the old and new policy:**

> The compatibility policy of acme.orders.OrderCreated was changed by carol: BACKWARD/WIRE -> FULL/SOURCE.
> ("inherited" means the environment's default applies.) Versions already registered are not judged again.

The policy is spelled `MODE/SURFACE` or `inherited` by the **same function the audit detail uses**
(`SetCompatibilityPolicyHandler.Describe`), so a subscriber and an auditor read the same words for the same
change; a test holds that the message carries the entry's own change text.

**It names the caller, which is a decision.** The deprecation message does not; the approval, rejection,
promotion and breaking-registration messages all end "by *actor*", and the subscription form already tells
whoever creates a webhook that "a webhook body names subjects, versions and reviewers". Naming wins for three
reasons. A team told its contract is now judged differently asks *who*, and the person reading a mailbox or a
webhook channel cannot read the audit log. The caller is the authenticated one (the command carries no actor, so
a request cannot name another), the same value the audit entry holds, so the two cannot disagree. And the one
precedent for silence, deprecation, is advisory (it still accepts versions), where a rule change is not. The
cost is that a person's identifier now goes to whatever endpoint a subscription names; that was already true of
four events, and a webhook must already be `https`.

**No stricter-or-laxer wording.** Deciding which of two policies is stricter is not a comparison of two enums:
there are seven modes on one axis and three surfaces on another, and `FORWARD` against `BACKWARD` is not ordered.
A message that guessed would be wrong in exactly the cases a person most needs it. Both policies are in the
message and the screen's page explains each; the reader decides.

**Not changed: an empty event set still means every event** (`NotificationSubscription.Wants`, the web's
`isAllEvents`, the registry's own rule, for the reason M7.5 gives: the alternative is a subscription
that is configured, enabled, and silently delivers nothing). That rule is what makes the next section a
behaviour change.

### The behaviour change to treat as one

**Every existing "all events" subscription starts receiving policy changes the moment this ships.** A subscription
with no event filter wants every event, and there is now one more. A subscription that lists its events does not
(measured: `APolicyChangeIsDeliveredToTheSubscriptionsThatWantIt` holds a subscription listing the seven
pre-existing events beside one with no filter and one listing only the new event: the first is not sent a policy
change, the other two are). Nothing is migrated and nothing is sent about a change that happened before the
upgrade.

**How loud.** Policy changes are rare, administrative and require `subject:admin`: this is not a new stream
of per-registration traffic. A subscription with no filter is already receiving every registration and every
promotion in its environment.

**What an operator should check, before upgrading:**

1. `GET /v1/environments/{env}/notifications` for each environment. Every entry with `"events": []` will start
   receiving `SUBJECT_POLICY_CHANGED`: on a **webhook**, a new `event` value the receiver may not handle (an
   allow-list of event names, a `switch` with no default), on an **email**, a new kind of message in a mailbox.
2. For each one that should not: there is no edit endpoint for a subscription's events, so either **mute** it
   (`PUT …/notifications/{id}/enabled`, which stops everything, not just this), or **delete it and create it again
   listing the events you want**. A recreated webhook is issued **a new signing secret**, which the receiver must
   be given; that is the cost of the second option and the reason to check the receiver first.
   > **Amended 2026-10-15.** There is an edit endpoint now: `PUT …/notifications/{id}/events` replaces the list and keeps the
   > subscription's address and its signing secret, so neither option above is needed (STATUS.md has the section; the
   > `events` member is required, because an empty list means every event). A registry built before it still needs the
   > workaround.
3. A receiver that rejects an unknown event name with a non-2xx answer will have the message retried five times
   and then parked (`GET /v1/notifications/outbox` shows the parked count). It does not affect any other message.

### Rolling back past this change has a real cost, and it is worse than the audit one

ADR-033 recorded that a build which does not know `SUBJECT_POLICY_CHANGED` as an audit action throws when it reads
such a row (`AuditEntryConfiguration`), which breaks the audit page. **The notification vocabulary has the same
property in two more places, and one of them stops delivery for everyone:**

- **The outbox.** `OutboxMessageConfiguration` converts the stored `event` back with `NotificationTokens.TryParse`
  and throws `InvalidOperationException("The outbox contains event '…', which this build does not know.")`. The
  pump's one query (`ClaimDueAcrossTenantsAsync`: undelivered, unparked, due, oldest first, fifty) materialises
  its rows, so **a single undelivered `SUBJECT_POLICY_CHANGED` row that is due and reaches the front fails the
  whole pass, for every tenant and every environment, on every pass**, and nothing marks it delivered. The pump
  logs it (`An outbox pass failed. The next pass will retry.`) and does not die, so the registry keeps accepting
  changes and **quietly stops telling anyone about any of them**, which is the failure the pump's own comment says
  it exists to prevent. A *delivered* or *parked* row is never read by the pump, and a count (`GET
  /v1/notifications/outbox`) does not materialise, so those are inert.
- **A subscription that lists the event.** `NotificationSubscriptionConfiguration` throws the same way
  (`A subscription names event '…', which this build does not know.`), so `GET /v1/environments/{env}/notifications`
  answers 500 for that environment, and the pump's read of that environment's subscriptions throws, with the same
  consequence as above for any message in it.
- **The audit row**, as ADR-033 said.

**Before rolling back**, from the new build or in SQL: delete any subscription whose `events` names
`SUBJECT_POLICY_CHANGED`; and mark any undelivered `SUBJECT_POLICY_CHANGED` outbox row delivered (or parked), which
is a decision that its notification will not be sent (`UPDATE outbox_message SET delivered_at = now() WHERE event
= 'SUBJECT_POLICY_CHANGED' AND delivered_at IS NULL`). The audit rows can only be waited out, as ADR-033 said.
The probe that measured the above is in [STATUS.md](../STATUS.md).

> **Amended 2026-10-14, later the same day.** The outbox hazard above is closed for builds that contain the fix, in a
> separate change: the pump's claim no longer reads a row whose event token the build does not know (it filters on the
> tokens it does, in SQL, so the row stays pending, still counted, for a build that can read it), and the dispatcher fails
> only the messages of an environment whose subscriptions it cannot read, instead of aborting the pass. So a **future**
> new event does not carry this rollback cost, **but rolling back to `01bce23` or earlier, which lack the fix, still
> does**, and the cleanup above still applies to them. `GET …/notifications` still answers 500 for an environment holding
> a subscription naming an unknown event, and the audit page's behaviour is unchanged. STATUS has the measurement.

## Alternatives considered

- **Keep ADR-033's "No notification".** Rejected by the maintainer; the reasoning above is why: a rule change that
  a consumer team cannot learn of except by looking is the case an alert is for.
- **Reuse an existing event** (`SUBJECT_DEPRECATED` or an "updated" event with a detail). Rejected for the reason
  ADR-033 gave for the audit action: a subscription that wants deprecations does not want this, and a filter that
  returns two different things makes every receiver read the body to find out which. A new event costs a vocabulary
  change and the rollback hazard above; reuse would cost the subscription its meaning.
- **Leave subscriptions with no filter out of it** (a "new events are opt-in" rule). Rejected: it would give
  `Events` two meanings of "empty" (every event *as of when it was created*, or every event), and the registry's own
  reason for the rule is that two spellings of "all" is one too many. It would also hide the new event from exactly
  the subscriptions whose owners asked to hear everything. The price is the behaviour change above, written down
  rather than avoided.
- **A migration that rewrites every empty subscription into an explicit list of the seven.** Rejected: it
  destroys the distinction between "everything" and "these seven", on the first day a registry has an eighth, and
  there is no way back from it.
- **Say which policy is stricter** in the message. Above.
- **Leave the caller out**, as the deprecation does. Above.
- **Notify on a no-op**, as the owner path once audited one. The owner path's entry was a recorded defect; a
  notification is read by a person who then goes looking for a change, and there is none.

## Consequences

- **Positive:** a team reading a subject hears that it is now judged differently, at the time, with who and from
  what to what, without anyone remembering to look. The audit log and the notification are one name.
- **Positive:** the confirmation on the subject page is true again, in both directions: it said *Nobody is
  notified* while that was so, and now says who is, including the half nobody expects (every subscription with no
  event filter).
- **Negative: a behaviour change for every subscription with no event filter**, above, with what to check.
- **Negative: rolling the registry back past this change can stop delivery for every tenant**, above, until the
  subscriptions and outbox rows that name the event are dealt with. Worse than ADR-033's audit cost, which only
  broke a page.
- **Negative:** the web's event list and the registry's are two lists, as before; a version-skewed pair (a new web
  against an old registry) offers a box the registry answers `400 subscription_invalid` for. Held at runtime by
  the browser suite, which subscribes to the new event through the real screen.
- **Neutral:** an unfiltered subscription's webhook body carries `"event": "SUBJECT_POLICY_CHANGED"`, a value no
  receiver has seen.
- **Not covered:** a real webhook or SMTP delivery of this event (the delivery test pumps the real dispatcher over
  real PostgreSQL with a channel that records what it is sent, as the existing notification tests do for the others);
  the `concordat` CLI, the SDKs and the GitHub Action, which hold no copy of the event list (a search of the repository
  for the existing tokens finds none outside the registry and the web app) and were not run against this change; the
  **browser** does not assert that a message is staged (the registry's pump drains the outbox every fifteen seconds,
  so a depth read from a browser races it): that is the .NET suite's, and a one-off probe against the running
  registry is recorded in STATUS.

## References

- [ADR-033](033-inherit-means-the-environments-default.md) ("No notification", and its alternatives, which this
  reverses), [ADR-032](032-subject-settings.md), [ADR-017](017-gated-latest-pointer.md)
- [M7.5 Notifications](../plan/M7-governance.md), [STATUS.md](../STATUS.md), [DECISIONS-PENDING.md](../DECISIONS-PENDING.md)
  decision 39
- `src/core/Concordat.Domain/Governance/OutboxMessage.cs` (`NotificationEvent`, `NotificationTokens`),
  `src/core/Concordat.Application/Registry/DiffAndLifecycle.cs` (`SetCompatibilityPolicyHandler`),
  `src/core/Concordat.Infrastructure/Persistence/NotificationConfiguration.cs`,
  `tests/Concordat.Api.IntegrationTests/NotificationApiTests.cs`, `GovernanceApiTests.cs`,
  `web/e2e/notifications.spec.ts`, `web/e2e/subject-settings.spec.ts`
