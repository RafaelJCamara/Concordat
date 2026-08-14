# M1 — Registry core, JSON Schema only

**Depends on:** [M0](M0-foundations.md) · **Unlocks:** M2, M3, M4, M5, M7 · **Design refs:** [§4](../DESIGN.md#4-domain-model), [§5](../DESIGN.md#5-api-surface-and-cross-language-strategy), [§7](../DESIGN.md#7-contract-checks--cli-and-build-time), decisions 004, 007, 015, 016, 017, 019

The largest milestone by far. If it stalls, split at the M1.5/M1.6 boundary — domain plus
engine is independently reviewable without the API on top.

---

## M1.1 Domain model

**Done 2026-08-13 · DESIGN §4**

- [x] `SchemaId` — 32 lowercase hex, validated; `FromTrusted` escape hatch for M1.2/M1.5
- [x] `Schema` — immutable; references name-unique and canonically ordered so M1.2's hash is stable
- [x] `Subject` aggregate root, owning every invariant below
- [x] `SchemaVersion` entity — ordinal, optional semver, status, decision audit
- [x] `LatestPointer` — explicit and gated, **not** "highest ordinal" (ADR-017)
- [x] `CompatibilityPolicy` — the two-axis pair, with the `Wire ⊂ WireJson ⊂ Source` lattice
- [x] `RegistrationPolicy` — `Open | CiOnly | Closed`
- [x] Invariants: one format per subject; ordinals contiguous and monotonic from 1; approval gate; semver verification; soft delete
- [x] 78 unit tests — build 0 warnings, `dotnet format` clean

### Decisions taken during implementation

- **`VersionStatus` gained `Rejected`.** Rejection had nowhere to record its outcome, and a
  declined proposal left as `AwaitingApproval` is ADR-017's graveyard. DESIGN §4 amended.
- **`Result<T>` lives in `Concordat.Domain.Results`**, not Application — Domain must return it
  and cannot reference upward. DESIGN §8 amended rather than deviating silently.
- **`Subject.CompatibilityPolicy` is nullable**, meaning "inherit the environment default".
  This one cannot be retrofitted: copying the default at creation would permanently destroy
  the distinction between *configured* and *inheriting*, with no data to reconstruct it from.
- **`SubjectId` and `EnvironmentId` added** though environments are M7, for the same reason
  M1.5 wires `ITenantContext` early — adding a required FK to a populated table later is worse.
- **Registration is idempotent at the tip.** Re-registering the current schema returns the
  existing version and allocates no ordinal; re-registering an *older* schema is a revert and
  does. A retried publish must not inflate the history.
- **A `Deprecated` subject still accepts versions; a `Retired` one does not.** Deprecated is
  advisory — existing producers still need to patch their contract. Retired is the soft delete
  and must be a wall or it means nothing.
- **Approval never regresses `LatestPointer`.** If a compatible v3 registered while breaking v2
  sat pending, approving v2 marks it active but leaves latest at 3.
- **Pre-release and build metadata are rejected** with a dedicated code. A pipeline emitting
  `2.0.0-rc.1` cannot label a version until this lands — a known gap, not a bug.

### Verification-caught defects

Two real bugs, both found by tests rather than review:

- `IReadOnlyList<T>` backed by `List<T>` **can be cast to `ICollection<T>` and mutated**,
  bypassing every ordinal invariant. Both `Subject.Versions` and `Schema.References` now wrap.
- Scanning for `-` anywhere to detect a pre-release misreported `-1.0.0` as
  "pre-release unsupported" when it is simply malformed, sending the user to the wrong place.

### Known hole, closable only in M1.6

The aggregate **trusts `CompatibilityVerdict`**. It narrows the risk — the verdict carries the
policy it was evaluated under, and a subject with an explicit override rejects a mismatched
one — but a handler passing `Compatible` for a genuinely breaking change still moves the
pointer. M1.6 must guarantee exactly one handler constructs a verdict and that it sources it
from the engine, with a recording-fake test asserting the handler cannot complete without
invoking the checker.

### Product decisions settled 2026-08-13

- **Default policy for a new environment is `Backward × WireJson`**, shipped as
  `CompatibilityPolicy.Default`. Recorded in [ADR-016](../adr/016-two-axis-compatibility.md)
  and DESIGN §7.
- **The `Schema` table is global**, keyed by `SchemaId` alone, not tenant-scoped. Recorded in
  [ADR-015](../adr/015-content-addressed-ids.md). Carries an authorisation obligation into
  M1.6 — see below.

## M1.2 Canonicalisation and identity

**🔴 Heavy · Done 2026-08-13 · ADR-015**

- [x] JSON Schema canonical form — key-sorted, whitespace-normalised, escaping normalised
- [x] SHA-256 of the preimage, truncated to 128 bits, lowercase hex
- [x] **Hash covers format + canonical body + references**, not body alone
- [x] Size ceiling — 512 KiB of UTF-8, enforced in `Schema.Create` → `schema_too_large`
- [x] Golden tests: whitespace and key-order variants collapse to one id; differing reference sets produce **different** ids
- [x] Unique constraint on schema id → **M1.5**, it is a database constraint

36 tests in `Concordat.Formats.Json.Tests`; 118 across the solution.

### The preimage is itself normative

The hash input is not the body. It is a length-prefixed, version-tagged framing:

```
concordat-schema-id/v1
format:json
body:<utf8-bytes>:<canonical body>
refs:<count>
ref-name:<utf8-bytes>:<name>
ref-subject:<utf8-bytes>:<subject>
ref-version:<n>
```

Two properties this buys, both tested. **Length prefixes remove ambiguity**: without them a
reference named `a:b` and a pair named `a` and `b` could serialise to identical bytes.
**The version tag makes the derivation evolvable** — changing it invalidates every stored id
in every installation, so it needs a bump plus a migration, not a quiet edit. Azure's
unversioned scheme is the cautionary example (ADR-010).

`SchemaIdComputer.BuildPreimage` is public so the M1.7 corpus can pin the bytes directly, and
one test pins a golden id so any accidental change to the derivation fails loudly.

### Two deliberate deviations

- **Number literals are preserved verbatim**, so `1.0` and `1` yield different ids. RFC 8785
  routes numbers through an ECMAScript double, which loses precision on large integers and
  would silently corrupt a `maximum` or `multipleOf`. A missed deduplication is the safer
  failure, and raw preservation is markedly easier to reimplement identically in Python, Go
  and Java — which ADR-019 requires.
- **Duplicate object keys are rejected** rather than resolved. Parsers disagree on which value
  wins, so such a document has no single meaning and must not get an id at all.

### Deferred to M1.4: `$id` and `$ref` normalisation

ADR-015 lists "`$id` resolved" as part of the canonical form; it is **not implemented**.
Normalising `$id` without also resolving the `$ref` values that point at it is incoherent —
it changes the base a reference resolves against while leaving the reference alone. It
belongs with M1.4's reference work.

Consequence until then: two documents differing only in the spelling of an equivalent `$id`
URI get different ids. A missed deduplication, not a correctness bug.

## M1.3 Compatibility engine

**🔴🔴 Heaviest · Done 2026-08-13 · ADR-016, DESIGN §7**

- [x] Axis 1 — all seven modes, transitive and non-transitive
- [x] Axis 2 — the `Wire ⊂ WireJson ⊂ Source` lattice
- [x] JSON Schema rules, designed from scratch — every acceptance criterion:
  - [x] **Adding an optional property is fully compatible**, in all three directions
  - [x] **Removing an optional property is fully compatible**
  - [x] Content model is explicit subject config, never inferred per-schema
  - [x] Narrowing `type`/`enum`/`maximum`, adding to `required`, `additionalProperties: true → false` are backward-breaking
  - [x] Widening is forward-breaking
- [x] Every finding carries an exact JSON-Pointer path (RFC 6901 escaped), `kind`, direction, surface, actionable message, `conflictsWithVersion`
- [x] `suggestedSemver` derivation
- [x] Golden corpus — 32 compatibility tests; 150 across the solution
- [x] Semver label verification — already enforced by the aggregate in M1.1

### What the second axis buys, concretely

`integer → number` is JSON Schema's `int32 → int64`: every existing document still validates,
but generated code changes from an integral to a floating-point type. It passes
`Backward × WireJson` and fails `Backward × Source`. A single-axis registry cannot express
that at all, and this is the case ADR-016 exists for.

The same shape covers `format` changes, which are annotation-only in JSON Schema — validation
is untouched, but a generator maps `date-time` to a date type.

**A `× Wire` policy is effectively no checking for JSON Schema.** JSON is self-describing, so
no divergence breaks byte decoding. That is asserted as a test, because it is the justification
for `Backward × WireJson` being the default rather than `× Wire`.

`AllDivergences` reports everything found; `BreakingChanges` reports only what the policy
actually violates. The difference is what lets the API explain *why* a change was allowed
instead of silently permitting it.

### Documentation defect this surfaced

DESIGN §5's example error showed `required_field_removed` under a BACKWARD policy with the
message "consumers on v1 will fail". That contradicts §7's own definition — data written under
the old schema always carries a field the old schema required, so a new reader copes; it is
readers *on v1* that break, which is **forward**. The example now shows genuine engine output
and the correction is noted inline.

### Deliberate limits, so the gaps are known rather than assumed

- **Comparison covers a practical subset**: `type`, `enum`, `required`, `properties`, `items`,
  numeric and string bounds, `pattern`, `additionalProperties`, `format`. Not compared:
  `oneOf`/`anyOf`/`allOf`/`not`, `$ref` targets, `patternProperties`, `dependentRequired`,
  conditional `if`/`then`/`else`, tuple-form `prefixItems`. A change confined to those is
  currently reported as compatible.
- **`pattern` changes are always treated as narrowing.** Two regexes cannot be proven
  equivalent in general, so over-reporting is the safe direction.
- **Property renames read as a remove plus an add**, not a rename. Under an open content model
  that is compatible, which is correct by the validation rules but may surprise.

## M1.4 Schema references

**Done 2026-08-13**

- [x] `ConcordatRef` — parses and renders `concordat://<env>/<subject>/<version>`
- [x] **`$id` and `$ref` normalisation**, deferred from M1.2 and landed here with the rest of
      reference handling
- [x] Edges are **derived from the document**, not supplied alongside it
- [x] Cycle detection over the version-level graph
- [x] Referrer queries — direct and transitive, for re-checking on a referenced subject's change
- [x] 41 reference tests; 191 across the solution
- [x] Bundled canonical form → **delivered in M1.6**, see below

### Edges are derived, never supplied

`ISchemaReferenceExtractor` reads `$ref` values out of the document. A client-supplied edge
list can disagree with what the schema actually points at, and the disagreement surfaces much
later as a resolution failure or a missed transitive check.

Local (`#/$defs/Address`) and HTTP refs are ignored — the validator resolves those, not the
registry. A **malformed `concordat://` ref is reported rather than skipped**: a typo in our own
scheme would otherwise register a schema with no edges that fails to resolve later.

### The graph is keyed by version, not by subject

`A@2 → B@1 → A@1` is a perfectly good DAG: A@1 existed before B@1 referenced it, and A@2 came
afterwards. A subject-level graph would reject it. Only genuine version-level cycles fail, plus
self-reference, which registration order does not prevent.

### Bundling is deferred to M1.6, and this is a real decision

The plan said registration should resolve references "into a bundled canonical form". It should
not, for one decisive reason: **bundling would make canonicalisation depend on registry state.**
The same authored document would canonicalise differently depending on which schemas happened to
be registered, so canonicalisation would stop being a pure function of the document — and every
SDK would need registry access to reproduce an id, which the M1.7 conformance corpus and
ADR-019 both rule out.

It also duplicates work: ADR-015 hashes body **plus references**, which presumes the body is not
bundled, or the references would already be baked into it.

Bundling is a *serving* concern — a client wants one self-contained document — so it belongs
with `/bootstrap` and `GET /schemas/{id}` in M1.6. The stored body stays as authored and
canonicalised; the edges stay authoritative.

### Note for after M1.5

Adding `$id`/`$ref` normalisation changed the canonical form, and therefore the ids of documents
using those keywords. That is free today because nothing is persisted. Once M1.5 stores schemas,
**any change to canonicalisation requires a preimage version bump and a migration** — the golden
id test exists to make such a change impossible to miss.

## M1.5 Persistence

**Done 2026-08-13 · ADR-007**

- [x] EF Core 10 + Npgsql (ADR-007), four tables: `schema`, `schema_reference`, `subject`, `schema_version`
- [x] `InitialRegistry` migration; `Concordat.Migrator` host
- [x] Content-addressed id **is** the primary key — that is the unique constraint backing M1.2 idempotency
- [x] `ITenantContext` + global query filter, with write-side tenant stamping
- [x] **`Schema` global, excluded from the filter** (ADR-015)
- [x] `xmin` optimistic concurrency, check constraints, unique index per `(tenant, environment, name)`
- [x] 12 Testcontainers integration tests against real PostgreSQL 17; 203 across the solution
- [ ] Hard-delete semantics → **M7**, see below

### Two defects the migration caught before any data existed

- **`ordinal` was generated as a PostgreSQL identity column.** EF treats an integer key as
  database-generated by default, so the database would have allocated the ordinal — and the
  contiguous-from-1 invariant, the approval gate and the latest pointer all depend on the
  *domain* owning that number. Fixed with `ValueGeneratedNever`, and there is now a test that
  registers three versions and asserts `[1, 2, 3]`.
- **Testcontainers 4.13.0 pulls in SSH.NET 2025.1.0**, which carries a high-severity advisory
  (GHSA-q939-rpr3-3284). NuGet audit turned it into a build error. Pinned forward to 2026.0.0
  through central transitive pinning rather than suppressed — SSH.NET is only used by the
  docker-over-SSH transport we never touch, but a suppressed advisory stays suppressed long
  after it stops being harmless.

### Deviation: no auto-migrate on startup

The plan said "auto-migrate on startup, toggleable". `Concordat.Migrator` is a **separate
process** instead. Under a rolling deployment every replica would race to migrate; running it
once as a deployment step is the difference between a predictable schema change and an
intermittent one. `docker compose` runs it before the API in M1.8.

### Sharp edges recorded rather than discovered later

- **`xmin` only changes when the `subject` row itself is updated.** Inserting a
  `schema_version` child alone does not bump it. `RegisterVersion` happens to dirty the root
  because it moves `LatestPointer` — but a future mutator touching only children would slip
  past the concurrency guard.
- **Value converters do not translate `StartsWith`.** `SubjectName` maps through a converter,
  so subject prefix search in M1.6 needs either a `ComplexProperty` mapping or a shadow string
  column. Not a problem today; a surprise if discovered while writing the endpoint.
- **Domain constructors exist purely for EF.** `Schema` and `Subject` each carry a private
  parameterless constructor because EF cannot bind an owned collection through one. This is
  the single place persistence reaches into the domain.

### Deferred to M7

Hard delete requires "no registered consumers, an explicit force flag, and an audit entry".
Registered consumers and the audit log are both M7. Soft delete works today —
`Subject.Retire()` — and schemas are never deleted at all, which is the part ADR-015 requires.

## M1.6 REST API

**DESIGN §5 · done 2026-08-13, except three items that belong elsewhere**

Split into two passes because it is the largest package in M1: the application layer, then
the HTTP surface. Both are complete.

### Pass A, done

- [x] Hand-rolled CQRS dispatcher (`ICommand`/`IQuery` + handlers), **not MediatR** (ADR-009)
- [x] Handlers: create subject, get subject, list subjects, register version, check
      compatibility (dry run, never writes), approve/reject, get schema, get schema usages
- [x] Repository ports in Application, EF implementations in Infrastructure
- [x] `suggestedSemver` derived from the tip label and the engine's bump
- [x] 10 new tests; 213 across the solution

**Two carried commitments discharged.**

`ICompatibilityEvaluator` is now **the only type that constructs a `CompatibilityVerdict`**,
and it cannot produce one without calling the checker.
`EvaluatorTests.Evaluate_AlwaysConsultsTheChecker` uses a recording fake and fails if a future
fast path ever fabricates one. This closes the hole M1.1 recorded: the aggregate trusts the
verdict it is handed.

`GetSchemaHandler` **authorises by reachability**, expressed as a query over `Subjects` so it
inherits the tenant filter the schema table cannot have. Four integration tests against real
PostgreSQL: the owner reads it, a foreign tenant is refused, a stored-but-unreferenced schema
is refused *even to the tenant that stored it*, and refusal is byte-identical to absence — a
distinct "forbidden" would confirm another tenant's schema exists.

### Pass B, done

- [x] Minimal-API endpoints at `/v1` — subjects, versions, approve/reject, compatibility
      dry run, compatibility-policy, schemas, lookup
- [x] RFC 9457 Problem Details with a stable `concordatCode` extension on every failure
- [x] `/health/live` and `/health/ready`, separated
- [x] 17 end-to-end tests over real HTTP against real PostgreSQL; 230 across the solution
- [x] **Bundling**, deferred from M1.4 — `GET /v1/schemas/{id}/bundled`
- [x] **OpenAPI 3.1 generated, committed and drift-gated** — `docs/api/openapi.v1.json`,
      12 paths, emitted on every build; CI fails when it differs from what is committed
- [x] **`POST /environments/{env}/bootstrap`** — every subject plus every schema they need,
      including ones reachable only by reference, in one request
- [x] **`GET …/versions/{from}/diff/{to}`**
- [x] **Subject patch and retire** — `PATCH` for owner and deprecation, `DELETE` for the
      soft delete
- [x] `GET|PUT …/registration-policy` → was blocked on M7; built once the `Environment`
      aggregate existed, **and made to do something** — the field had been stored and never read.
      See [M7.1](M7-governance.md#m71-environments-and-brokers)
- [x] Negative-lookup caching semantics → **delivered in M2.1**, in `SchemaCache`; deferred from, where the client cache exists
- [ ] Subject prefix search — needs a `ComplexProperty` mapping or a shadow column

14 paths in the committed OpenAPI document; 245 tests.

### Bootstrap exists because cold start is the real load pattern

Not steady state. A fleet-wide rolling restart empties every cache at the same instant and
stampedes the registry — which sits on the *deserialize* path, so a registry that buckles
takes consumption down with it. Confluent Cloud caps Schema Registry at 75 reads/sec on every
tier, which is a low ceiling for exactly that burst.

The payload is self-sufficient: referenced schemas are included transitively, so a cold client
never has to follow a reference with a call it did not plan for. Retired subjects are
excluded — priming a cache with soft-deleted contracts would be worse than useless.

### The diff reports everything, not just violations

Filtering a diff by the current policy would hide differences that matter to a consumer who
generates code. A test asserts a forward-breaking widening still appears on a subject whose
policy is `Backward`, where a policy-filtered view would show nothing.

### There is still no hard delete

`DELETE` retires. Schemas are never removed (ADR-015), and removing a subject needs the
registered-consumer check and audit entry from DESIGN §4, both M7. Retirement is terminal and
blocks further registration; deprecation is advisory and does not, because existing producers
still need to patch their contract.

### Bundling stayed out of storage, deliberately

`GET /v1/schemas/{id}/bundled` inlines every reference transitively into `$defs` and rewrites
the refs as local pointers, so the result validates without further fetches. The **stored**
body keeps its `concordat://` references — an integration test asserts exactly that, because
bundling at registration would make canonicalisation depend on registry state and no SDK
could then reproduce a schema id offline (ADR-019).

Resolution is breadth-first with a visited set, so a reference graph that somehow contains a
cycle terminates. Cycles are rejected at registration, but a read path that trusts that
assumption is one bug away from hanging. A missing reference is a failure rather than a
bundle that still contains a `concordat://` ref — a half-bundled document is not
self-contained, and the client would only discover that at consume time.

### The OpenAPI document is now enforced

Generated on every build into `docs/api/openapi.v1.json` and committed. CI runs
`git diff --exit-code` against it, so the published protocol cannot drift from the code. A
spec refreshed by hand only ever documents what someone remembered to update.

Health endpoints do not appear in it — health checks are not API-described. Acceptable, and
worth knowing before someone generates a client and wonders where they went.

### The end-to-end tests are the M1 exit criterion

Not mocked at any layer, deliberately: the point is that canonicalisation, identity, the
compatibility engine, the aggregate and the database agree, and a test double at any of those
seams would hide precisely the disagreement worth finding.

They cover: adding an optional property is accepted; a breaking change returns **201 with
`AWAITING_APPROVAL`** and leaves `latest` alone; approval advances it; re-registering the tip
returns **200 with `created: false`** and allocates no ordinal; a breaking change labelled
MINOR is refused with `semver_label_understates_breakage`; the dry run reports
`#/required` and writes nothing; `integer → number` passes the default policy while staying
visible in `allDivergences`; a closed content model flips the verdict for the same two
documents; and an unreachable schema id is 404.

### `concordatCode` is the contract, not the status

Statuses are coarse — three unrelated failures share 409 — so a client branching on status
alone cannot tell a name collision from an incompatible schema. Every failure carries the
stable string. An unmapped code falls through to 400 rather than throwing: wrong-but-safe
beats an unhandled exception, and the code is still in the body.

### Registration policy was blocked on M7 — now built, and now enforced

`GET|PUT …/registration-policy` is per **environment**, and the `Environment` aggregate is
M7 (ADR-012) — there was nowhere to store it. Both routes exist as of
[M7.1](M7-governance.md#m71-environments-and-brokers).

**The field turned out to be the smaller half of the problem.** M7.1 shipped
`Environment.RegistrationPolicy`, defaulted it to `CiOnly` for anything named like production,
and documented it in three places as *"enforced server-side, which is the whole point"* —
against Confluent, whose equivalent is client-side only. No handler ever read it. It is
enforced now, on subject creation as well as version registration.

`{env}` in the route is still resolved by `DerivedEnvironmentResolver`, an M1 shim that hashes
the name to a stable id so the API works before environments exist. **M7 must still either adopt
those derived ids or migrate `subject.environment_id`** — and note the consequence for this
policy: an environment with no row has no policy and is allowed, which is what keeps the
quickstart working and is also a gap somebody could stand in.

### One gap not yet closed

`impactedConsumers[]` on the compatibility response (DESIGN §5) is **not implemented**. It
needs `ServiceRegistration` — services declaring producer and consumer intent — which is M7.
Until then the dry run answers "is this change safe?" but not "who does it break?", and the
latter is the capability DESIGN calls the one Confluent most conspicuously lacks.

## M1.7 Conformance corpus v0

**🔴 Heavy · ADR-019**

Normative from day one — a corpus written later only ratifies whatever .NET already did.

- [x] `tests/Concordat.Conformance` layout and fixture format, specified in its own README
- [x] 7 canonicalisation cases
- [x] 4 schema-id cases, **pinning the preimage bytes as well as the id**
- [x] 12 compatibility cases
- [x] 4 payload-validation cases — fixtures written, execution deferred, see below
- [x] .NET runner, 28 tests, running in CI with the rest

### The fixtures are JSON on disk, not C#

Loose files beside the assembly rather than embedded resources or test literals, because the
acceptance test for ADR-019 is that a Go team runs *exactly these files*. The format is
specified in [`tests/Concordat.Conformance/README.md`](../../tests/Concordat.Conformance/README.md),
which is part of the normative set.

Findings are matched on path, kind, direction and surface, **ignoring the message** — messages
are for humans and must stay free to improve without breaking every SDK's test run.

### The preimage is pinned, not just the id

An implementation that produces the right id from the wrong framing diverges the moment a
reference set changes, and the id alone would not catch it. Writing these by hand was worth
it: all four preimages matched the implementation exactly on the first run, including the
UTF-8 byte counts, which means the framing is genuinely reproducible from the written spec
rather than only from the code.

### One category cannot execute yet, and that is recorded rather than hidden

Concordat has no payload validator of its own — validation is client-side and uses a
different third-party library in every language. The runner currently asserts only that the
payload fixtures load, that their schema canonicalises and that every document parses. **M2
wires the first real validator; M6.1 makes every SDK run them.** They are written now because
that is the whole point: five independent validators disagree at the edges, and a corpus
written after the fact would just ratify whatever the first one did.

### A meta-test guards the corpus itself

`EveryFixtureExplainsWhyItExists` fails if any fixture lacks a `why`. A fixture whose purpose
nobody recorded is one nobody dares change when it fails — so it gets deleted or suppressed,
and both are worse than the failure.

## M1.8 Deployment

**Minimum viable: container plus compose**

- [x] `Concordat.Api` container image — `docker/api.Dockerfile`, published to GHCR
- [x] `docker compose --profile registry up` → Concordat + PostgreSQL. The default profile
      still leaves the registry out, because `dotnet run` is the loop you want while writing
      code against the SDK; the profile is for evaluating without a .NET SDK at all
- [ ] `CONCORDAT__*` configuration binding

---

## Exit

Register a JSON Schema over REST; register a second version and get a correct two-axis
verdict with JSON-Pointer paths; a breaking change registers as `AwaitingApproval` and
leaves `latest` unmoved; identical schemas in two environments share one id; OpenAPI
committed and drift-gated; corpus running in CI.

---

← [M0 — Foundations](M0-foundations.md) · [Plan index](../PLAN.md) · [M2 — .NET client →](M2-dotnet-client.md)
