# M0 — Foundations

**Depends on:** nothing · **Unlocks:** everything · **Design refs:** [§8](../DESIGN.md#8-backend-architecture-ddd--clean-architecture), decisions 003, 009, 021

---

## M0.1 Name availability 🔴 **blocking — DONE 2026-08-13**

**Outcome: the project was renamed from Signet to Concordat** (ADR-022). This package did
its job — it found a blocker on day one instead of during M6.

- [x] NuGet: `concordat`, `concordat.client`, `concordat.contracts` — all free
- [x] PyPI: `concordat`, `concordat-client` — free
- [x] npm: `concordat` unscoped **and** the `@concordat` scope — free
- [x] Go module path — `github.com/RafaelJCamara/…`, owned
- [x] Maven groupId `io.github.rafaeljcamara` — needs no domain; Maven Central accepts it for GitHub projects
- [x] Domains — `concordat.dev`, `concordat.io`, `concordat.sh` all free
- [x] Branding decided before any code was written

**Rejected names**, recorded so none of them resurface:

| Name | Why not |
|---|---|
| **Signet** | `Signet.Client` (NuGet) and `signet-client` (PyPI) are published by an **active** project — `bytepunx/signet-proto`, PyPI upload 2026-08-08 — the exact two names ADR-021 depends on. NuGet's `signet` id is also **SigNET** (7,452 downloads; ids are case-insensitive); `signet.dev` registered |
| **Hutch** | `ruby-amqp/hutch`, 878 stars: *"a system for processing messages from RabbitMQ."* Same ecosystem — strictly worse than Signet |
| **Syngraph / Chirograph** | Clean on every registry, but `-graph` reads as graph database or GraphQL to this audience |
| **Stipula** | Best metaphor of all of them, but `stipula-language/stipula` is a DSL for legal contracts |
| **Warrenty** | 160 GitHub repos, every one a zero-star misspelling of "warranty" |
| Indenture | Available, but nine characters, archaic, and `indenture.dev` was gone |

> **The lesson worth carrying:** package-registry availability is *not* the test —
> ecosystem collision is. Signet and Hutch were both free on NuGet and PyPI. What
> disqualified them only showed up on GitHub and RubyGems.

### Remaining — availability is not reservation

Free today, claimed by someone else tomorrow. These are cheap and worth doing before M1:

- [ ] Buy `concordat.dev` (Problem Details `type` URIs point at it — see DESIGN §5)
- [ ] Create the `@concordat` npm org
- [ ] Note that NuGet/PyPI/Maven ids are only claimed on first publish (M2, M3, M6)

## M0.2 Repository skeleton — **DONE 2026-08-13**

- [x] `Concordat.slnx` (12 projects), `global.json` pinning .NET 10 — `10.0.100` + `rollForward: latestFeature`, so any 10.0.x SDK works rather than only the one on this machine
- [x] `Directory.Build.props` — `net10.0`, nullable, implicit usings, **warnings-as-errors**, analyzers at `latest-recommended`, `EnforceCodeStyleInBuild`, deterministic output
- [x] `Directory.Packages.props` — central package management, transitive pinning on
- [x] `.editorconfig` — LF, file-scoped namespaces, `I`-prefixed interfaces, two analyzer rules explicitly downgraded with reasons
- [x] Folder structure per DESIGN §8 — see [`src/README.md`](../../src/README.md)
- [x] `LICENSE` (Apache-2.0 verbatim) and `NOTICE`

**Verified:** `dotnet build` 0 warnings 0 errors · `dotnet format --verify-no-changes` clean ·
`dotnet test` exit 0.

### Three deviations, each deliberate

1. **Only M1's 12 projects exist.** Creating `Cloud.Billing` or `Formats.Avro` now would
   add empty projects that build in CI for eight milestones before anyone opens them.
   Each is created by the milestone that first has something to put in it; the full
   intended layout is tabulated in `src/README.md` so nothing is lost.
2. **No per-file licence headers.** `LICENSE` + `NOTICE` satisfy Apache-2.0. Headers on
   every `.cs` are a maintenance tax most .NET OSS projects skip. Revisit only if a
   downstream consumer's compliance process requires them.
3. **`UnitTest1.cs` placeholders deleted.** `dotnet test` therefore prints "no test is
   available" per project until M1 adds real ones — it still **exits 0**, so CI is not
   affected. Committing tests named `Test1` that assert nothing is worse than the noise.

### Enforcement gap worth knowing

The DESIGN §8 dependency rule (Domain ← Application ← Infrastructure/Api) is wired
correctly in the project references but **enforced only by review**. If it drifts, add
NetArchTest assertions to `Concordat.Domain.Tests` — noted in `src/README.md`.

## M0.3 CI — **DONE 2026-08-13**

`.github/workflows/ci.yml`, one job, eight steps.

- [x] Build + test — triggers on pull request, push to `main`, and manual dispatch
- [x] Format gate — `dotnet format --verify-no-changes`, run **before** build so a
      formatting failure reports in seconds instead of after a full compile
- [x] Analyzer gate — no separate step needed; `TreatWarningsAsErrors` in
      `Directory.Build.props` means analyzer violations already fail the build
- [x] Coverage — collected via coverlet and uploaded as an artifact, non-blocking
- [x] NuGet cache keyed on `Directory.Packages.props`, the one file that owns every
      version, so the cache invalidates exactly when dependencies change
- [x] `permissions: contents: read`, and `concurrency` cancels superseded runs

**Verified locally:** the exact Release-configuration command sequence CI runs — restore,
format, build, test — all exit 0 and produce `test-results.trx` plus five
`coverage.cobertura.xml` files. The workflow YAML parses.

### Decisions taken

- **Ubuntu only**, not a three-OS matrix. Development is on Windows and deployment is
  Linux containers, but there is nothing platform-specific to catch in a solution with no
  code. **Add `windows-latest` at M2**, where Testcontainers and the RabbitMQ integration
  tests are the first things that can genuinely differ by platform.
- **No external coverage service.** Codecov means an account and a token to rotate before
  there is any coverage worth reading. Revisit at [M1.3](M1-registry-core.md#m13-compatibility-engine--adr-016-design-7),
  where the compatibility corpus makes the number meaningful.

> **Not yet observed running on GitHub.** With these triggers, pushing a feature branch
> does *not* start CI — it fires on pull requests and on `main`. The first real run
> happens when this branch is merged or a PR is opened.

### Later additions, noted not built

- OpenAPI drift check (M1.6) — CI must fail when the generated spec differs from the
  committed one
- Testcontainers services for PostgreSQL and RabbitMQ (M1.5, M2.6)
- Dependabot for `nuget` and `github-actions`
- A markdown link check — the docs already carry ~60 cross-references

## M0.4 ADRs — **DONE 2026-08-13**

- [x] [`docs/adr/TEMPLATE.md`](../adr/TEMPLATE.md) — Context / Decision / Alternatives / Consequences / References
- [x] [`docs/adr/README.md`](../adr/README.md) — index of all 22, with status
- [x] All 22 decisions expanded, one file each

**The ADRs are canonical; the DESIGN.md table is a digest that links to them.** Two sources
of truth would drift, so the relationship is stated in both places.

Each record carries what a table cannot: **alternatives rejected with the specific reason**,
and **consequences including the negative ones**. An ADR with only upside is not finished —
[ADR-005](../adr/005-enforcement-location.md) states plainly that enforcement is opt-in and
Concordat cannot stop a publisher that skips the SDK; [ADR-013](../adr/013-amqp-091-only.md)
states that its 1.0-safety claim is currently an assertion, not a verified property.

The rejected-alternatives sections exist so settled questions stay settled.
[ADR-022](../adr/022-project-name-concordat.md) is the clearest case: it records why Signet,
Hutch, Syngraph, Stipula, Warrenty and Indenture were each rejected, so "why not Signet?"
is answered once rather than every few months.

---

## Exit

CI green on an empty solution; ADRs merged; names secured or branding changed.

---

← [Plan index](../PLAN.md) · [M1 — Registry core →](M1-registry-core.md)
