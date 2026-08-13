<#
.SYNOPSIS
    Seeds GitHub milestones, labels and issues from docs/PLAN.md.

.DESCRIPTION
    Creates 10 milestones (M0-M9) and 48 issues, one per work package, mirroring
    docs/plan/*.md. Idempotent: existing milestones, labels and issues (matched by
    title) are skipped, so re-running after editing the plan only adds what is new.

    Requires the GitHub CLI, authenticated:  gh auth login

.PARAMETER DryRun
    Print what would be created without calling GitHub. Works without gh installed
    or authenticated, provided -Repo is supplied.

.PARAMETER Repo
    Target repository as owner/name. Defaults to whatever gh resolves for the current
    directory, which requires gh to be authenticated.

.EXAMPLE
    ./scripts/seed-github.ps1 -DryRun -Repo owner/name
    ./scripts/seed-github.ps1
#>
[CmdletBinding()]
param(
    [switch]$DryRun,
    [string]$Repo
)

$ErrorActionPreference = 'Stop'

$hasGh = [bool](Get-Command gh -ErrorAction SilentlyContinue)
if (-not $hasGh -and -not $DryRun) {
    throw "GitHub CLI not found. Install with: winget install --id GitHub.cli"
}

if ($Repo) {
    $repo = $Repo
} elseif ($hasGh) {
    $repo = (gh repo view --json nameWithOwner --jq .nameWithOwner)
}
if (-not $repo) { throw "Could not resolve repository. Pass -Repo owner/name, or run 'gh auth login'." }
Write-Host "Repository: $repo" -ForegroundColor Cyan
if ($DryRun) { Write-Host "DRY RUN - nothing will be created`n" -ForegroundColor Yellow }

# --------------------------------------------------------------------------- labels

$labels = @(
    @{ Name = 'area:infra';      Color = '6e7781'; Desc = 'Build, CI, repo plumbing' }
    @{ Name = 'area:registry';   Color = '0969da'; Desc = 'Registry core: domain, compatibility, API' }
    @{ Name = 'area:client';     Color = '1f883d'; Desc = '.NET client and RabbitMQ middleware' }
    @{ Name = 'area:cli';        Color = '8250df'; Desc = 'signet CLI and CI gate' }
    @{ Name = 'area:web';        Color = 'bf3989'; Desc = 'Angular web app' }
    @{ Name = 'area:formats';    Color = 'bc4c00'; Desc = 'JSON Schema, Avro, Protobuf' }
    @{ Name = 'area:sdk';        Color = '0e8a16'; Desc = 'Tier 2 polyglot SDKs' }
    @{ Name = 'area:governance'; Color = '9a6700'; Desc = 'Environments, contracts, impact, promotion' }
    @{ Name = 'area:identity';   Color = 'b60205'; Desc = 'Identity, RBAC, API keys' }
    @{ Name = 'area:cloud';      Color = '5319e7'; Desc = 'Multi-tenancy and billing' }
    @{ Name = 'critical-path';   Color = 'd93f0b'; Desc = 'M0-M3: the smallest genuinely useful product' }
    @{ Name = 'heavy';           Color = 'e99695'; Desc = 'Heaviest packages - schedule first, protect from squeeze' }
    @{ Name = 'blocking';        Color = 'd73a4a'; Desc = 'Blocks other work until resolved' }
)

Write-Host "`n== Labels ==" -ForegroundColor Cyan
$existingLabels = if ($DryRun) { @() } else { (gh label list --limit 200 --json name --jq '.[].name') }
foreach ($l in $labels) {
    if ($existingLabels -contains $l.Name) {
        Write-Host "  = $($l.Name)" -ForegroundColor DarkGray
        continue
    }
    Write-Host "  + $($l.Name)" -ForegroundColor Green
    if (-not $DryRun) {
        gh label create $l.Name --color $l.Color --description $l.Desc | Out-Null
    }
}

# ----------------------------------------------------------------------- milestones

$milestones = @(
    @{ Key = 'M0'; Title = 'M0 - Foundations'
       Desc = 'Repo skeleton, CI, ADRs. Blocking first item: check name availability across all five package registries. Exit: CI green on an empty solution; ADRs merged; names secured. docs/plan/M0-foundations.md' }
    @{ Key = 'M1'; Title = 'M1 - Registry core (JSON Schema only)'
       Desc = 'Domain model, content-addressed identity, two-axis compatibility engine, references, REST API, conformance corpus. Largest milestone. Exit: register a schema, get a correct two-axis verdict with JSON-Pointer paths, breaking change gated at AwaitingApproval. docs/plan/M1-registry-core.md' }
    @{ Key = 'M2'; Title = 'M2 - .NET client + RabbitMQ.Client middleware'
       Desc = 'Signet.Client with caching, the Signet envelope, publish/consume middleware, quarantine. Exit: an invalid message is rejected to quarantine and not retried; the registry can be killed after warm-up without affecting delivery. docs/plan/M2-dotnet-client.md' }
    @{ Key = 'M3'; Title = 'M3 - CLI, CI gate, build-time packages'
       Desc = 'signet CLI including infer, NativeAOT binaries, GitHub Action, MSBuild/Roslyn contract packages. Exit: a breaking change to a C# record fails the build and CI, naming the JSON-Pointer path. docs/plan/M3-cli.md' }
    @{ Key = 'M4'; Title = 'M4 - Angular web app'
       Desc = 'Angular 22 + Spartan UI port of the React prototype, with the admin-only write gate built from day one. Exit: create a subject, register a version, view diff and impact, approve a breaking change - entirely in the UI. docs/plan/M4-web-app.md' }
    @{ Key = 'M5'; Title = 'M5 - Avro + Protobuf'
       Desc = 'Format abstraction plus Avro and Protobuf canonicalisation, references and two-axis compatibility. Exit: all three formats round-trip with the corpus green. docs/plan/M5-formats.md' }
    @{ Key = 'M6'; Title = 'M6 - Tier 2 SDKs'
       Desc = 'Gated sequence: TypeScript/JavaScript, then Python, then Go, then Java. Protocol freeze first. Exit per SDK: the conformance corpus passes unmodified and a quickstart written from published docs alone works. docs/plan/M6-sdks.md' }
    @{ Key = 'M7'; Title = 'M7 - Environments, brokers, governance'
       Desc = 'Environments, encrypted broker credentials, contracts, impact analysis, promotion, audit, notifications. Exit: registering in staging reports exactly which consumers break and can be promoted with a fresh check. docs/plan/M7-governance.md' }
    @{ Key = 'M8'; Title = 'M8 - Identity, RBAC, API keys'
       Desc = 'Tenants, users, roles, hashed API keys, and server-side enforcement of admin-only schema writes. Exit: a non-admin can browse everything and change nothing, and that holds against curl. docs/plan/M8-identity.md' }
    @{ Key = 'M9'; Title = 'M9 - Signet Cloud'
       Desc = 'Multi-tenancy, SSO, Stripe metering, Helm parity. Exit: two tenants on one deployment cannot see each other subjects, proven by test, and metered usage reaches Stripe. docs/plan/M9-cloud.md' }
)

Write-Host "`n== Milestones ==" -ForegroundColor Cyan
$existingMs = if ($DryRun) { @() } else { (gh api "repos/$repo/milestones?state=all&per_page=100" --jq '.[].title') }
$msNumbers = @{}
foreach ($m in $milestones) {
    if ($existingMs -contains $m.Title) {
        Write-Host "  = $($m.Title)" -ForegroundColor DarkGray
    } else {
        Write-Host "  + $($m.Title)" -ForegroundColor Green
        if (-not $DryRun) {
            gh api "repos/$repo/milestones" -f "title=$($m.Title)" -f "description=$($m.Desc)" | Out-Null
        }
    }
    $msNumbers[$m.Key] = $m.Title
}

# --------------------------------------------------------------------------- issues

function New-Issue {
    param($Id, $Milestone, $Title, $Labels, $Body)
    [pscustomobject]@{
        Id = $Id; Milestone = $Milestone; Title = "$Id $Title"; Labels = $Labels; Body = $Body
    }
}

$planLink = 'https://github.com/{0}/blob/main/docs/plan' -f $repo

$issues = @(
    # ------------------------------------------------------------------ M0
    New-Issue 'M0.1' 'M0' 'Name availability' @('area:infra','critical-path','heavy','blocking') @'
**Blocking. Do this before anything else.** ADR-021 makes all five package registries load-bearing; renaming after M1 touches every artifact.

- [ ] NuGet: `Signet`, `Signet.Client`, `Signet.Contracts`
- [ ] npm: the `@signet` scope
- [ ] PyPI: `signet-client`
- [ ] Go module path
- [ ] Maven groupId `dev.signet`
- [ ] Domain(s)
- [ ] If any are taken, decide branding **now**
'@

    New-Issue 'M0.2' 'M0' 'Repository skeleton' @('area:infra','critical-path') @'
- [ ] `Signet.slnx`, `global.json` pinning .NET SDK 10 (`net10.0`)
- [ ] `Directory.Build.props` - shared TFM, nullable, warnings-as-errors, deterministic builds
- [ ] `Directory.Packages.props` - central package management
- [ ] `.editorconfig`, analyzer ruleset
- [ ] Folder structure per DESIGN §8
- [ ] `LICENSE` (Apache-2.0), `NOTICE`, license headers
'@

    New-Issue 'M0.3' 'M0' 'CI' @('area:infra','critical-path') @'
- [ ] Build + test on PR, matrix over supported OS
- [ ] Format and analyzer gate
- [ ] Coverage reporting, non-blocking initially
'@

    New-Issue 'M0.4' 'M0' 'ADRs' @('area:infra') @'
- [ ] ADR template
- [ ] Expand decisions 001-021 from the DESIGN.md table into `docs/adr/`, one file each
'@

    # ------------------------------------------------------------------ M1
    New-Issue 'M1.1' 'M1' 'Domain model' @('area:registry','critical-path') @'
- [ ] `SchemaId` value object - content-addressed, 128-bit, lowercase hex
- [ ] `Schema` aggregate - immutable; `Format`, `Body` (canonical text), `References[]`
- [ ] `Subject` aggregate root - `SubjectName`, `Format`, `CompatibilityPolicy`, `Owner`, `Lifecycle`, `Versions[]`
- [ ] `SchemaVersion` entity - `Ordinal`, `SemanticVersion?`, `SchemaId`, `Changelog`, `RegisteredAt/By`, `Deprecated`, `Status`
- [ ] `LatestPointer` - explicit and gated, **not** "highest ordinal" (ADR-017)
- [ ] `CompatibilityPolicy` - the two-axis pair (ADR-016)
- [ ] `RegistrationPolicy` - `Open | CiOnly | Closed`, per environment
- [ ] Invariants: one format across all versions; ordinals contiguous and monotonic; new version satisfies policy
- [ ] Domain unit tests
'@

    New-Issue 'M1.2' 'M1' 'Canonicalisation and identity' @('area:registry','area:formats','critical-path','heavy') @'
ADR-015. Get the hash envelope wrong and schemas with different reference sets collide.

- [ ] JSON Schema canonical form - key-sorted, whitespace-normalised, `$id` resolved
- [ ] SHA-256 of canonical form, truncated to 128 bits
- [ ] **Hash covers body + references + metadata**, not body alone
- [ ] Unique constraint on schema id -> registration idempotent, no single-writer counter
- [ ] Size ceiling with a documented limit -> `schema_too_large`
- [ ] Golden tests: whitespace / key order / `$id` variants collapse to one id; differing reference sets produce **different** ids
'@

    New-Issue 'M1.3' 'M1' 'Compatibility engine' @('area:registry','area:formats','critical-path','heavy') @'
**The correctness heart of the product.** Heaviest test investment in the repo - a wrong verdict either blocks safe changes or waves breaking ones through. ADR-016, DESIGN §7.

- [ ] Axis 1 - `Backward | BackwardTransitive | Forward | ForwardTransitive | Full | FullTransitive | None`
- [ ] Axis 2 - `Wire` subset of `WireJson` subset of `Source`
- [ ] Policy resolution: environment default, per-subject override
- [ ] JSON Schema rules, designed from scratch - acceptance criteria:
  - [ ] **Adding an optional property is fully compatible**
  - [ ] **Removing an optional property is fully compatible**
  - [ ] Content model is explicit subject config, never inferred per-schema
  - [ ] Narrowing `type`/`enum`/`maximum`, adding to `required`, `additionalProperties: true -> false` are backward-breaking
  - [ ] Widening is forward-breaking
- [ ] Every finding carries an exact **JSON-Pointer path**, `kind`, actionable message, `conflictsWithVersion`, and the narrowest axis it violates
- [ ] `suggestedSemver` derivation
- [ ] Semver label verification (ADR-004) - a breaking change cannot be labelled MINOR
- [ ] Golden corpus: table-driven `(old, new, who-axis, what-axis) -> (verdict, expected paths)`, including the cases Confluent gets wrong
'@

    New-Issue 'M1.4' 'M1' 'Schema references' @('area:registry','critical-path') @'
- [ ] `Reference = (name, subject, version)`; `signet://<env>/<subject>/<version>` resolution
- [ ] Registration resolves into a bundled canonical form **and** retains the edges
- [ ] Cycle detection, rejected at registration
- [ ] Transitive compatibility - a referenced subject new version re-checks every referrer
- [ ] Reference tests
'@

    New-Issue 'M1.5' 'M1' 'Persistence' @('area:registry','critical-path') @'
`ITenantContext` and the global query filters are wired now, with a single implicit tenant, so M9 multi-tenancy is a config swap rather than surgery.

- [ ] EF Core model + PostgreSQL provider (ADR-007)
- [ ] Migrations; `Signet.Migrator` host; auto-migrate on startup, toggleable
- [ ] Unique constraint backing M1.2 idempotency
- [ ] `ITenantContext` + EF global query filters
- [ ] Deletion semantics: schemas never deleted; subjects soft-delete to `Retired`; hard delete requires no registered consumers + force flag + audit entry
- [ ] Testcontainers PostgreSQL integration tests
'@

    New-Issue 'M1.6' 'M1' 'REST API' @('area:registry','critical-path') @'
DESIGN §5.

- [ ] Minimal-API endpoints at `/v1`, CQRS dispatcher (hand-rolled, **not** MediatR - ADR-009)
- [ ] `Result<T>` -> Problem Details mapping
- [ ] Schemas: `GET /schemas/{id}`, `GET /schemas/{id}/subjects`, `POST /schemas/lookup`
- [ ] Subjects: list, create, get, patch, delete
- [ ] Versions: list, register, get by `{ordinal|latest}`
- [ ] Approval gate: `POST .../versions/{n}/approve`, `.../reject` (ADR-017)
- [ ] `GET|PUT /environments/{env}/registration-policy` - **server-side**, so no client config can bypass it
- [ ] `POST .../compatibility` dry run - never writes
- [ ] `GET|PUT .../compatibility-policy`; `GET .../versions/{a}/diff/{b}`
- [ ] `POST /environments/{env}/bootstrap` - every schema a client needs in **one** request
- [ ] **RFC 9457 Problem Details + stable string `signetCode`**; catalogue documented
- [ ] Negative-lookup caching so a missing subject cannot retry-storm
- [ ] `/health/live`, `/health/ready`
- [ ] OpenAPI 3.1 generated and committed to `docs/api/openapi.v1.json`
- [ ] **CI fails on OpenAPI drift**
'@

    New-Issue 'M1.7' 'M1' 'Conformance corpus v0' @('area:registry','critical-path','heavy') @'
ADR-019. Normative from day one - a corpus written later only ratifies whatever .NET already did.

- [ ] `tests/Signet.Conformance` layout and fixture format, language-neutral
- [ ] Canonicalisation cases
- [ ] Compatibility verdict cases
- [ ] Payload-validation corpus - documents that must accept / must reject
- [ ] .NET runner executing the corpus in CI
'@

    New-Issue 'M1.8' 'M1' 'Deployment (minimum)' @('area:infra','critical-path') @'
- [ ] `Signet.Api` container image
- [ ] `docker compose up` -> Signet + PostgreSQL
- [ ] `SIGNET__*` configuration binding
'@

    # ------------------------------------------------------------------ M2
    New-Issue 'M2.1' 'M2' 'Signet.Client' @('area:client','critical-path') @'
- [ ] Typed HTTP client over the `/v1` surface; API-key auth
- [ ] Problem Details -> typed exceptions, `signetCode` preserved
- [ ] Cache: `schema-id -> schema` and `subject+version -> schema-id` **immutable, cached forever**
- [ ] Cache: `subject -> latest` 30s TTL; contract resolution 60s TTL; negative lookups cached
- [ ] Warm-up via `POST /bootstrap`, **with jitter** - a fleet-wide rolling restart must not stampede
- [ ] `fail-open | fail-closed` configuration
- [ ] **Hard rule enforced by test: after warm-up the registry is never in the delivery path**
'@

    New-Issue 'M2.2' 'M2' 'Envelope' @('area:client','critical-path') @'
DESIGN §2, ADR-010.

- [ ] Mode A writer/reader - `signet-v`, `signet-schema-id`, `signet-subject`, `signet-version`, `signet-semver`, `signet-format`
- [ ] All values UTF-8 strings; **decode `byte[]` on read** (RabbitMQ.Client writes `S`, reads back `byte[]`)
- [ ] No `x-` prefix; avoid `ulong`, `bool false`, values > 64 KiB
- [ ] Mode B: `content-type: application/json+signet.v1.<hex-id>`
- [ ] Mode B: `0x01 | <16-byte id> | payload`, opt-in
- [ ] Legacy `0x00 | <int32 BE>` - **read-only**, for Kafka-bridge ingestion
- [ ] CloudEvents read-only interop: both `cloudEvents_`/`cloudEvents:` and `ce-`
'@

    New-Issue 'M2.3' 'M2' 'Subject resolution' @('area:client','critical-path') @'
- [ ] `ISubjectResolver` seam
- [ ] RabbitMQ.Client resolver - `properties.type`, as-is
- [ ] Canonical-form validation `^[A-Za-z0-9_]+(\.[A-Za-z0-9_]+)*$`
'@

    New-Issue 'M2.4' 'M2' 'Middleware' @('area:client','critical-path') @'
- [ ] Publish: decorator over `IChannel.BasicPublishAsync`; a throw blocks the publish
- [ ] Consume: `IAsyncBasicConsumer` decorator - **nack, never throw** (throws surface via `CallbackExceptionAsync`)
- [ ] Payload validation **on by default**
- [ ] Reject to a `signet.quarantine` exchange with failure-reason headers
- [ ] **No retry on schema violation** - deterministic, so redelivery is pure waste
- [ ] `EnforcementMode` honoured: `Off | Monitor | Enforce`
'@

    New-Issue 'M2.5' 'M2' 'Header survival experiments' @('area:client','critical-path','heavy') @'
Empirical work with **no code deliverable**. The result *is* the documented Mode A vs Mode B guidance, so it cannot be skipped without leaving DESIGN §2 as speculation.

- [ ] Dead-lettering
- [ ] Shovel
- [ ] Federation
- [ ] STOMP and MQTT adapters
- [ ] Write findings back into DESIGN §2
'@

    New-Issue 'M2.6' 'M2' 'Tests' @('area:client','critical-path') @'
- [ ] Testcontainers RabbitMQ: publish conforming + non-conforming; assert the latter lands in `signet.quarantine` with reason headers
- [ ] Header round-trip: `string -> byte[]` holds; no collision with `MT-`, `NServiceBus.`, `rbs2-`, `rabbitmq-`, `x-`
- [ ] Conformance corpus runs against the client
'@

    # ------------------------------------------------------------------ M3
    New-Issue 'M3.1' 'M3' 'signet CLI' @('area:cli','critical-path') @'
- [ ] `check --env <env> --dir ./contracts` - dry-run compatibility, **exit 1 on break**, offending JSON-Pointer path in output
- [ ] `push`, `promote`, `diff`, `impact`, `lint`, `export`
- [ ] `--json` output mode for scripting
- [ ] Documented exit codes
'@

    New-Issue 'M3.2' 'M3' 'signet infer' @('area:cli','critical-path') @'
ADR-014. Turns a 200-message-type estate from weeks of authoring into an afternoon.

- [ ] File mode - infer from a corpus of sample payloads (**the default**)
- [ ] Queue mode - read-only drain via `basic.get` with requeue, or an exclusive consumer that nacks with requeue
- [ ] **Document that queue mode can reorder a live queue**
- [ ] Inference: types, required-by-presence, `format` detection (uuid, date-time, email), low-cardinality enums, nullability
- [ ] Output is a draft **plus a confidence/ambiguity report** - never auto-registers
'@

    New-Issue 'M3.3' 'M3' 'Distribution' @('area:cli','critical-path') @'
- [ ] NativeAOT binaries: win-x64, linux-x64, linux-arm64, osx-arm64
- [ ] Docker image
- [ ] GitHub Action wrapping the container
- [ ] Verify: a Python or Go shop can gate CI with **zero .NET installed**
'@

    New-Issue 'M3.4' 'M3' 'Build-time packages' @('area:cli','critical-path') @'
- [ ] `Signet.Contracts` - `[SignetContract("acme.orders.OrderCreated")]`
- [ ] `Signet.Contracts.MSBuild` - MSBuild task + Roslyn analyzer; generate a schema per attributed type, diff against checked-in `contracts/`, **error on drift**
- [ ] `Signet.Contracts.Testing` - `await Signet.Assert.CompatibleAsync<OrderCreated>(env: "prod")`
'@

    # ------------------------------------------------------------------ M4
    New-Issue 'M4.1' 'M4' 'Scaffold' @('area:web') @'
- [ ] Angular 22 standalone + signals, no NgModules; Angular CLI 21+
- [ ] Spartan UI - `@spartan-ng/brain` + `helm` components generated into the repo as source
- [ ] Port the prototype `index.css` token set **verbatim**; add a light theme
- [ ] `@ngrx/signals` SignalStore per feature
- [ ] Folder structure per DESIGN §9; **ESLint boundaries rule** enforcing it
- [ ] `core/` interceptors: auth, tenant, problem-details
'@

    New-Issue 'M4.2' 'M4' 'Access control' @('area:web','area:identity') @'
ADR-018. **Built now, not in M8** - retrofitting an authorization check across finished screens is how a write path gets missed.

- [ ] `canWriteSchemas` computed on the session store, derived from API-returned scopes
- [ ] `*sgIfScope` structural directive for affordances
- [ ] `scopeGuard` on write routes; direct navigation redirects to the read view
- [ ] Stub returning admin in the single-user self-hosted profile until M8
- [ ] Write affordances **absent, not disabled**, for non-admins
'@

    New-Issue 'M4.3' 'M4' 'Pages' @('area:web') @'
- [ ] `Dashboard` - environment-scoped overview
- [ ] `SubjectListPage`, `SubjectDetailPage` (`/subjects/:name`, **real routes not query params**), `VersionDetailPage`, `NewVersionPage`
- [ ] `ContractsPage` - bindings, enforcement mode, version selectors
- [ ] `CompatibilityDiffPage`, `ImpactAnalysisPage`
- [ ] `ApprovalsPage` - pending breaking changes with diff and impact; approve/reject (admin only)
- [ ] `AuditLogPage`, `LoginPage`
- [ ] Settings split: `EnvironmentSettings`, `Brokers`, `ApiKeys`, `Members`
- [ ] Notifications as reactive forms that actually persist
'@

    New-Issue 'M4.4' 'M4' 'Port corrections' @('area:web') @'
- [ ] **Monaco replaces the regex highlighter** - the prototype `dangerouslySetInnerHTML` is an XSS hole and is not ported
- [ ] Collapse the two competing HTTP paths into one typed data-access layer
- [ ] Uncontrolled `defaultValue` forms -> reactive forms
- [ ] Drop the unused dependency surface (React Query, react-hook-form, zod, recharts, next-themes, cmdk, vaul, embla, input-otp)
- [ ] `ajv` for client-side validation and sample-payload checking
- [ ] Preserve: immutable-id confirmation, auto-slugged id with a touched flag, semver auto-increment seeded from latest, clone-previous-version JSON, compatibility tooltips, "No versions yet" empty state, Format/Validate buttons
'@

    New-Issue 'M4.5' 'M4' 'Tests' @('area:web') @'
- [ ] Playwright E2E against a real API
- [ ] Non-admin E2E: no write affordance renders; direct URL to a write route redirects
'@

    # ------------------------------------------------------------------ M5
    New-Issue 'M5.1' 'M5' 'Format abstraction' @('area:formats') @'
- [ ] `Signet.Formats.Abstractions` - canonicalisation, validation, compatibility per format
- [ ] Format projects depend only on the abstraction + their parser library
'@

    New-Issue 'M5.2' 'M5' 'Avro' @('area:formats') @'
- [ ] Parsing Canonical Form
- [ ] Resolution rules: defaults, aliases, union widening, enum symbols
- [ ] Named-type FQN -> subject reference resolution
- [ ] Two-axis mapping
'@

    New-Issue 'M5.3' 'M5' 'Protobuf' @('area:formats') @'
- [ ] Normalised `FileDescriptorProto`, import ordering normalised
- [ ] Field numbers as identity - break on number or wire-type change, and on removal without `reserved`
- [ ] `import` filename -> subject reference resolution
- [ ] Two-axis mapping - a rename is `WIRE`-safe but `WIRE_JSON`- and `SOURCE`-breaking
'@

    New-Issue 'M5.4' 'M5' 'Corpus extension' @('area:formats') @'
- [ ] `int32 -> int64` passes `WIRE`, fails `SOURCE`
- [ ] Protobuf message rename with stable field tags passes `WIRE`, fails `WIRE_JSON`
- [ ] Splitting a `.proto` across files is compatible (Confluent rejects this)
'@

    # ------------------------------------------------------------------ M6
    New-Issue 'M6.1' 'M6' 'Protocol freeze and interop prerequisites' @('area:sdk','heavy') @'
Do this **before** the first SDK, not during it.

- [ ] Publish the five normative artifacts as a coherent set (OpenAPI, envelope spec, canonicalisation rules, `signetCode` catalogue, conformance corpus)
- [ ] **Pin JSON Schema draft 2020-12** as the only supported dialect
- [ ] Define the **interoperable keyword subset**; warn at registration when a schema strays outside it
- [ ] Expand the payload-validation corpus - five independent validators (`ajv`, `jsonschema`, `santhosh-tekuri`, `networknt`, .NET) disagree at the edges
- [ ] Audit the API for CLR-shaped leakage: no assembly-qualified names, no `System.*` type strings
'@

    New-Issue 'M6.2' 'M6' 'TypeScript / JavaScript SDK' @('area:sdk') @'
- [ ] `@signet/client` - isomorphic REST + cache, browser-safe
- [ ] `@signet/amqp` - Node-only middleware over `amqplib` (**separate package**, so `amqplib` never enters a browser bundle)
- [ ] `ajv` validation, shared behaviour with the Angular app
- [ ] ESM + CJS builds + `.d.ts`; a plain-JS consumer needs no TypeScript toolchain
- [ ] Conformance corpus in CI; publish to npm
'@

    New-Issue 'M6.3' 'M6' 'Python SDK' @('area:sdk') @'
- [ ] `signet-client`, Python 3.11+
- [ ] `pika` adapter (sync)
- [ ] `aio-pika` adapter (async) - **a separate programming model, not a flag**
- [ ] `jsonschema` validation
- [ ] Conformance corpus in CI; publish to PyPI
'@

    New-Issue 'M6.4' 'M6' 'Go SDK' @('area:sdk') @'
- [ ] `signet-go` over `rabbitmq/amqp091-go`
- [ ] `santhosh-tekuri/jsonschema`
- [ ] Fail-open/closed and reject paths as **error returns** - expect this to surface places where the corpus specified .NET control flow rather than behaviour; fix the corpus, not just the client
- [ ] Conformance corpus in CI; publish module
'@

    New-Issue 'M6.5' 'M6' 'Java SDK' @('area:sdk') @'
- [ ] `dev.signet:signet-client`, Java 21 LTS, over `com.rabbitmq:amqp-client`
- [ ] `networknt/json-schema-validator`
- [ ] Conformance corpus in CI; publish to Maven Central
- [ ] **Known gap:** Spring AMQP is deferred (DESIGN Appendix A), so this reaches a minority of Java RabbitMQ estate. Budget the Spring adapter alongside it or expect the SDK to under-deliver.
'@

    # ------------------------------------------------------------------ M7
    New-Issue 'M7.1' 'M7' 'Environments and brokers' @('area:governance') @'
ADR-012.

- [ ] `Environment` aggregate - `Name`, `Description`, `Brokers[]`, `DefaultCompatibilityPolicy`
- [ ] `BrokerConnection` entity - `Uri`, `VirtualHost`, `CredentialRef`, `TlsSettings`, `Status`
- [ ] Connection health check
'@

    New-Issue 'M7.2' 'M7' 'Credential handling' @('area:governance','area:identity') @'
- [ ] Encryption at rest via ASP.NET Core Data Protection; disk/DB key ring self-hosted
- [ ] **Write-only over the API** - reads return `hasCredentials`, never the secret
'@

    New-Issue 'M7.3' 'M7' 'Contracts' @('area:governance') @'
The differentiator - the part Kafka has no equivalent for. DESIGN §4 Context B.

- [ ] `Contract` aggregate, environment-scoped
- [ ] `PublishBinding` - `TopologyScope + Exchange + RoutingKeyPattern -> SubjectRef[]`
- [ ] `ConsumeBinding` - `TopologyScope + Queue -> SubjectRef[]`
- [ ] `VersionSelector` - `Latest | Pinned(n) | Range(">=2")`
- [ ] `EnforcementMode` - `Off | Monitor | Enforce`
- [ ] Invariant: valid AMQP topic patterns; overlapping patterns cannot bind conflicting subjects without explicit precedence
- [ ] `POST /contracts/resolve` for SDK startup
'@

    New-Issue 'M7.4' 'M7' 'Governance' @('area:governance') @'
- [ ] `ServiceRegistration` - producer/consumer intent from the SDK at startup and the CLI in CI
- [ ] **Impact analysis** - "who breaks if I change this?"
- [ ] **Promotion** `dev -> staging -> prod`, re-checking compatibility in the target
- [ ] Audit log + `GET /v1/audit`
- [ ] Approval reviewers; **auto-dismiss a pending approval when the change is reverted**
'@

    New-Issue 'M7.5' 'M7' 'Notifications' @('area:governance') @'
- [ ] Outbox for domain events
- [ ] `INotificationChannel`: Email (SMTP) and Webhook
- [ ] Events: breaking change attempted/blocked, version registered, enforcement violation, subject deprecated
- [ ] Per-environment subscriptions
'@

    # ------------------------------------------------------------------ M8
    New-Issue 'M8.1' 'M8' 'Identity model' @('area:identity') @'
- [ ] `Tenant`, `User`, `Membership`, `Role`
- [ ] `ApiKey`, hashed at rest, with scopes
- [ ] Scopes: `subject:read|write|admin`, `contract:*`, `env:*`, `broker:*`, `org:admin`
- [ ] Local accounts; **OIDC optional** (ADR-008 - no third-party dependency required to run Signet)
'@

    New-Issue 'M8.2' 'M8' 'Authorization' @('area:identity') @'
ADR-018.

- [ ] `subject:write` and `subject:admin` granted to admin roles only; non-admin membership carries `subject:read`
- [ ] Every mutating subject/version endpoint checks scope server-side -> `403 insufficient_scope`
- [ ] Approve/reject is admin-only - keeps the author of a breaking change from waving it through
- [ ] Replace the M4.2 admin stub with real role resolution
'@

    New-Issue 'M8.3' 'M8' 'Tests' @('area:identity') @'
- [ ] Every mutating endpoint rejects a `subject:read` principal - for API keys and sessions alike
- [ ] The read surface stays fully available to non-admins
- [ ] E2E as a non-admin: no write affordance renders; direct URL to a write route redirects
'@

    # ------------------------------------------------------------------ M9
    New-Issue 'M9.1' 'M9' 'Tenancy' @('area:cloud') @'
- [ ] `SignetProfile` swaps `ITenantResolver`, `IBillingGate`, `IIdentityProvider` **at the composition root** - no `if (cloud)` scattered around
- [ ] Multi-tenant row-level isolation via the EF global query filters wired in M1.5
- [ ] KMS-backed Data Protection key ring
'@

    New-Issue 'M9.2' 'M9' 'Signup and identity' @('area:cloud','area:identity') @'
- [ ] Org signup
- [ ] Google / GitHub SSO
- [ ] SAML on the top tier
'@

    New-Issue 'M9.3' 'M9' 'Billing' @('area:cloud') @'
- [ ] Stripe: `Subscription`, `Plan`, `UsageMeter`, `Invoice`
- [ ] Metering: subjects, versions/month, API requests, environments, seats
- [ ] Tiers: Free (1 env, 10 subjects) -> Team -> Business -> Enterprise
- [ ] `BillingPage` in the web app
'@

    New-Issue 'M9.4' 'M9' 'Self-hosted parity' @('area:cloud','area:infra') @'
- [ ] Helm chart
- [ ] Verify the same image serves both profiles
'@
)

Write-Host "`n== Issues ($($issues.Count)) ==" -ForegroundColor Cyan
$existingIssues = if ($DryRun) { @() } else { (gh issue list --state all --limit 500 --json title --jq '.[].title') }

$created = 0; $skipped = 0
foreach ($i in $issues) {
    if ($existingIssues -contains $i.Title) {
        Write-Host "  = $($i.Title)" -ForegroundColor DarkGray
        $skipped++
        continue
    }
    Write-Host "  + $($i.Title)" -ForegroundColor Green
    if (-not $DryRun) {
        $planFile = switch ($i.Milestone) {
            'M0' { 'M0-foundations' }   'M1' { 'M1-registry-core' } 'M2' { 'M2-dotnet-client' }
            'M3' { 'M3-cli' }           'M4' { 'M4-web-app' }       'M5' { 'M5-formats' }
            'M6' { 'M6-sdks' }          'M7' { 'M7-governance' }    'M8' { 'M8-identity' }
            'M9' { 'M9-cloud' }
        }
        $body = "$($i.Body)`n`n---`nPlan: [``docs/plan/$planFile.md``]($planLink/$planFile.md) - Design: [``docs/DESIGN.md``](https://github.com/$repo/blob/main/docs/DESIGN.md)"
        $ghArgs = @('issue','create','--title',$i.Title,'--body',$body,'--milestone',$msNumbers[$i.Milestone])
        foreach ($lab in $i.Labels) { $ghArgs += @('--label',$lab) }
        gh @ghArgs | Out-Null
        $created++
    }
}

Write-Host "`nDone. Created $created, skipped $skipped." -ForegroundColor Cyan
if ($DryRun) { Write-Host "(dry run - nothing was actually created)" -ForegroundColor Yellow }
