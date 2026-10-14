# ADR-036: The audit read returns an action its build does not know, as stored

- **Status:** Accepted
- **Date:** 2026-10-14
- **Deciders:** Rafael Camara

## Context

`audit_entry.action` is a token stored as text, and `AuditEntryConfiguration` turned it into an `AuditAction` on
every read with a converter that **threw** for a token the build had no member for (`"The audit trail contains
action '…', which this build does not know."`). That was written on purpose: it names the row instead of reading
it as the first enum member. But the trail is the one table that outlives the build that wrote it, so the throw
has a cost that is paid exactly when a registry is rolled back, or an older instance runs beside a newer one:
**one row written by a newer build fails the whole read, for every caller, for as long as the row exists**, and
the row cannot be removed through the application because the trail is append-only.

[ADR-033](033-inherit-means-the-environments-default.md) (`SUBJECT_POLICY_CHANGED`) and
[ADR-035](035-contract-bindings-have-an-identity.md) (`CONTRACT_BINDING_CHANGED`, `CONTRACT_BINDING_REMOVED`) each
recorded it as a rollback cost, and commit `144aea4` fixed the same shape for the notification outbox and
subscriptions and said in its "Not covered" that the audit page was unchanged. Measured with the previous build
against the real database, on the registry on `:5101` with one row inserted by SQL
(`action = 'A_FUTURE_ACTION'`):

| Request | Before | After |
|---|---|---|
| `GET /v1/audit` | **500** `An error occurred while processing your request.` | 200, the row among the others |
| `GET /v1/audit?env=…` (the row's environment) | 500 (the new integration test, red first) | 200 |
| `GET /v1/audit?action=SUBJECT_CREATED` | 200 `[]` (the row is excluded by SQL before it is read) | unchanged |
| `GET /v1/audit?action=A_FUTURE_ACTION` | 400 `audit_filter_invalid` | unchanged, decided below |

The web app was already half-ready: `audit.ts` says a row's `action` is a bare `string` *"so a newer registry's
new kind of event renders as itself instead of failing the whole page"*, and the table renders an action it has
no tone for neutral. The registry was the part that refused.

The precedent from `144aea4` is to **filter or isolate what the build cannot read, never silently drop or rewrite
it.** It cannot be applied unchanged, and why it cannot is most of this decision.

## Decision

**The audit read returns every row, and an action this build was never taught comes back exactly as stored.**
Four decisions fall out of that and are made here rather than left to the code.

**1. An `AuditEntry` holds the token; the enum is a view of it.** `AuditEntry.ActionToken` (a `string`, no setter)
is the stored column with no conversion. `AuditEntry.Action` is now `AuditAction?`, computed from the token by
`AuditTokens.TryResolve`, and is **null for a token this build does not know**. `AuditEntry.Record(…)` still takes
an `AuditAction` and writes its token, so nothing that records an entry changed, and `FromStorage(…)` takes the
token, because there is no member to hand it. `AuditTokens.TryResolve` never throws, and is the one place a stored
token is classified. The wire projection (`AuditResponse.From`) spells a known action from the catalogue
(`AuditTokens.For`), so **known actions are byte-identical on the wire** whatever case a hand-edited row holds,
and spells an unknown one as the row holds it: not trimmed, not upper-cased, not mapped towards anything close.
The EF property is `ActionToken` over the same column (`action`, `varchar(64)`), so there is **no migration**; the
model snapshot's property name changes in one line and `dotnet ef migrations has-pending-model-changes` answers
*No changes* before and after.

**2. A filter naming an action this build does not know is still refused (`400 audit_filter_invalid`).** The
rows are reached by the unfiltered read, which returns them in time order with the rest, and the audit page's
action filter offers only the actions the app knows. Accepting any text would answer a typo with `[]`, which the
handler already refuses for a reversed window in so many words (*"the one answer an audit query must never give by
accident"*). Accepting a token only if a row holds it would put an `EXISTS` over a table that only grows, with no
index on `action`, on a request any read scope may make and that a typo makes expensive. The 400's message lists
the tokens this build knows, which is accurate. If an operator needs "every row of an action this build cannot
name", that is a feature with an index behind it and its own decision.

**3. Nothing can write the row back, and that is by construction and measured.** The table is append-only, so the
question is whether a row read by a build that cannot name it could be turned into something else. It cannot:
`ActionToken` has no setter (a domain test pins that `SetMethod` is null, and goes red if one is added);
`Record` takes only an `AuditAction`, so an unknown token cannot be *created* through the domain either; the
repository exposes `Append` and `QueryAsync` and nothing that updates; the read is `AsNoTracking`; and with no
converter in between, even a tracked read followed by a save would send nothing, or the text it read. The
integration test reads the row through the API and then asserts the row is **byte for byte** what was inserted
(`action|actor|target|detail|at`).

**4. The audit page shows an unknown action as received, neutral, with a hint.** The label is the token. The tone
stays `neutral`: a severity guessed for a kind of event nobody here has read the meaning of would be a mislabel,
and mislabelling is the failure `audit.ts` says the other vocabularies are strict to avoid. The one thing the
badge can add without guessing is that it is *not an ordinary one*, because an unfamiliar destructive action from a
newer registry would otherwise read as the neutral background of a registry being used: the badge carries a
`title`, *"This version of the app does not know this action. It is shown as the registry sent it."*, and an
action the app knows carries none. "Known" is `isKnownAuditAction` over `AUDIT_ACTIONS`; it never gates rendering.

**The same change fixed a latent bug the spec found in the table's tone lookup.** `TONES` is a plain object, and
`TONES['constructor']` is `Object`: an action whose token is an `Object.prototype` member (`constructor`,
`toString`, `__proto__`) rendered a badge with no colour classes at all. It is now an own-property lookup. No
registry action is spelt that way; the point of "as received" is that it must not depend on that.

## Alternatives considered

- **Add an `Unknown` member to `AuditAction`.** The obvious shape, and the one that makes the converter total. It
  loses: a member has to have a token for `AuditTokens.For` to return, and the only honest token is the original
  text, which an enum does not carry, so a row read as `Unknown` and spelt from the enum would **rewrite** what it
  said as `UNKNOWN` on the wire; every `switch` over `AuditAction` would need an arm for a case that is not an
  action; and `WireTokenTests.AuditActionsMatchWhatTheFrontendPublishes` and `EveryAuditActionHasATokenAndParsesBack`,
  which enumerate the enum and compare it with the web's list, would have to special-case the one member that must
  not be in either, without saying so. Keeping the vocabulary exactly the actions a build can *record* leaves those
  tests meaning what they say.
- **Apply `144aea4`'s filter: `action IN (known tokens)` in SQL.** It is right there, because the outbox is a
  queue and a row it cannot read **stays pending for a build that can deliver it**, so nothing is lost. The audit
  read has no such second reader and no such deferral: it exists to show what happened, and a rollback is the moment
  somebody most needs to see a change a newer build made. A filtered read would hide the row from the only people
  who would be looking for it, silently. That is the precedent's rule ("never silently drop") applied, not
  departed from.
- **Skip the row in memory, or return it with a placeholder action.** The same hiding with a log line, or a
  rewrite with a polite name. Both lose the token, which is the one fact that lets someone find out what the
  newer build did.
- **A value object (`KnownOrUnknownAction`) instead of a `string` and a nullable enum.** Cleaner on paper, and
  more to change: `Record`'s thirty-odd call sites, every test that reads `entry.Action`, and the repository
  filter. The token plus a computed view carries the same information with one new property and one nullable type,
  and the compiler still makes every reader of `Action` handle null.
- **Accept any `?action=` and filter by text.** Decision 2. It turns a typo into "nothing happened".
- **Accept an unknown `?action=` only when a row holds it.** Decision 2: a scan on a typo, for a feature nobody
  asked for yet.
- **Colour an unknown action as a warning.** It would be seen. It would also say something nobody knows: most
  unfamiliar actions are the ordinary background of a newer registry, and a page that cries wolf on every one
  teaches people to stop reading the colour.

## Consequences

- **Positive:** rolling the registry back past a change that adds an audit action no longer breaks the audit page
  or `GET /v1/audit` for anyone. This closes the cost ADR-033 and ADR-035 wrote down, and removes it from every
  *future* new action for builds that contain this change.
- **Positive:** the trail shows the row. After a rollback the audit page lists what the newer build did, under the
  name it gave it.
- **Positive:** no migration, no new wire field, no change to any known action's bytes, and no hand-written
  exception in the vocabulary tests.
- **Negative: it protects a rollback to a build that contains it.** Rolling back to `612f7c8` or earlier keeps the
  old behaviour, and a row written by a newer build still fails the read there. The cleanup is the one ADR-033 and
  ADR-035 described, and it is a decision to delete audit rows, which the application will not do for you.
- **Negative: an unknown action is not filterable.** Decision 2. An operator can read it and cannot ask for "only
  these".
- **Negative: `AuditEntry.Action` is nullable now.** A reader that wants a kind of change must handle the case of
  not knowing it. That is the point, and every existing reader recorded (`Record`) and needed no change; the test
  fake that listed recorded actions needed one `!`.
- **Negative: the integration tests insert into a database other tests share.** The row is deleted in a `finally`;
  a killed test run between the two would leave it. It is scoped to an environment of its own and every other test
  that reads the trail filters by environment or by action, so it would not fail them, but it would stay.
- **Neutral:** `AuditResponse` and the web DTO are unchanged in shape; the endpoint's description says what an
  unknown action does.
- **Not covered:** a build older than this one reading such a row (by definition); filtering by an unknown action;
  the other two stored-token converters that throw the same way, `DeploymentEvent.Action` (nothing reads
  `deployment_event` today, `IDeploymentLog.ReadAsync` has no caller) and `EnforcementViolation.Side` (read by the
  violations screens; a new side would be the trigger), which were not touched and were not probed; an unknown
  action in the audit page in a real browser against the shared
  stack (a SQL-inserted row cannot be made by the browser suite, and a permanent row in the shared database would
  be the opposite of what this is for), so the page was checked on a private registry with a row inserted and
  removed by SQL, and the table and page specs hold the rest.

## References

- [ADR-033](033-inherit-means-the-environments-default.md) and [ADR-035](035-contract-bindings-have-an-identity.md)
  — the two rollback costs this closes (each carries a dated amendment).
- [ADR-034](034-a-policy-change-notifies-subscribers.md) and commit `144aea4` — the precedent: the outbox claim
  filters on the tokens the build knows, and why that is the wrong tool here.
- `src/core/Concordat.Domain/Governance/AuditEntry.cs`,
  `src/core/Concordat.Infrastructure/Persistence/GovernanceConfiguration.cs`,
  `src/hosts/Concordat.Api/GovernanceEndpoints.cs` (`AuditResponse`),
  `web/src/app/domain/governance/audit.ts`, `web/src/app/features/governance/ui/audit-table.ts`.
- Tests: `AuditEntryTests`, `WireTokenTests.ResolvingAStoredAuditTokenNeverThrowsAndNamesOnlyWhatThisBuildKnows`,
  `AuditQueryHandlerTests`, `AuditResponseTests`, and in `GovernanceApiTests`
  `ARowWhoseActionThisBuildWasNeverTaughtIsReadAsReceivedAndTheRestUnchanged` (real PostgreSQL), and the audit
  table and page specs.
