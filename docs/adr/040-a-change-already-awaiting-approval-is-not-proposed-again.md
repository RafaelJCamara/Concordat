# ADR-040: A change that is already awaiting approval is not proposed again

- **Status:** Accepted
- **Date:** 2026-10-14
- **Deciders:** Rafael Camara (asked to establish what registration should do, and to change it if it was a defect).
  Reversal is one block in `Subject.RegisterVersion` (the `if (isBreaking)` after the tip check) and the two client changes below.

## Context

Observed on a real stack and not caused by [ADR-039](039-a-schema-that-is-not-a-schema-is-refused.md): registering identical
content again while the previous copy was `AWAITING_APPROVAL` answered **201 `created: true` with a new ordinal each time**
(ordinals 2 then 3 on the stack it was seen on, one `schemaId`). Reproduced through the HTTP API on real PostgreSQL before anything was changed
(`ApprovalGateTests`, both red): three registrations of one breaking schema left four versions (the first plus three copies) and three `VERSION_SUBMITTED` audit
rows. The same code path stages a `BreakingChangeSubmitted` message each time; that was read in `RegisterVersionHandler.Audit`, not
counted.

What the code and the record said it should do, read before deciding:

- `Subject.RegisterVersion` was idempotent **only at the approved tip** (`ActiveTip().SchemaId == schema.Id`): "a retried publish
  of the current schema must not allocate an ordinal". A pending version is not the tip, so nothing matched.
- [ADR-017](017-gated-latest-pointer.md) lists "a subject can accumulate unapproved versions, so the UI must make pending state
  obvious or it becomes a graveyard" as a *cost*. It does not say copies of one change are a feature, and nothing in STATUS or
  DECISIONS-PENDING records several pending proposals of the same content as an audit trail.
- `CompatibilityHistory` already names the case: "submitting the same breaking schema twice ... A retrying CI job did it unaided",
  and fixed the half of it that moved `latest`. The half that allocates an ordinal, audits and notifies was left, and no test
  asserted either way: `ResubmittingAPendingBreakingSchemaMustNotBypassTheGate` checks the status and the pointer and nothing else.
- `RegisterVersionHandler.Audit` says an idempotent retry "writes nothing ... an entry for it would bury the trail in rows that
  describe no change". A retry of a held change wrote a full submission entry and a notification to every subscriber, each time.

An audit trail of proposals was weighed and rejected: it is the *queue* that carries a proposal, the proposals are already
distinguishable by who and when only if they differ, and a retry is by definition the same actor sending the same bytes.

## Decision

**Registering content that is already `AWAITING_APPROVAL` on the subject, while the policy would still hold it, returns that
version: `200`, `created: false`, `status: AWAITING_APPROVAL`, the existing ordinal. It allocates no ordinal, writes no audit
entry and stages no notification.** Four parts.

**1. "Already awaiting approval" is a match on the content-addressed schema id and the status, nothing else.** The label and
changelog of the retry are not compared: the first attempt's stand, as at the tip.

**2. Only while the verdict is still "held".** If the policy was relaxed after the proposal was held, the same content now
registers `ACTIVE` at a new ordinal and moves `latest`, as it did before; the held version stays held (the browser suite pins
that a relaxed policy does not release it). Answering it with the stale held version would leave the author's schema
unregistered while telling them it is.

**3. A `REJECTED` or `DISMISSED` copy is not matched.** Those are decisions about that version. Resubmitting rejected content is
a new ask and gets a new ordinal, as before.

**4. The check precedes the semver check.** The increasing-label rule counts pending labels, so a retry carrying the label its
pending version already holds would be refused `semver_not_increasing` by the very version it repeats.

**Two clients read the answer, and both had to change.**

- The CLI's `push` took `created: false` to mean "unchanged". For a retried held change that is a false all-clear: it would drop
  out of the `awaiting` count and the note that says nothing recorded is live. It now reads the status first and reports
  `awaiting-approval` with the existing ordinal.
- The new-version page said "This document is already the current version" for any `created: false`. For a held change that
  tells the author their consumers have it. It now says the change is already waiting for approval as v*n*.
- The `action.yml` GitHub Action, `Concordat.Client` and `Concordat.RabbitMq` do not read `created` (searched, none does).

## Alternatives considered

- **Leave it, as an audit trail of proposals.** Rejected for the reasons in Context: nothing records the intent, the queue is
  where proposals live, and a CI retry floods subscribers.
- **Match any version with the content, whatever its status.** Rejected: a rejected proposal is not pending, and an `ACTIVE`
  non-tip copy is a revert, which `Subject` deliberately allocates an ordinal for.
- **Match regardless of the verdict.** Rejected (part 2), measured by a test that goes red.
- **Dismiss the old pending copy and propose afresh.** Rejected: it would still allocate, audit and notify, and move the
  reviewer's link from under them.
- **Answer `409`.** Rejected: a retrying client would fail a job over a request that is, in effect, already satisfied.

## Consequences

- **Positive:** a CI retry is a no-op end to end: no ordinal, no entry, no second notification, and the same ordinal it was
  given the first time, which the author can open.
- **Positive:** the review queue holds one row per change.
- **Negative:** a client that took `201` to mean "a human should look at this" now gets `200` for the repeat. It is told the
  same `status`, and the first `201` is where the notification was sent.
- **Negative:** versions already duplicated before this build stay duplicated. Nothing is rewritten or dismissed.
- **Neutral:** `created: false` now means "nothing new was allocated", not "this is the tip"; `status` says which.
