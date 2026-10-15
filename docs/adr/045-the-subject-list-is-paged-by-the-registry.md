# ADR-045: The subject list is paged by the registry, and says how much it is not showing

- **Status:** Accepted
- **Date:** 2026-10-15
- **Deciders:** Rafael Camara

## Context

`GET /v1/environments/{env}/subjects` returned **every** subject, each with its whole version history, and had no way to
return fewer. [ADR-038](038-subject-prefix-search.md) moved the search to the registry and recorded the cost it left:
the box filtered a list that was one request, and *"whoever adds paging has to move this to the server"*. The approval
queue ([ADR-042](042-the-approval-queue-is-answered-by-the-registry.md)) and the dashboard
([ADR-037](037-the-dashboard-is-answered-by-the-registry.md)) had already stopped building their answers from this list,
because both would have under-counted the day it was paged. Nothing was paged yet, so the screen was correct and its
cost grew with the environment: a response of every subject's every version, parsed and rendered in full.

## Decision

**`limit` and `offset` on the same route, and the whole length in a header.**

- **`limit` and `offset`, clamped and never refused.** `limit` is 1-500 (the audit read and the approval queue clamp theirs the
  same way: a page size is a preference and a bound on cost, and the nearest page is a better answer than a `400`); an offset
  alone pages with 50; a negative offset is none. Ordered by name, ordinally, as every listing already is.
- **Without either the answer is what it always was**: every subject, as a bare array. The CLI's `export` is the other caller
  and wants all of them, so paging is opt-in and nothing a client sends today changes.
- **`X-Total-Count`**, how many subjects match (the prefix included), **on every answer** and a header and not a wrapper
  around the array: the body is the list that every client already reads. The count is a second query, `COUNT(*)` over the same
  predicate, asked **only when a page was**, so the unpaged route costs what it did. It loads no subject, so the version
  rows the aggregate auto-includes are not read for it.
- **The prefix is judged as before**: text no name could begin with lists nothing and counts zero, and the store is not asked.
- **Offset and not a cursor**, for the reason ADR-042 gave: a list is read by a person, and the one failure (a subject
  registered between two pages shifts the next page by one) is a subject seen twice or skipped, and the screen drops a
  repeat by name and the next read corrects the rest. A keyset cursor costs an opaque token on the wire and is the answer
  the day anything but this screen pages the route.

**The screen.** The list store asks for 50, keeps what it has read, and says so: *"Showing the first 50 of 213 starting with
`acme.`. 163 more are not shown yet."* with **Show more**. A list that stopped at fifty without a word would be the dashboard's
old defect with a bigger constant: the registry knows how many there are and the screen would show fewer as though it were all.

- **The next page continues the prefix the rows answer** (`shown`), not what has been typed since: between a keystroke and the
  end of its pause the box says one thing and the rows another, and a page of the box's prefix appended to rows for another
  would be a list that is neither.
- **A reload, a new search or a new environment discards the pages** and a late answer to the old list is dropped (an epoch,
  checked when the page arrives), so a page asked for before a replacement never lands after it.
- **Failing to read a later page does not discard the earlier ones**: what is on screen is true, the screen says it could not read
  more, and **Show more** is still there. A first page that fails still empties the list, as it did.
- **A second click while a page is on its way asks for nothing** (`exhaustMap`).

## What it costs

- **A second query per page.** Measured against real PostgreSQL in the integration tests, not against a large table; the count has
  the same `WHERE` the page has and nothing else. There is no index (ADR-038 measured one for the prefix and found it not worth
  it), so the count is a scan of the environment's subjects, which is the one thing this ADR does not make cheaper.
- **`offset` scans what it skips.** Fine at the sizes a registry of contracts reaches (thousands), and the reason a cursor stays the
  answer for a bulk reader.
- **The total can be stale by the time the next page is asked for** and the screen says what the registry said when it said it;
  **Show more** reads the registry's current total from the page it fetches.

## Not decided here

- **The dashboard's version counts are still linear in the environment's versions** ([ADR-037](037-the-dashboard-is-answered-by-the-registry.md)
  measured 100-125 ms at 200,000). It was listed beside this and is a different question: a counter maintained when a status
  changes. ADR-037 said to build it when it is measured to hurt, and it has not been.
- **Sorting by anything but name**, and filtering by owner. Both are separate parameters on the same route and neither needs this
  one changed.
