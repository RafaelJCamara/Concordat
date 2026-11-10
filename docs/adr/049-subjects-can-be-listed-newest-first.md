# ADR-049: Subjects can be listed newest first, and one subject's held versions can be asked for by name

- **Status:** Accepted
- **Date:** 2026-11-09
- **Deciders:** Rafael Camara

## Context

The subject list is paged ([ADR-045](045-the-subject-list-is-paged-by-the-registry.md)) and searched by prefix
([ADR-038](038-subject-prefix-search.md)), and it comes in one order: by name. ADR-045 listed the gap in one line
(*"Sorting by anything but name … a separate parameter on the same route"*) and it is the first thing a person with a few
thousand subjects asks of the page: *what is new here?* The dashboard already answers it for six subjects
([ADR-037](037-the-dashboard-is-answered-by-the-registry.md): the newest by creation time, served by
`ix_subject_tenant_environment_created`); the subjects page cannot, because name order puts a subject registered today
wherever its name falls.

Two facts shape the answer:

1. **A subject has a creation time and nothing else.** `Subject.CreatedAt` is set once and never written again. There is no
   updated-at: the aggregate has a `Revision` counter, which says *that* something changed and not *when*, and a version has its
   own `RegisteredAt`. "Last changed" is therefore not a column the list could order by; it would be a new column, written on
   every registration, approval, decision, owner change, deprecation and reviewer edit, on a table the registry reads far more
   than it writes.
2. **A page that moves under the reader is a defect, not a nuisance.** ADR-045 accepted that a subject registered between two
   requests shifts the next page by one (offset, not a cursor), and the screen drops a repeat by name. That is tolerable when
   the thing that moves a row is *a new subject* — rare, and it goes to the end of an ordered-by-name list or the front of a
   newest one. It would stop being tolerable if *any registration anywhere* could pull a subject from page 9 to page 1.

A second, smaller request arrives with it. The web app will link a subject's "1 awaiting approval" pill to that subject's held
version. `GET …/approvals` ([ADR-042](042-the-approval-queue-is-answered-by-the-registry.md)) pages the whole queue at the
oldest 50, so a client that filters the page it has cannot reach a held version that sits past it: the same defect the
dashboard had before ADR-037, with a larger constant.

## Decision

**`GET …/subjects?sort=` chooses the order, and `GET …/approvals?subject=` narrows the queue to one subject.**

### `sort`

- **`name`** is today's order and **the default**. No parameter, a blank one, or `name` is the list exactly as it was, byte for
  byte: `concordat export` and every existing client send no `sort` and see no change.
- **`newest`** is by **creation time, newest first**, then by **name** as the tiebreaker (`created_at DESC, name`). Creation time
  does not change, so registering a version, approving one, changing an owner or retiring a subject does not move it in the list.
- **The order is total.** The name is unique within an environment, so no two rows tie on `(created_at, name)`, and a page
  boundary falls between two rows whatever instant they were created in. That is the property that stops a subject being on two
  pages or on none. Through the API ties are rare, since each creation reads the clock; they are not impossible (two requests
  in one clock tick, a restore or an import written straight to the database, the seed script before it spreads its
  times), and an order that is correct except on a tie fails on exactly the data nobody tested. The tiebreaker is nearly free:
  the planner reads the index in `created_at` order and sorts only inside a group of equal instants (an "incremental sort").
  The tiebreaker is the *same* ordering the name list
  uses (`OrderBy(s => s.Name)`, the column's collation), so the two orders agree about names. Note this is the database's order
  for names and not a bytewise one on every deployment: ADR-045 says "ordinally"; on the `postgres:17-alpine` image CI and the
  compose stack use, the two coincide, and on a glibc image the name order (and so this tiebreaker) follows its locale. It is
  deterministic either way, which is all totality needs.
- **It composes with everything the name order composes with**: `prefix` filters first, then the order, then `offset` and
  `limit` (clamped exactly as before: 1-500, an offset alone pages with 50, a negative offset is none); `X-Total-Count` is on
  every paged answer and counts the matching subjects, independent of the order. `sort=newest` with no `limit` or `offset` is the
  whole list, newest first (the registry asks its page query for an unbounded page), and says its length in the header too.
- **An unknown `sort` is `400 subject_sort_invalid`** (a new code, catalogued in `docs/protocol/concordat-codes.md` and mirrored
  to the web app), whether or not the request is paged and whatever the prefix is. This is a deliberate difference from the
  rest of the route, so it is argued:
  - ADR-045 clamps `limit` and `offset` and never refuses them because *a page size is a preference and a bound on cost*: any
    number maps to a nearest page, and the answer is still true. ADR-038 answers a prefix no name could begin with by an empty
    list because *"which subjects begin with this" has a true answer, which is none*. A `sort` is neither: it is an instruction
    that changes **which rows are the first page**. There is no nearest order to a misspelt one. Falling back to name order
    would return a well-formed `200` whose first page is silently the wrong one, to the caller least likely to notice (a script
    that wanted the latest ten subjects and got the ten alphabetically first), and a typo (`sort=newset`) would survive into
    production because nothing ever failed.
  - A refusal costs a developer one failing request, at once, in the language of the registry's other refusals (an RFC 9457
    problem with a stable `concordatCode`, which clients branch on, [ADR-019](019-language-neutral-protocol.md)).
  - The cost of refusing is a client that must be written against the set of orders the registry has. That set is two
    words, documented on the route, and a client that sends one it was never given is wrong.
  - The token is trimmed and not case-sensitive (`Newest`, ` newest `), as the other tokens a request carries (`json`, `open`).
    Blank is `name`, as a blank prefix is no prefix. Two `sort` parameters join into `name,newest`, which is not a token, so it
    is refused.
- **`createdAt`** is on the subject on every route that returns one (list, get, create, the decisions, retire, reviewers): the
  instant it was created, ISO-8601 UTC, written as every other instant on this API is (`2026-11-09T10:15:30.1234567+00:00`;
  parse it as an instant, never compare it as a string). It is additive: the other members are the same, in the same order,
  which `AddingCreatedAt_RemovesAndRenamesNothing` pins. The CLI reads the list with a source-generated reader that ignores
  members it does not model, the SDK does not read this route, and the web app's DTOs name the members they use; the CLI's
  `export` is exercised against the real route in `Concordat.Cli.Tests`.

### `subject` on the approval queue

- **`GET …/approvals?subject=acme.orders.OrderCreated`** lists only that subject's held versions. It is an **exact match on the
  whole name**: ordinal, case-sensitive, trimmed as a name is, and never a prefix (`acme.order` does not find
  `acme.orders.OrderCreated`; a pill wants one subject, and a prefix would link a subject to its neighbours' proposals).
- **`total` counts only that subject's versions**, on every page of it, and `limit`/`offset` apply to the filtered queue, so a
  held version past the first page of the *whole* queue is the first page of its subject's. The order is unchanged: oldest
  proposal first, ties by name and ordinal.
- **A name no subject has is `200` and an empty queue**, not an error, and so is text no subject could be named (`acme-bad`, a
  comma, a NUL): the answer to "which of this subject's versions are held" is none, as ADR-038's is for a prefix, and the store
  is not asked. Blank is no filter. Here a refusal would be wrong where it is right for `sort`: the question has a true answer.
- It is the filter the page and the count share: it narrows the subjects before their versions are joined in.

## What was measured

PostgreSQL 17 (`postgres:17-alpine`), the real schema, the registry published in Release and called over loopback HTTP with a
real API key; `scripts/seed-subjects.mjs` made the data **through the API** (5,000 subjects, 25 with a held breaking version,
ten namespaces from `acme.orders.*` at 21% down to `umbrella.labs.*` at 0.4%, about 17 s at 16 in flight) and then spread the
creation times over 90 days with one `UPDATE`, seven subjects to each instant. The registry stamps `CreatedAt` from its own
clock, so the API alone cannot back-date a subject; that, and nothing else, is done in SQL.

**The page query** (`EXPLAIN (ANALYZE, BUFFERS)`, the SQL EF Core logged for `sort=newest&limit=50`):

| Case | Plan | 5,000 rows, as seeded | the same, in a 110,000-row table |
|---|---|---|---|
| `created_at DESC, name LIMIT 50` | **Incremental Sort** over an **Index Scan on `ix_subject_tenant_environment_created`**, presorted on `created_at`; 57 index entries read for 50 rows | 0.70 ms | 0.26 ms |
| plus `prefix = acme.orders.` (1,060 match) | the same index, the name tested as a filter; 51 rows read, 182 filtered out | 0.99 ms | 0.24 ms |
| plus a narrow prefix (`umbrella.labs.`, 19 match) | sort of the 19 survivors after a sequential scan (a bitmap scan of the environment's index range in the larger table) | 2.5 ms | 1.2 ms |
| the `X-Total-Count` `COUNT` | sequential scan (an Index Only Scan on the same index once the table was analysed and its visibility map set) | 1.8 ms | 0.46 ms |
| `offset 4950` | the whole environment sorted, 5,000 rows | 8.9 ms | not rerun |
| the **name** order, for comparison | Index Scan on `ux_subject_name_per_environment`, no sort | 0.24 ms | not rerun |

The second column was taken after `ANALYZE` and with 105,000 more rows around it (twelve environments of 5,000, one of 45,000, no
versions); the first on the freshly seeded table, so its larger numbers are mostly a cold cache and a stale plan, not the shape.
In the **45,000-subject** environment: page 0.17 ms, count 5.3 ms, a broad prefix 0.16 ms, **a narrow prefix 20.7 ms (a
sequential scan of the table**, 171 rows kept of 110,000), `offset 40000` 24 ms (the 40,050 rows before the page are read and
sorted in groups).

**Over HTTP** (p50 / p95 / max over 40 warm requests on one connection, the registry in Release on Windows with PostgreSQL in
Docker; a request for the first page is about 7-8 ms end to end, of which the page statement is about 3 ms as EF Core logged
it: the join to the 50 subjects' versions, then the API key lookups and the serialising of 22 KB):

| Request, 5,000 subjects | p50 | p95 | max | Body |
|---|---|---|---|---|
| `limit=50` (the name order, as today) | 7.2 ms | 8.6 | 9.3 | 22.8 KB |
| `sort=newest&limit=50` | 7.7 ms | 9.6 | 9.7 | 22.8 KB |
| `sort=newest&prefix=acme.orders.&limit=50` | 7.6 ms | 9.1 | 9.5 | 22.7 KB |
| `sort=newest&prefix=umbrella.labs.&limit=50` | 7.8 ms | 9.0 | 9.3 | 8.7 KB |
| `sort=newest&limit=50&offset=2500` / `offset=4950` | 12.1 / 13.2 ms | 15.5 / 14.5 | 17.2 / 15.3 | 22.8 KB |
| `sort=newest&limit=500` | 24.1 ms | 36.0 | 322.8 (one outlier) | 228 KB |
| unpaged, name order (what `export` reads) | 212 ms | 336 | 371 | 2.28 MB |
| unpaged, `sort=newest` | 188 ms | 219 | 220 | 2.28 MB |

In the 45,000-subject environment (rows without versions, so smaller bodies) the first page is 11.9 ms in either order, a broad
prefix 18-23 ms, **a narrow prefix 37 ms**.

**So no index is added, and there is no migration.** The index this order needs is the one ADR-037 built for the dashboard, and
the plan uses it for the first page with or without a prefix. The `COUNT` is not what makes `X-Total-Count` cost: it is
under a millisecond at 5,000. What the first page costs is the registry's fixed request overhead and 50 subjects' version
histories on the wire. The one slow case is a **narrow prefix in a very large environment**, where the name filter cannot use
this index and the table is scanned; that is the case ADR-038 measured, with the same remedy it named
(`(tenant_id, environment_id, name varchar_pattern_ops)`) and the same reason not to add it before an environment needs it: it
would cost a write on every subject creation and a migration on the table. A composite index on `(tenant, environment, name,
created_at)` was considered and does not help: the planner can use `name` for the prefix *or* `created_at` for the order, and
the second is the cheap one.

## What it costs

- **`offset` still scans what it skips**, and with this order it also sorts it: 9 ms to reach the last page of 5,000, 24 ms for
  40,000 of 45,000. A person does not page that far; a bulk reader should not use offset, and the cursor is still the answer
  for one (below).
- **A narrow prefix in a very large environment is a table scan** (20 ms at 45,000 subjects in a 110,000-row table), as it was
  under the name order. Measured and left (above).
- **The newest order is only as stable as `created_at`.** A subject purged and created again under the same name is new
  (ADR-046), and a promotion creates the subject in the target environment when it is promoted. Both are right for "newest here".
- **A subject created while someone reads shifts the next page by one**, as in ADR-045; under `newest` it goes to the *front*,
  so the reader sees the last row of page 1 again at the top of page 2 and the screen drops the repeat by name. Under `name`
  it landed wherever its name fell, which could be a skipped row. Neither loses a subject for good: the next read is right.
- **A rollback to a build without `sort`** ignores the unknown query parameter and returns the name order, a `200` under a screen
  that says "newest". As with the prefix in ADR-038, the web app and the registry roll back together. The same is true of
  `subject` on the approval queue, which an older registry would ignore and answer with the whole queue.
- **One more code in the catalogue** (99, from 98), and the web app's generated list moves with it.

## Not decided here

- **The other direction, and other keys.** `oldest`, `name` descending, an owner order, a "most versions" order are not offered. A
  new token is an additive change to the set `subject_sort_invalid` names, and each is a question of its own index.
- **A "recently changed" list.** If it is wanted it is a column (`last_changed_at`, set by every mutator next to `Revision`), a
  migration, and an order whose page moves under the reader by design; it should be a different token with that said on it,
  not a different meaning of `newest`.
- **A keyset cursor.** Still the answer the day anything but a screen pages this route. The total order added here
  (`created_at DESC, name`) is exactly the key such a cursor would carry, so this change does not stand in its way.
- **An owner filter, or filtering the subjects list to those with something awaiting approval.** The web app can link to the
  approval queue by subject now; the other direction (the subjects that have a held version) is a different parameter.
- **The CLI.** `concordat export` sends no `sort` and wants the name order; a flag is the CLI's to design.

## Alternatives considered

- **Order by last change.** What a person might first say: "newest" meaning "most recently touched". Rejected for the two reasons
  in the context: there is no timestamp to order by (a new column, written on every mutation, plus a migration on the table
  that holds every subject), and the order it gives moves a subject to the top of the list whenever anyone registers a version,
  so a page changes under the reader for a reason that is not a new subject. Creation time answers "what is new", which is the
  question; "what changed" is the audit log's (`/audit`) and the dashboard's breaking-changes list.
- **Fall back to name order for a `sort` we do not know.** Rejected; argued under "Decision". It is the choice ADR-045 makes for
  `limit`, and the difference is that a limit has a nearest value and a sort does not.
- **`400 invalid_request`, or an existing code such as `audit_filter_invalid`.** The first is not in the catalogue, and the
  second names the audit read. Clients branch on the code (ADR-019), and a code that names the audit filter on the subjects
  route would send a reader of the problem to the wrong page of the docs. One more constant is cheap.
- **`sort=-createdAt`, or `sort=createdAt&order=desc`.** More general, and two parameters to validate where one will do; a
  direction is only a question once there is a second key that wants both. The tokens name what the caller wants to see
  (`newest`), not a column.
- **`sort=created`.** Reads as ascending. `newest` says the direction and the key together, and `oldest` can be added without a
  reader having to wonder what `created` meant.
- **An order that is not total** (`ORDER BY created_at DESC` alone), leaning on the physical order of rows for ties. Passes
  every test whose subjects were created a millisecond apart and fails on the first import. The test arranges thirty subjects
  in six groups that share an instant and walks them at six page sizes.
- **A keyset cursor now.** The right shape for a bulk reader; it costs an opaque token on the wire and a second way to
  ask for a page, for a screen that reads one page at a time and has `Show more`. ADR-042 and ADR-045 gave the same answer and
  nothing has changed except that the key now exists.
- **A new index for the order, or for the narrow prefix.** The order already has its index; the prefix has a candidate and a
  measurement (above, and ADR-038), and a threshold where it starts to matter that this registry has not reached.
- **Filtering the approval queue in the browser.** The defect the filter exists to remove: it cannot reach what is past the page.
- **`subject` as a prefix, like the subjects route.** A pill links to *one* subject, and `acme.order` finding
  `acme.orders.OrderCreated` is right for a search box and wrong for a link. The two parameters are named differently
  (`prefix`, `subject`) so neither can be mistaken for the other.
- **`GET /subjects/{subject}/approvals`**, a route under the subject. It would be a second shape of the queue (the item, the
  total, the page) to document and to keep in step with the first, for a filter on a query string.
- **A `400` for a subject name that cannot be a name.** Rejected as ADR-038 rejects it for a prefix: it is a question with an
  empty answer, and a link built from a stale or hand-edited name should show an empty queue and not an error panel.

## Consequences

- **Positive:** the subjects page can show what is new without loading it all; the order is cheap (the first page of 5,000 reads
  57 index entries); a subject is on exactly one page of it, even when many share an instant; a typo in `sort` is found by the
  first request; the approvals pill can reach a subject's held version wherever it sits in the queue; the response says when a
  subject was created, which the web app could not tell before.
- **Negative:** another way for a request to be a `400` on a route that mostly clamps; a field every subject response carries;
  the narrow-prefix cost in huge environments is unchanged (and now documented).
- **Neutral:** `GET …/subjects` with no `sort` is exactly what it was. No migration, no new index. The OpenAPI document and the
  protocol's code catalogue are regenerated.

## References

- [ADR-037](037-the-dashboard-is-answered-by-the-registry.md) — the index this order is served by, and `createdAt` on the newest subjects
- [ADR-038](038-subject-prefix-search.md) — the prefix, the empty answer for text that cannot be a name, and the index not added
- [ADR-042](042-the-approval-queue-is-answered-by-the-registry.md) — the queue the `subject` filter narrows
- [ADR-045](045-the-subject-list-is-paged-by-the-registry.md) — the page, the header, and the clamp this does not extend to `sort`
- [ADR-019](019-language-neutral-protocol.md) — the codes clients branch on
- `scripts/seed-subjects.mjs` — makes the environment this was measured on
