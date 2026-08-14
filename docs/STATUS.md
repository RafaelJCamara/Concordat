# What is missing

**As of 2026-08-14.** A companion to [PLAN.md](PLAN.md), which records what was built, and
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
| Contract + publish binding, then `POST /contracts/resolve` | governed route returns the contract in `ENFORCE`; `order.line.added` returns `{contract: null, enforcement: "OFF"}` — `*` is one word |
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
| M3 CLI | 18 / 19 |
| M4 web app | 11 / 27 |
| M5 formats | 14 / 15 |
| M6 SDKs | 5 / 24 |
| M7 governance | 29 / 31 |
| M8 identity | 12 / 15 |
| M9 cloud | 11 / 16 |
| **Total** | **214 / 276** |

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

Real remaining work is therefore closer to **51 items**, and about 31 of those are M4 and M6.

---

## Missing product surface

### M4 — the web application

The honest count is **12**, not the 16 unchecked boxes suggest. Every API behind these exists,
so this is Angular work rather than design work.

**Genuinely missing:** Dashboard · SubjectDetail / VersionDetail / NewVersion pages ·
ContractsPage · CompatibilityDiffPage · ImpactAnalysisPage · ApprovalsPage · AuditLogPage ·
the settings split (environment, brokers, API keys, members) · notification forms that persist ·
Monaco for schema editing · `ajv` for client-side validation · the two Playwright E2E passes ·
the "preserve" list of prototype behaviours (immutable-id confirmation, semver auto-increment,
clone-previous-version, empty states).

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
| **Mode B binary framing (`0x01 \| 16-byte id \| payload`) is specified and not implemented** | The envelope spec describes a wire format nothing writes or reads. Recorded as decision #19. The content-type form of Mode B does work. |
| **`ENFORCEMENT_VIOLATION` is a notification event nothing emits** | The violation happens in the SDK, on the publisher's machine, and there is no endpoint for a client to report one. A subscriber can subscribe to silence. Decision #25. |
| **An environment with no row has no registration policy** | Routes accept an environment name before the aggregate exists (`DerivedEnvironmentResolver` hashes it). Registration into a never-created environment is unpoliced. This is what keeps the quickstart working; it is also a door. |
| **Approval reviewers do not exist** | Anyone with `subject:admin` can approve anything. A reviewer *set* was deferred to M8 and M8 did not build it. |
| **Hard delete does not exist** | Soft delete is all there is. The full rule — no registered consumers, force flag, audit entry — is an outstanding commitment. |
| **Subject prefix search is not implemented** | Value converters do not translate `StartsWith`; it needs a `ComplexProperty` mapping or a shadow column. |
| **Two contracts can govern one route, first-by-name wins** | Decision #21, and M7.4's impact analysis inherits the ambiguity. |
| **A page reload signs you out** | Sessions are API keys in memory. The fix is an httpOnly cookie. Decision #26. |
| **`AllowAnonymousUntilClaimed` is on by default** | A fresh deployment answers every request as an owner until an account exists. Deliberate, documented in the Azure README, and still the thing most likely to surprise. Decision #27. |
| **No browser E2E over sign-in + guards** | Unit tests cover each half; nothing drives the two together. |
| **`Tenant` is not an aggregate** | There is exactly one, `TenantId.SelfHosted`. Cloud multi-tenancy is tested but single-rowed. |
| **The derived-environment-id decision is unmade** | Either adopt the hashed ids or migrate `subject.environment_id`. Outstanding commitment; the longer real rows accumulate, the more it costs. |

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

## Decisions

**30 open**, 16 settled. Three whose cost is actively rising:

- **#17 — Avro's canonical form.** Free to overturn until the first Avro schema is stored, then
  it is a preimage bump and a migration.
- **#33 — `ci` is a marker scope and no role grants it.** New today. A human cannot register
  into a `CI_ONLY` environment however senior, and neither can an unclaimed instance. Both are
  intentional and both will read as bugs the first time they are hit.
- **#26 — a reload signs you out.** Every page added to M4 makes the session model more
  expensive to change.

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

# 5. stop it
docker compose --profile registry down          # add -v to drop the database volume
```

The `concordat-postgres` volume persists between runs, so a second `up` starts with whatever the
first one wrote.
