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
# 1. the registry, on :5062
docker build -f docker/api.Dockerfile -t concordat/api:local .
cd deploy/compose && CONCORDAT_IMAGE=concordat/api:local docker compose --profile registry up -d

# 2. the web app, on :4300
cd web && npm start -- --port 4300

# 3. the tests
cd web && npm run e2e
```

`npm run e2e:headed` watches it happen; `npm run e2e:ui` opens Playwright's runner.

Override either endpoint with `CONCORDAT_REGISTRY` and `CONCORDAT_WEB_URL`.

## What they assume

**A registry they may claim and write to.** `global-setup.ts` bootstraps an owner if nobody has,
adds a reader, and creates the `dev` environment. All three are idempotent, because claiming an
instance is not — a suite that assumed a fresh database would pass once and fail every run after.

**A signed-in browser by default.** [ADR-027](../../docs/adr/027-read-requires-authentication.md)
made reading require a caller, and most of this suite is about reading — so `global-setup.ts`
also signs in as OWNER through the real form once and saves the resulting `storageState`, which
`playwright.config.ts` loads for every test. A test that is instead about being signed *out*
(`authorization.spec.ts`'s anonymous case, `session.spec.ts`'s sign-out case) clears it for
itself with `page.context().clearCookies()` rather than this file carrying an exception; a test
that is about being signed in as someone specific (`READER`) still calls `signIn(page, READER)`
as before, which simply overwrites the default session.

**One worker, serially.** Every test signs in against one shared registry; parallel workers would
race over a single global state.

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
