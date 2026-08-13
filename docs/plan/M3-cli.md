# M3 — CLI, CI gate, and build-time packages

**Depends on:** [M1](M1-registry-core.md) (M2 for runtime bits) · **Design refs:** [§7](../DESIGN.md#7-contract-checks--cli-and-build-time), decisions 005, 014

Last milestone on the critical path. With M3 done, Signet is genuinely useful.

---

## M3.1 `signet` CLI

- [ ] `check --env <env> --dir ./contracts` — dry-run compatibility, **exit 1 on break**, offending JSON-Pointer path in output
- [ ] `push`, `promote`, `diff`, `impact`, `lint`, `export`
- [ ] `--json` output mode for scripting
- [ ] Documented exit codes

## M3.2 `signet infer` (ADR-014)

- [ ] File mode — infer from a corpus of sample payloads (**the default**)
- [ ] Queue mode — read-only drain via `basic.get` with requeue, or an exclusive consumer that nacks with requeue
- [ ] **Document that queue mode can reorder a live queue**
- [ ] Inference: types, required-by-presence across samples, `format` detection (uuid, date-time, email), low-cardinality enums, nullability
- [ ] Output is a draft **plus a confidence/ambiguity report** — never auto-registers

## M3.3 Distribution

- [ ] NativeAOT binaries: win-x64, linux-x64, linux-arm64, osx-arm64
- [ ] Docker image
- [ ] GitHub Action wrapping the container
- [ ] Verify: a Python or Go shop can gate CI with **zero .NET installed**

## M3.4 Build-time packages

- [ ] `Signet.Contracts` — `[SignetContract("acme.orders.OrderCreated")]`
- [ ] `Signet.Contracts.MSBuild` — MSBuild task + Roslyn analyzer; generate a schema per attributed type, diff against checked-in `contracts/`, **error on drift**
- [ ] `Signet.Contracts.Testing` — `await Signet.Assert.CompatibleAsync<OrderCreated>(env: "prod")`

---

## Exit

A breaking change to a C# record fails the build locally and fails CI via the GitHub
Action, both naming the exact JSON-Pointer path.

---

← [M2 — .NET client](M2-dotnet-client.md) · [Plan index](../PLAN.md) · [M4 — Web app →](M4-web-app.md)
