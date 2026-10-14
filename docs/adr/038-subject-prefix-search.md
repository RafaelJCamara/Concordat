# ADR-038: Subjects are searched by the start of their name, and the registry does the searching

- **Status:** Accepted
- **Date:** 2026-10-14
- **Deciders:** Rafael Camara (the brief). The choices under "Decision" that the brief left open were made in implementation, and
  [STATUS.md](../STATUS.md) lists each so it can be reversed.

## Context

[STATUS.md](../STATUS.md) and [DECISIONS-PENDING.md](../DECISIONS-PENDING.md) (M1.6) both recorded the same hole since the
first milestone: *subject prefix search is not implemented, because value converters do not translate `StartsWith`*. Nobody had
asked for it on the wire, so it stayed a note. What was wanted was already in the product, faked:

- `GET /v1/environments/{env}/subjects` returned **every** subject, each with its full version history, and had no way to
  return fewer. The CLI's `export` is the only other caller and wants them all.
- The web app's search box filtered **the list already in memory**: a case-insensitive substring match on the name *or the
  owner*. Its own comment said what that was worth: *"right while the whole list is one request and wrong the moment it is
  paginated; whoever adds paging has to move this to the server, or the box will quietly search one page."*

Three facts shape the answer:

1. **Subject names are case-sensitive** ([ADR-011](011-subject-is-message-type.md)): `OrderCreated` and `ordercreated` are two
   subjects, and a search that folds case would find one while naming the other.
2. **Subject names are `[A-Za-z0-9_]` segments joined by dots, and `_` is a LIKE wildcard.** The one pattern metacharacter
   that is also a legal name character is the one an escape is most likely to forget, and `a_` would find `ab`.
3. **The column is value-converted** (`SubjectName` ↔ `varchar(512)`). EF Core does not translate `StartsWith` through a
   converter; measured, not assumed: `EF.Property<string>(s, "Name").StartsWith(p)` becomes a `LIKE` whose constant inherits the
   *column's* mapping, so SQL generation tries to convert the string `p` into a `SubjectName` and throws `InvalidCastException`.
   (A `SubjectName` could not hold a half-typed `acme.` anyway.)

## Decision

**`GET …/subjects?prefix=` lists the subjects whose name begins with the prefix.** No prefix, or a blank one, is the whole list,
through the original query. Everything else follows from one idea: **a prefix is a literal, and the registry says which literals
mean anything.**

- **Ordinal and case-sensitive.** Compared bytewise, as names are.
- **Whitespace is trimmed**, as `SubjectName.Create` trims (so `GET /subjects/%20acme` and `?prefix=%20acme` agree). Case is
  never touched.
- **A prefix is a valid name, or a valid name and one more dot** (`SubjectNamePrefix`, in the domain): exactly the strings
  that are the start of some name. Mid-segment is fine (`acme.ord` finds `acme.orders.OrderCreated`); the dot is how a caller
  says "the `acme.orders` namespace". The trailing-dot form is bounded one character short of the 512 limit, because the dot
  promises another character.
- **A prefix no name could begin with is `200` and an empty list**, not `400`, and the database is never asked. That covers
  `%`, a backslash, a hyphen, two dots, a comma (which is how ASP.NET joins `?prefix=a&prefix=b`), a NUL, a lone surrogate,
  invalid UTF-8 and anything over 512 characters. It is also the rule that keeps every character that could harm a query out
  of one: PostgreSQL refuses a NUL in text outright, which was a `500`, and the NUL test goes red without the grammar.
- **`starts_with(name, prefix)`, not `LIKE`.** A mapped database function (`ConcordatDbFunctions.StartsWith`,
  `HasDbFunction`), with the column passed as `EF.Property<string>(s, nameof(Subject.Name))`. It takes a literal, so there is
  no escape to get wrong and `_` is an underscore by construction. It is bytewise on a deterministic collation, which is what
  makes it ordinal and case-sensitive. The tenant filter is the context's global query filter and is ANDed in as it is for
  every query.
- **No new index.** See the measurement below. **No migration.**
- **The web box is this filter** (debounced 250 ms; the stale request is cancelled by `switchMap`, which unsubscribes the
  HTTP call; the rows stay on screen, dimmed, while the answer is awaited; empty and "nothing can match" states; the box is
  never unmounted by its own answer). It no longer matches in the middle of a name or on the owner, and says so under the box
  and in the empty state. `maxlength` is 512.

### What was measured

PostgreSQL 17 (`postgres:17-alpine`, the image CI and the compose stack use; its `datcollate` is `en_US.utf8`, which the
planner treats as **non-C**, so a plain btree cannot serve `LIKE 'p%'` here either). The real `subject` table, 65,000 rows, of
which 5,000 in the environment queried; `EXPLAIN (ANALYZE, BUFFERS)`:

| Query | Plan | Time |
|---|---|---|
| The unfiltered list (what the old box loaded, then filtered in the browser) | bitmap scan on `ux_subject_name_per_environment` (tenant, environment), sort | 4.7 ms for 5,000 rows |
| `starts_with(name, 'acme.orders.Event12')` (22 rows), shipped | the same bitmap scan on the two leading columns, filter on the name | 1.2 ms |
| `starts_with(name, 'acme.orders.')` (1,000 rows), shipped | the same | 1.9 ms |
| the same two, with a **candidate** `(tenant_id, environment_id, name varchar_pattern_ops)` index | index scan with `name ~>=~ … AND name ~<~ …` as an index condition | 0.075 ms and 1.1 ms |

The plan never degrades to scanning the table: the tenant filter and the environment are equality conditions on the index's two
leading columns, so the name is only ever tested against **one environment's** subjects. On a scratch table with 100,000
subjects in a single environment (of 180,000) the planner chose a parallel sequential scan, 19 ms against 0.05 ms with the
index. That is where an index starts to be worth its cost, and it is two orders of magnitude past what the old box could have
loaded. `starts_with` and `LIKE` plan identically with and without the index.

**So no index is added.** A subject registry holds one subject per message type; a few thousand per environment is already
large, and at that size the filter costs about a millisecond and returns *fewer* rows than the endpoint did before, each of
which is loaded with its version history (the dominant cost, and the reason the filter is a win at any size). An index would
cost write amplification on every subject creation and a migration on a table holding every subject, for ~1 ms. If an
environment ever holds tens of thousands, the index is
`CREATE INDEX ix_subject_name_prefix ON subject (tenant_id, environment_id, name varchar_pattern_ops);` and the query needs no
change.

## Alternatives considered

- **`LIKE` with an escape character.** Works, and measured to plan the same. Lost because the correctness of the whole feature
  would rest on one escape being right for three characters (`%`, `_`, and the escape itself), in every future caller, with the
  most likely failure (`_`) being a legal name character that shows up in real names. `starts_with` removes the class of bug.
- **A `ComplexProperty` mapping of the name, or a second shadow column** (the two the hole row proposed). The first changes how
  every query, index and comparison that names the column is built, for a feature that needs one function; the second is a
  duplicated column that must be kept in step with the first, and a migration on a table with rows. A mapped function changes
  neither the model nor the schema.
- **`ILIKE`, `lower(name)`, or a `citext` column.** Case-insensitive, so wrong: it would find `OrderCreated` for `ordercreated`
  and the other way round.
- **A range comparison (`name >= p AND name < p || U+FFFF`).** Compares under the database collation, not bytewise, so which
  names fall in the range depends on the locale the database was created with.
- **`400 subject_name_invalid` for a prefix no name could start with**, as `GET /subjects/{bad}` is. Consistent, and wrong for a
  filter: the true answer to "which subjects begin with this" is none, and a search box is typed into a character at a time, so
  `acme-` would be an error banner. A lookup of a name that cannot exist is a mistake; a search for one is a question with an
  empty answer. The client explains the empty answer (`checkSubjectNamePrefix`), but never decides it.
- **Keeping the box's substring and owner match** and adding the prefix beside it. The box would then mean two things, and the
  one that cannot be paged (a substring match over a whole environment) is the one the old comment warned about. Owner and
  substring search are real wants; they are separate features (an owner filter is a different parameter, and a substring match
  has no index at all).
- **An index now**, to be safe. See the measurement.
- **Adding `--prefix` to `concordat export` in the same change.** The CLI is the only other caller and wants a filter, but a
  flag and its semantics are the CLI's to design; the endpoint is ready for it.

## Consequences

- **Positive:** the box finds what the registry finds and cannot go wrong when the list grows; the filtered response is the
  same shape (and, byte for byte, the same subject JSON) as the unfiltered one; `%`, `_`, backslash, NUL, invalid UTF-8,
  repeated and absent parameters are all pinned over real HTTP as `Development` as well as in unit tests.
- **Negative:** **the box no longer matches the owner, or the middle of a name** (`ordercreated` in `acme.orders.OrderCreated`),
  both of which it did. This is a loss of function the brief asked for and the maintainer may want to give back as separate
  features. The list endpoint is still unpaginated: a broad prefix returns many subjects with their histories. A prefix
  beyond about 8 KB is refused with `414` by Kestrel before the route is reached (the web box stops at 512).
- **Neutral:** `GET /subjects` without a prefix is exactly what it was. A rollback to a build without the parameter ignores
  it and returns the whole list: the web app would then show every subject under a box that says it filtered, so the box and
  the registry must be rolled back together.

## References

- [ADR-011](011-subject-is-message-type.md) — the grammar a prefix is a prefix of
- [ADR-028](028-schema-editor.md) — a client check is never stricter than the registry, and only ever words an answer
- [STATUS.md](../STATUS.md) — the hole this closes; [DECISIONS-PENDING.md](../DECISIONS-PENDING.md) M1.6
