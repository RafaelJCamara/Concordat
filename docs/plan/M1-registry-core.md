# M1 — Registry core, JSON Schema only

**Depends on:** [M0](M0-foundations.md) · **Unlocks:** M2, M3, M4, M5, M7 · **Design refs:** [§4](../DESIGN.md#4-domain-model), [§5](../DESIGN.md#5-api-surface-and-cross-language-strategy), [§7](../DESIGN.md#7-contract-checks--cli-and-build-time), decisions 004, 007, 015, 016, 017, 019

The largest milestone by far. If it stalls, split at the M1.5/M1.6 boundary — domain plus
engine is independently reviewable without the API on top.

---

## M1.1 Domain model (DESIGN §4)

- [ ] `SchemaId` value object — content-addressed, 128-bit, lowercase hex
- [ ] `Schema` aggregate — immutable; `Format`, `Body` (canonical text), `References[]`
- [ ] `Subject` aggregate root — `SubjectName`, `Format`, `CompatibilityPolicy`, `Owner`, `Lifecycle`, `Versions[]`
- [ ] `SchemaVersion` entity — `Ordinal`, `SemanticVersion?`, `SchemaId`, `Changelog`, `RegisteredAt/By`, `Deprecated`, `Status`
- [ ] `LatestPointer` — explicit and gated, **not** "highest ordinal" (ADR-017)
- [ ] `CompatibilityPolicy` — the two-axis pair (ADR-016)
- [ ] `RegistrationPolicy` — `Open | CiOnly | Closed`, per environment
- [ ] Invariants: one format across all versions; ordinals contiguous and monotonic; new version satisfies policy
- [ ] Domain unit tests

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

- [ ] `Reference = (name, subject, version)`; `indenture://<env>/<subject>/<version>` resolution
- [ ] Registration resolves into a bundled canonical form **and** retains the edges
- [ ] Cycle detection, rejected at registration
- [ ] Transitive compatibility — a referenced subject's new version re-checks every referrer
- [ ] Reference tests

## M1.5 Persistence

- [ ] EF Core model + PostgreSQL provider (ADR-007)
- [ ] Migrations; `Indenture.Migrator` host; auto-migrate on startup, toggleable
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
- [ ] **RFC 9457 Problem Details + stable string `indentureCode`**; catalogue documented
- [ ] Negative-lookup caching semantics so a missing subject cannot retry-storm
- [ ] `/health/live`, `/health/ready`
- [ ] OpenAPI 3.1 generated from endpoints, committed to `docs/api/openapi.v1.json`
- [ ] **CI fails on OpenAPI drift**

## M1.7 Conformance corpus v0 🔴 (ADR-019)

Normative from day one — a corpus written later only ratifies whatever .NET already did.

- [ ] `tests/Indenture.Conformance` layout and fixture format, language-neutral
- [ ] Canonicalisation cases
- [ ] Compatibility verdict cases
- [ ] Payload-validation corpus — documents that must accept / must reject
- [ ] .NET runner executing the corpus in CI

## M1.8 Deployment (minimum)

- [ ] `Indenture.Api` container image
- [ ] `docker compose up` → Indenture + PostgreSQL
- [ ] `INDENTURE__*` configuration binding

---

## Exit

Register a JSON Schema over REST; register a second version and get a correct two-axis
verdict with JSON-Pointer paths; a breaking change registers as `AwaitingApproval` and
leaves `latest` unmoved; identical schemas in two environments share one id; OpenAPI
committed and drift-gated; corpus running in CI.

---

← [M0 — Foundations](M0-foundations.md) · [Plan index](../PLAN.md) · [M2 — .NET client →](M2-dotnet-client.md)
