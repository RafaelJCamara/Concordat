# M0 — Foundations

**Depends on:** nothing · **Unlocks:** everything · **Design refs:** [§8](../DESIGN.md#8-backend-architecture-ddd--clean-architecture), decisions 003, 009, 021

---

## M0.1 Name availability 🔴 **blocking, do first**

- [ ] NuGet: `Signet`, `Signet.Client`, `Signet.Contracts`
- [ ] npm: the `@signet` scope
- [ ] PyPI: `signet-client`
- [ ] Go module path
- [ ] Maven groupId `dev.signet`
- [ ] Domain(s)
- [ ] If any are taken, decide branding **now** — renaming after M1 touches every artifact

> ADR-021 makes all five registries load-bearing. This is an afternoon of work that
> invalidates weeks if skipped.

## M0.2 Repository skeleton

- [ ] `Signet.slnx`, `global.json` pinning .NET SDK 10 (`net10.0`)
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
- [ ] Expand decisions 001–021 from the DESIGN.md table into `docs/adr/`, one file each

---

## Exit

CI green on an empty solution; ADRs merged; names secured or branding changed.

---

← [Plan index](../PLAN.md) · [M1 — Registry core →](M1-registry-core.md)
