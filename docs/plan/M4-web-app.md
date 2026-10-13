# M4 — Angular web app

**Depends on:** [M1](M1-registry-core.md) · **Design refs:** [§9](../DESIGN.md#9-frontend-architecture-angular), decisions 006, 018

Off the critical path — can slip without invalidating M0–M3.

---

## M4.1 Scaffold

- [x] Angular 22 standalone + signals, no NgModules; Angular CLI 21+
- [x] Spartan UI — `@spartan-ng/brain` + `helm` components generated into the repo as source
- [x] Port the prototype's `index.css` token set **verbatim**; add a light theme
- [x] `@ngrx/signals` SignalStore per feature
- [x] Folder structure per DESIGN §9; **ESLint boundaries rule** enforcing it
- [x] `core/` interceptors: auth, tenant, problem-details

## M4.2 Access control

**ADR-018**

Build now, not in M8 — retrofitting across finished screens is how a write path gets missed.

- [x] `canWriteSchemas` computed on the session store, derived from API-returned scopes
- [x] `*cdIfScope` structural directive for affordances
- [x] `scopeGuard` on write routes; direct navigation redirects to the read view. **Was marked
      done, then unmarked on 2026-08-15** when the Playwright suite went to write the redirect
      test and found there was nothing to redirect *from* — the guard was built, unit-tested
      and referenced by no route at all. Genuinely done now that `NewVersionPage` gives it
      something to guard: `/subjects/:name/versions/new` carries `schemaWriteGuard`, which is
      named in `core/auth` rather than spelled per route so the route and the `*cdIfScope` on
      the button that points at it read the same constant and cannot drift
- [x] ~~Stub returning admin~~ — **superseded, and better.** The stub was never built; the
      API's unclaimed-instance caller (M8.2) does the same job on the server, so the UI and
      the server agree by construction rather than by two people remembering to.
- [x] Write affordances **absent, not disabled**, for non-admins

**Delivered across M4.1 and M8.2.** The access-control half deliberately waited for real
scopes rather than shipping against a stub — the sequencing hazard ADR-018 warned about was
retrofitting the *check*, and the check went in with the screens. What arrived late was the
sign-in screen that lets someone pass it, which is recorded as
[decisions-pending #26](../DECISIONS-PENDING.md).

## M4.3 Pages

> **The sidebar grows with this list.** `NAV_ITEMS` in `app.ts` carries one row per screen
> that exists — two today. Unbuilt screens are *not* shown as inert "Soon" rows: matching the
> prototype's five-row rail with placeholders imitates the screenshot rather than the design,
> and a row you can see and cannot use is a small recurring cost paid on every visit, where a
> sparse rail is a cosmetic one that ends here. The roadmap is this list; it does not also
> need to live in the chrome, where it would go stale in a second place. Add the row in the
> same commit as the route.

- [~] `Dashboard` — environment-scoped overview. **First slice shipped** on `/`: subject,
      version and awaiting-approval counts, and the most recently registered subjects. Every
      number is derived from the subject list rather than fetched, which is honest at this size
      and has to move server-side the day that list is paginated. Recent breaking changes and
      enforcement coverage wait on the endpoints behind them
- [x] `SubjectListPage`, `SubjectDetailPage` (`/subjects/:name`) and `VersionDetailPage`
      (`/subjects/:name/versions/:ordinal`) — **real routes, not query params.** `:ordinal`
      takes the literal `latest` as well as a number, matching the API, so a pasted link keeps
      meaning "whatever is current". Both resolve `latest` through the subject's gated pointer
      rather than `max(ordinal)` ([ADR-017](../adr/017-gated-latest-pointer.md)); a store that
      derived it would show an unapproved schema as the live contract, and that is pinned by a
      test on each of the two stores. The version screen needs two requests — a version carries
      a schema *id*, never its text — so it tracks two loading states and renders the header
      against the first while the document is still in flight
- [x] `NewVersionPage` — **split out of the line above**, and the app's first write route, so
      it is also what finally wired `scopeGuard` to a route and unblocked M4.5's redirect
      test. A breaking change is rendered as a *success that was held*, not as an error: the
      registry accepted it, left `latest` unmoved and the proposal is reviewable, and a screen
      that reported that as a failure would have people re-submitting a change that landed.
      The three outcomes — registered, held, and already-the-tip (200, no ordinal allocated) —
      are three distinct messages, because conflating any two of them misleads. The store uses
      `exhaustMap` rather than `switchMap`: this is the one store that writes, and cancelling
      an in-flight POST abandons only the browser's half of a request the registry still
      processes
- [x] `NewSubjectPage` — `/subjects/new-subject`, **the last step of the exit criterion that was
      missing** ([ADR-030](../adr/030-create-subject-screen.md)). A write route like `NewVersionPage`,
      behind the same `schemaWriteGuard`, and **no sidebar row, by the rule above**: it is an action
      on Subjects, which stays marked on it. The path is not `/subjects/new` because `new` is a
      valid subject name and the route would hide one; a hyphen is outside the name grammar, so no
      subject can be named this. It is reached from the list's "New subject" (a button with no
      handler until now) and from the empty list and empty dashboard, all `*cdIfScope` on the one
      constant, **absent for a caller who cannot write**. Everything that can block the submit is
      the registry's own refusal, ported into `domain/` as pure TypeScript (`subject-name.ts`,
      `new-subject.ts`) and **held against the real create endpoint by
      `e2e/subject-name-parity.spec.ts`**, which is where two things came from that reading the C#
      did not give: the registry trims with .NET's `Trim()`, which JavaScript's `trim()` disagrees
      with in both directions (U+0085 and U+FEFF), and names are case-sensitive. A name that is taken
      or differs only in case is *advice* — the list is a snapshot, and a check that vetoes on a
      snapshot is wrong in the strict direction. **The name follows a title until it is edited by
      hand** (the prototype's touched flag) by a rule derived from what the registry refuses and
      pinned by creating every suggestion for real. **Creating takes two clicks, and the second says
      what is permanent** — name, format and content model, with the owner and policy changeable
      from the subject's own page (next item; it said "only through the API" until that existed) — as a snapshot of what would be sent, so the second click cannot send
      something the first did not show. Nothing is applied ahead of the registry: it navigates to
      the *name in the answer* (the registry trims), and the page it lands on reads afresh. Not
      here: editing an owner or a policy, which is the next item
- [x] **Changing a subject's owner and compatibility policy** — on `SubjectDetailPage`, **inline, with no
      route and no sidebar row** ([ADR-032](../adr/032-subject-settings.md)). A button beside each value
      opens a panel above them; both are **absent, not disabled**, for a caller without the scope and for a
      retired subject (both present for a deprecated one). They are gated on *different* lists on purpose:
      the owner on `SCHEMA_WRITE_SCOPES` (`subject:write` or `subject:admin`), the policy on a new
      `POLICY_WRITE_SCOPES` (`subject:admin`), the registry's own split — which is why there is no route
      guard to add: one guard could not express "either", and a route would be a page nobody opens except
      from the page it edits. **Each takes two clicks and says what it does, and every sentence was measured
      against the real registry rather than read.** An owner change records one `SUBJECT_UPDATED` entry reading
      "owner changed", naming the account that made it and neither owner, and notifies nobody. A policy change
      takes effect for the *next* registration (a held version stays held when it is relaxed), is **not
      written to the audit log**, and notifies nobody. "Inherit" is a different request from choosing today's
      default (`isInherited`), and the confirmation says what registration does for a subject with no policy of
      its own — which is to judge it by the registry's built-in `BACKWARD · WIRE_JSON`, not its environment's
      default (a registry defect, in STATUS, pinned so the sentence cannot go stale in silence). **Blocks only
      on what the registry certainly refuses** (an owner over 256 characters after its `Trim()`, half a
      policy) **and on drafts with no request** (a blank owner; what the page already shows), held against the
      real `PATCH` and `PUT` by `e2e/subject-settings-parity.spec.ts` — which also holds the scope split with
      one API key per scope. Nothing is applied ahead of the registry: the status line is what it *answered*
      (it trims; `PUT` answers a policy, not a subject) and the subject is then re-read, without the loading
      state that would blank the page. **Not here:** Deprecate and Retire, on the same `PATCH` and `DELETE`,
      which cannot be undone — and the request the app sends cannot carry `deprecate`. **The first thing
      reading the code got wrong** was who the audit entry names: `UpdateSubjectHandler` attributed an owner
      change to the *new owner* and answered 500 for a whitespace-only one; fixed in the same change.
- [x] `ContractsPage` — two screens, `/contracts` and `/contracts/:name`, with a sidebar row.
      List, create, read the bindings (each subject links to its own screen, with its version
      selector beside it), move the enforcement mode, add publish and consume bindings. **Every
      control is absent without `contract:write`** (`CONTRACT_WRITE_SCOPES`, not the schema write
      scopes: a subject admin is not thereby someone who should be able to start refusing the
      fleet's messages), and the contract is fully readable without it. **Moving to `ENFORCE`
      takes two clicks and says what it does** — a contract's mode wins over a service's own
      configuration in both directions ([decisions-pending #30](../DECISIONS-PENDING.md)), so
      every service resolving one of its routes starts refusing non-conforming messages; `OFF`
      and `MONITOR` are the safe direction and take one. Creating leaves the mode to the
      registry when none is chosen, because its default — `MONITOR` — is a decision it owns. A
      binding is shown only once the registry says it stored it, and a refusal renders under the
      form that caused it, leaving the contract and the other form alone. All three writes go
      through one gate: each rxMethod's own `exhaustMap` only guards against itself.
      **The real registry corrected the first draft here too.** Overlap is decided by
      intersecting patterns (`orders.*` and `*.created` both match `orders.created`), which the
      browser cannot see, and separating two overlapping bindings needs a *distinct precedence on
      both* — one binding with a value and one without is still a conflict. Bindings cannot be
      edited or removed (there is no endpoint), so the form says to set one up front. Not here:
      choosing a broker for a binding, which waits on the brokers page, so bindings apply to
      every broker in the environment
- [x] `CompatibilityDiffPage` — `/subjects/:name/versions/:from/diff/:to`, the API's own path with the
      resource prefix dropped. **Three different answers, because "no divergences" is not one of
      them:** the same document, a difference no rule reads, or a list of divergences. The middle
      one is real — under an open content model adding a property is invisible to the engine
      ([decisions-pending #12](../DECISIONS-PENDING.md)) — and rendering it as "no changes"
      would contradict the two different schema ids in the header. Reached from the version
      screen's "Compare with v*n−1*", which is absent on v1 rather than disabled. `latest` is
      refused as an endpoint of a comparison: it would change meaning under the reader after the
      next approval. The subject is fetched alongside the diff so a held proposal is labelled as
      one before the verdict renders
- [x] `ImpactAnalysisPage` — `/subjects/:name/versions/:ordinal/impact`, registered versions only
      (the `POST` form, for a schema nobody has registered, is what CI calls and has no screen
      to call it from). **The verdict is read from `certainty` before `breaks`**: a consumer on
      `latest` carries `breaks: false`, which is the absence of an answer rather than a good
      one, so it is badged `FOLLOWS LATEST` and counted separately — "0 of 3 break" would
      otherwise be true and misleading at once. An empty list says it is a statement about
      declarations, not about safety. **Its first browser run corrected the fixture, not the
      page:** the e2e draft expected a v1-pinned consumer to break under a newly required
      field, and the engine said otherwise — impact is judged FORWARD, and an old reader
      ignores an extra property. The suite now carries both a compatible reader and a type
      change that genuinely breaks one
- [x] `ApprovalsPage` — `/approvals`, with its sidebar row in the same change. Pending breaking
      changes across the environment, oldest first, each opening to its diff and impact — the
      same stores and components the two screens above use, so what the reviewer reads here is
      what those screens would say. **Anyone may read the queue; only `subject:admin` may act on
      it** (`APPROVAL_SCOPES`, narrower than the write scopes on purpose: the author of a
      breaking change approving it is the arrangement the gate exists to prevent), and the
      buttons are *absent*, not disabled (ADR-018). Deciding takes two clicks, and the second
      says what it does — approving moves `latest` for every consumer, rejecting is final for
      that version. A decision replaces the subject with the one the registry returned rather
      than removing a row, because approving moves `latest` and with it every other pending
      version's baseline. The outcome is announced in a `role="status"` line: a row that leaves
      the queue without a word reads as the page having lost it. The queue is derived from the
      subject list like the dashboard's counts, and is bound by the same limit — a held version
      on page two of a paginated list would silently not be in it
- [x] `AuditLogPage` — `/audit`, the first screen of a new `governance` feature. (`LoginPage`
      shipped with [M8.2](M8-identity.md) as `sign-in-page.ts`.) Filter by action, target, whole
      local days and environment-or-all; **applied on submit, not on typing**, because a request
      per keystroke is a query per letter against a table that only grows. **It says when the
      answer was cut off**: the registry returns the newest `limit` with no cursor, so a full
      page and a complete one look identical unless somebody counts. **An empty result carries
      the same caveat the table's caption does** — the trail records what happened, not what was
      attempted, so a `403` leaves no row and "no entries" is not evidence nobody tried
      ([decisions-pending #24](../DECISIONS-PENDING.md)). Deliberately the *opposite* of the
      other wire guards on one point: an action this build has never heard of renders as
      received rather than failing the page, because a log that cannot show the newest kind of
      entry hides exactly what somebody opened it to find. What that costs — the action cannot
      be filtered for — is quiet, so `WireTokenTests.AuditActionsMatchWhatTheFrontendPublishes`
      pins the list
- [x] Settings split — `/settings/environment`, `/settings/brokers`, `/settings/members`,
      `/settings/api-keys` and `/settings/notifications`, behind one sidebar row and one tab
      strip. **Environment** shows the rules as text for anyone and a form for `env:write`, which
      sends only what changed (the registry leaves an omitted member alone, so saving a
      description never restates the compatibility default) and keeps "not set" as a state apart
      from a default the registry chose; it also carries the **pre-release toggle** that
      [decisions-pending #8](../DECISIONS-PENDING.md) left owing. **Brokers** shows an unreachable
      broker as a finding, never an error — the check is a real AMQP handshake and a dead broker
      is the registry doing its job. Credentials are write-only ([ADR-012](../adr/012-environment-over-brokers.md)): the form is always
      empty, the password field is `autocomplete="new-password"` so a manager does not offer the
      registry's own, it empties itself, and the store never holds the password. **Members and
      API keys are `org:admin` to read as well as write**, so they have a route guard and an
      absent tab rather than a read-only fallback. A key's **secret is shown once, in a panel
      that stays until dismissed** (the shared `OneTimeSecret`, also used for a webhook's
      signing key), only the issuer's own scopes are offered, and none is pre-ticked. The
      browser run checks the credential is real: an issued key authenticates, carries exactly
      what was ticked, and stops working when revoked. A refused role change puts the selector
      back — the browser had already moved it, and a selector saying READER beside an OWNER is a
      screen that lies about the one thing it is for
- [x] Notifications as reactive forms that actually persist — the last settings tab. "None
      ticked" is said, beside the boxes, to mean **every event**: the registry reads an empty
      list as all so a subscription can never be enabled and silently deliver nothing, and the
      list renders it as "Every event" rather than as none. The outbox's **parked count is
      surfaced above the list** — a webhook whose receiver is down is retried and then parked
      quietly, and the depth is the only place that shows — and a failure to read it does not
      fail the list. Subscriptions are shown with events as received, so a newer registry's new
      event renders as itself

## M4.4 Port corrections

**Three of these were moot and are struck through**, audited 2026-08-14. They were written
against a React prototype on the assumption it would be ported file by file. It was not — the
Angular app was written fresh — so the defects they describe were never introduced. Struck
rather than deleted, because "why is there no zod here?" is a question worth answering once.

- [x] **Monaco for the schema editor.** Listed here as replacing the prototype's
      `dangerouslySetInnerHTML` regex highlighter, which was also never ported — so the XSS
      framing had expired; Monaco was wanted on its own merits. **A lazy chunk, self-hosted, that
      the textarea can always replace** ([ADR-028](../adr/028-schema-editor.md)). Initial bundle
      +4.7 kB (405.9 → 410.6 kB raw, 102.5 → 104.3 kB transferred); Monaco itself is 3.32 MB raw /
      667 kB transferred, fetched only by this screen, with its worker (307 kB), stylesheet (187 kB)
      and icon font (153 kB) beside it. **The production build corrected the first draft**: the
      stylesheet a lazy chunk imports is emitted and never linked, so the editor rendered unstyled
      in a production build while `ng serve` — which injects it from the module — looked right.
      It is now a vendored file (`tools/vendor-monaco.mjs`, `npm run monaco:check`) linked on load.
      Not Monaco's JSON language service, which would be a second opinion about the document that
      accepts comments; JSON is coloured by a small tokenizer of our own, and every judgement about
      the text comes from `domain/`. Auto-closing is off, because dictation and input methods
      deliver text the way typing does
- [x] `ajv` for client-side validation and sample-payload checking. **In a Web Worker**, because
      `ajv` compiles with `new Function` and the page's policy has no `unsafe-eval` — and because a
      pathological `pattern` can then be killed (four seconds) instead of freezing the tab. Everything
      it says is *advice* and nothing blocks on it: the registry registers `{"type":"bogus"}`. A
      document in another dialect is not handed to it at all, and a sample that refers to
      something only the registry resolves is "could not be checked", never "valid"
- [x] Preserve: immutable-id confirmation (**registering takes two clicks and names the ordinal it
      allocates**; the confirmation is a snapshot of the form, so editing afterwards withdraws it),
      semver auto-increment (**seeded from the highest *live* label, not from `latest`** — with v1
      current at `1.0.0` and v2 held at `2.0.0` the registry compares against `2.0.0`; offered as
      buttons, never applied), clone-previous-version (the registered text verbatim, with a second
      click before it replaces anything written, and `Ctrl+Z` after), the "No versions yet" empty
      state, Format (**changes whitespace and nothing else**: `1.0`, `1E5` and `12345678901234567890`
      survive, because the registry hashes number literals as written — a test asks the registry
      whether the formatted text has the original's id) and Validate
- [x] Preserve: **auto-slugged id with a touched flag** — on the create-subject screen
      ([M4.3](#m43-pages), ADR-030), which is what it had been waiting for. A title suggests the
      subject name and the name follows it until the author edits it by hand; "touched" is set by
      the name input's own `input` event, which programmatic updates do not fire, and is not
      Angular's `touched` (which means blurred). An offer to follow the title again appears once it
      has been left. The slug rule is the least that makes text acceptable to the registry — a valid
      name is returned unchanged, `+` and `:` become `.` as `SubjectNormalizer` does for the SDKs,
      accents are folded, other runs become one `_` — and it never changes case or joins words in a
      style, because a name is case-sensitive and that would be guessing which subject was meant
- [ ] Preserve, **not built**: *compatibility tooltips*
- [x] ~~Collapse the two competing HTTP paths into one typed data-access layer~~ — **moot.**
      There is one: `HttpClient` plus three interceptors, and a typed data-access file per feature
- [x] ~~Uncontrolled `defaultValue` forms → reactive forms~~ — **moot.** A React idiom; no such
      forms exist. New pages still use reactive forms, which is a convention, not a correction
- [x] ~~Drop the unused dependency surface (React Query, react-hook-form, zod, recharts, next-themes, cmdk, vaul, embla, input-otp)~~
      — **moot.** None of them are in `package.json`; the Angular app never took them

## M4.5 Tests

- [x] Playwright E2E against a real API — `web/e2e/`, 218 tests, **against the dev server and
      against the production image** (`CONCORDAT_WEB_URL=http://localhost:5063`). Found the subject list
      broken on its first run (see [STATUS.md](../STATUS.md)), and on 2026-08-15 found two
      more: a first-hit lazy-route compile that made a correct page look absent, and a
      fixture of its own that was growing the registry by two versions per run
- [x] Design-system E2E — `design-system.spec.ts` pins the ported palette, both themes, the
      typography and the shell as **computed styles rather than pixel screenshots**, which are
      exact and identical on Windows and on CI's Linux. It exists because the placeholder
      palette survived a whole milestone with every other test green: nothing named a colour,
      so nothing could notice they were all wrong
- [x] Non-admin E2E: no write affordance renders — a reader signs in and the button is absent,
      an owner signs in and it is there
- [x] Direct URL to a write route redirects — **closed**, now that `/subjects/:name/versions/new`
      exists to paste. Three cases, and the first is what makes the other two mean anything: an
      owner gets the screen, a reader is redirected to the read surface rather than to a dead
      end, and a signed-out visitor reaches sign-in with `returnTo` set. Written earlier it
      would have asserted the `**` wildcard and passed while proving nothing

---

## Exit

Create a subject, register a version, view a diff and an impact report, and approve a
pending breaking change — entirely in the UI.

> **Met on 2026-10-13.** It had been recorded as met while its first step — "create a subject" —
> was not: nothing in the web app called `POST …/subjects`, so a subject had to be made through the
> API or the CLI before the UI could do anything with it. The create screen
> ([ADR-030](../adr/030-create-subject-screen.md)) closed it, and
> `e2e/new-subject.spec.ts` walks the first half with nothing arranged over HTTP: a subject made on
> that screen, then a version registered on the next. The other specs still arrange their subjects
> over HTTP, on purpose: a create is permanent, and a spec that is not about creating should not
> leave one behind.

---

← [M3 — CLI](M3-cli.md) · [Plan index](../PLAN.md) · [M5 — Formats →](M5-formats.md)
