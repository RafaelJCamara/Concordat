# M8 — Identity, RBAC, API keys

**Depends on:** [M7](M7-governance.md) · **Unlocks:** [M9](M9-cloud.md) · **Design refs:** [§4 Context E](../DESIGN.md#context-e--identity--access), decisions 008, 018

---

## M8.1 Identity model

- [x] `User`, `Membership`, `Role` (`READER`, `ADMIN`, `OWNER`)
- [x] `ApiKey`, hashed at rest, with scopes
- [x] Scopes: `subject:read|write|admin`, `contract:read|write`, `env:read|write`,
      `broker:read|write`, `org:admin`
- [x] Local accounts, claimed by a one-time `POST /v1/auth/bootstrap`
- [ ] **OIDC** — deferred. ADR-008 makes it optional and the built-in path is what the
      decision was for; nothing here forecloses it, and no test would be meaningful until
      there is a provider to test against.
- [ ] `Tenant` as an aggregate — there is still exactly one, `TenantId.SelfHosted`.
      Memberships already carry a tenant, so the row is what M9 adds, not the model.

**Scope implication is enumerated, never derived from the string.** A prefix rule would
make a future `subject:read-only` satisfy a `subject:read` check. `subject:admin` implies
write implies read, nothing crosses resources, and **`org:admin` does not imply
`subject:write`** — acquiring schema authority by managing the org would be a way around
ADR-018.

**API keys are SHA-256 at rest; passwords are PBKDF2.** The opposite treatments are the
point: a slow KDF makes guessing a *low-entropy* secret expensive, and a key secret is 256
bits from a CSPRNG. Passwords get a length minimum and no character classes, because
composition rules measurably push people towards predictable substitutions.

**A credential is `cdt_<keyId>_<secret>`**, both halves alphanumeric. The obvious choice —
base64url — contains the `_` separator, so roughly half of all issued keys would have
failed to parse and arrived as an *intermittent* authentication failure. There is a test
that issues 200 keys and parses every one.

**An unknown key, a wrong secret, a revoked key, an expired key, a disabled account and a
missing membership all answer identically.** A failed sign-in also runs a real key
derivation against a throwaway hash, so a request for a nonexistent address costs what a
real one does — without that, response timing enumerates users.

**A key may never grant more than its issuer holds.** Without it, anyone who can reach the
issue endpoint mints themselves `subject:admin`.

## M8.2 Authorization

**ADR-018**

- [x] `subject:write` and `subject:admin` granted to admin roles only; a non-admin
      membership carries `subject:read`
- [x] Every mutating endpoint checks scope server-side → `403 insufficient_scope`
- [x] Approve/reject is `subject:admin` — the author of a breaking change cannot wave it through
- [x] Replace [M4.2](M4-web-app.md#m42-access-control)'s admin stub with real role
      resolution — a sign-in screen, an app initializer that probes `/v1/auth/status`,
      `canWriteSchemas` derived from real scopes, a `scopeGuard` and the `*cdIfScope`
      directive

**Enforcement is an endpoint filter, declared next to the route.** A check inside each
handler is one a handler can forget, and the failure is silent: the endpoint works, for
everyone. `RequireScope` also records metadata, and a test enumerates every `POST`/`PUT`/
`PATCH`/`DELETE` endpoint and fails if one carries no requirement — with an explicit,
commented exemption list for the routes that mutate nothing despite their verb.

**401 and 403 are kept apart.** 401 means "tell me who you are", 403 means "I know who you
are and the answer is still no". Collapsing them sends a client into a sign-in loop it
cannot win.

**An unclaimed instance answers as an owner, and stops the moment anyone creates an
account.** ADR-008 promises `docker compose up` gives you something usable immediately, and
M4's web app was built against a stub that returned admin — without this, adopting M8 locks
every existing installation out of its own registry. It is *never* the answer for a request
that presented a credential and failed to verify, so a stale key is a 401 rather than full
access.

**A browser session is a short-lived API key, not a second credential format.** One thing
to verify, one thing to revoke, and sign-in exercises the same path CI does every day.
Session keys are excluded from the key listing so they cannot bury the standing keys
somebody actually has to manage.

## M8.3 Tests

- [x] Every mutating endpoint rejects a `subject:read` principal — for API keys and sessions alike
- [x] The read surface stays fully available to non-admins
- [x] A structural test that no mutating route ships without a declared scope
- [x] No write affordance renders for a non-admin, and a direct URL to a write route
      redirects — covered by `if-scope.spec.ts` and `scope-guard.spec.ts`
- [x] A browser-driven E2E pass over the two together — **closed 2026-08-15.** Playwright, in
      `web/e2e/`, drives a real Chromium against a real API: `authorization.spec.ts` signs in as
      a reader and asserts the write affordance is absent, then as an owner and asserts it is
      there. It earned its keep on the first run by finding the subject list broken against a
      real registry — the two halves had been correct about themselves and wrong about each
      other since M7, because nothing loaded a page

---

## Exit

A non-admin can browse everything and change nothing, and that holds against curl, not
just the UI.

**Met.** `AuthorizationTests.AReaderCanChangeNothing` drives eight distinct write paths as
a `READER` over real HTTP and asserts `403 insufficient_scope` on every one;
`AReaderCanBrowseEverything` asserts the read surface is untouched. The web app signs in
against the same endpoints and hides what the server would refuse — a presentation of the
boundary, not a substitute for it.

**The audit trail now names people.** M7.4 attributed environment, broker and contract
changes to `unknown`, because there was no identity to name; those handlers read
`ICallerContext` now. A session credential carries the signing-in user's own actor rather
than `key:session for alice@example.com`, so a change made through the web app reads as
`alice@example.com` in the trail.

---

← [M7 — Governance](M7-governance.md) · [Plan index](../PLAN.md) · [M9 — Cloud →](M9-cloud.md)
