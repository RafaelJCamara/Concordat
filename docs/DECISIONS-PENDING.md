# Decisions pending

Everything waiting on you, in one place. Ordered by when it starts to hurt.

Once a decision is made it moves to **[Settled](#settled)** at the bottom and, if it is
architectural, becomes an ADR in [`adr/`](adr/README.md).

---

## Blocking nothing today, but getting more expensive

### 1. Rename the GitHub repository `Signet` → `Concordat`

The project was renamed in ADR-022 but the repository was not. `Directory.Build.props`
hardcodes `RepositoryUrl` and `PackageProjectUrl` as `github.com/RafaelJCamara/Signet`, and
**those strings get baked into every NuGet package from M2 onward**.

GitHub redirects the old URL, so the change is low-risk. It needs you because it is your
account — or say the word and I'll do it via the API once `gh` is authenticated.

> **Cost of waiting:** after M2 publishes, the wrong URL is in released package metadata.

### 2. What to do with the `docs/design-and-plan` branch

It now carries the entire solution, so the name is inaccurate, and `main` is still the initial
commit. **CI has never run** — with the triggers you chose it fires on pull requests and on
`main`, not on feature-branch pushes.

**Recommendation:** merge to `main`. You are one person; the branch buys nothing and it is the
only thing standing between you and a green CI badge.

### 3. Reserve the names that are still unreserved

Availability is not reservation. From M0.1:

- Buy **`concordat.dev`** — Problem Details `type` URIs point at it, first used in M1.6
- Create the **`@concordat`** npm organisation
- NuGet, PyPI and Maven ids are only claimed on first publish (M2, M3, M6)

---

## Needed before a specific milestone

### 4. CLI alias — before M3

`concordat check --env staging` is a lot to type in CI. **Recommendation:** ship `concordat`
as the binary name and add `cdt` as an alias in M3.3. Deciding now costs nothing; deciding
after the GitHub Action and docs exist means changing both.

### 5. External coverage reporting — now live

M0.3 deferred this to "when M1.3 makes the number meaningful". **M1.3 is done**, so it is
decidable: wire up Codecov (needs an account and a token to rotate), or keep the current
coverage artifact and look at it manually.

**Recommendation:** keep the artifact for now. A coverage percentage on a solo project mostly
generates noise; the useful signal is whether the compatibility corpus grows, which a number
does not capture.

### 6. Windows in the CI matrix — at M2

M0.3 shipped Ubuntu-only deliberately, with the note to add `windows-latest` at M2 where
Testcontainers and the RabbitMQ integration tests first make platform differences real.
Confirm when we get there.

### 7. Milestone order: SDKs (M6) before governance (M7) and identity (M8)

I moved polyglot SDKs ahead of governance and identity on ADR-019 grounds — a second-language
client is the only real proof the protocol is language-neutral, and every milestone it waits
behind is a chance for a .NET assumption to set. **You never confirmed this.** Reversible until
M5 ends.

### 8. Semver pre-release support

M1.1 rejects `2.0.0-rc.1` with a dedicated code. A team whose pipeline emits pre-release labels
**cannot label a version at all** until this lands. Known gap, not a bug — but if your own
pipeline does that, it moves up.

### 9. JSON Schema keyword coverage — before v1 ships

M1.3 compares a practical subset. **Not compared:** `oneOf`/`anyOf`/`allOf`/`not`, `$ref`
targets, `patternProperties`, `dependentRequired`, `if`/`then`/`else`, tuple-form `prefixItems`.
A change confined to those is currently reported as **compatible**.

That is a real hole for anyone using composition keywords, which is common in mature schemas.
Decide whether v1 ships with it, closes it, or rejects schemas using unsupported keywords
outright rather than silently under-reporting.

### 10. Hard-delete semantics — before v1 ships

M1.5 implements soft delete (`Subject.Retire()`) and never deletes schemas, which is what
ADR-015 requires. **Hard delete is not implemented**: DESIGN §4 wants "no registered
consumers, an explicit force flag, and an audit entry", and both registered consumers and the
audit log are M7. Confirm that v1 can ship with retire-only, or pull the pieces forward.

---

## Commitments that must not be forgotten

Not decisions — obligations already incurred that land in a later milestone. Each is written
into the milestone that owes it, and collected here because they are the ones that get lost.

| Owed by | Commitment | Why it matters |
|---|---|---|
| ~~M1.6~~ ✅ | ~~Authorise `GET /schemas/{id}` by reachability~~ | **Discharged.** `GetSchemaHandler`, four integration tests including refusal-equals-absence |
| ~~M1.6~~ ✅ | ~~One constructor for `CompatibilityVerdict`, proven by a recording fake~~ | **Discharged.** `ICompatibilityEvaluator` + `Evaluate_AlwaysConsultsTheChecker` |
| **M1.6** | Bundling, deferred from M1.4 | Cannot live at registration: it would make canonicalisation depend on registry state and stop any SDK reproducing an id offline |
| **M1.6** | Subject prefix search needs a `ComplexProperty` mapping or a shadow column | Value converters do not translate `StartsWith` |
| ~~M1.7~~ ✅ | ~~Pin the schema-id preimage bytes, not just ids~~ | **Discharged.** 4 fixtures pin the exact framing; all matched hand-written expectations first run |
| **M2** | Run the payload-validation fixtures against a real validator | Written in M1.7 but unexecutable — Concordat has no validator of its own |
| **M2.5** | Verify the AMQP 1.0 header conversion | ADR-013's "designed to survive 1.0" is an assertion until a 1.0 client reads a Concordat message |
| **M7** | Hard delete: no registered consumers + force flag + audit entry | Soft delete is all that exists today |
| **M7** | Adopt the derived environment ids, or migrate `subject.environment_id` | `DerivedEnvironmentResolver` hashes the name to a stable id so `/environments/{env}/…` works before environments exist. Real rows will generate their own |
| **M7** | `GET\|PUT …/registration-policy` | Per-environment, and the `Environment` aggregate does not exist yet, so there is nowhere to store it |
| **After M1.5** | Any change to canonicalisation now needs a **preimage version bump and a migration** | Schemas are persisted from here on. The golden id test exists to make such a change impossible to miss |
| **Maintenance** | Drop the `SSH.NET` pin when Testcontainers requires a patched version itself | A stale forward-pin eventually holds a dependency *back* |

---

## Taken on your behalf — object if any is wrong

Reversible, recorded where they were made, listed here so none of them is a surprise later.

| Decision | Where | Reverse cost |
|---|---|---|
| `VersionStatus` gained `Rejected` | [M1.1](plan/M1-registry-core.md), DESIGN §4 | Low, pre-persistence |
| `Result<T>` lives in Domain, not Application | M1.1, DESIGN §8 | Low |
| `Subject.CompatibilityPolicy` nullable = inherit | M1.1 | **Cannot be retrofitted** |
| `SubjectId` and `EnvironmentId` added ahead of M7 | M1.1 | Low now, high after M1.5 |
| Registration idempotent at the tip | M1.1 | Low |
| `Deprecated` accepts versions, `Retired` does not | M1.1 | Low |
| Approval never regresses `LatestPointer` | M1.1 | Low |
| Number literals preserved verbatim, not RFC 8785 | [M1.2](plan/M1-registry-core.md) | **Changes every id** |
| Duplicate JSON keys rejected outright | M1.2 | Low |
| Schema body ceiling of 512 KiB | M1.2 | Low |
| `ContentModel` defaults to `Open` per subject | M1.3 | Low |
| `pattern` changes always read as narrowing | M1.3 | Low |
| Bundling deferred to M1.6, not stored | [M1.4](plan/M1-registry-core.md) | Low |
| No per-file licence headers | [M0.2](plan/M0-foundations.md) | Low |
| Migration runs as a separate process, **not** auto-migrate on startup | [M1.5](plan/M1-registry-core.md) | Low |
| snake_case column names, set explicitly per property | M1.5 | **Renames every column** |
| `SemanticVersion` stored as `MAJOR.MINOR.PATCH` text, not three columns | M1.5 | Low, needs a migration |
| Enums stored as text via `WireTokens`, never `Enum.ToString()` | M1.5 | Low |
| `SSH.NET` pinned forward past a security advisory | M1.5 | Low |
| PostgreSQL image pinned to `17-alpine` in tests | M1.5 | Low |
| Rejected versions are excluded from the compatibility history | [M1.6](plan/M1-registry-core.md) | Low |
| A subject must exist before a version can be registered — no implicit creation | M1.6 | Low, but it is user-visible REST behaviour |
| Schema refusal returns `schema_not_found`, never a distinct "forbidden" | M1.6 | Low |
| Re-registering the tip returns **200**, not 201, since nothing was created | M1.6 | Low, but user-visible |
| A breaking change returns **201**, not 409 — it is a reviewable artifact | M1.6 | Low, but user-visible |
| An unmapped `concordatCode` falls through to HTTP 400 | M1.6 | Low |
| `/health/live` and `/health/ready` are separate, and liveness ignores the database | M1.6 | Low |
| Environment ids are derived by hashing the name until M7 exists | M1.6 | **Needs an M7 migration** |
| With no prior semver label, a breaking change suggests `1.0.0` — an unlabelled history reads as pre-1.0 | M1.6 | Low |

---

## Settled

| Decision | Outcome | Recorded in |
|---|---|---|
| Project name | **Concordat**, after rejecting Signet, Hutch, Syngraph, Stipula, Warrenty, Indenture | [ADR-022](adr/022-project-name-concordat.md) |
| Default compatibility policy | **`Backward × WireJson`** | [ADR-016](adr/016-two-axis-compatibility.md), DESIGN §7 |
| Schema table scope | **Global**, keyed by `SchemaId` alone | [ADR-015](adr/015-content-addressed-ids.md) |
| Tier 2 SDK set | TypeScript/JavaScript, Python, Go, Java | [ADR-021](adr/021-tier-2-sdk-set.md) |
| Java included | Yes, last in the M6 sequence | ADR-021 |
| CI OS matrix | Ubuntu only for now | [M0.3](plan/M0-foundations.md) |
| CI triggers | Pull request, push to `main`, manual dispatch | M0.3 |
| GitHub issue seeding | Parked; `scripts/seed-github.ps1` is ready and idempotent | — |
