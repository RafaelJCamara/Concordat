# ADR-052: The subject list is paged with page controls, and the page and the search are in the address

- **Status:** Accepted
- **Date:** 2026-11-09
- **Deciders:** Rafael Camara

Supersedes the screen section of [ADR-045](045-the-subject-list-is-paged-by-the-registry.md). Its API (`limit`, `offset`,
`X-Total-Count`) stays exactly as it was.

## Context

[ADR-045](045-the-subject-list-is-paged-by-the-registry.md) made the registry page the subject list and gave the screen a
**Show more** button that appended the next fifty to what was on screen. That was the smallest honest screen for a list of
hundreds. It is the wrong one for a list of thousands, for four reasons that only show at size:

- **Nothing is reachable but the next fifty.** To see the hundredth subject you press the button twice and scroll past two
  hundred rows; there is no way to the end, and no way back to the start short of a reload.
- **The page grows without bound.** Every press adds fifty rows to the document, so the cost of the screen (rows rendered, memory,
  the scroll bar) is the cost of everything read so far. A person who pages to row 5,000 is rendering 5,000.
- **Nothing is in the address.** The prefix, and how far down the list a person had read, lived in the component. A reload, a
  link pasted into an incident channel, and the Back button all returned to the first fifty. [DESIGN §9](../DESIGN.md) already keeps a
  resource's identity in the path; a *view* of a list belongs in the query, where it can be shared.
- **The order was the name's** (a subject made today was on a page nobody reads), which [ADR-049](049-subjects-can-be-listed-newest-first.md) changes.
  A list newest first with no way to go further than the newest fifty is half a feature.

The subject list is also the screen that the new pill ([item 3 below](#the-pill-is-a-link)) and the dashboard link into, so what its
address says is now something other screens depend on.

## Decision

**Pages, not an accumulating list; the page, the size and the search are the address; the newest subjects are page one.**

### The page model: previous, numbered pages with ellipses, next

- **Numbered pages with ellipses, between *Previous* and *Next*** (`1 … 4 5 6 … 100`), always seven places wide once there are more than
  seven pages, and the first and the last page always present. The alternative is only *Previous / Next* with "Page 3 of 12": it is
  simpler, and it is not enough, because a list of a hundred pages is **jumped around, not walked**: the person who wants the oldest
  subjects, or who was on page 47 yesterday and is told "it was around there", cannot get there in under forty-six presses.
  Numbers cost one row of buttons. Seven places because that is what fits a 390 px screen in one row, and *the same* seven wherever the
  current page is, so the row does not change width under the pointer as somebody clicks along it.
- **Buttons, not links,** `aria-current="page"` on the current one and every button named for what it does (*Page 4*, *Previous*,
  *Next*). The page change is a navigation the screen makes (below) and the address is in the address bar afterwards; a link would give
  "open in a new tab", which the address bar already gives.
- **A page that cannot be gone to is `aria-disabled`, not `disabled`**, and still focusable: pressing *Next* on the second to last page
  leaves the focus on a *Next* that is then dead, and a `disabled` button drops the focus to the top of the document. The click is
  ignored.
- **It is a shared component** (`shared/ui/pagination`), because the approval queue ([ADR-042](042-the-approval-queue-is-answered-by-the-registry.md))
  will want the same and keeps its **Show more** for now: that queue is ordered oldest first, is short, and is not shared by address.

### A visible size, and a visible total

- **A page size of 25, 50 or 100, default 50.** Three, because a person choosing is choosing between "fits a laptop screen without much
  scrolling" and "I am scanning", and a fourth is a decision about nothing. None above 100 although the registry accepts 500: each
  subject on the wire carries its whole version history ([ADR-045](045-the-subject-list-is-paged-by-the-registry.md)), so the response
  grows with the page, and a bound is the cost of one request. Changing the size keeps the first row being looked at in view: the page
  becomes the one that contains it (page 3 of 25, which begins at row 51, becomes page 2 of 50), not a page 3 of 100 that is somewhere
  else in the list.
- **"Page 3 of 100 · subjects 101-150 of 5,000 · newest first"**, above the table, a polite status. It is the whole accessible account of
  a page change, and it is **the only announcement one makes**: the focus is not moved. After a click the focus stays on the pager button
  that was pressed, which is the same element (the number buttons are keyed by page) so the next key press turns the next page; the
  router scrolls the new page into view from its top as it does for any navigation.
- **When the registry sends no total** (a proxy that strips `X-Total-Count`, or a registry that predates it) the screen does not make
  one up. `SubjectPage.total` is `null` for a request that asked for a page and got no count, the pager shows only the page it is on, and
  the sentence says "subjects 51-100 · more may follow" while the page is full and "the end of the list" when it is not. A page that is full
  is the only evidence there is, and the screen says "may", because it is evidence and not knowledge. (The length of a page is not the
  length of the list: ADR-045 let the client take it for one when the header was missing, which was only right when it had asked for no page.)
- **The pager is not drawn for a list of one page**, and is for a list of several. The summary and the size selector always are.

### The address

- **`?q=<prefix>&page=<n>&size=<n>`**, each present only when it differs from the default (empty, 1, 50), so the plain list is `/subjects` and
  a copied link says nothing it does not need to. The names are short because they are read aloud and pasted. These are a **view**, not an
  identity: DESIGN §9's rule that identity lives in the path is not touched.
- **The address is the screen's state and the only way into the store.** The router binds the three parameters to the page's inputs
  (`withComponentInputBinding`, already on); the page turns them into a query and tells the store what to show; a click, a pause in typing or
  a size change is a **navigation**, never a call to the store. So a click, a reload, a link and the Back button take one path and cannot
  disagree about which page is showing. That is why `SubjectListStore` lost `search()` and `loadMore()` and gained `show(query)`.
- **History.** A page or a size change **adds** an entry, so Back returns to the previous page. A search **replaces** the entry: it is
  refined letter by letter, and Back should leave the list and not walk back through every prefix typed on the way. (The typing is waited out
  by the page, 250 ms as before, *before* the address changes, so one request per pause is unchanged.)
- **A changed prefix resets to the first page**, keeping the size: page 7 of one search is not a place in another. **A changed environment
  resets to the first page** for the same reason (page 7 of `prod` is not a place in `dev`, which may have two pages), and starts from a
  clean slate rather than dimming another environment's rows. The environment is not in the address, it is the deployment's.
- **An address that says what it means in a way the screen would not write is rewritten in place**: `page=0`, `page=abc`, a size nobody
  offered, a `page=1` spelled out, a prefix with spaces round it. Always read as the nearest thing the screen can show, never as an error and
  never as a blank table, and the entry is replaced so there are not two addresses for one list in the history. A page number is capped at
  one million, since an offset past anything a database holds is not somewhere a person got to by clicking.
- **A page past the end lands on the last page, with a sentence.** `?page=99` of a list of twelve pages is what an old link looks like
  after the list shrank, or an edited address. The registry answers it with no rows and the true total; the store flags it and keeps
  `loading`, so **no blank table and no "no subjects" panel is drawn**; the page replaces the address with the last page (so Back does not
  return to the bad one) and says *"Page 99 does not exist, so this is page 12, the last."* The page it goes to is never *after* the one it
  was given (a count that contradicted an empty page would otherwise send it forward), and with no count it goes back one page at a time.
  The cost is one extra request in a case that is rare and does not repeat.
- **Stale answers.** ADR-045 kept an epoch so that a page asked for before a replacement and answered after it was dropped. Pages are no
  longer appended, so there is nothing to append *to*; every change goes through one `switchMap`, which unsubscribes from the previous
  request, so a late answer has no subscription left to arrive on: **a slow page 3 cannot land over page 4**, which is stronger than
  checking a counter when it arrives. The rows of the old page stay (dimmed, `aria-busy`) until the new ones arrive, as they did for a
  search; the status sentence names the rows that are on screen and not the page asked for.

### The order, and the dates

- **The screen always asks for `sort=newest`** ([ADR-049](049-subjects-can-be-listed-newest-first.md)): by creation date, latest first, ties by name, so the
  newest subjects are on page one **with no action**. There is no control to change the order; the registry's `sort=name` remains for the
  CLI's export and for any caller that wants it.
- **The table says what it is ordered by, and shows the date.** The *Registered* column was the **latest version's** `registeredAt`, which
  moves whenever somebody registers a version and is not the subject's age: a subject made a year ago with a version registered yesterday
  looked as new as one made yesterday. It is replaced by **Created**: the subject's `createdAt` as relative text ("3 days ago"), the day
  and the minute in UTC beneath it (`2026-11-09 14:03 UTC`) so that the order can be *checked* against the rows and not only asserted, and
  the full ISO instant in the `title` of a `<time datetime>`. The header carries `aria-sort="descending"` and the footer caption says
  *Newest first: ordered by when each subject was created, then by name.* What the old column held that is worth keeping, when the latest version
  was registered, stays in the **Latest** cell, labelled *registered 2 days ago*, beside the version it describes. A registry that predates
  ADR-049 sends no `createdAt`, and the cell shows a dash with a title saying so: the screen does not invent a date from the versions.
- **Created, not last changed**, as ADR-049 decided; the second reason there matters here: a "last changed" order would move a row to
  the top every time somebody registered a version, so the page a person is reading would reshuffle under them between two clicks.

### <a id="the-pill-is-a-link"></a>The held-version pill is a link

- **The State column's *N awaiting approval* is a real `<a>`** to `/approvals?subject=<name>` and, when exactly one version is held,
  `&version=<ordinal>`. A real anchor: in the tab order, announced as a link, middle-clickable. It wraps the **same `StatusBadge`**
  every other state uses and not a second pill; what says it can be clicked is a ring on hover and on keyboard focus, the words underlined,
  and an arrow, none of which the inert lifecycle badge beside it (`DEPRECATED`, `RETIRED`) has. Its accessible name starts with the words
  it shows (*"1 awaiting approval: review the held version of orders.created"*) so a voice-control user who says what they see reaches it.
  The lifecycle badge stays plainly inert: a state with no destination is not a link.
- **With several held versions it lands on the approvals list filtered to the subject**, not on a version chosen for the reader. With one,
  it names it.
- **The approvals screen reads `subject` and `version` from the address**, asks the registry for that subject only (`GET …/approvals?subject=`,
  [ADR-049](049-subjects-can-be-listed-newest-first.md); its `total` counts only that subject's), says so (*"Showing changes waiting on orders.created"*, with
  **Show all**), opens the named version's review and scrolls it into view and moves the focus to its heading (`tabindex="-1"`, no trap),
  **once** per target, so a refresh after a decision does not reopen what the reader closed. A subject with nothing held says *"Nothing is
  waiting on a decision for orders.created — it may already have been decided"* with **Show all**; a version that is not in the list says it
  is not waiting any more and the rest are right below. A bad `version` (`abc`, `0`, `2.5`) is ignored. A subject name with dots, or any
  character that means something in an address, goes through the router's encoding and arrives whole.

## Alternatives considered

- **Keep *Show more*, add a "Back to top" and a total.** Rejected: it does not fix any of the four problems above except the total.
  It is the cheapest answer for a list of hundreds and is why ADR-045 chose it.
- **Infinite scroll.** Rejected: the footer is unreachable, the scroll bar lies, the address cannot hold "how far", and a keyboard user cannot
  get past the list to anything below it. It also leaves the document growing without bound.
- **Previous / Next and "Page X of Y" only.** Rejected for a hundred pages (above). It is the right model for the approval queue, which has
  tens, and the pager supports it (`pageCount` and the numbers are the same component).
- **A page-number box ("go to page: __").** Rejected: a control that takes a number and an Enter to do what a button does in one press,
  and an error state for a number out of range. The ellipsis row reaches the first, the last and the neighbours; a person who wants page 47
  types `?page=47`, and that works.
- **The page and search in component state, with the store as the source of truth, writing the address afterwards.** Rejected: two
  directions of synchronisation between the address and the store, with the loop risk and the "which wins on Back" question that brings.
  One direction (address → store) has neither.
- **A route segment (`/subjects/page/3`).** Rejected: the route table matches `/subjects/:name`, and a subject can be named `page`.
- **Page size in `localStorage`.** Rejected for now: the size is in the address, so a shared link shows the page the sender saw, and a
  preference stored beside it would be a second source for the same thing. A "remember my size" is a separate small decision.
- **Keyset (cursor) paging.** Still the answer the day anything but this screen pages the route (ADR-045 said so); a numbered page is an
  offset by nature. The cost of offset at the sizes a registry reaches is measured below.
- **Counting `X-Total-Count` on the client when missing.** Rejected: see "when the registry sends no total".

## What it costs

- **The first page, measured and not assumed.** An environment of **5,000 subjects** made through the API (`scripts/seed-subjects.mjs`, creation
  times spread over ninety days, seven to each instant, twenty-five with a held version), PostgreSQL 17 in Docker on a developer's laptop, Chromium
  with an empty cache for every run, eleven runs with the first (which pays the lazy chunk) set aside:
  - **The request** (`?limit=50&offset=0&sort=newest`): **23.5 KB**, 16 ms at the median (20 ms worst) from the command line, 32 ms (39 ms) as the
    browser sees it. The last page (`offset=4950`) is 17 ms (42 ms worst), so the offset costs nothing measurable at this size; a page of 100 is 47 KB
    and 14 ms, of 25 is 11.8 KB and 11 ms; `sort=name` is 12 ms, so the order is not what costs. For scale, the **whole list**, which is what the screen
    asked for before ADR-045 and what the CLI's export still does, is **2.36 MB and 185 ms** (338 ms worst): the first page is a hundredth of it.
  - **The screen**: the first row is in the document **99 ms after the response ends** on the development server (75 ms on a production build), at a
    median of 0.64 s (0.81 s worst) from navigation on the development server and 0.55 s on the production build, with every script and stylesheet
    fetched and compiled: the list is the smaller part of that. One to two long tasks (the application's start-up and the render of fifty rows),
    **52-147 ms** in all on the development server; 741 elements and 13-25 MB of heap once the rows are drawn, however far into the list the page is.
  - **A page turn** (click *Next* to the new rows painted) is **76-82 ms** at the median; the slowest of ten was 100 ms on the development server and
    487 ms once on the production build while the machine was short of memory.
  The figures are one laptop's and one run's. What they are for is the shape: the request is the same size and the same cost wherever the page is, and
  nothing grows with how far in somebody reads.
- **A page change is a request and a render of fifty rows**, where *Show more* was a request and fifty rows added to a growing document: the
  document is now bounded by the page size, whatever the position.
- **A reshuffle between pages is possible and is the same one ADR-045 accepted.** A subject created between two clicks sits at the top of
  `sort=newest`, so the next page begins one row earlier than the previous one ended: one row is seen twice, never skipped, and the sentence
  names the range the registry answered. Sorting by name had the opposite and rarer failure. Neither is a reason for a cursor on a screen a
  person reads.
- **`offset` scans what it skips**, and the count is a second query (ADR-045). Unchanged.
- **The environment's page is forgotten on a switch.** Deliberate (above), and a person who switches back lands on page one.
- **Three more parameters in a URL others may paste.** They are harmless to share (they carry no identity and no secret) and they are
  forgiving: any of them can be junk and the screen shows the nearest sensible page.
- **`SubjectListStore` lost `search()` and `loadMore()`.** The create screen reads the same store for one thing ("is this name already
  taken?"), through `load()`, which is kept: it reads the first page of the newest subjects (and not the first fifty by name; both are
  partial, and the registry's `409` is what holds the line).

## Not decided here

- **A "remember my page size"**, and a column chooser. Both are preferences; neither changes what is in the address.
- **The approval queue's own pager.** It is ordered by age and short; if it grows, the component is ready.
- **A cursor.** See above.

## References

- [ADR-045](045-the-subject-list-is-paged-by-the-registry.md): the API this keeps and the screen it replaces.
- [ADR-049](049-subjects-can-be-listed-newest-first.md): `sort=newest`, `createdAt`, and the `subject` filter on the approval queue.
- [ADR-038](038-subject-prefix-search.md): the prefix, still the registry's.
- [ADR-042](042-the-approval-queue-is-answered-by-the-registry.md): the queue the pill leads to.
- [DESIGN §9](../DESIGN.md): resource identity in the path.
