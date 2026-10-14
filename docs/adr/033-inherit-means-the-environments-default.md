# ADR-033: A subject with no policy of its own is judged by its environment's default, and changing a policy is audited

- **Status:** Accepted
- **Date:** 2026-10-13
- **Deciders:** Rafael Camara

## Context

[ADR-032](032-subject-settings.md) put an owner and policy editor on the subject's page and, to say
only what the registry was measured to do, ended up describing two registry defects in its confirmations
rather than fixing them. Both were left as decisions for the maintainer. This is the decision: fix them, and
make the sentences say what is now true. Two changes, kept separable (Task A and Task B in the history):
**A** is a behaviour change to what the registry holds; **B** is a new audit action.

### A. "Inherit" did not mean what every document says it means

`Subject.CompatibilityPolicy` is nullable so that *inheriting* and *choosing the current default* stay
distinguishable ([M1.1](../plan/M1-registry-core.md)). `Subject.EffectivePolicy(environmentDefault)`
resolves it, `Environment.SetDefaultCompatibilityPolicy` says a change "takes effect immediately for every
subject that has not set its own", [ADR-016](016-two-axis-compatibility.md) says the pair is "configured per
environment and overridable per subject", and `CompatibilityPolicy.Default`'s own comment says it is "the
*environment* seed, not a subject fallback". Five call sites resolve the policy; two of them agreed with all
of that and three did not:

| Call site | Passed to `EffectivePolicy` | Before this change, measured |
|---|---|---|
| `POST …/versions` (`RegisterVersionHandler`) | `CompatibilityPolicy.Default` | `NONE` environment, inheriting subject, breaking v2 → **`AWAITING_APPROVAL`** (it should have been `ACTIVE`) |
| `POST …/compatibility` (`CheckCompatibilityHandler`) | `CompatibilityPolicy.Default` | the same subject: `compatible: false`, `policy: BACKWARD · WIRE_JSON` |
| `GET …/versions/1/diff/2` (`DiffVersionsHandler`) | `CompatibilityPolicy.Default` | the same subject: `policy: BACKWARD · WIRE_JSON` |
| `GET`/`POST …/impact` (`AnalyseImpactHandler`) | the environment's | right: in a `BACKWARD · WIRE` environment a consumer pinned to v1 was *not* reported broken by an `id: string → integer` change, in a `NONE · WIRE_JSON` one it was (the surface decides, and it was the environment's) |
| `POST …/promote` (`PromoteVersionHandler`) | the environment's | right: a version promoted into a `NONE` environment onto an inheriting subject landed `ACTIVE` |

So one subject had two answers to "what is my policy", and the dry run CI calls before pushing disagreed
with the registration it was a rehearsal for. A pinned subject was never affected: it never reads the
environment. The probe ran against the real registry in new environments and subjects named by timestamp,
and was run again after the change; the table above is the "before", and the "after" is in the section
below.

**There is no "unset" default.** The brief asked what an environment whose default is not set resolves to.
The registry has no such state: `Environment.DefaultCompatibilityPolicy` is a non-nullable value, creating an
environment with both members absent stores `BACKWARD · WIRE_JSON` (`Environment.Create` substitutes
`CompatibilityPolicy.Default`), and an update with both absent leaves the stored value alone. Measured:
`POST /v1/environments {"name":…}` answers `defaultCompatibilityPolicy: {BACKWARD, WIRE_JSON}`, and so does a
`PATCH` that sends two nulls. (The web app's environment model has a "not set" case and the settings screen
an option for it; the registry cannot produce one. See "Found, not fixed".) The only way to reach a subject
with no environment row is legacy data from before environments were rows, or a command built by hand without
a name; that is the one place a fallback is needed, and it is `CompatibilityPolicy.Default`.

### B. A policy change was not in the audit trail

`PUT …/compatibility-policy` saved through the endpoint handler itself (`SubjectEndpoints.SetPolicy`), which had
no `IAuditLog` or `IOutbox` in reach, so changing what the registry will let through left no trace, while the
owner change next to it (`PATCH`, `UpdateSubjectHandler`) did. The audit action is a vocabulary the frontend
pins by name (`WireTokenTests.AuditActionsMatchWhatTheFrontendPublishes`, `web/src/app/domain/governance/audit.ts`),
so recording it touches both sides.

## Decision

### A. Registration, the dry run and the diff read the environment

Registration, the dry-run check and the diff resolve an inheriting subject's policy against the environment's
default, through one function, `EnvironmentPolicies.EffectiveFor(subject, environment)`, so that there is one
rule and not three copies of it. Registration already holds the environment (`RegistrationGate` loads it,
and creates it on the first write, which is why the new row's default is the built-in); the check and the diff
take only an `EnvironmentId`, so they load it with `IEnvironmentRepository.FindAsync(EnvironmentId)`, the same
call the gate makes, rather than `RequireAsync` by name as impact and promotion do. Those two are *about*
environments and refuse an unknown one (`environment_not_found`); the check and the diff are about subjects,
and a subject can exist in an environment with no row (the same legacy case), where refusing would turn a read
that works today into a 404.

**A missing environment row falls back to `CompatibilityPolicy.Default`, and only that.** It is the value
every environment is created with, so for an environment nobody has configured it is also what "the
environment's default" would have said. Pinned by a test at each of the three sites.

**Not re-judged.** Versions already registered are not judged again, and a version held at the approval gate
stays held when a default is relaxed. That is the rule ADR-032 measured for a subject's own policy and it holds
for an environment's default too: a registration is judged once, when it is made, by whatever the policy was
then. It is tested (a held version stays held after the environment's default goes to `NONE`).

**What this changes, for whom.** Only an environment whose default is **not** `BACKWARD · WIRE_JSON` has
inheriting subjects that now behave differently, and only from their next registration:

- an environment whose default is **laxer** (`NONE`, `FORWARD`, `BACKWARD · WIRE`): a version the built-in would
  have held now goes in `ACTIVE`, and `latest` moves;
- an environment whose default is **stricter** (`FULL`, any `*_TRANSITIVE`, `· SOURCE`): a version the built-in
  let through is now held at the approval gate, and `latest` does not move;
- `concordat check` and the dry-run endpoint change with it, so a pipeline gate that was red for an inheriting
  subject in a `NONE` environment goes green, and one that was green in a `FULL` environment can go red. A gate
  and a registration now agree, which is the point.

**What an operator with such an environment should check**, before upgrading: `GET /v1/environments` for each
environment's `defaultCompatibilityPolicy`; and for each one that is not `BACKWARD · WIRE_JSON`, which subjects
have `compatibilityPolicy: {mode: null, surface: null}` (those inherit). Nothing needs migrating, nothing
already registered changes, and a subject pinned to a policy never changes. If an inheriting subject was
relying on being judged by the built-in default in an environment whose default is something else, pin it
(`PUT …/compatibility-policy`).

**After the change, measured** (the same probe, against the rebuilt registry): in the `NONE · WIRE_JSON`
environment the inheriting subject's breaking v2 is `ACTIVE`, its dry run says `compatible: true` under
`NONE · WIRE_JSON`, and its diff names `NONE · WIRE_JSON`; the pinned subject beside it is still held and still
reports `BACKWARD · WIRE_JSON`; in the `BACKWARD · WIRE` environment the inheriting subject reports
`BACKWARD · WIRE`; impact and promotion answer as they did.

**The web app.** The subject page's inherit confirmation says what is now true: that from the next version the
subject is judged by its environment's default, and that registering, checking, comparing, impact analysis and
promotion all read it. The "As this registry stands…" sentence and `BUILT_IN_POLICY` are gone (nothing else read
it). The environment settings screen's "takes effect at once for every subject here that has not set its own" is
now true at registration, and says what "at once" is: the next version registered, checked or compared; versions
already registered or held are not judged again. A new spec, `environment-default.spec.ts`, holds it against an
environment of its own (`e2e-follows`) and moves nothing shared.

### B. A policy change is audited, in the same transaction, as the caller

`PUT …/compatibility-policy` is a command (`SetCompatibilityPolicyCommand`) with a handler
(`SetCompatibilityPolicyHandler`) beside `UpdateSubjectHandler`, so the audit append and the save are one unit of
work and the handler is tested with the same fakes (`RecordingAuditLog`, and a unit of work that says what had been
appended at the moment of commit). The endpoint still parses the body first and answers `invalid_request` before
any lookup, then dispatches: the order of refusals (400, 404, 409) and every status and body are what they were,
which the parity spec runs against the registry in full.

**A new action, `SUBJECT_POLICY_CHANGED`, not `SUBJECT_UPDATED` with a detail.** The brief preferred reuse if the
detail could say what changed without leaking anything and the action filter stayed useful. The first holds (a
policy is two enum tokens; nothing in it is private) and the second does not. The question this entry exists to
answer is *who made this stricter or laxer, and when*, across an environment: an auditor filters the trail for it.
`SUBJECT_UPDATED` is already the row for an owner change and for a deprecation, so under reuse that filter returns
all three and the person reads every detail to find the one kind they wanted. A separate action is also what
`ENVIRONMENT_UPDATED`, `CONTRACT_ENFORCEMENT_CHANGED` and `MEMBER_ROLE_CHANGED` already are: the registry names a
change to a rule by what it changed. It is the only vocabulary change in this decision, and it touches every place
that pins the vocabulary: `AuditAction` and `AuditTokens`, `WireTokenTests.AuditActionsMatchWhatTheFrontendPublishes`,
the web app's `AUDIT_ACTIONS` (which is the audit page's action filter, and its spec's option count), and the
confirmation. The audit table tones it neutral like every ordinary action. Stored as its token in
`audit_entry.action` (`varchar(64)`), so no migration. The `SubjectUpdated` member's own comment said it covered "a
subject's policy, description or lifecycle", which was never true of the code; it says owner or deprecation now.

**What the row says.** The actor is `ICallerContext.Current.Actor`, never anything in the request (the defect ADR-032
fixed in `UpdateSubjectHandler`; a `PUT` carries no actor). The detail is the old and new policy, each as
`MODE/SURFACE` (the spelling `ENVIRONMENT_UPDATED` already uses) or `inherited`: `policy inherited -> FULL/SOURCE`,
`policy BACKWARD/WIRE_JSON -> inherited`. That answers "who made this stricter" without anything else on the page.
**A change that changes nothing writes nothing**: setting the policy the subject already has (including inherit on an
inheriting subject) answers 200 as before and the trail is not told something happened that did not, the same rule an
idempotent registration follows. A refused request (half a policy, an unknown subject, a retired one) writes nothing.

**No notification.** `NotificationEvent` has seven members (a version registered, a breaking change submitted,
approved or rejected, a version promoted, a subject deprecated, an enforcement violation); none is a policy change,
and a deprecation is the only subject-level one. A consumer team *would* plausibly want to hear that a subject they
read became laxer, but a new event is a new subscription vocabulary (`NotificationTokens`, webhook bodies, the
subscription screen), and that is a decision about who is told, not a side effect of an audit entry. The handler holds
no outbox, the confirmation keeps saying *Nobody is notified*, and `APolicyChangeNotifiesNobody` measures it where a
notification would show: the tenant's outbox does not deepen.

**The owner-change entry was left alone in this change, and changed in the next one (2026-10-13, at the maintainer's
word).** It read "owner changed", with neither the previous nor the new owner, and the policy entry made it the odd one
out. Who owned a contract before, and who took it, is the first question after an incident, and owners are not secrets
(any reader sees them on the subject page). It now reads `owner <previous> -> <new>`, the new one as the registry stored
it (after its trim). A request that changes the owner *and* deprecates writes one row reading
`owner a -> b; deprecated` (it used to read only "deprecated" and lose the owner); a deprecation alone still reads
"deprecated". **A PATCH that changes nothing (an empty body, a blank owner) keeps the old "owner changed"**, because
nothing was changed and no owner is named: that is the separate recorded hole, and it is pinned so it is not altered by
accident. Setting the owner a subject already has now reads `owner a -> a`, which is at least honest about what was
asked; the form still blocks it. It changes a sentence ADR-032 pinned ("not the previous owner or the new one"), which
its amendment note and the web confirmation now reflect. Anything that parsed the old fixed string will see a
different one.

**The web app.** The policy confirmation says what is now true: the audit log gets one entry, `SUBJECT_POLICY_CHANGED`,
naming the account that made it and the policy before and after ("inherited" for none of its own), and nobody is
notified. The policy journey asserts the row (its action, the signed-in account as actor, the exact detail
`policy inherited -> FULL_TRANSITIVE/SOURCE`) and then finds it on the audit page by filtering on the new action.

## Alternatives considered

- **Weaken the claims instead** (make the docs and the screen say "inherit means the built-in"). Rejected by the
  maintainer, and rightly: it would leave a per-environment default that does nothing at the one place it
  matters, which is the only reason to have one.
- **Re-judge held and registered versions when a default or policy changes.** Rejected: a registration is judged
  once, a held version is a decision a reviewer may already be making, and re-judging the past makes the audit
  trail disagree with the data (`Environment.SetPreReleasePolicy` says the same of labels).
- **`RequireAsync` by name in the check and the diff, like impact and promotion.** Needs the route's name in two
  more queries and turns a legacy environment-without-a-row from a working read into a 404. The id is what the
  query already holds.
- **Copy the environment's default onto a subject when it is created.** Destroys the distinction between
  inheriting and pinned (`ANullPolicyIsStoredAsNull…`), and is the thing nullable policy exists to avoid.
- **Reuse `SUBJECT_UPDATED` for a policy change, with a detail.** Above: it saves a vocabulary change and a rollback
  hazard (below) and costs the filter its meaning.
- **Stage a notification for a policy change.** Above: a new event is a decision about who is told.
- **Record the previous and new owner in the same change.** Done afterwards, as its own commit, once asked.
- **Write an entry for a change that changes nothing**, as the owner path does. The owner path's entry is a recorded
  defect ("owner changed" for `{}`); this one did not have to inherit it.

## Consequences

- **Positive:** a subject has one policy. The dry run, the registration, the diff, the impact report and a
  promotion agree, in every environment, and ADR-016's "configured per environment and overridable per subject"
  is true of the code.
- **Positive:** "who made this subject stricter, and when" has an answer, by action, across an environment, and a
  policy change is attributed to the authenticated caller like every other recorded change.
- **Positive:** the confirmations no longer describe registry defects. Both sentences were pinned by tests that went
  red on purpose; they are now inverted and pin the new behaviour (`APolicyChangeLeavesNoAuditEntry` became
  `APolicyChangeIsRecordedAsTheCallerWithTheOldAndNewPolicy`).
- **Negative: a behaviour change for any environment whose default is not `BACKWARD · WIRE_JSON`**, described above
  with what to check. Nothing already registered or held changes.
- **Negative: rolling the registry back past this change breaks the audit page** for as long as a
  `SUBJECT_POLICY_CHANGED` row exists. `AuditEntryConfiguration` throws on a stored action the build does not know
  ("the audit trail contains action…"), which is deliberate (it names the row rather than reading it as the first
  member) and is the cost of any new action, written down because a rollback is when it is paid.
- **Negative:** the dry run and the diff each cost one more read (the environment row).
- **Not covered:** an environment with no row, in the browser or over HTTP (only legacy data produces one; it is
  tested at the application layer, once per call site); a policy-change notification; the owner entry's detail; the
  `concordat` CLI and the GitHub Action, which call the same endpoints and so change with them, but were not run for
  this; a `subject:write`-only account (it cannot `PUT` a policy, and no browser role is one).
- **Found, not fixed** (separable, all in [STATUS.md](../STATUS.md)): **the environment settings screen cannot change
  a default by changing one select** — it sends only the member that changed and the registry refuses half a
  policy (`400`, "needs both a mode and a surface"), so a default is changed by choosing a whole pair; pinned by
  `environment-default.spec.ts`. The screen and the web model also carry a "not set" default that the registry
  never produces. A `PATCH` that changes nothing still writes "owner changed". Retiring a subject records the
  subject's owner as the actor. *Note, 2026-10-13, later the same day: the first two are closed.* The form now sends
  both compatibility members when either select changed (probed against the real registry first: one member `400`,
  both or none `200`), and the "not set" default is removed from the web model and the screen rather than kept.
  `environment-default.spec.ts` was inverted and now changes the default one select at a time. *The last two were
  closed on 2026-10-14:* a `PATCH` that changes nothing (`{}`, a null or blank owner) writes no entry, still answering
  200 (an explicit owner the subject already has is a request and still reads `owner a -> a`), and retiring a subject
  records the authenticated caller as the actor instead of the subject's owner. The paragraph above that says a no-op
  `PATCH` "keeps the old 'owner changed'" describes the code as of the day it was written.

## References

- [ADR-016](016-two-axis-compatibility.md), [ADR-017](017-gated-latest-pointer.md),
  [ADR-028](028-schema-editor.md), [ADR-032](032-subject-settings.md)
- [STATUS.md](../STATUS.md), [DECISIONS-PENDING.md](../DECISIONS-PENDING.md) decision 39
- `src/core/Concordat.Application/Registry/` (`EnvironmentCommands.cs`: `EnvironmentPolicies`;
  `RegisterVersion.cs`, `SubjectQueries.cs`, `DiffAndLifecycle.cs`: `SetCompatibilityPolicyHandler`),
  `src/core/Concordat.Domain/Governance/AuditEntry.cs`, `web/e2e/environment-default.spec.ts`,
  `web/e2e/subject-settings.spec.ts`, `tests/Concordat.Api.IntegrationTests/InheritedPolicyApiTests.cs`,
  `tests/Concordat.Api.IntegrationTests/GovernanceApiTests.cs`
