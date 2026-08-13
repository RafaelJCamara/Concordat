# M1 — Registry core, JSON Schema only

**Depends on:** [M0](M0-foundations.md) · **Unlocks:** M2, M3, M4, M5, M7 · **Design refs:** [§4](../DESIGN.md#4-domain-model), [§5](../DESIGN.md#5-api-surface-and-cross-language-strategy), [§7](../DESIGN.md#7-contract-checks--cli-and-build-time), decisions 004, 007, 015, 016, 017, 019

The largest milestone by far. If it stalls, split at the M1.5/M1.6 boundary — domain plus
engine is independently reviewable without the API on top.

---

## M1.1 Domain model — **DONE 2026-08-13**

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

### Needs a product decision before M1.6

- **The default `CompatibilityPolicy` pair for new environments.** DESIGN §7 gives the who-axis
  default (`Backward`) and is silent on the surface. The domain deliberately ships no default.
  Suggested `Backward × WireJson`: `Backward × Source` would block `int32 → int64`, the exact
  change ADR-016 celebrates permitting, while `Backward × Wire` is a no-op for JSON Schema and
  becomes surprisingly permissive once Avro and Protobuf land.
- **Is the `Schema` table tenant-scoped?** Recommend global and deduplicated, keyed by
  `SchemaId` alone, preserving ADR-015's "same content ⇒ same id everywhere" at the storage
  layer. Cost: M1.6 must authorise `GET /schemas/{id}` by reachability from a subject in the
  caller's tenant. Decide before M1.5 writes a migration.

## M1.2 Canonicalisation and identity 🔴 (ADR-015)

- [ ] JSON Schema canonical form — key-sorted, whitespace-normalised, `$id` resolved
- [ ] SHA-256 of canonical form, truncated to 128 bits
- [ ] **Hash covers body + references + metadata**, not body alone
- [ ] Unique constraint on schema id → registration idempotent, no single-writer counter
- [ ] Size ceiling with a documented limit → `schema_too_large`
- [ ] Golden tests: whitespace / key order / `$id` form variants collapse to one id; differing reference sets produce **different** ids

## M1.3 Compatibility engine 🔴🔴 (ADR-016, DESIGN §7)

**The correctness heart of the product. Heaviest test investment in the repo.**

- [ ] Axis 1 — `Backward | BackwardTransitive | Forward | ForwardTransitive | Full | FullTransitive | None`
- [ ] Axis 2 — `Wire ⊂ WireJson ⊂ Source`
- [ ] Policy resolution: environment default, per-subject override
- [ ] JSON Schema rules, designed from scratch — acceptance criteria:
  - [ ] **Adding an optional property is fully compatible**
  - [ ] **Removing an optional property is fully compatible**
  - [ ] Content model is explicit subject config, never inferred per-schema
  - [ ] Narrowing `type`/`enum`/`maximum`, adding to `required`, `additionalProperties: true → false` are backward-breaking
  - [ ] Widening is forward-breaking
- [ ] Every finding carries an exact **JSON-Pointer path**, `kind`, actionable message, `conflictsWithVersion`, and the narrowest axis it violates
- [ ] `suggestedSemver` derivation
- [ ] Semver label verification (ADR-004) — a breaking change cannot be labelled MINOR
- [ ] Golden corpus: table-driven `(old, new, who-axis, what-axis) → (verdict, expected paths)`, including the cases Confluent gets wrong

## M1.4 Schema references

- [ ] `Reference = (name, subject, version)`; `concordat://<env>/<subject>/<version>` resolution
- [ ] Registration resolves into a bundled canonical form **and** retains the edges
- [ ] Cycle detection, rejected at registration
- [ ] Transitive compatibility — a referenced subject's new version re-checks every referrer
- [ ] Reference tests

## M1.5 Persistence

- [ ] EF Core model + PostgreSQL provider (ADR-007)
- [ ] Migrations; `Concordat.Migrator` host; auto-migrate on startup, toggleable
- [ ] Unique constraint backing M1.2 idempotency
- [ ] `ITenantContext` + EF global query filters — wired now with a single implicit tenant, so M9 is not a retrofit
- [ ] Deletion semantics: schemas never deleted; subjects soft-delete to `Retired`; hard delete requires no registered consumers + force flag + audit entry
- [ ] Testcontainers PostgreSQL integration tests

## M1.6 REST API (DESIGN §5)

- [ ] Minimal-API endpoints at `/v1`, CQRS dispatcher (hand-rolled, **not** MediatR — ADR-009)
- [ ] `Result<T>` → Problem Details mapping
- [ ] Schemas: `GET /schemas/{id}`, `GET /schemas/{id}/subjects`, `POST /schemas/lookup`
- [ ] Subjects: list, create, get, patch, delete
- [ ] Versions: list, register, get by `{ordinal|latest}`
- [ ] Approval gate: `POST …/versions/{n}/approve`, `…/reject` (ADR-017)
- [ ] `GET|PUT /environments/{env}/registration-policy` — **server-side**, so no client config can bypass it
- [ ] `POST …/compatibility` dry run — never writes; returns `compatible`, `breakingChanges[]`, `suggestedSemver`, `impactedConsumers[]`
- [ ] `GET|PUT …/compatibility-policy`; `GET …/versions/{a}/diff/{b}`
- [ ] `POST /environments/{env}/bootstrap` — every schema a client needs in **one** request
- [ ] **RFC 9457 Problem Details + stable string `concordatCode`**; catalogue documented
- [ ] Negative-lookup caching semantics so a missing subject cannot retry-storm
- [ ] `/health/live`, `/health/ready`
- [ ] OpenAPI 3.1 generated from endpoints, committed to `docs/api/openapi.v1.json`
- [ ] **CI fails on OpenAPI drift**

## M1.7 Conformance corpus v0 🔴 (ADR-019)

Normative from day one — a corpus written later only ratifies whatever .NET already did.

- [ ] `tests/Concordat.Conformance` layout and fixture format, language-neutral
- [ ] Canonicalisation cases
- [ ] Compatibility verdict cases
- [ ] Payload-validation corpus — documents that must accept / must reject
- [ ] .NET runner executing the corpus in CI

## M1.8 Deployment (minimum)

- [ ] `Concordat.Api` container image
- [ ] `docker compose up` → Concordat + PostgreSQL
- [ ] `CONCORDAT__*` configuration binding

---

## Exit

Register a JSON Schema over REST; register a second version and get a correct two-axis
verdict with JSON-Pointer paths; a breaking change registers as `AwaitingApproval` and
leaves `latest` unmoved; identical schemas in two environments share one id; OpenAPI
committed and drift-gated; corpus running in CI.

---

← [M0 — Foundations](M0-foundations.md) · [Plan index](../PLAN.md) · [M2 — .NET client →](M2-dotnet-client.md)
