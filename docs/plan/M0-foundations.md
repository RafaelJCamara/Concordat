# M0 — Foundations

**Depends on:** nothing · **Unlocks:** everything · **Design refs:** [§8](../DESIGN.md#8-backend-architecture-ddd--clean-architecture), decisions 003, 009, 021

---

## M0.1 Name availability 🔴 **blocking — DONE 2026-08-13**

**Outcome: the project was renamed from Signet to Indenture** (ADR-022). This package did
its job — it found a blocker on day one instead of during M6.

- [x] NuGet: `indenture`, `indenture.client`, `indenture.contracts` — all free
- [x] PyPI: `indenture`, `indenture-client` — free
- [x] npm: `indenture` unscoped **and** the `@indenture` scope — free
- [x] Go module path — `github.com/RafaelJCamara/…`, owned
- [x] Maven groupId `io.github.rafaeljcamara` — needs no domain; Maven Central accepts it for GitHub projects
- [x] Domains — `indenture.io`, `indenture.sh`, `getindenture.dev` free (`indenture.dev` is taken)
- [x] Branding decided before any code was written

**Why Signet failed**, recorded so nobody proposes it again:

| Name | Status |
|---|---|
| NuGet `Signet.Client` | **taken** — v0.4.0, *"C# client for signet, generated from bytepunx/signet-proto"* |
| PyPI `signet-client` | **taken** — v0.3.0, same project, uploaded 2026-08-08 |
| NuGet `signet` | taken by **SigNET** (7,452 downloads; NuGet ids are case-insensitive) |
| npm `signet` / PyPI `signet` | taken |
| `signet.dev` | registered to a domain reseller |

An *active* project was publishing the exact two package names ADR-021 depends on, in the
same registries, for the same polyglot-client shape.

### Remaining — availability is not reservation

Free today, claimed by someone else tomorrow. These are cheap and worth doing before M1:

- [ ] Buy `indenture.io` (Problem Details `type` URIs point at it — see DESIGN §5)
- [ ] Create the `@indenture` npm org
- [ ] Note that NuGet/PyPI/Maven ids are only claimed on first publish (M2, M3, M6)

## M0.2 Repository skeleton

- [ ] `Indenture.slnx`, `global.json` pinning .NET SDK 10 (`net10.0`)
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
