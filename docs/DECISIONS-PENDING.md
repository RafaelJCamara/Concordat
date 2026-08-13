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

### 4. External coverage reporting — now live

M0.3 deferred this to "when M1.3 makes the number meaningful". **M1.3 is done**, so it is
decidable: wire up Codecov (needs an account and a token to rotate), or keep the current
coverage artifact and look at it manually.

**Recommendation:** keep the artifact for now. A coverage percentage on a solo project mostly
generates noise; the useful signal is whether the compatibility corpus grows, which a number
does not capture.

### 5. Windows in the CI matrix — now decidable, and now cheaper to skip

M0.3 shipped Ubuntu-only with a note to reconsider at M2, where Testcontainers and the RabbitMQ
tests first make platform differences real. **M2.5 is that point**, and it changed the sum:
the suite raises up to three Linux broker containers, which on `windows-latest` needs
Linux-container support that GitHub's Windows runners do not provide.

**Recommendation:** stay Ubuntu-only. Add Windows later as a *build-and-unit-test-only* job if
you want the platform signal — the container suites cannot follow it there regardless.

### 6. When should the header-survival suite run?

It raises three brokers (two of them for federation alone) and takes ~15 s locally, more on a
cold runner pulling `rabbitmq:4.1-management`. It also almost never changes: it re-measures
broker behaviour, not our code.

**Recommendation:** run it on pull requests anyway, at least until v1. It is the only thing
standing between DESIGN §2 and quiet fiction, and a broker upgrade landing unnoticed is exactly
the scenario it exists to catch. Revisit if CI time becomes a real cost.

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

### 10. Generic message types are unsupported — before v1 ships

M2.3 **refuses** a generic type name rather than inventing a spelling for it, because any
spelling becomes a rule five SDKs must reproduce character for character, and Go and Python
have no CLR generic syntax to reproduce it from.

That is right for the protocol and a hard stop for anyone whose publishers send
`Envelope<OrderCreated>`. Raw RabbitMQ.Client publishers rarely do, which is why this is a
gap rather than a blocker under ADR-020 — but if **your** code does, it moves up.

> **Options:** ship refusing them; require an explicit subject for generic types; or define a
> normative spelling in the corpus and make every SDK implement it.

### 11. Nested and top-level types collide — confirm the trade

`+` → `.` (DESIGN §3) makes `Acme.Orders+OrderCreated` and a top-level
`Acme.Orders.OrderCreated` **the same subject**, silently. The alternative was refusing nested
types outright.

This follows the design as written, and the consequence may not have been visible when it was
written. **Recommendation:** keep it. The collision needs two types whose names collide *after*
the rewrite, which is rare and immediately visible in the registry's subject list; refusing all
nested types would hurt far more publishers.

### 12. `diff` is blind to added properties under an open content model

Found while building M3.1. The compatibility engine records a divergence only where one could
affect compatibility. Under an **open** content model — the default — adding or removing a
property cannot, so it produces no finding. `concordat diff v1 v2` therefore shows two
different schema ids and an empty list for the single most common schema change there is.

`check` is unaffected and correct: the change genuinely is compatible. It is `diff` that
disappoints, because a human reading it wants to know what changed, not only what broke.

> **Options:** leave it and rely on `git diff` of the schema files (what the CLI now tells you
> to do); or have the engine record informational divergences with no surface, which changes
> the meaning of `allDivergences[]` and touches the M1.3 corpus.
>
> **Recommendation:** leave it for v1. The CLI says so explicitly, and widening
> `allDivergences[]` risks the corpus for a reporting nicety.

### 13. The quarantine exchange is declared by the application — confirm

`ConcordatRabbitMqOptions.DeclareQuarantineExchange` defaults to **on**, so the middleware
declares `concordat.quarantine` itself the first time it needs it. The alternative is that the
first quarantine in production fails on a missing exchange — the worst possible moment to
discover a topology gap.

That assumes applications hold `exchange.declare` rights. In estates where topology is owned by
infrastructure-as-code and applications deliberately cannot declare, this must be turned off
and the exchange provisioned ahead of time.

> **Which is your estate?** It changes the recommended default in the deployment docs, not the
> code.

### 14. Hard-delete semantics — before v1 ships

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
| ~~M2~~ ✅ | ~~Run the payload-validation fixtures against a real validator~~ | **Discharged.** NJsonSchema behind `IPayloadValidator`; the corpus caught a real draft-conformance gap on its first run |
| **M7** | Contract-resolution caching, deferred from M2.1 | The 60 s TTL was specified before contracts existed; there is no endpoint to cache until M7 ships one |
| ~~M2.5~~ ✅ | ~~Verify the AMQP 1.0 header conversion~~ | **Discharged.** Measured on `rabbitmq:4.1-management`: `concordat-*` arrive as application-properties, and an `x-`-prefixed control header on the same message is demoted to an annotation, so the prefix rule is load-bearing rather than precautionary |
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
| A 4xx sets `LastFailure` but **not** `IsDegraded`; only 5xx/408/429 count as unreachable | [M2.1](plan/M2-dotnet-client.md) | Low, but it changes what an alert fires on |
| `ResolutionFailures` counts every unenforced *operation*, not distinct causes | M2.1 | Low, but it changes the shape of the metric |
| Fail-closed throws `ConcordatException`; quarantine behaviour is M2.4's, not the client's | M2.1 | Low |
| `SchemaUnresolvable` added to `ConcordatCodes` — a client-raised code in a domain catalogue, because ADR-019 makes the strings normative for every SDK | M2.1 | Low |
| `ConcordatClient` does not dispose the `HttpClient` it is handed | M2.1 | Low |
| `ISubjectResolver` is transport-neutral and lives in the Domain; no `System.Type` on the context | [M2.3](plan/M2-dotnet-client.md) | Low, and it is what keeps .NET's type system out of subject names |
| The separator list is **closed** — `+` and `:` normalise, `/` does not | M2.3 | Low now, **breaks existing subjects** once anyone publishes |
| Subject case is preserved, never folded | M2.3 | **Would merge existing subjects** |
| Hyphens are refused rather than rewritten to underscores | M2.3 | Low |
| The publish side trims surrounding whitespace; envelope reading still does not | M2.3 | Low |
| M2.5 ships a **code** deliverable — findings as assertions — against a brief that said there would be none | [M2.5](plan/M2-dotnet-client.md) | Low. A written report is true on the day it is written and silently becomes fiction at the next broker upgrade |
| The broker image is pinned to `rabbitmq:4.1-management`; findings are stated as true *of that version* | M2.5 | Low, but the pin must be bumped deliberately, not floated |
| Four test-only dependencies added (RabbitMQ.Client, Testcontainers, AMQPNetLite, MQTTnet) | M2.5 | Low; all licence-checked, none reaches a shipped package |
| `ConcordatChannel` is a **full 63-member `IChannel` decorator**, not an extension method | [M2.4](plan/M2-dotnet-client.md) | Low, but the whole point: an opt-in publish method leaves every other `BasicPublishAsync` unenforced |
| `EnforcementMode.Monitor` is the **default**, not `Enforce` | M2.4 | Low, but user-visible. Defaulting to Enforce would let a package reference start rejecting production traffic |
| The envelope is stamped even when the payload is invalid, provided identity resolved | M2.4 | Low. It is what lets consumers read schema ids before publishers are clean |
| When quarantine itself fails, the message is **delivered** to the application, not dropped or requeued | M2.4 | Low, but it means an application can receive a message Concordat knows is invalid |
| An unexpected middleware exception fails **open** | M2.4 | Low |
| Quarantine keeps the original routing key, so operators can bind selectively | M2.4 | Low |
| A format with no registered validator is treated as valid, not invalid | M2.4 | Low, until Avro/Protobuf validators land |
| Quarantine detail is truncated at 4 KiB | M2.4 | Low |
| `./contracts` layout is `<subject>.<ext>` — no manifest, no front-matter | [M3.1](plan/M3-cli.md) | **User-visible convention.** Changing it breaks everyone's repo layout |
| Parse errors are intercepted and mapped to exit **2**, overriding `System.CommandLine`'s default of 1 | M3.1 | Low, and it is what keeps a typo from reading as a contract violation |
| `push` exits **0** on a breaking change; only `check` gates | M3.1 | Low, but it defines how pipelines are wired |
| An empty contracts directory is exit 4, not a vacuous pass | M3.1 | Low |
| `promote` moves one subject, never a whole environment | M3.1 | Low. Bulk promotion reads as atomic and is not |
| `impact` deferred to M7, where registered consumers first exist | M3.1 | Low |
| The CLI talks to the registry directly rather than through the caching `Concordat.Client` | M3.1 | Low, and required: a gate must never answer from cache |
| `Concordat.Cli.Tests` references `Concordat.Api.IntegrationTests` to reuse its harness | M3.1 | Low; extract a shared test-support project if a third consumer appears |
| Queue mode **refuses to run** without `--i-understand-this-reorders-the-queue`, rather than printing a warning | [M3.2](plan/M3-cli.md) | Low, and user-visible. ADR-014 asked for documentation; nobody reads it before running a command |
| A single repeated value is **not** inferred as an enum — needs ≥2 distinct, ≥10 observations, ≥3× repetition | M3.2 | Low |
| Whole numbers narrow to `integer` rather than staying `number` | M3.2 | Low, and reported on every occurrence |
| `format` is inferred (uuid, date-time, date, email); `additionalProperties` never is | M3.2 | Low |
| Inference is **not** corpus-pinned, unlike canonicalisation and compatibility | M3.2 | Low. It is a drafting aid a human edits, not protocol |
| `RabbitMQ.Client` is now a CLI dependency, for queue mode | M3.2 | ~~Possible NativeAOT problem~~ — **resolved in M3.3: it produced no AOT warnings at all.** The warnings were all from our own JSON usage |
| Every `--json` shape has a concrete type and source-generated serialisation | [M3.3](plan/M3-cli.md) | Low, and forced by AOT. Reflection-based output would silently emit `{}` from a trimmed binary |
| The container base is `runtime-deps` (native prerequisites, no .NET runtime) | M3.3 | Low |
| One release runner per architecture instead of cross-compiling | M3.3 | Low; four jobs, but a mis-linked AOT binary builds cleanly and refuses to start |
| The GitHub Action passes config as environment variables, not `args` | M3.3 | Low, and required: a fixed `args` list turns an empty optional input into a parse error |
| The Action wraps `check`/`lint`/`push` only; other commands run the container directly | M3.3 | Low |

---

## Settled

| Decision | Outcome | Recorded in |
|---|---|---|
| Project name | **Concordat**, after rejecting Signet, Hutch, Syngraph, Stipula, Warrenty, Indenture | [ADR-022](adr/022-project-name-concordat.md) |
| CLI binary name | **`concordat` only**, with a shell alias documented for anyone who wants one. No `cdt` | [M3](plan/M3-cli.md) — an alias is *additive*: shipping it later breaks nobody, removing it later breaks every script. That asymmetry says wait. `kubectl`, `terraform`, `docker` and `git` all ship one name and let users abbreviate. If it is ever added, `cdt` needs the same ecosystem-collision check that killed Signet and Hutch |
| JSON Schema validator | **NJsonSchema (MIT)** behind an `IPayloadValidator` port | [M2](plan/M2-dotnet-client.md) — `JsonSchema.Net` publishes its binary under a maintenance-fee agreement that would propagate to our users; `Newtonsoft.Json.Schema` is commercial |
| Default compatibility policy | **`Backward × WireJson`** | [ADR-016](adr/016-two-axis-compatibility.md), DESIGN §7 |
| Schema table scope | **Global**, keyed by `SchemaId` alone | [ADR-015](adr/015-content-addressed-ids.md) |
| Tier 2 SDK set | TypeScript/JavaScript, Python, Go, Java | [ADR-021](adr/021-tier-2-sdk-set.md) |
| Java included | Yes, last in the M6 sequence | ADR-021 |
| CI OS matrix | Ubuntu only for now | [M0.3](plan/M0-foundations.md) |
| CI triggers | Pull request, push to `main`, manual dispatch | M0.3 |
| GitHub issue seeding | Parked; `scripts/seed-github.ps1` is ready and idempotent | — |
