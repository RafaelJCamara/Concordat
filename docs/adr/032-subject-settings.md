# ADR-032: A subject's owner and policy are changed on its own page, and each confirmation says only what the registry was measured to do

- **Status:** Accepted
- **Date:** 2026-10-13
- **Deciders:** Rafael Camara

> **Amended 2026-10-13 by [ADR-033](033-inherit-means-the-environments-default.md).** The two registry behaviours
> this ADR describes as defects were fixed the same day: a subject with no policy of its own judged by the built-in
> `BACKWARD · WIRE_JSON` instead of its environment's default, and a policy change written to no audit entry (in
> Context, the last three rows of the confirmation table, Alternatives, Consequences and "Found and not fixed").
> Read those passages as the registry stood when this was written. The inheriting sentence now says the opposite and
> `e2e-inherit` asserts it; the policy sentence now says what is recorded (`SUBJECT_POLICY_CHANGED`, the caller, the old
> and new policy), and the policy journey asserts it. The owner entry now reads both owners ("owner a -> b") rather
> than "owner changed" (ADR-033), so the owner row of the table and the "owner changed" detail described below are the
> registry as it stood when this was written.

## Context

[ADR-030](030-create-subject-screen.md) found that a subject's owner and compatibility policy are the
only things that can change after it is created, and that nothing in the web app (or the CLI) changed
either. The create screen's confirmation said *"only through the API"*, because that was true, and
recorded the gap as a hole. This closes it.

Everything below was sent to the real registry (the compose stack, the production image) before any
code was written, and the parity and journey specs re-send it on every run. Reading `SubjectEndpoints.cs`
had most of it right and three things wrong.

**What the two endpoints accept**

- `PATCH …/subjects/{subject}` takes `{ owner, deprecate }`. The owner is trimmed with .NET's
  `Trim()` (not JavaScript's: U+0085 is stripped, U+FEFF is not) and counted after the trim against a
  256-character limit (`actor_id_invalid`). A new name, format or content model in the body is ignored
  and answered 200.
- **An owner that is empty, absent, `null` or only whitespace is ignored** and answered 200 with the
  subject unchanged. *Whitespace-only used to answer 500*; see "What was fixed".
- `PUT …/compatibility-policy` takes `{ mode, surface }`, both or neither (`invalid_request` otherwise);
  two nulls, two empty strings or two blanks clear it. Tokens are read case-insensitively, trimmed, with
  or without the underscores, and a number that *is* a member is read as one (`"2"` and `"1"` are
  `BACKWARD` and `WIRE`). An undefined number, a comma list, a negative or an unknown word is
  `invalid_request`, and a refused request changes nothing. It answers with the *policy*, not the subject.
- **The scopes differ.** One key per scope, each holding only that: `PATCH` is 200 for `subject:write` and
  `subject:admin` and 403 for the other nine; `PUT` is 200 for `subject:admin` alone. A reader gets 403
  and 403, an anonymous caller 401. No role is `write` without `admin`, so the difference only shows for
  an API key, which is why it is held by `subject-settings-parity.spec.ts` and not by a browser run.
- **A retired subject refuses both** with 409 `lifecycle_transition_invalid`. **A deprecated one takes
  both** and stays deprecated. Retired is terminal.

**What each change does and records**

- An owner change writes one audit entry, `SUBJECT_UPDATED`, with the detail *"owner changed"* — not the
  old owner, not the new one — and **notifies nobody** (only deprecating stages a notification).
- **A policy change writes no audit entry and notifies nobody.** The endpoint has no audit or outbox
  call at all.
- A policy takes effect **for the next registration**. Versions already registered are not judged again
  when it tightens, and **a version held at the approval gate stays held when it is relaxed**.
- **A subject with no policy of its own is judged at registration by the registry's built-in
  `BACKWARD · WIRE_JSON`, and not by the environment's default.** With an environment whose default is
  `NONE`, a breaking second version of an inheriting subject was *held*. The environment's default is
  read by impact analysis and promotion, so one subject has two answers to "what is my policy".
  `Environment.SetDefaultCompatibilityPolicy`'s own comment, the environment settings screen and
  `EffectivePolicy` all say it follows the environment.

**What was wrong, and is fixed here (`UpdateSubjectHandler`)**

The audit entry's actor was built from the *owner in the request* — the **new** owner, or `unknown` when
none was given. So an owner change was attributed to whoever was being made responsible, and a request
whose owner was whitespace answered **500**: the actor was built from the very text the owner rule had
just declined to apply. It records the authenticated caller now, as every other handler does.

## Decision

**On the subject's page, inline, with no route and no sidebar row.** The page already shows both values
(the owner under the name, the policy in the Compatibility cell) and a button beside each opens a
panel above them. A route (`/subjects/:name/settings`) was the other candidate and loses on three
counts. The two controls have *different* scopes, so one route guard would admit a writer to a page with
half its controls absent. The page would have to load the subject, show it again, and navigate back for
a one-field change. And the sidebar rule in [M4](../plan/M4-web-app.md) is "a row only if it is a
screen of its own", which this is not, so it would be a route nobody opens except from the page it
edits. Without a route there is no guard to add; the buttons are the gate (ADR-018).

**Absent, not disabled, on two lists.** "Change owner" reads `SCHEMA_WRITE_SCOPES` (`subject:write` or
`subject:admin`) and "Change policy" reads a new `POLICY_WRITE_SCOPES` (`subject:admin`), in
`domain/identity/scope.ts` beside `APPROVAL_SCOPES`. Neither renders for a retired subject; both
render for a deprecated one. The parity spec issues a key for each scope the registry has and checks
that the page's two lists admit exactly the keys the registry does.

**Two clicks, and the second says what it does, in the registry's terms.** The first click asks; the
confirmation is a snapshot of what would be sent (`confirming` compares it with the form, so editing
anything withdraws it) and the second sends exactly that. What each says, and what pins it:

| Sentence | Held by |
|---|---|
| Only the owner changes; the name, format, content model, policy and versions stay | `subject-settings.spec.ts` reads the subject back |
| It is a label, not access: anyone who may change the subject may change it, whoever owns it | the scope test: a write-only key changes an owner it does not hold |
| One `SUBJECT_UPDATED` entry reading "owner changed", naming the account that made it, not the previous owner or the new one | the audit read in `subject-settings.spec.ts`, and `AnOwnerChangeIsRecordedAsTheCallerAndSaysNothingMore` |
| Nobody is notified (owner) | `AnOwnerChange_NotifiesNobody`, an outbox assertion on the handler |
| It takes effect for the next version; registered versions are not judged again; a held version stays held | the "what a policy change does to registration" journey, end to end |
| Pinned: the subject does not follow the environment's default | `EffectivePolicy` (a unit test on the domain) |
| This change is not written to the audit log (policy) | `APolicyChangeLeavesNoAuditEntry` and the audit read in the policy journey |
| Inheriting is not the same as choosing the current default | `isInherited`; the two requests are different (`checkPolicyEdit`) |
| As this registry stands, an inheriting subject is judged by `BACKWARD · WIRE_JSON` whatever the environment says | the `e2e-inherit` test, below |

The last two registry facts are **defects, not designs**, and the confirmation says what is true today
rather than what was meant. Each is pinned by a test that goes red the day the registry changes, and the
failure message names the sentence, the STATUS row and this ADR as the things to change with it.

**Blocks on what the registry certainly refuses, and on drafts that have no request.** The refusals are
the registry's: an owner over 256 characters after its trim (`actor_id_invalid`) and half a policy
(`invalid_request`), reusing `registry-text.ts` and `new-subject.ts` (the owner-length rule is extracted
and shared with create, so the two cannot drift). Two things block that are *not* refusals:

- **A blank owner.** The registry answers 200 and changes nothing; there is no request that means "blank".
- **The owner or policy the page already shows.** The registry would take it and write an audit entry
  saying "owner changed" for a change that did not happen.

The second is the one place this client is knowingly stricter than the registry, and only in the case
where its snapshot is stale: somebody changed the owner since the page read it, and a person wants it back
to what the page still shows. The editor opens on a page that has just read the registry, re-reads after
every save, and says "as of the last time this page read the registry" in the message, so the way out is
a reload. ADR-030 refused to let advice veto from a snapshot; this is the exception, taken because the
registry has no refusal behind it to make the veto harmless. The messages are advice and never claim to be
the registry's.

**Nothing is applied ahead of the registry.** The status line says what the registry *answered*: the owner
it stored (it trims), the policy in its `PUT` answer (it reads `backward` as `BACKWARD`). The subject on
screen is then replaced by a fresh read (`SubjectDetailStore.refresh`), which does not raise the loading
state, so the page does not blank after a save. A failed re-read is its own notice (`refreshError`: the
change is in, the page cannot show it) and never the screen-replacing `error`.

**Deprecate is left out, and the request type cannot carry it.** `PATCH` also takes `deprecate`; the web
app sends `{ owner }` and nothing else (`ChangeOwnerDto`, pinned by a test on the body). Deprecating
**has no inverse** (`deprecate: false` is a no-op), it **notifies every subscriber**
(`SUBJECT_DEPRECATED`), and it changes what every consumer sees: that is an outward-facing, one-way action
that wants its own confirmation and its own probe of what a deprecated subject means downstream. What was
probed — a deprecated subject still accepts versions, owner and policy changes — says only that it is
advisory. Retiring (`DELETE`) is terminal and `subject:admin`, and is equally its own decision. Neither is
here.

## Alternatives considered

- **A route, `/subjects/:name/settings`, behind `schemaWriteGuard`.** Above. If more settings of a subject
  appear, this is the shape they would want.
- **A dialog.** The panel has the page's subject beside it and a refused submit must leave the form
  editable; a dialog over the page the change is about hides the value it changes.
- **One editor for both.** Different scopes, different consequences, different confirmations; a writer
  would see a form with half its fields refused.
- **Allow a no-op and make the registry skip its audit entry.** The client would then block nothing
  that is not refused, which is the stricter reading of ADR-028. It was the more principled option and
  needs a second registry change in the audit path of a screen that has no business being there; the
  stale-snapshot case it protects is rare and costs a reload.
- **Show the environment's default beside "inherit".** It needs a read across the feature boundary
  (`registry` may not import `settings`), and it would display a value registration does not use.
- **Say "inherit follows the environment's default" in the confirmation.** It is what the registry's own
  model and docs say, and it is false at registration.
- **Fix the inheriting defect here.** It changes which versions are held in every environment whose
  default is not `BACKWARD · WIRE_JSON`, which is the people who set one. It deserves its own change,
  its own tests and a note to them.
- **Record policy changes in the audit log here.** A new audit action is a new token in a vocabulary the
  frontend pins (`AuditActionsMatchWhatTheFrontendPublishes`). Small, but a decision about the registry's
  trail and not a screen.

## Consequences

- **Positive:** the hole is closed and the create confirmation is true (it says the owner and policy
  change from the subject's own page, and that the policy takes an admin).
- **Positive:** what the screen claims is *measured*. Reading the C# gave the wrong answer to three
  questions: the audit entry's actor (the new owner), whitespace (a 500) and which default an inheriting
  subject is judged by (not the environment's).
- **Positive:** the parity spec holds the owner against the real `PATCH` over about 70 rows, including each
  character in the trim set and its neighbours, and the policy over 21 token pairs, 10 half-policies, the
  refusals a select cannot send and every scope. Five mutations of the client (JavaScript's `trim()`, a
  255-character limit, no both-or-neither rule, `POLICY_WRITE_SCOPES` admitting `subject:write`, a blank
  owner that sends) each turn it red for the right reason, and the pre-fix registry turns it and the audit
  test red.
- **Positive:** initial bundle +0.47 kB (410.89 → 411.36 kB raw; the transferred figure moved less than
  the build-to-build noise of about 0.1 kB). The subject page's chunk grew from 8.79 to 28.39 kB raw
  (3.16 → 7.36 kB transferred), which is where both editors live, loaded only by somebody who opens a
  subject.
- **Negative:** two sentences in a confirmation describe registry defects, and will be wrong the day those
  are fixed. Both are pinned; neither is silent.
- **Negative:** the e2e suite makes seven more permanent subjects per run in `dev` and one in `e2e-inherit`
  (an environment whose default is `NONE`, created once, never changed). `README.md` says so.
- **Not covered:** deprecating and retiring; touch input; right-to-left text; **a `subject:write`-only
  account in a browser** (the sign-in screen takes a password, and every role that can sign in holds
  `subject:admin` or nothing; the split is held through API keys, and unit-tested on the page); a
  cross-origin registry; and a lone surrogate typed into the owner field, which is the one place the form
  is *looser* — the registry refuses the envelope with a bare 400 and no code, and the form has no rule
  to agree with.
- **Found and not fixed**, all in [STATUS.md](../STATUS.md) and none of them this screen's: registration (measured),
  the dry-run check and the diff (both by reading the code) use the built-in default for an inheriting subject (impact analysis and
  promotion use the environment's); a policy change is not audited; a no-op `PATCH` (`{}`, or a blank
  owner) still writes an audit entry reading "owner changed" (*closed 2026-10-14: it writes nothing, and retiring now
  records the caller; see ADR-033*); and retiring a subject records the
  *subject's owner* as the actor, the same defect this ADR fixed for updating one.

## References

- [ADR-011](011-subject-is-message-type.md), [ADR-016](016-two-axis-compatibility.md),
  [ADR-017](017-gated-latest-pointer.md), [ADR-018](018-admin-only-schema-editing.md),
  [ADR-028](028-schema-editor.md), [ADR-030](030-create-subject-screen.md)
- [M4.3](../plan/M4-web-app.md), [DESIGN §9](../DESIGN.md#9-frontend-architecture-angular)
- `web/src/app/domain/registry/subject-settings.ts`, `web/src/app/features/registry/`
  (`feature/subject-detail-page.ts`, `application/subject-settings-store.ts`,
  `ui/subject-owner-editor.ts`, `ui/subject-policy-editor.ts`, `ui/change-confirmation.ts`),
  `web/e2e/subject-settings.spec.ts`, `web/e2e/subject-settings-parity.spec.ts`,
  `UpdateSubjectHandler` in `src/core/Concordat.Application/Registry/DiffAndLifecycle.cs`
