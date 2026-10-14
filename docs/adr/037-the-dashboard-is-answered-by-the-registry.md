# ADR-037: The dashboard's numbers come from one endpoint in the registry, and say what they are not

- **Status:** Accepted
- **Date:** 2026-10-14
- **Deciders:** Rafael Camara

## Context

The Dashboard (`/`) was the one screen marked part-built. It derived every number in the browser from the
subject list: the subject count, the version count, the awaiting-approval count and the six "recent"
subjects. [STATUS.md](../STATUS.md) said so, and said when it stops being true: *"honest at this size and has to
move server-side the day that list is paginated"*, because a count taken from a page of rows is the count of
the page. Two things DESIGN §9 lists for it were blocked on an endpoint that did not exist: recent breaking
changes and enforcement coverage.

The same shape had a second cost that was not about pagination. Opening the landing page fetched every subject
with **every version of every subject** (the list endpoint embeds them; `Subject.Versions` is auto-included)
and counted them in a loop in the browser, to draw four numbers and six cards.

## Decision

**`GET /v1/environments/{env}/summary`** answers everything the dashboard shows, with six aggregate statements
and no per-subject loop, and the web app **derives nothing**: the old computation in `DashboardPage` is deleted,
not kept beside it, and `e2e/dashboard.spec.ts` asserts the page makes one request for the summary and none for
the subject list.

### The scope: one environment, in one tenant

An environment is the unit every other route is scoped by ([ADR-012](012-environment-over-brokers.md)) and the
unit the dashboard is already titled with ("Subjects and contracts in `dev`"). The tenant is not a parameter
and is never one: the route sits behind the same query filter as every repository, so a caller cannot ask about
another organisation's environment, and the same environment name in two organisations is two environments
(`CloudEnvironmentSummaryTests` uses `dev` in both). An environment nothing has been written to answers **zeros
and `200`**, as the subject list does, because the landing page of a fresh install must be able to render the
offer to create the first subject instead of an error.

It needs **`subject:read`**, like the violation list beside it ([ADR-018](018-admin-only-schema-editing.md),
[ADR-027](027-read-requires-authentication.md)); every built-in role holds it. The payload also carries contract
and violation *counts*, which the routes that list those gate by `contract:read`, so a key holding only
`subject:read` can learn how many contracts an environment has per mode. They are counts, with no name or
identifier, and the alternative (a payload that omits sections by scope) is a conditional shape every client has
to handle; this is the maintainer's to reverse and is listed under what this does not cover.

### The payload

```json
{
  "subjects":        { "total": 12, "active": 10, "deprecated": 1, "retired": 1 },
  "versions":        { "total": 40, "active": 35, "awaitingApproval": 2, "rejected": 1, "dismissed": 2 },
  "newestSubjects":  [ { "name", "format", "owner", "lifecycle", "latest", "versions",
                         "awaitingApproval", "createdAt" } ],
  "breakingChanges": [ { "subject", "ordinal", "status", "semanticVersion", "registeredAt",
                         "registeredBy", "decidedAt", "decidedBy" } ],
  "enforcement":     [ { "mode": "OFF" | "MONITOR" | "ENFORCE", "contracts", "publishBindings", "consumeBindings" } ],
  "violations":      { "distinct": 7, "recent": 2, "windowHours": 24 }
}
```

**Counts.** `subjects.total` includes retired subjects, because the subject list does, and a total that
disagreed with the list it links to would be the first thing anyone checked. `versions` is by status; the
dashboard's "Awaiting approval" is `versions.awaitingApproval`, which is what the approvals queue shows.

**The newest subjects** are the six most recently *created* (`subject.created_at`, ties by name), with what a
card needs. This changes what "recent" meant: the browser ranked by the registration time of the version
`latest` pointed at, which put a subject somebody had just created, and not yet registered a version for, *last*.
Creation time is a column of the subject, so the top six is a bounded read; a new subject is where it should be;
and the card now says "Created" instead of "Registered", so a tile does not carry two clocks. What is lost is
ordering by activity.

**Breaking changes** are the five most recently *proposed* versions whose status is `AWAITING_APPROVAL` or
`REJECTED`, newest first (`registered_at`, then subject name, then ordinal descending, so two requests cannot
disagree about a batch registered together). "Breaking" is what the registry did about the change, not a
judgement of it:

- a version the gate **held** (it broke the subject's compatibility policy, [ADR-017](017-gated-latest-pointer.md))
  and nobody has decided, or one a reviewer then **rejected**;
- **not** an approved one. An approved breaking change is `ACTIVE` and live; it can be told from an ordinary
  version only by `decided_at` being set, and the dashboard's question is "what is waiting on a person, and what
  did we turn away". Its approval is in the audit trail.
- **not** a `DISMISSED` one: reverted before anyone judged it.

**Enforcement coverage** is, for each mode `OFF`, `MONITOR` and `ENFORCE` (always all three, in that order, zeros
included, so the shape does not depend on the data): **how many contracts are set to it, and how many publish
and consume bindings those contracts carry.** Consume bindings are included because `ENFORCE` also quarantines
invalid deliveries, and a coverage figure that left them out would under-report what the mode governs.

**Violations** are `distinct`, the number of rows the registry holds (one row per side, route, subject and code,
[decision 25](../DECISIONS-PENDING.md), however many messages hit it), and `recent`, how many of them were *last
seen* within `windowHours` (24, fixed). A violation row has no resolved or closed state, so "open" cannot mean
unresolved; the existing repository comment that says "open violations" means "recorded".

### What the numbers are not

Said on the screen, in the OpenAPI description and here, because a dashboard is believed without checking:

- **Coverage is what contracts declare, not what happens.** The registry never sees message traffic. A contract
  set to `ENFORCE` is an instruction a client receives when it resolves a route; the number says nothing about
  whether a service is running a client that follows it, or has resolved the route at all.
- **It is a count, not a share.** There is no "percent of subjects covered", "services protected" or "traffic
  governed": a binding names its subjects in one free-text column the database does not index, so subject-level
  coverage would mean parsing every binding in the environment and would still not be traffic. A percentage would
  look like it could say something it cannot.
- **A contract with no bindings is counted as a contract** and adds no bindings. Two contracts whose bindings
  overlap are counted twice.
- **Zero violations means none were reported, not that none happened.** Only an SDK can observe a violation and
  only one that reports; an environment with no instrumented service shows zero forever.
- **`recent` is last seen, not first seen**, so a violation that started a month ago and happened this morning is
  recent, and one counted last week and fixed since still appears in `distinct` for ever (nothing closes a row).
- The six statements are **not one snapshot**. A registration landing between the version count and the newest
  subjects can leave them one version apart for a moment. Each is consistent with itself.

### What each number costs

Six statements, always, however many subjects, versions or contracts exist (`TheNumberOfQueriesDoesNotGrowWithTheData`
counts them at 2 and 60 subjects). Measured with `EXPLAIN (ANALYZE)` on the SQL EF generates, PostgreSQL 17 in
Docker on a development machine, warm cache: **one environment of 20,000 subjects and 200,000 versions, inside
tables of 200,000 subjects and 2,000,000 versions (nine other tenants of the same size), 300 contracts with 900
publish and 600 consume bindings, 1,000 violations.**

| Number | Shape | Grows with | Needs | Measured |
|---|---|---|---|---|
| Subjects by lifecycle | `GROUP BY lifecycle` over the environment's subjects | subjects in the environment | `ux_subject_name_per_environment` (tenant, environment, name), existing | 9-11 ms |
| Versions by status | the environment's subjects joined to their versions on the primary key, `GROUP BY status` | **versions in the environment** | `PK_schema_version`, existing | 98-124 ms (200 ms cold) |
| Newest subjects | top 6 by `created_at`; the two per-subject counts are correlated sub-selects, run six times | **bounded: 6 rows** | **`ix_subject_tenant_environment_created` (tenant, environment, created_at desc), new** | 14-62 ms before, **0.2-0.3 ms** after |
| Breaking changes | top 5 held or rejected, newest first | **bounded: 5 rows** | **`ix_schema_version_held` (registered_at desc) where status is held or rejected, new** | 160-570 ms before, **1.0-1.2 ms** after |
| Enforcement coverage | contracts `GROUP BY enforcement`, bindings counted per contract | contracts and bindings in the environment | `ix_contract_tenant_environment_name`, `IX_publish_binding_contract_id`, `IX_consume_binding_contract_id` (ADR-035), existing | 1.6-2.6 ms |
| Violations | one pass, two counts | violations in the environment | `ix_violation_tenant_environment_last_seen`, existing (an index-only scan) | 0.2-0.3 ms |

**The route as a whole costs about 115-140 ms at that size, and nearly all of it is one number**: counting
versions by status has to visit every version of the environment, and no index makes a count O(1). A covering
index `(subject_id, status)` was tried and brought it to about 70 ms for 25 MB and a second index entry on every
version written; that is not worth it for one number, so it was not added. The honest statement is that this
route is **linear in the versions of the environment, and that line is cheap until it is not**: at 20 times that
size (400,000 subjects) it is seconds. The remedy then is a counter maintained when a version's status changes (or
a cached summary), which this ADR does not build; it is named so that whoever sees this route slow knows which
number to look at.

**Two indexes are added, in one migration (`DashboardSummaryIndexes`)**, because both turn a read that grows
with the environment into one that does not:

- `ix_subject_tenant_environment_created`: 11 MB per 200,000 subjects, and one more index entry per subject
  created (subjects are created rarely; the dashboard is read on every visit).
- `ix_schema_version_held`: **partial**, covering only versions held or rejected (1.3 MB for 60,000 of 2,000,000
  rows), so a version registered `ACTIVE`, which is almost all of them, writes nothing to it. A version row has
  no tenant or environment of its own, so the index runs across tenants and the join to the subject filters.
  That is the usual way to make this plan go wrong (a tenant with nothing held walking every other tenant's
  held versions), so a small tenant with one held version and a tenant with no subjects were measured: the
  planner kept its subject-first plan for both (2.7-4.4 ms and 0.1 ms). The worst case it can choose is about the
  cost the query had without the index.

The status text in the partial index's predicate (`'AwaitingApproval'`, `'Rejected'`) is the stored form of two
`VersionStatus` members. They are stored by name, so renaming either would already need a data migration, and
this index is in it. The migration was run up, down and up on a database holding 2,000,000 versions
(`dotnet ef database update`): it only creates and drops indexes, and the row counts were unchanged.

## Alternatives considered

- **Keep deriving in the browser.** Rejected: the reason this ADR exists. It is the defect STATUS.md recorded,
  and it made the landing page the heaviest request in the app.
- **Page the subject list and sum the pages.** Rejected: the same loop over the same rows, with the server doing
  the paging for it.
- **A counter table, maintained on every registration and decision.** Would make versions-by-status O(1). Rejected
  for now: it is a second source of truth for a number the rows already hold, with a write path that has to be
  right on registration, approval, rejection, dismissal, and every future status; the measured cost does not yet
  ask for it, and the ADR says where to look when it does.
- **A materialised view or a cached response.** Rejected for the same reason, plus staleness: a dashboard that
  says nothing is waiting for a minute after somebody registered a breaking change is the one failure the
  "Awaiting approval" card cannot have.
- **Order the newest subjects by the version `latest` points at, as the browser did.** Needs the maximum
  registration time per subject, which is a scan of every version. Rejected: see above.
- **Include approved breaking changes** (`ACTIVE` with `decided_at` set). Possible without a migration, and it
  would answer "what changed the contract lately". Rejected for the dashboard's question; it would also need a
  third status in the panel, and the audit trail already answers it.
- **Per-subject coverage, or a percentage.** Rejected, above: it cannot be computed honestly from what the
  registry holds, and a number that looks like it can is worse than a count that says what it counts.
- **Treat violations as "open" until acknowledged.** There is no acknowledge, and adding one is a feature with a
  state machine, a route and an audit action, not a dashboard query. `recent` is the stand-in the data supports.
- **One route per section** (`/summary/versions`, …). Rejected: the screen needs all of them on every load, and
  six round trips would put back the latency the loop was removed for.
- **Per-section scopes** (`contract:read` for enforcement, and so on), with sections omitted for a caller without
  them. Considered; see the scope paragraph.
- **A caller-chosen limit** (`?newest=50`). Rejected: a caller-chosen limit is a caller-chosen cost, and the
  bound is the point.

## Consequences

- **Positive:** the numbers mean the same at ten subjects and ten thousand; paginating the subject list can no
  longer under-count the dashboard. The landing page stops transferring every version of every subject.
- **Positive:** the two panels DESIGN §9 listed exist, with empty states that say what an empty panel means and
  does not claim.
- **Positive:** the cost of each number is written down with the index it uses, and a test (not a stopwatch) holds
  the number of statements.
- **Negative:** versions-by-status is linear in the environment's versions, as above. At the size measured it is
  about a tenth of a second; it is the first thing to replace with a counter.
- **Negative:** two indexes and a migration. They cost write amplification on subject creation and on holding or
  rejecting a version, both rare.
- **Negative:** "recent subjects" lost its activity ordering (it is by creation now), and the card says "Created"
  where it said "Registered".
- **Negative:** the Approvals page still derives its queue from the subject list, as the dashboard did
  (`pending-approvals-store.ts` says so). It has the same pagination problem and is not changed here; it can read
  `breakingChanges` only for five, so it needs its own endpoint.
- **Neutral:** the six statements are not a snapshot.
- **Not covered:** a long-lived browser session sitting on the dashboard does not refresh (no polling, as before);
  the per-section scope question; the counter replacement above; the Approvals page; the .NET SDK, the CLI and
  the GitHub Action do not read this route and were not changed; the numbers were measured on one machine at one
  scale and are an order of magnitude, not a promise.

## References

- [ADR-012](012-environment-over-brokers.md) (the environment as the scope), [ADR-017](017-gated-latest-pointer.md)
  (what held means), [ADR-018](018-admin-only-schema-editing.md) and [ADR-027](027-read-requires-authentication.md)
  (the scope convention), [ADR-035](035-contract-bindings-have-an-identity.md) (the binding foreign-key index
  this uses)
- [DESIGN §9](../DESIGN.md#9-frontend-architecture-angular), [M4.3](../plan/M4-web-app.md#m43-pages), [STATUS.md](../STATUS.md)
- `src/core/Concordat.Infrastructure/Persistence/EnvironmentSummaryReader.cs`,
  `src/core/Concordat.Application/Registry/EnvironmentSummary.cs`, `src/hosts/Concordat.Api/SummaryEndpoints.cs`,
  `src/core/Concordat.Infrastructure/Persistence/Migrations/20261014121057_DashboardSummaryIndexes.cs`
- `tests/Concordat.Api.IntegrationTests/EnvironmentSummaryReaderTests.cs`,
  `tests/Concordat.Api.IntegrationTests/EnvironmentSummaryApiTests.cs`,
  `tests/Concordat.Application.Tests/EnvironmentSummaryHandlerTests.cs`, `web/e2e/dashboard.spec.ts`
