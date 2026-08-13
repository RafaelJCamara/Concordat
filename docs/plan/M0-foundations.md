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

## M0.2 Repository skeleton

- [ ] `Concordat.slnx`, `global.json` pinning .NET SDK 10 (`net10.0`)
- [ ] `Directory.Build.props` — shared TFM, nullable, warnings-as-errors, deterministic builds
- [ ] `Directory.Packages.props` — central package management
- [ ] `.editorconfig`, analyzer ruleset
- [ ] Folder structure per DESIGN §8 (`src/core`, `src/formats`, `src/hosts`, `src/clients`, `tools`, `clients`, `web`, `deploy`, `tests`)
- [ ] `LICENSE` (Apache-2.0), `NOTICE`, license headers

## M0.3 CI

- [ ] Build + test on PR, matrix over supported OS
- [ ] Format and analyzer gate
- [ ] Coverage reporting, non-blocking initially

## M0.4 ADRs

- [ ] ADR template
- [ ] Expand decisions 001–022 from the DESIGN.md table into `docs/adr/`, one file each

---

## Exit

CI green on an empty solution; ADRs merged; names secured or branding changed.

---

← [Plan index](../PLAN.md) · [M1 — Registry core →](M1-registry-core.md)
