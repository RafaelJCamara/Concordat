# Browser end-to-end tests

M4.5, and [decision 26](../../docs/DECISIONS-PENDING.md) — both halves done.

These drive a real Chromium against a real registry and a real database. Nothing here is
mocked — that is the entire reason they exist.

## Why, concretely

The first time a browser loaded this app against a running registry, the subject list was
broken. `VersionStatus.Dismissed` shipped with M7 and `wire-tokens.ts` never learned it, so one
dismissed version failed the whole page:

> The registry sent 'DISMISSED' for 'status', which this build does not recognise.

**1,489 .NET tests and 187 Angular tests were green throughout.** Each side was correct about
itself. Nothing checked that the two agreed at runtime, because nothing loaded a page.

## Running them

Two processes, neither started by Playwright. A config that quietly started its own copy of
either would produce a suite that passes against the wrong thing.

```bash
# 1. the registry, on :5062 (RabbitMQ is not needed: nothing here publishes)
docker build -f docker/api.Dockerfile -t concordat/api:local .
cd deploy/compose
CONCORDAT_IMAGE=concordat/api:local docker compose --profile registry up -d postgres migrate registry

# 2. the web app, on :4300
cd web && npm start -- --port 4300

# 3. the tests
cd web && npm run e2e
```

**Against the production image instead.** The dev server is not what ships, and the difference
has already bitten: the production build inlines its critical CSS with an `onload` handler that
the app's own Content-Security-Policy blocks, so the full stylesheet never applied — and every test
here was green, because every test here was running against `ng serve`.

```bash
docker build -f docker/web.Dockerfile -t concordat/web:local .
cd deploy/compose
CONCORDAT_IMAGE=concordat/api:local CONCORDAT_WEB_IMAGE=concordat/web:local \
  docker compose --profile registry up -d postgres migrate registry web

cd ../../web && CONCORDAT_WEB_URL=http://localhost:5063 npm run e2e
```

That is also the arrangement that proves the editor works as shipped: Monaco's stylesheet is a
vendored file the page links, the schema checker's worker is served with its own policy, and both
are things the dev server provides by a different route.

`npm run e2e:headed` watches it happen; `npm run e2e:ui` opens Playwright's runner.

Override either endpoint with `CONCORDAT_REGISTRY` and `CONCORDAT_WEB_URL`.

**Running two copies at once.** The saved browser states and the owner's credential live in `e2e/.auth`, and a second run
against a second registry would overwrite them and start every test from the other's session. `CONCORDAT_E2E_AUTH_DIR` moves that
directory (and `--output` moves Playwright's `test-results`), so two runs in one tree do not share a file.

## What they assume

**A registry they may claim and write to.** `global-setup.ts` bootstraps an owner if nobody has,
adds a reader, and creates the `dev` environment. All three are idempotent, because claiming an
instance is not — a suite that assumed a fresh database would pass once and fail every run after.

**A signed-in browser by default.** [ADR-027](../../docs/adr/027-read-requires-authentication.md)
made reading require a caller, and most of this suite is about reading — so `global-setup.ts`
also signs in as OWNER through the real form once and saves the resulting `storageState`, which
`playwright.config.ts` loads for every test. A test that is instead about being signed _out_
(`authorization.spec.ts`'s anonymous case, `session.spec.ts`'s sign-out case) clears it for
itself with `page.context().clearCookies()` rather than this file carrying an exception; a test
that is about being signed in as someone specific (`READER`) still calls `signIn(page, READER)`
as before, which simply overwrites the default session.

**A browser that has already seen the guided tour.** The app starts a tour by itself the first time someone is signed in
([ADR-051](../../docs/adr/051-a-first-time-visitor-is-shown-a-guided-tour.md)), and its card would sit over the first page of every
spec. So `global-setup.ts` writes the tour's "seen" flag (`concordat.tour.seen`, in `localStorage`) into the saved OWNER and
READER states, and `signIn()` writes it before it signs in. A spec that signs in some other way in a fresh context must do the
same (`markTourSeen(page)` in `sign-in.ts`), or the tour starts and its card is in the way. `tour.spec.ts` is the one spec that wants
the opposite, and it takes the saved state with only that flag removed, so it is a first-time visitor who is still signed in and
spends no sign-in; one test signs in through the form with `signIn(page, who, { tour: 'fresh' })`, to prove the tour waits for it. It
reads the page's real buttons and keys, asserts a keyboard can reach, drive and leave it without a trap, that the page underneath
still takes clicks, that it holds on a phone and in both themes, and that it raises no console error or `securitypolicyviolation`.
A new spec that is about being signed _out_ and then _in_ needs no change; one about first-run experience may want `'fresh'`.

**One worker, serially.** Every test signs in against one shared registry; parallel workers would
race over a single global state.

**Shared fixtures that read can use and decisions must not touch.** `global-setup.ts` arranges two
subjects whose proposals are held at the approval gate — `acme.e2e.PaymentTaken` (adds a required
field, which does _not_ break a reader pinned to v1) and `acme.e2e.LedgerPosted` (changes a
property's type, which does) — and registered consumers of each, so the diff, impact and approval
screens have real engine answers to render. Both are idempotent: they look before they write.

**Fixtures the editor specs use, none of which they change.** `acme.e2e.parity.json`,
`acme.e2e.parity.avro` and `acme.e2e.parity.proto` are created once, empty (`schema-validate.spec.ts`'s
test that holds the editor's advice to the registry's `schema_invalid` is dry runs only and leaves nothing
behind: a refused document is never stored), so the registry's dry-run
endpoint has a subject of each format to ask about; nothing is ever registered against them.
`schema-editor.spec.ts` registers into a subject of its own, named by the run
(`acme.e2e.editor.<timestamp>`), because a version cannot be undone.

**The editor is Monaco, and `fill` does not work on it.** Chromium gives it an `EditContext`
element, which is a `div`. `e2e/schema-editor.ts` puts text in by pasting and reads it back from the
rendered lines, and every spec goes through it, so a spec says "put this in" and does not care
whether it is talking to Monaco or to the textarea it falls back to. It pastes rather than using
`keyboard.insertText` because the latter is delivered as input-method composition and a 140 KB
document took over a minute where the same document pasted takes under half a second.

**Deciding is the one thing that cannot be.** Approving a version makes it `latest` and rejecting
is final, so `approvals.spec.ts` creates a subject of its own per decision, named by the run
(`acme.e2e.approve.<timestamp>`), and decides that. That costs the registry two subjects per run;
the shared pair above is never decided, which is what keeps every other spec repeatable.

**Reviewers are named on subjects of their own, and a held version is never left waiting.**
`subject-reviewers.spec.ts` ([ADR-043](../../docs/adr/043-a-subject-names-who-may-decide-its-held-versions.md)) makes **four subjects
per run** in `dev` (`acme.e2e.reviewers.<stem>.<timestamp>`: `else`, `me`, `reader`, `refused`), each with a version held at the gate,
and names reviewers on them. A held version whose reviewers are named, and do not include the signed-in account, is one nobody
signed in can decide, and `approvals.spec.ts` reads the shared queue, so **an `afterAll` clears every subject's reviewers and rejects
what is still held**, even when a test failed. It cannot run if the process is killed: then clear the set with `PUT
/v1/environments/dev/subjects/<name>/reviewers` and `{"reviewers":[]}` using the owner credential, and reject the version. The
signed-in account is the one named, so the spec uses `OWNER.email` and a second address, `someone-else@example.com`, that no account
holds. It never touches the shared pair (`PaymentTaken`, `LedgerPosted`) or `approvals.spec.ts`'s own subjects.

**The approval review's detailed view (the line diff, [ADR-050](../../docs/adr/050-a-held-change-can-be-read-as-a-line-diff.md)) reads five fixed subjects
and decides none.** `approval-diff.spec.ts` needs a held change in every schema format, one long enough to fold, one with more changed lines than the view
will show, and one near the registry's size ceiling, so its `beforeAll` creates `acme.e2e.diff.avro`, `.proto`, `.long`, `.huge` and `.dense` — **fixed
names, created once and looked for on every later run** (it reads the subject first, as `ensureHeldVersion` does, because re-posting the base while a
proposal is pending is a revert) — and the specs only open their reviews. The `dense` one is about 450 KiB (17 000 properties, some 51 000 lines laid out), so
the first run takes a few seconds longer; it is what the spec uses to check that the diff runs in a worker (the page's `worker` event) and that no
main-thread block is long. The preference the view remembers is `localStorage['concordat.approvals.review-view']`; every test is a fresh context, so none
sees another's. The shared `PaymentTaken` is read, never decided. The suite adds those five held versions to the queue for good, which `approvals.spec.ts`
tolerates by paging in to the row it wants (ADR-042).
**Creating a subject is the other thing that cannot be undone, so it leaves things behind.** Nothing
in the app deletes a subject, and a retired subject keeps its name until an administrator deletes it through
the API (ADR-046), which these tests do not do, so a test that creates one has made it for good.
Three arrangements keep that from costing anything:

- `new-subject.spec.ts` names what it creates for the run (`acme.e2e.created.<stem>.<timestamp>`)
  and creates two subjects per run in `dev`, which every run adds to the list for good. The cases
  about a name that is taken or differs only in case use the shared `acme.e2e.OrderCreated` and
  create nothing: the registry answers 409 and holds the line.
- `subject-name-parity.spec.ts` has no dry run to ask — a create either happens or it does not —
  so it runs in an environment of its own, **`e2e-names`**, and its table is _fixed_ rather than
  named per run. The first run creates each accepted row; every later run finds the name taken and
  the registry answers 409 `subject_already_exists`, which is only reached once the name has passed
  validation and which names the name it holds after its trim. So a re-run proves the same thing
  and creates nothing. **Do not point other specs at `e2e-names`**, and do not rename a row: that
  creates another subject.
- The empty states can only be seen in an empty environment, and `dev` never is. **`e2e-empty`**
  is created once and **no spec may create a subject in it**: the tests there click through to the
  form and stop. They aim the app at it by answering `config.json`, the one request in this suite
  that is stubbed, and the one a deployment itself supplies; everything else is the real registry.

**Changing a subject has no dry run, and what it is tried on is permanent.** An owner or a policy can be
changed back, but the subject cannot be deleted and the audit trail an owner change writes into is
append-only. So the two specs about it follow the rule creating one does, and add one fixture:

- `subject-settings.spec.ts` makes **six subjects of its own per run** in `dev`, named
  `acme.e2e.settings.<stem>.<timestamp>`, and one in `e2e-inherit`. A test that changes an owner or a
  policy changes one of these and nothing else: **never `acme.e2e.PaymentTaken`, `acme.e2e.LedgerPosted`,
  `acme.e2e.OrderCreated`, or the defaults of `dev`** — other specs read them, and they are what makes the
  rest of the suite repeatable. (Its reader test opens `acme.e2e.OrderCreated` and only _looks_.)
- `subject-settings-parity.spec.ts` makes **two more** (`acme.e2e.settingsparity.<timestamp>`, and
  `acme.e2e.settingslife.<timestamp>`, which it deprecates and then retires) and writes to them about a
  hundred times: every owner row is another change to the same subject. It is plain Node like
  `subject-name-parity.spec.ts`, and it issues one API key per scope the registry has, so it can ask
  what each may do — **and revokes them in a `finally`**, so a run leaves no working credential behind.
  `trim-table.ts` holds the whitespace table both parity specs measure, so the two cannot drift.
- **`e2e-inherit` is an environment whose default compatibility policy is `NONE`**, created once and
  **never changed**. It exists for one test: a subject that sets no policy of its own is judged by the
  environment's default, so a breaking second version is _accepted_ there (`ACTIVE`). Until
  [ADR-033](../../docs/adr/033-inherit-means-the-environments-default.md) it was _held_, because registration
  read the registry's built-in default instead, and this test asserted that; if it goes red the other way the
  registry has stopped reading the environment, and the failure to act on is "the sentence in
  `subject-policy-editor.ts`, STATUS.md and ADR-033", not "fix the test". Do not point other specs at it.
- **`e2e-follows` is the one environment whose default a spec moves, because it is the only one a spec
  owns.** `environment-default.spec.ts` creates it once (default `BACKWARD · WIRE_JSON`, chosen at creation),
  puts that default back before every run (it is its own, and nothing else reads it), then changes it on the
  real environment settings screen and watches an inheriting subject follow it and a pinned one not. It makes
  **two subjects per run** in `e2e-follows`. **Do not point other specs at it**, and do not make a second
  spec move a default: `dev` and `e2e-inherit` are read by everything else. The registry refuses half a policy
  (`400`), so the form sends both members when either select changed, and the spec changes the default one
  select at a time: the mode alone (`BACKWARD · WIRE_JSON` → `NONE · WIRE_JSON`), then the surface alone
  (→ `NONE · WIRE`), asking the registry after each. It used to pin the one-member `400` as a defect, and that
  test was inverted when the form was fixed; a test that goes red on a "Could not save the settings" alert
  means the form has gone back to sending one member.
- The audit assertions in `subject-settings.spec.ts` are the confirmations' own claims: an owner change leaves
  exactly one `SUBJECT_UPDATED` row attributed to the signed-in account and reading both owners, and a policy change exactly one
  `SUBJECT_POLICY_CHANGED` row attributed to the same account and reading `policy <old> -> <new>`. They
  are what stop a sentence on the screen from going stale when the registry starts recording more.

**Notification subscriptions are the one thing a spec makes and then deletes, and an `afterAll` deletes them even when a test fails.**
`notifications.spec.ts` creates an email subscription with no event filter, a webhook to a closed local port and (since
[ADR-034](../../docs/adr/034-a-policy-change-notifies-subscribers.md)) a second webhook listing only
`SUBJECT_POLICY_CHANGED`, and deletes all three in its last tests. They are not permanent fixtures because a live
subscription is delivered to by the registry's background pump, and the specs after it change policies and register
versions: an unfiltered one is told of every one of them, a policy change included. The block is serial, so a test that
fails before the deletes skips them, which used to leave all three live in `dev`; an `afterAll` now deletes whatever the
block made, by endpoint (`deleteSubscriptionsAt` in `registry.ts`), so a red run leaves nothing. It is a no-op when the
delete tests ran. It cannot run if the process is killed, so after a killed run list
`GET /v1/environments/dev/notifications` and delete what is there (the owner credential is in
`e2e/.auth/owner-credential.txt`). The new-event test is
deliberately about the _screen and the registry agreeing on the token_ (the registry answers `400` for a token it does
not know) and not about a message being staged: a browser reading `GET /v1/notifications/outbox` races the pump, which
drains it every fifteen seconds, so that claim is the .NET suite's (`APolicyChangeStagesExactlyOneNotification`,
`APolicyChangeIsDeliveredToTheSubscriptionsThatWantIt`). The policy journey in `subject-settings.spec.ts` holds the
confirmation's sentence about who is notified as text, and asserts the old _Nobody is notified_ is gone.

**Contract bindings are changed and removed on contracts of their own, and a contract cannot be deleted.**
`contract-bindings.spec.ts` ([ADR-035](../../docs/adr/035-contract-bindings-have-an-identity.md)) makes **three
contracts per run** in `dev` (`e2e-bindings-<timestamp>`, and `-quiet` and `-read` beside it, for the console-error check and the
reader), arranged through the API where the arrangement is not what is tested. It never touches the contract
`contracts.spec.ts` makes, and the other way round. **Its bindings use an exchange of its own per run**
(`bindings-<timestamp>`), and that is not decoration: a route is governed by _every_ contract in the environment that matches
it, and `dev` already holds the other specs' contracts and every earlier run's, so resolving `orders` / `orders.created` would
answer with all of them and the spec could not say that _its_ binding is what resolves. A run that goes red part-way leaves what it
made, like every spec here that creates something permanent.

**The compatibility explanations read and create nothing, and are checked on a phone.** `settings.spec.ts` (390px, light and
dark, and a touch context), `new-subject.spec.ts` and `registry-data.spec.ts` open the "What do these mean?" buttons from the
keyboard and check the words against `COMPATIBILITY_*_MEANING` in the app's domain, which they import rather than retype. They
use the default signed-in browser (no sign-in), choose options in selects without saving, and add the one extra
assertion to a test that already creates its subject. `compatibility-help.ts` holds the helpers, including a contrast
check that paints the ancestors' backgrounds onto a canvas, so it holds in either theme whatever colour syntax the browser
reports. A guide is closed with no token in the DOM because other specs find a policy by `getByText(token, { exact: true })`,
which matches hidden text; a new control near a policy must not repeat a token in the DOM while closed, and must not
carry an `aria-label` containing "mode" or "surface", because `getByLabel('Mode')` matches by substring.

**The subject search box is tested on subjects of its own, five per run, and they are permanent.**
`subject-search.spec.ts` ([ADR-038](../../docs/adr/038-subject-prefix-search.md)) makes **five subjects per run** in `dev`, all
under one prefix named for the run (`acme.e2e.prefix.<timestamp>.`: `orders.OrderCreated`, `orders.ordercreated`,
`billing.InvoiceRaised`, `wild.a_b` and `wild.axb`), each with one version, and searches that prefix, so what it finds is this run's
subjects whatever earlier runs left behind and the case and underscore assertions have names that differ only in case and in `_`.
It reads nothing another spec makes and no other spec reads these. Like every spec here that creates something, a run that goes
red part-way leaves what it made. It signs in nowhere (the default browser is the owner); it holds two registry answers back with
`page.route` to look at the in-between states, which are the only requests it touches.

**The subject list's pages are asserted in an environment of their own, `e2e-paging`, which holds 130 subjects and nothing else writes to.**
`subject-paging.spec.ts` ([ADR-052](../../docs/adr/052-the-subject-list-is-paged-with-page-controls-and-the-url.md), [ADR-049](../../docs/adr/049-subjects-can-be-listed-newest-first.md))
creates `e2e-paging` once and fills it with **130 subjects named `e2e.paging.s001` to `s130`**, in order and one at a time (the registry stamps each with
its own clock, and the order the screen shows is that), and a re-run finds them and creates nothing: it is a fixed fixture, like `e2e-names`, and **no
spec may create a subject in it**. 130 is three pages at the default size of 50 and six at 25, with a last page that is not full (30 and 5), which the
page arithmetic needs; and an environment of its own so that the count is a fact and not whatever `dev` has grown to. It points the app at it by answering
`config.json`, as `new-subject.spec.ts` does for `e2e-empty`; everything else is the real registry. What the screen is held to is **the registry's own
order**, read back with `sort=newest` before each test and compared row for row, so no assertion depends on which subject was made when: the pages, Next
and Back and Forward, a reload and a link opened in a second browser context, a page past the end (it must land on the last page with the sentence, and
Back must not return to the bad address), an address that is junk (`page=abc&size=7`), a change of size, a prefix across two pages, a slow page that must not
land over the one clicked after it, the keyboard, a phone, and the console. If it ever fails on a count of 130 an earlier interrupted run left the
fixture short; run it again.

**The pill that links a held version to the approvals screen is clicked, in a browser, on subjects of its own, four per run, and the held versions are
rejected at the end.** `subject-approval-link.spec.ts` ([ADR-052](../../docs/adr/052-the-subject-list-is-paged-with-page-controls-and-the-url.md))
makes `acme.e2e.pill.<timestamp>.one` (one held version), `.many` (two), `.plain` (none) and `.old` (deprecated, none) in `dev`, and `.stale` and `.both`
inside two of its tests (a version decided between the list being read and the click, and a deprecated subject that is also held), and an `afterAll`
rejects whatever this run still holds, even when a test failed, so the shared queue (`approvals.spec.ts` reads it) is not left holding proposals. It checks
the link, its accessible name, that a hover shows a ring, that Tab reaches it, that it lands on the approvals screen narrowed to the subject with the one
held version's review open and its heading focused, that several held versions open none, that **Show all** brings the rest of the queue back, the empty
sentence, and that a state with no destination is inert. It never touches the shared pair (`PaymentTaken`, `LedgerPosted`).

**The deletion test leaves no subject behind, which is the one exception to the rule above.**
`subject-purge.spec.ts` ([ADR-046](../../docs/adr/046-a-retired-subject-can-be-deleted-for-good.md)) makes **two subjects per run** in
`dev` (`acme.e2e.purge.<timestamp>.Gone` and `.StillActive`), deletes both again (one in a test, both in `afterAll`, so a red run does
not leave them), and so does not change the permanent-subject counts above. What it leaves is audit entries, named for the run. It has
no controls to click (`subject-lifecycle.spec.ts` has those): it purges through the API and asserts on what the pages then show.

**The lifecycle spec deletes its subjects too, and leaves one empty contract.** `subject-lifecycle.spec.ts` makes **four subjects per run** in `dev`
(`acme.e2e.lifecycle.<timestamp>.Retired`, `.Deleted`, `.Bound` and `.Watched`), retires and deletes each by the end (an `afterAll` finishes the ones a
red run left), and so leaves no subject. **It does leave `e2e-lifecycle-<timestamp>`, one contract with no bindings**, because nothing deletes a contract.

**The dashboard is asserted against an environment of its own, and never by an absolute count of `dev`.**
`dashboard.spec.ts` ([ADR-037](../../docs/adr/037-the-dashboard-is-answered-by-the-registry.md)) arranges **`e2e-dashboard`**, created
once by the spec and **never written to by any other spec**, and makes **two subjects, one contract (two publish bindings) and one reported
violation per run** in it, named for the run (`acme.e2e.dashboard.held.<timestamp>`, `…bare…`, `e2e-dashboard-<timestamp>`). It reads the
overview through the API before and after arranging and asserts the difference (two subjects, one held version, one more `ENFORCE`
contract, and so on), then asserts the page shows what the registry reports for the same environment. That is why the environment
is its own: "the newest subject" and "the newest breaking change" can only be asserted by name where nothing else writes. `dev` is
only ever compared to the registry's own answer for `dev`, never to a number. The empty states use `e2e-empty` as
`new-subject.spec.ts` does (answering `config.json`), and **nothing may create a subject, a contract or a violation in it**: the dashboard's three
empty panels depend on that. The first test asserts the page asks for the summary once and for the subject list never, which is the
claim that nothing is derived in the browser.

**Account changes are tried on one throwaway member per run, who is never an owner, and is disabled at the end.**
`account-lifecycle.spec.ts` ([ADR-048](../../docs/adr/048-an-account-can-change-its-password-be-reset-and-be-disabled.md)) creates **one ADMIN
member per run** (`e2e-account-<timestamp>@concordat.test`) and takes it through the whole story: an owner resets its password on the
members screen, it signs in with the temporary one and is held on the account screen until it chooses a new one, changes it again by
choice, is disabled (its session stops at once) and is enabled. **Nothing here touches the
shared owner or reader**: changing, resetting or disabling either would break every spec after. **The member is never an OWNER**, and that
is load-bearing: a second enabled owner would let `identity.spec.ts`'s "will not let an owner demote themselves" demote the shared owner for
real. Members cannot be deleted, so an `afterAll` disables the member (it leaves no working credential), and a run adds one more disabled
row to Settings → Members. It also holds a reader to the Account tab (and a refused password change that must not sign them out). Every
sentence in the confirmations is asserted against the registry after the click; that a change ends the member's _other_ sessions, that a
reset ends their sessions, that a disabled member is refused at sign-in, and that keys survive a change and a reset and die with a
disabling, is the .NET HTTP suite's, which can arrange a second session and a key for free. **It spends two of the sign-in limiter's
attempts** (the forced sign-in through the form, and the reader's refused password change). The first version spent six, and the last two
tests of `session.spec.ts` failed with `429`; the second spent four, which left no room for CI's one retry.

**Match the account control, not the account's email.** A subject the browser creates is owned by
the signed-in account, so its email now appears in a table cell on the list as well as in the
sidebar, and `getByText(email)` stopped being unique the first time `new-subject.spec.ts` ran. The
sign-in helpers look in `cd-app-sidebar`.

**The auth limiter budgets about one run, and the suite is built to stay under it.** Sign-in is
rate-limited to 20 attempts per five minutes per address. A run makes thirteen: three in
`global-setup` (the owner through the API, then the owner and the reader through the real form,
whose browser states every test starts from), four in `session.spec.ts`, which is about signing
in, and six in `authorization.spec.ts` and `registry-data.spec.ts`. **A spec that is not about
signing in must not sign in.** The default browser is already the owner; a spec about what a
reader is offered uses `test.use({ storageState: READER_STORAGE_STATE })`; and arranging data
through the API uses `ownerCredential()`, which reads what global setup saved. When this suite
went over ninety tests it overran the budget by one and failed an unrelated sign-in near the end
— a red run that said nothing about the screen under test, and would have been red in CI too. **The figure of thirteen is stale:** with six more, the run failed, and with four it passed, so
the rest of the suite spends 15 or 16. `account-lifecycle.spec.ts` adds two (see above), which leaves **two or three**, so the next
spec that signs in must find a way not to. `tour.spec.ts` spends one of them (the first-time visitor who signs in through the form),
which leaves **one or two**.

Two runs inside five minutes will still exceed it. Restarting the registry resets the limiter
(it is in-memory); CI runs once.

## What is deliberately not here

**A network stub.** `design-system.spec.ts` asserts the page makes _no_ cross-origin request
at all ([ADR-026](../../docs/adr/026-self-hosted-web-fonts.md)), which only means something
against real network conditions. Stubbing would make it tautological.

**Pixel screenshots.** `design-system.spec.ts` pins the design with computed styles instead —
token values, resolved colours, font stacks, the sidebar's two widths. `toHaveScreenshot` keeps
one baseline per platform, and this suite runs on a developer's Windows machine and on
`ubuntu-latest` in CI, which render text differently enough to differ on thousands of pixels.
The choice was a suite that fails every CI run until someone commits Linux baselines from a
container, or a `maxDiffPixelRatio` slack enough to sleep through a real regression. Computed
styles are exact, identical on both, and name what broke: _"primary is no longer the
prototype's teal"_ beats _"1,297 pixels differ"_.
