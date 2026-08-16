# ADR-027: Reading the registry requires authentication

- **Status:** Accepted
- **Date:** 2026-08-16
- **Deciders:** Rafael Camara

## Context

A production-readiness security review found every `GET` endpoint under `/v1` open to an
unauthenticated caller — the subject list, subject and version detail, schema bodies, contract
definitions, audit entries, everything ADR-018 already gates on the write side. In `SelfHosted`
mode that was the original, deliberate design: a schema catalogue is meant to be browsed, and
`app.routes.ts` had no guard on a single read route. In `Cloud` mode it was safer than it looked
— `CallerTenantContext` resolves an anonymous caller to `TenantId.SelfHosted` (M9.1), a tenant
nobody in a real deployment is a member of, so the global query filter returns an empty view
rather than another organisation's data — but "safer than it looked" is not the same as
"intended," and a filter an anonymous caller happens to land outside of is not a substitute for
a caller being asked to authenticate at all.

How much of the product assumed the old answer only became visible once the fix was tried.
`web/e2e/`'s suite was built against the original assumption: 24 of 43 tests visited `/subjects`
or a detail route with no sign-in step and expected a normal page, because that had always
worked. That is signal, not just friction — it is a working system's test suite pinning its
actual, shipped behaviour, and the size of the change this decision requires on the frontend is
exactly the size of how much of the product assumed anonymous reading.

## Decision

Every read endpoint requires an authenticated caller, the same as every write endpoint has
required since ADR-018. Server-side, `RequireScope` gates the `GET` routes in
`SchemaEndpoints.cs`, `SubjectEndpoints.cs`, `ContractEndpoints.cs`, `EnvironmentEndpoints.cs`,
`GovernanceEndpoints.cs` and `NotificationEndpoints.cs` that previously carried none. An
unclaimed instance is unaffected — it already answers every request, read or write, as an owner
(M8.2), so there is nothing to gate until somebody exists to authenticate as.

Client-side, `signedInGuard` (`web/src/app/core/auth/scope-guard.ts`) redirects a signed-out
visitor to `/sign-in` before the dashboard, the subject list, or a subject/version detail route
activates — the same shape `scopeGuard` already used for write routes, minus a specific scope
requirement, because reading needs a caller and not a particular one (`scope.ts`: "Anyone may
read"). Unlike `scopeGuard`, this guard protects the read surface itself, so an unresolved
session (`claimed` still `null`, before `/v1/auth/status` answers) refuses rather than falling
back to `/subjects` the way `scopeGuard` does — there is no other read route to fall back to.

The e2e suite now defaults every test to an already-signed-in browser session:
`global-setup.ts` signs in as OWNER through the real form once and saves the resulting
`storageState`; `playwright.config.ts` loads it for every test. The handful of tests
specifically about being signed out (`authorization.spec.ts`, `session.spec.ts`) clear it for
themselves, the same way `authorization.spec.ts` already isolated its one anonymous case before
this decision existed.

## Alternatives considered

- **Leave reads open.** Rejected as the original review found it: a security posture that relies
  on an anonymous caller happening to land in an empty tenant, rather than being asked who they
  are, is not a boundary anyone would design on purpose if reads had not simply started that way
  in `SelfHosted` mode and never been revisited for `Cloud`.
- **Gate `Cloud` only, leave `SelfHosted` public.** Considered specifically because the tenant
  isolation this closes was never actually broken in `SelfHosted` — there is exactly one tenant,
  so there was nothing an anonymous reader could see that a signed-in one could not. Rejected for
  consistency: `ITenantContext`'s whole point (M9.1) is one code path for both profiles with no
  `if (cloud)` above the composition root, and a security posture that forked on profile would be
  exactly that branch, in the one place it is supposed not to exist.
- **Server-side only, no frontend guard.** The cheaper fix, and the wrong one: a route with no
  guard still renders, then fails every request it makes with 401 — which is a broken screen, not
  a sign-in prompt, and is the actual defect this ADR's frontend half closes. `web/e2e/`'s 24
  broken tests are exactly what that looks like when nobody has fixed it yet.
- **Sign in inside every affected e2e test rather than defaulting the session.** Considered and
  rejected once the size of the change was known: editing sign-in into a dozen tests that are not
  otherwise about signing in would make each one partly a test of the sign-in form, so a broken
  form fails fifteen tests with fifteen different-looking failures instead of one. Defaulting the
  browser session, with the few genuinely-anonymous tests opting out, keeps each test asserting
  the one thing it is named for.

## Consequences

- **Positive:** the registry no longer has a security posture that depends on `CallerTenantContext`
  resolving an anonymous caller into an empty tenant being correct forever. It is one fewer thing
  a future change to tenant resolution could silently break.
- **Positive:** `SelfHosted` and `Cloud` now agree that reading requires a caller, closing the
  divergence ADR-018 already closed on the write side.
- **Negative, and real:** browsing the registry — in `SelfHosted` mode, on a network an operator
  may have trusted precisely because it was internal — now requires an account. A team that liked
  "point anyone at the URL, they can look around, nobody needs a login for that" has lost it. That
  trade was not free, and it is the one worth revisiting first if this ADR is ever reconsidered.
- **Negative:** every client of the registry — the CLI, the SDKs, a plain `curl` — that previously
  read anonymously now needs a credential for that too, not only for writes. `docs/RUNNING.md` and
  `docs/QUICKSTART.md` are written against an unclaimed instance and are unaffected by this in
  practice, but the moment either walkthrough is run against an already-claimed one, a read that
  used to just work now needs `POST /v1/auth/signin` first.
- **Neutral:** M9.1's "an anonymous caller resolves to an empty tenant" defense is now
  unreachable in the ordinary request path — an anonymous `Cloud` caller gets 401 before any query
  filter runs — but it is left in place rather than removed. It is what keeps the tenant-isolation
  guarantee true independent of this ADR, the same way a lock still matters on a door with a guard
  in front of it.

## References

- [ADR-018](018-admin-only-schema-editing.md) — the write-side precedent this mirrors
- [M9.1](../plan/M9-cloud.md#m91-tenancy) — the tenant-resolution safety net this ADR makes
  redundant in the ordinary path without removing
- [DESIGN §5, Context E](../DESIGN.md#context-e--identity--access)
- `web/e2e/README.md` — what the suite assumes now that reading requires a session
