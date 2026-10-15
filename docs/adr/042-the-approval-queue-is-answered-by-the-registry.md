# ADR-042: The approval queue is answered by the registry, a page at a time

- **Status:** Accepted
- **Date:** 2026-10-15
- **Deciders:** Rafael Camara

## Context

The Approvals page built its queue in the browser: `GET …/subjects` (every subject, with every version of
every subject embedded), then a filter for `AWAITING_APPROVAL`. [STATUS.md](../STATUS.md) recorded it as the
defect the dashboard had before [ADR-037](037-the-dashboard-is-answered-by-the-registry.md): it is right only
while the subject list is one page long, and the day that list is paged a held version on page two is simply
absent from the one screen whose only job is to show what is waiting. The dashboard's summary could not feed it:
it carries the five newest held versions, by design, not the queue.

## Decision

**`GET /v1/environments/{env}/approvals`** answers one page of the queue, **oldest proposal first**, with the
length of the whole queue beside it:

```json
{ "total": 120, "items": [ { "subject": "…", "ordinal": 2, "semanticVersion": null, "changelog": null,
                             "registeredAt": "…", "registeredBy": "…", "baseline": 1 } ] }
```

- **`baseline`** is the ordinal consumers resolve as `latest` today, which is what approving the item would
  replace. It is the registry's gated pointer, never "the highest ordinal"; a held version always has a live
  first version behind it, so `null` is a defensive answer the domain does not currently produce.
- **Oldest first**, so the longest wait leads; ties are broken by subject name and then ordinal, so two
  requests agree about where a page ends.
- **`limit` (default 50, at most 200) and `offset` (default 0), clamped and never refused**, as the audit read
  does with its limit: a page size is a preference and a bound on cost, and the nearest page the registry
  will give is a better answer than an error. **Offset, not a cursor**: a queue is read by a person, a
  decision removes a row, and the screen reads the queue again after every decision. If a row is removed by
  someone else between two pages, the second page starts one row late and the first row of it is skipped;
  `total` shows the shortfall and the next read corrects it. A keyset cursor would remove that and cost an
  opaque token on the wire. Revisit if anything other than the page starts reading this route.
- **Retired subjects are included**: a proposal nobody decided is waiting whatever became of its subject, and
  the dashboard's awaiting count includes it, so a queue that dropped it would disagree with the card that
  links to it.
- Needs **`subject:read`**, as the subject list did: anyone may read the queue and only `subject:admin` may
  decide (ADR-018). An environment nothing was written to answers an empty queue and `200`, not `404`.
- **Two statements, no loop.** A `COUNT` and a page, both projections, so neither materialises a `Subject`
  (which auto-includes every version), under the same tenant filter as every repository.

### The screen

The store holds the pages it has read and `total`. When `total` exceeds what is on screen the page says
*"Showing the N oldest of M waiting"* and offers **Show more**; a screen that showed fifty and said nothing
would be the dashboard's old defect with a larger constant. A page that fails to load keeps what was read and
says so, and a second request while one is in flight is ignored.

**A decision removes its row at once and then reads the queue again**, asking for as many as are on screen (at
most one request's worth). Approving moves `latest`, which changes what every other held version on that subject
replaces; the old code replaced the whole subject with the one the decision returned for that reason, and the
queue now takes the registry's word for it instead. If the re-read fails, the screen keeps what it has and says
it may be out of date.

## Consequences

- The Approvals page no longer requests the subject list, and `e2e/approvals.spec.ts` asserts it makes one
  request for the queue and none for the list.
- Subject-list pagination is no longer blocked by this screen. It is still blocked by nothing else we know of
  except the CLI's `export`, which wants the whole list.
- A reader who does not decide never sees past the first page unless they ask for more; that is the point of
  `total`.
- `GET …/approvals` is a new route and a new response type in `docs/api/openapi.v1.json`.
- **Not verified:** the production web image and `smoke-web-image.sh` were not re-run; CI; a registry holding
  tens of thousands of held versions (the plan was not measured at scale, only the shape: a count and a
  `LIMIT` over the same join the dashboard's held-versions list uses).
