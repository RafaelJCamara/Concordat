# ADR-043: A subject can name who may decide its held versions

- **Status:** Accepted
- **Date:** 2026-10-15
- **Deciders:** Rafael Camara

## Context

[ADR-017](017-gated-latest-pointer.md) holds a breaking version at a gate and
[ADR-018](018-admin-only-schema-editing.md) says who may open it: every holder of `subject:admin`. That was the
placeholder for something M7 planned and deferred to M8 ([M7.4](../plan/M7-governance.md): *"a reviewer set is a
statement about who may approve, and there is no User, Membership or Role to name until identity exists"*). Identity
exists now, and the gap is concrete: in an organisation with several administrators, **any of them can approve a
breaking change to a contract they have nothing to do with**, and nothing on the subject says whose call it is. ADR-018
deferred the refinement ("per-subject ownership instead of a global role ... a reasonable future refinement once there
are enough subjects for it to matter"); `Subject.Owner` already exists and is a label, not access.

## Decision

A subject carries an optional set of **reviewers**, a list of actors.

- **Empty is the default and means what it always meant:** every holder of `subject:admin` may approve or reject.
  Nothing existing changes, and the migration gives every row `[]`.
- **Non-empty narrows it:** only a named actor may approve or reject a held version of *that subject*; anyone else, an
  administrator included, is refused **`403 not_a_reviewer`**. The refusal is its own code and not `insufficient_scope`,
  because the credential is sufficient and what refuses is a rule about the subject: a client that retried with a
  stronger credential, which is what `insufficient_scope` asks for, would be refused again.
- **The rule lives in the aggregate** (`Subject.Approve` and `Reject` ask it first), so no handler can forget it. It is asked
  **before** anything about the version, so a caller who may not decide learns nothing about whether the version exists or
  is held.
- **`PUT /v1/environments/{env}/subjects/{subject}/reviewers`**, `subject:admin` (the policy's scope). Body
  `{"reviewers": [...]}`; the member is **required**, an empty list clears. A body that omits it is `400`, because `{}`
  reading as "clear" would let a typo loosen a control. Answers `200` with the subject, which now carries `reviewers`.
- **At most 20, compared without regard to case, a repeat dropped and the first spelling kept**, stored as written and in
  the order given. Each entry is an actor id (non-blank, at most 256 characters). A set that is the one already held
  changes nothing, saves nothing and records nothing. A retired subject is `409`.
- **Audited as `SUBJECT_REVIEWERS_CHANGED`**, as the authenticated caller, in the same unit of work, reading the previous
  and the new set (`reviewers anyone with the scope -> a@x, b@x`). The audit vocabulary is pinned on both sides, so it
  touched `AuditAction`/`AuditTokens`, `WireTokenTests`, the web's `AUDIT_ACTIONS` and the audit page's filter (28
  options). **Nobody is notified**: a new notification event is a new vocabulary and, since an empty event filter means
  every event, a message to every subscription that has one (ADR-034 is the precedent for how large a decision that is);
  the trail is where this is looked up.
- **The queue says it** ([ADR-042](042-the-approval-queue-is-answered-by-the-registry.md)): each item carries `reviewers`
  and `mayDecide`, the subject's rule applied to the caller, computed in the handler that knows the caller with the
  aggregate's own implementation of the rule (`Subject.Allows`), not a second copy. The page omits Approve and Reject when
  `mayDecide` is false, **says why** ("Only a, b may decide this"), because an administrator who sees neither button and
  no reason would look for the bug in the page, and still lets them open the review, which is reading. A reader, who was
  never offered a decision, is not told they may not make one. `mayDecide` defaults to `false` in the read model, so an
  item that skipped the handler denies instead of allowing.

### What it is not, said as plainly as the screens say it

- **It routes a decision; it is not a boundary against an administrator.** The scope that edits the set is the scope that
  decides when the set is empty, so an administrator who wants to decide can name themselves first, and the entry is the
  trail of it. The confirmation says so ("It routes the decision and does not lock it"). A stronger rule needs a second
  scope, or a role above `subject:admin`, and is not here.
- **The proposer is not excluded.** The text in DESIGN §Context E that approve/reject being admin-only "keeps the author
  of a breaking change from waving it through themselves" was never true of one administrator who is both; this does not
  make it so. A set that names the proposer lets them decide their own proposal, and a one-person team that names itself
  would otherwise lock itself out. Independence ("never the actor who proposed it") is a rule to add on top, per subject
  or per environment, and is the natural next step.
- **Reviewers are not verified to exist**, like an owner. An entry can be a person's email address or an API key's label,
  and nothing could tell the two apart; a typo is a reviewer nobody can be. The web form says when the set would leave the
  signed-in person unable to decide (advice, not a refusal: handing a subject over is a thing a team does).
- **On an unclaimed instance** every caller is `unclaimed-instance`, so a set that does not name it refuses everyone there.
  That is the rule working, and it is undoable: the caller still holds the scope that edits the set, so a `PUT
  …/reviewers` naming `unclaimed-instance` (or an empty list) opens it again.
- **A held version is judged under the set at the moment it is decided**, not the set when it was proposed. The
  confirmation says "already waiting as well as later ones".

## Alternatives considered

- **A reviewer *group* or role.** Rejected for now: there is no group in the model (`Membership` carries a single role), and a
  set of named actors answers the question asked without inventing one. The set is the data a group would be built from.
- **Environment-wide reviewers.** Rejected: the people who answer for `orders.*` and for `payments.*` are different, which is
  why the subject already has an owner.
- **Never the proposer (four eyes) as the whole feature.** Rejected as the only rule: it deadlocks a single-administrator
  instance (the common self-hosted case) and names nobody. See above; it composes with this.
- **Verify that each reviewer is a member or a key.** Rejected: cross-tenant lookups on a write path to forbid a typo, for
  actors (a key's custom label) that are not members at all.
- **Use `Owner` as the reviewer.** Rejected: ADR-032 records that the owner is a label ("a team as often as a person") and
  anyone with `subject:write` may change it, which is the opposite of a control.
- **Notify on change.** Rejected for now, above.

## Consequences

- A new column (`subject.reviewers`, a JSON array in text, because an actor id may contain a comma and a comma-joined
  list would turn `ci, prod` into two reviewers) and one migration. It was applied to a database holding subjects without
  loss.
- **Rolling back to a build without this is a loosening, silently:** the older build ignores the column and any
  administrator may decide again. The audit rows it wrote stay, and an older build that predates
  [ADR-036](036-the-audit-read-tolerates-an-action-its-build-does-not-know.md) fails the audit read for as long as one
  exists, as ADR-033 and ADR-035 record for their actions.
- Two new error codes in the catalogue (`not_a_reviewer`, `reviewers_invalid`) and in the web's generated list.
- The CLI and SDKs do not read `reviewers`; the CLI has no approve command. `concordat push` of a breaking change still
  reports `awaiting`; whom it is waiting for is on the subject page.
- **Not verified:** the production web image and `smoke-web-image.sh`; CI; a registry upgraded in place from the previous
  build with held versions in flight (the migration was run over a database with existing subjects, not over held ones); a
  screen reader on the new textarea and its live region.
