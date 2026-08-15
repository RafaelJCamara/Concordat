# Browser end-to-end tests

M4.5, and the open half of [decision 26](../../docs/DECISIONS-PENDING.md).

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

**One worker, serially.** Every test signs in against one shared registry; parallel workers would
race over a single global state.

## What is deliberately not here

**"Direct URL to a write route redirects"**, the other test M4.5 names. There is no write route
to paste: `app.routes.ts` has two entries, and `**` redirects everything else to the subject
list. `scopeGuard` is built, unit-tested, and referenced by no route.

Writing it anyway would assert that `/subjects/new` redirects — and it would pass, on the
wildcard, proving nothing. A test that passes for the wrong reason is worse than a missing one,
because the missing one is still on the list. It goes in with M4.3's pages.
