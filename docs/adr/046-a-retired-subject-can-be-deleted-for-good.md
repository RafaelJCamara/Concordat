# ADR-046: A retired subject can be deleted for good, by an administrator, through the API

- **Status:** Accepted
- **Date:** 2026-10-15
- **Deciders:** Rafael Camara

## Context

Soft delete was all there was. `DELETE …/subjects/{subject}` retires, which is terminal: the subject keeps its name, refuses
new versions and keeps every version it has. [DESIGN §4](../DESIGN.md) and decision 15 recorded the other half as an obligation owed
by M7: *"hard delete requires no registered consumers, an explicit force flag, and writes an audit entry. A version cannot be
hard-deleted while referenced by a contract, another schema, or a registered consumer."* The three things it needed (contracts,
registered services, the audit trail) have all existed since M7, and nothing used them for this.

The cost of having no deletion was concrete. A subject created by mistake, with a typo in its name or in the wrong environment,
is there for ever: the name is taken, it is listed, it counts on the dashboard, and `subject:admin` can do nothing about it.
[ADR-030](030-create-subject-screen.md) made the create screen ask twice for exactly that reason.

## Decision

**`POST /v1/environments/{env}/subjects/{subject}/purge?force=true`**, scope `subject:admin` (the scope that retires), deletes a
**retired** subject and every version it holds. It answers `200 {"subject": "...", "versionsRemoved": n}`.

A verb on the subject and not `DELETE` with a flag, so a request that merely resembles the harmless one (a retirement with a
stray query value) cannot be the destructive one. The refusals, in the order the handler asks them, each its own code so a
client can say what to do next:

| Refusal | Status | Code | What it says |
|---|---|---|---|
| `force` is not exactly `true` | 400 | `purge_requires_force` | Said before anything is read, so a request that did not mean it learns nothing about the subject. Absent, `false` and anything the binder cannot read as a boolean are all "no". |
| no such subject | 404 | `subject_not_found` | |
| not retired | 409 | `subject_not_retired` | "Retire it first." The rule is **the aggregate's** (`Subject.CheckPurgeable`), so no handler can forget it. |
| a contract binds it, or a registered service declares it | 409 | `subject_in_use` | Names up to five contracts and services, then counts the rest. A contract's publish *and* consume bindings count, and a service that *produces* the subject counts as well as one that consumes it. |
| a schema another subject's version uses refers to it | 409 | `subject_referenced` | Names up to five `subject@version`, and says when one is in another environment. |

**`force` is not an override.** DESIGN's "explicit force flag" is read as an acknowledgment that the caller means it, and every other
refusal stands with it. A flag that overrode the consumer check would make the check advice, and the point of the check is that it
is not.

**Retired first, and why.** Retiring is the decision a person can be asked about and that closes the door to new versions; the
deletion only removes what has already been said to be gone. A subject that is active or deprecated has producers and consumers
who have not been told, and deprecation is advisory: it still accepts versions. This is stricter than DESIGN, which did not
require it, and it is the one thing here worth overturning if it gets in the way (see below).

**Every consequence, said plainly:**

- **The subject and its versions are removed; schemas are not.** Schemas are content-addressed and global (ADR-015), and another
  subject, in this tenant or another, may hold the same bytes. No id is ever freed for reuse, which is the reason ADR-015 gives for
  never deleting a schema.
- **A schema only this subject reached is no longer reachable**, so `GET /v1/schemas/{id}` answers `404 schema_not_found` for it,
  as for any id the tenant never held (authorisation by reachability, ADR-015). A consumer holding such an id will see that.
  A schema another subject also holds stays reachable. **This is the consequence to read first**; both are tested over HTTP.
- **The name is free**, and a new subject with it starts at version 1, with a new owner, format and policy if it likes. Nothing in
  the registry remembers the old one except the audit trail.
- **Audited as `SUBJECT_PURGED`**, as the authenticated caller, in the same unit of work, with `N versions removed` as the detail.
  The entry survives the subject, because the audit table does not reference it, and is the only record left of what went. It is
  not notified, for the reason ADR-043 gives: a new event is a new vocabulary and, with an empty filter meaning every event, a
  message to every subscription that has one.
- **A held version goes with its subject.** A retired subject can have versions awaiting approval (the approval queue lists them,
  ADR-042); they are removed with it and leave the queue.
- **Cross-environment references count.** A reference is `concordat://{environment}/{subject}/{n}`, so a version in staging can
  refer to a subject in dev. The repository finds the versions whose schemas name the subject; the handler decides which of those
  mean *this* environment's subject, with the same resolver every other reader of a reference uses. A reference to the same name in
  another environment is not a reference to this subject.

## What it costs, and what it does not do

- **Check, then delete; not a lock.** A binding added between the checks and the commit would point at a subject that is no longer
  there. The window is one request, on a subject that is retired (it refuses versions) and that nobody is meant to be binding.
  Closing it needs a lock across two aggregates and is not built.
- **A screen, added the same day, and no CLI command.** The web app could not retire a subject either, so the subject's page
  gained one panel for both steps, for `subject:admin` only and absent for everyone else (ADR-018): *Retire subject…* while it is
  active or deprecated, *Delete for good…* once it is retired, each two clicks, the second asking for the subject's name typed out
  exactly (case-sensitive). A deletion replaces the page with what the registry said it removed. A refusal is shown in the registry's own
  words. The CLI is for CI and gets neither: deleting a contract from a pipeline is the wrong place to make that decision.
  The confirmation on the create-subject screen no longer says the name is lost "for good": it says a retired subject keeps its name
  taken and that an administrator can delete one from its page.
- **No deletion of a single version.** DESIGN's sentence about a version "referenced by a contract, another schema, or a registered
  consumer" is honoured for the whole subject. Removing a version from the middle of a history would break the contiguous ordinals the
  aggregate guarantees and every reference pinned to one; nothing asks for it.
- **Not reversible, by design.** There is no undelete and no grace period. A soft-delete period would be a second state between
  retired and gone, and a job that finishes it.
- **A subject that refers to nothing and is referred to by nothing can still be a surprise** to someone reading by name from outside
  the registry (a log, a runbook, a client that caches). The deletion cannot know, and the name is reusable at once. If that
  matters, retire and leave it.

## If you overturn it

- **To allow it without retiring first**, delete the `CheckPurgeable` call in `PurgeSubjectHandler` and the aggregate method; the
  codes and the audit action stay.
- **To keep a deleted subject's name taken for good**, the registry would need to remember it, which is a table; the audit trail is
  the only memory today.
- **To remove the capability**, delete the route and the handler; nothing is persisted that needs migrating, and the
  `SUBJECT_PURGED` entries already written stay readable (ADR-036 is why an audit action a build does not know is read as stored).
