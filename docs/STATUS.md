# What is missing

**As of 2026-08-15.** A companion to [PLAN.md](PLAN.md), which records what was built, and
[DECISIONS-PENDING.md](DECISIONS-PENDING.md), which records what has not been decided. This
file records what is **not there**, so the gaps are in one place rather than distributed across
ten milestone files as unchecked boxes.

Everything under "verified locally" below was run on this machine today, against the real
container image and a real PostgreSQL. Everything else is claimed on the strength of tests.

---

## What runs today

The whole product path works end to end. Verified 2026-08-14 by building
`docker/api.Dockerfile` — **its first real build outside CI** — and running the compose
`registry` profile:

```
docker build -f docker/api.Dockerfile -t concordat/api:local .
cd deploy/compose && CONCORDAT_IMAGE=concordat/api:local docker compose --profile registry up -d
```

| Step | Result |
|---|---|
| Migrator runs as its own service, API waits for it | exits 0, API starts |
| `GET /health/ready` | `Healthy` |
| Create environment, subject, register a schema | 201, ordinal 1, `ACTIVE` |
| Compatibility engine on a breaking change | `AWAITING_APPROVAL` with a `required_field_added` divergence naming the path |
| Contract + publish binding, then `POST /contracts/resolve` | governed route returns the contract in `ENFORCE`; `order.line.added` returns `{contracts: [], enforcement: "OFF"}` — `*` is one word |
| `PUT …/registration-policy` to `CLOSED`, then register | **403** `registration_policy_forbids` |
| CLI `export` against the live registry | wrote 2 contracts to disk |
| Quickstart sample over real RabbitMQ | valid message accepted, invalid one refused at publish, queue drained clean |
| Container healthcheck | **was broken, now fixed** — see below |

`CONCORDAT__*` configuration binding is listed as an open M1 item and appears to be **stale**:
the container ran on `ConnectionStrings__Concordat` and `Concordat__Profile` today.

### The one defect this found

`docker ps` reported the registry **unhealthy while it answered every request correctly**. The
compose healthcheck called `wget`, and the aspnet runtime image has neither wget nor curl — so
the check had never once passed, and `depends_on: condition: service_healthy` against the
registry would have waited forever.

It failed in the worst direction: a working service that looks broken to whoever is evaluating
the product. The image now installs `curl` for this alone. Azure Container Apps was never
affected — its probes are HTTP requests made by the platform, not commands run inside the
container — which is exactly why CI and the Bicep deployment never caught it.

### What the first browser run found

**The subject list was broken against a real registry**, and had been since M7.

`VersionStatus.Dismissed` shipped with M7 and `web/src/app/domain/registry/wire-tokens.ts` never
learned it. The web app's unknown-token guard is strict by design — an unrecognised token means a
newer server, and guessing would be worse — so one dismissed version failed the *entire* page:

> The registry sent 'DISMISSED' for 'status', which this build does not recognise.

**1,489 .NET tests and 187 Angular tests were green throughout.** Each side was correct about
itself; nothing checked that the two agreed at runtime, because nothing loaded a page. The
frontend spec even predicted it in a comment — *"a fourth status added server-side must reach
this list"* — and nothing enforced the prediction. `WireTokenTests` now compares all three shared
vocabularies against the .NET enums.

A second, smaller find from the same run: `/v1/auth/resume` answered **401** when there was no
cookie, so every signed-out page load of a healthy app logged a console error. The app has to
probe on startup — it cannot read an httpOnly cookie to know whether probing is worthwhile — so
"no session" is an ordinary answer, not an authentication failure. It answers 204 now, and 401
only when a cookie was presented and rejected.

### Two things that path does not cover

- **The web app is not in the compose stack.** There is no way to bring up the UI alongside the
  registry with one command, because most of the UI does not exist yet (below).
- **The quickstart does not exercise contracts.** It prints `0/0 routes governed` — the sample
  predates contract resolution and declares no topology, so the newest feature in the SDK is
  demonstrated by nothing a user would run.

---

## The numbers, and why the raw count misleads

| Milestone | Done |
|---|---|
| M0 foundations | 22 / 25 |
| M1 registry core | 60 / 63 |
| M2 .NET client | 32 / 41 |
| M3 CLI | **19 / 19** |
| M4 web app | 15 / 28 |
| M5 formats | 14 / 15 |
| M6 SDKs | 5 / 24 |
| M7 governance | 29 / 31 |
| M8 identity | 12 / 15 |
| M9 cloud | 11 / 16 |
| **Total** | **219 / 277** |

**Do not read that as a progress bar.** Auditing the 62 unchecked boxes found roughly 11 that
are not work at all:

- **M2 has an archived scope list.** `## M2.2 notes (original scope)` preserves the original
  seven-item envelope scope, deliberately left unchecked as a historical record while the
  section above it is marked done. Most of those items shipped — Mode A headers and the Mode B
  content-type are exercised by the quickstart. At least one did not: see the binary framing
  gap below.
- **One M2 item is a cross-reference,** not a gap: "binding it to `IReadOnlyBasicProperties`" is
  annotated *"— M2.4"*, where it was in fact done.
- **Three M4 items are moot.** They are corrections to a React prototype that was never ported.
  Detail below.

Real remaining work is therefore closer to **47 items**, and about 32 of those are M4 and M6.

M3 is the first milestone to reach 19/19, closed by `Concordat.Contracts.Testing` (decision 13).

---

## Missing product surface

### M4 — the web application

The honest count is **11**. Every API behind these exists, so this is Angular work rather than
design work.

**Genuinely missing:** Dashboard · SubjectDetail / VersionDetail / NewVersion pages ·
ContractsPage · CompatibilityDiffPage · ImpactAnalysisPage · ApprovalsPage · AuditLogPage ·
the settings split (environment, brokers, API keys, members) · notification forms that persist ·
Monaco for schema editing · `ajv` for client-side validation · the "preserve" list of prototype
behaviours (immutable-id confirmation, semver auto-increment, clone-previous-version, empty
states).

**Done 2026-08-15:** the Playwright E2E suite, and the non-admin affordance test. The third
M4.5 item — "direct URL to a write route redirects" — is blocked on M4.3 rather than unwritten:
there is no write route to paste, and `scopeGuard` is referenced by no route at all. That also
corrected an M4.2 line which had recorded the guard as wired.

**Already done but still listed:** `LoginPage` — `sign-in-page.ts` shipped with M8.2.

**Moot — corrections to a prototype that was never ported:**

| Listed | Why it is moot |
|---|---|
| "Collapse the two competing HTTP paths" | There is one. `HttpClient` plus three interceptors, two typed data-access files. |
| "Uncontrolled `defaultValue` forms → reactive forms" | A React idiom. No such forms exist. |
| "Drop the unused dependency surface (React Query, zod, recharts, …)" | None of them are in `package.json`. The Angular app never had them. |

The Monaco item is worth keeping but its stated reason has expired: it is framed as fixing the
prototype's `dangerouslySetInnerHTML` XSS hole, and that code was never ported either. Monaco is
wanted on its own merits.

**What exists today:** sign-in, a subject list with a table, and the routing, guards and
interceptors under it. Two features out of roughly ten.

### M6 — Tier 2 SDKs

**19 items, deferred on purpose** by [ADR-024](adr/024-v1-ships-dotnet-only.md) until the .NET
SDK has been tested against a real workload. The protocol and the conformance corpus are
published and already caught cross-language defects with no second SDK in existence, which was
the risk the deferral had to answer.

Not forgotten — waiting on your judgement that .NET is proven.

---

## Holes in what is already built

These matter more than the two lists above, because the surface exists and looks finished.

| Gap | Where it bites |
|---|---|
| ~~Mode B binary framing is specified and not implemented~~ | **Closed 2026-08-14** by decision 19 — amended out of v1 rather than built. M2.5 measured every transport it could raise and `concordat-*` headers survived all of them, so framing had no demonstrated need. ADR-010 carries the amendment and says what would bring it back. |
| ~~`ENFORCEMENT_VIOLATION` is a notification event nothing emits~~ | **Closed 2026-08-14** by decision 25 — `POST …/violations`, aggregated and reported by the SDK, upserted by fingerprint, notification on first sight only. **Nothing schedules the flush yet**: a host opts in by wrapping its observer and calling `FlushViolationsAsync` on a timer. |
| ~~An environment with no row has no registration policy~~ | **Closed 2026-08-14** by decision 23. The first write creates the row with the derived id, so the policy applies from the first request rather than from whenever somebody thought to create the environment. |
| **Approval reviewers do not exist** | Anyone with `subject:admin` can approve anything. A reviewer *set* was deferred to M8 and M8 did not build it. |
| **Hard delete does not exist** | Soft delete is all there is. The full rule — no registered consumers, force flag, audit entry — is an outstanding commitment. |
| **Subject prefix search is not implemented** | Value converters do not translate `StartsWith`; it needs a `ComplexProperty` mapping or a shadow column. |
| ~~Two contracts can govern one route, first-by-name wins~~ | **Closed 2026-08-14** by decision 21 — resolve returns all of them, strictest mode and union of subjects, counted on the client's status. M7.4's impact analysis still attributes a route to one contract. |
| ~~A page reload signs you out~~ | **Closed 2026-08-14** by decision 26 — an httpOnly `SameSite=Strict` cookie and a `/auth/resume` route that is the only thing accepting it. The credential still never touches `localStorage`. |
| ~~`AllowAnonymousUntilClaimed` is on by default~~ | **Still on, and now audible** (decision 27). The API logs a warning naming both ways to close it and repeats hourly until claimed; the web app shows a banner. Verified against a real container. |
| ~~No browser E2E over sign-in + guards~~ | **Closed 2026-08-15.** Playwright, 11 tests in `web/e2e/`. It found the subject list broken on its first run — see below. The one M4.5 test still absent is "direct URL to a write route redirects", because there is no write route to paste. |
| **`Tenant` is not an aggregate** | There is exactly one, `TenantId.SelfHosted`. Cloud multi-tenancy is tested but single-rowed. |
| ~~The derived-environment-id decision is unmade~~ | **Closed 2026-08-14.** Adopted, by creating rows that carry the derived id. No migration, and no orphaned subjects. |

---

## Blocked on you

Nothing here can be done from inside the repository.

| Item | Consequence of waiting |
|---|---|
| **Rename the GitHub repo `Signet` → `Concordat`** | Repo-side is done and pushed; only the two clicks remain. Package metadata is already corrected. |
| **Buy `concordat.dev`** | Problem Details `type` URIs already point at it — as seen in the 403 above. Blocks OAuth redirect URIs. |
| **Publish the GHCR images** | The workflow has never run. The package will be private by default, and Container Apps needs it public or a `registries` block plus a pull secret in the Bicep. |
| **Stripe account** | Columns and an index exist so a webhook has somewhere to land. Nothing charges anyone. |
| **Google / GitHub OAuth clients** | SSO cannot be built without registrations. |
| **Create the `@concordat` npm org** | Only matters at M6. |
| **Confirm the dunning policy** | Past-due currently still allows writes, deliberately. |

---

## What the tests actually cover

Six kinds, not one. Worth knowing which, because "1,493 tests" says nothing about what would
survive being wrong.

| Kind | Where | Tests | What it proves |
|---|---|---|---|
| Domain unit | `Domain.Tests`, three `Formats.*` | ~840 | Invariants in isolation. No I/O |
| Application handler | `Application.Tests` | 155 | Handler refusals and **ordering**, with hand-written fakes |
| HTTP integration | `Api.IntegrationTests` | 216 | Real HTTP against real PostgreSQL, via Testcontainers |
| Conformance corpus | `Conformance` | 99, over 98 fixtures | The protocol as an executable spec |
| Broker end-to-end | `EndToEnd`, `RabbitMq.Tests` | 63 | Publish and consume through real RabbitMQ |
| Empirical measurement | `HeaderSurvival` | 14 | What brokers actually do to headers |
| Browser end-to-end | `web/e2e` | 11 | A real Chromium against the real stack |

Plus 188 Angular unit tests.

**No mocking framework, deliberately.** No Moq, NSubstitute, FluentAssertions or AutoFixture —
`Directory.Packages.props` carries xunit, Testcontainers and `TimeProvider.Testing`. Every double
is hand-written in `TestSupport/Fakes.cs`, which is what lets `Application.Tests` assert things
like *"this refusal cost zero meter calls and staged nothing"* — a property mock-verify tends to
hide rather than express.

**`Application.Tests` is mostly about ordering.** The dominant shape is *"a refusal must happen
before anything is spent"*: no canonicalisation, no billing round trip, no staged schema.

**`HeaderSurvival` does not test our code at all.** It raises three brokers — two for federation
alone — and measures whether `concordat-*` headers survive dead-lettering, shovel, federation,
STOMP, MQTT and the AMQP 1.0 conversion. Those measurements are load-bearing: they are what
scoped binary framing out of v1 (decision 19).

**The corpus is a specification, not a suite.** 98 fixtures, each carrying a `why`, enforced by
`EveryFixtureExplainsWhyItExists`. It is what M6's SDKs will be built against, which is why
recent protocol changes landed there rather than only in C#.

### Where the layers have failed each other

Three structural tests exist because a gap between two correct halves shipped anyway:

- `EveryMutatingRouteDeclaresAScope` — caught unguarded routes twice.
- `TheVocabularyMatchesWhatTheFrontendPublishes` — caught the `ci` scope drifting.
- `VersionStatusesMatchWhatTheFrontendPublishes` — **added 2026-08-15, after a browser found
  the subject list broken.** The pattern was already known and had been applied to scopes only.

### Still thin

- **No load, soak or property-based tests.** The outbox pump and the violation reporter have no
  concurrency test beyond what their unique indexes enforce.
- **Coverage is collected and never read**, which is decision 4 and deliberate.
- **CI now splits**: `build & test`, `web app`, and a separate `browser end-to-end` that runs the
  API and dev server from source. A domain-unit change still waits on the broker containers in
  the first job.

---

## Decisions

**10 open.** The "say yes and I go fix it" group was worked through on 2026-08-14, and the
owner's own-estate questions — #14 exchange rights, #10 generic types, #8 pre-release labels —
plus #17's Avro window were answered and built on 2026-08-15.

What is left splits three ways:

| | |
|---|---|
| **Recent calls of mine, to confirm or overturn** | #30 contract beats local `Mode` · #31 `BasicConsumeAsync` wraps consumers · #32 pinned bindings judge `latest` · #33 `ci` is a marker scope · #34 the policy gates subject creation |
| **Product decisions still genuinely open** | #15 hard-delete semantics · #24 whether the audit trail records refusals |
| **Accounts and admin** | #1 repo rename (two clicks) · #3 reserve the unreserved names · #28 is informational |

**#33 is the one most likely to surprise.** No role grants `ci`, including Owner — so a human
cannot register into a `CI_ONLY` environment however senior, and neither can an unclaimed
instance. Both are intentional; both will read as bugs the first time they are hit.

**#24 got cheaper on 2026-08-14.** Recording refusals needs a write path that is deliberately
*not* transactional with the thing that did not happen — and `DeploymentEvent`, built for
decision 29, is already exactly that shape.

---

## If you want to run it yourself

```bash
# 1. build the image (CI publishes it, but GHCR is empty until the workflow runs)
docker build -f docker/api.Dockerfile -t concordat/api:local .

# 2. bring up PostgreSQL, RabbitMQ, the migrator and the registry
cd deploy/compose
CONCORDAT_IMAGE=concordat/api:local docker compose --profile registry up -d

# 3. the registry is on :5062, RabbitMQ management on :15672
curl localhost:5062/health/ready

# 4. the sample publishes a valid message and then an invalid one
dotnet run --project samples/Quickstart -c Release

# 5. the web app, on :4300 -- not in the compose stack, see above
cd ../../web && npm start -- --port 4300

# 6. the browser suite, against both of the above
npm run e2e            # or e2e:headed to watch it

# 7. stop it
cd ../deploy/compose && docker compose --profile registry down   # -v drops the database
```

The `concordat-postgres` volume persists between runs, so a second `up` starts with whatever the
first one wrote. The E2E suite claims the instance on first run and is idempotent afterwards; if
you have already claimed it by hand, point the suite at that account with `CONCORDAT_E2E_OWNER`
and `CONCORDAT_E2E_PASSWORD`. See [`web/e2e/README.md`](../web/e2e/README.md).
