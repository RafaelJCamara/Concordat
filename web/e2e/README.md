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

**One worker, serially.** Every test signs in against one shared registry; parallel workers would
race over a single global state.

**Shared fixtures that read can use and decisions must not touch.** `global-setup.ts` arranges two
subjects whose proposals are held at the approval gate — `acme.e2e.PaymentTaken` (adds a required
field, which does _not_ break a reader pinned to v1) and `acme.e2e.LedgerPosted` (changes a
property's type, which does) — and registered consumers of each, so the diff, impact and approval
screens have real engine answers to render. Both are idempotent: they look before they write.

**Fixtures the editor specs use, none of which they change.** `acme.e2e.parity.json`,
`acme.e2e.parity.avro` and `acme.e2e.parity.proto` are created once, empty, so the registry's dry-run
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

**Creating a subject is the other thing that cannot be undone, so it leaves things behind.** There
is no delete and a retired subject keeps its name, so a test that creates one has made it for good.
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

- `subject-settings.spec.ts` makes **five subjects of its own per run** in `dev`, named
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
  **never changed**. It exists for one test: a subject that sets no policy of its own is _held_ when it
  breaks, because registration judges it by the registry's built-in default and not by the environment's. That
  is a registry defect the policy editor's confirmation describes, and this is the test that goes red when it
  is fixed — the failure to act on is "update the sentence in `subject-policy-editor.ts`, STATUS.md and
  ADR-032", not "fix the test". Do not point other specs at it.
- The audit assertions in `subject-settings.spec.ts` are the confirmations' own claims: an owner change leaves
  exactly one `SUBJECT_UPDATED` row attributed to the signed-in account, and a policy change leaves none. They
  are what stop a sentence on the screen from going stale when the registry starts recording more.

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
— a red run that said nothing about the screen under test, and would have been red in CI too.

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
