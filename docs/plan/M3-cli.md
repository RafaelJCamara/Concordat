# M3 — CLI, CI gate, and build-time packages

**Depends on:** [M1](M1-registry-core.md) (M2 for runtime bits) · **Design refs:** [§7](../DESIGN.md#7-contract-checks--cli-and-build-time), decisions 005, 014

Last milestone on the critical path. With M3 done, Concordat is genuinely useful.

---

## M3.1 `concordat` CLI

**Done 2026-08-13 · DESIGN §7 · 19 tests**

- [x] `check --env <env> --dir ./contracts` — dry-run compatibility, **exit 1 on break**, offending JSON-Pointer path in output
- [x] `push`, `promote`, `diff`, `lint`, `export`
- [x] `--json` output mode for scripting
- [x] Documented exit codes, in `--help` as well as here
- [ ] `impact` — **deferred to M7.** It answers "who consumes this", and registered consumers
      do not exist until M7. A version that guessed from traffic would be worse than absent

`System.CommandLine` 2.0.11 (MIT, Microsoft, the parser the .NET SDK itself uses, AOT-friendly
for M3.3).

### Exit codes are the product here

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | **Contract violation** — the one CI gates on |
| 2 | Usage error |
| 3 | Registry unavailable |
| 4 | Local file error |
| 70 | Internal error |

**The split that matters is 1 against 3.** "Your change breaks the contract" and "the registry
is unreachable" must never share a code. If they do, a pipeline cannot tell a violation from an
outage, and the way that gets resolved in practice is somebody appending `|| true` — which
switches the gate off permanently and silently.

That nearly shipped broken: **`System.CommandLine` returns 1 for a parse error**, so
`concordat lint --jsno` reported a contract violation that never happened. Parse errors are now
intercepted before invocation and mapped to 2, and `ExitCodeTests` runs the real executable to
prove it, because an in-process test skips exactly the code that gets this wrong.

### The `./contracts` layout: file name is the subject, extension is the format

No manifest, no front-matter. A manifest is a second source of truth that can disagree with the
directory, and front-matter is impossible in JSON anyway. It also has to work for people who
will never run .NET (ADR-019): a Go shop writes `contracts/acme.orders.OrderCreated.json` from
its own tooling, and `ls` tells them what is registered.

This holds by construction rather than luck — the subject grammar (letters, digits, underscores,
dots) is a subset of what every filesystem allows.

### `check` gates, `push` records

A breaking change is **not** a push failure. Under ADR-017 it registers as `AwaitingApproval`
and does not move `latest` — a reviewable artifact, not an error. Making `push` fail too would
mean a deliberately-approved breaking change could never be recorded at all.

So the merge build runs `check` and the deploy build runs `push`.

### An empty contracts directory is a failure, not a pass

A gate that silently passes because it found nothing is worse than no gate: the pipeline is
green and nothing was verified. `check` exits 4 and says so.

### The CLI does not use `Concordat.Client`

That client caches schemas forever and the latest pointer for 30 seconds — right on the
delivery path, wrong for a CI gate. A build that passed because the CLI answered from a stale
cache is worse than no gate. Every call here goes to the registry.

### Three bugs the real-API tests caught

None of these would have failed against a mock, because a mock would have been written from the
same wrong assumptions:

- **`VersionResponse` carries no schema text**, only the id. `promote` and `export` both
  silently produced nulls. Both now fetch the document by id — the right split (the schema table
  is global and content-addressed, a version is per-environment) but it costs a second call.
- **The status token is `AWAITING_APPROVAL`, not `AwaitingApproval`.** `push` reported every
  gated version as a normal registration.
- **The subject list field is `latest`, not `latestOrdinal`.** It deserialised to null without
  error, so `export` skipped every subject as "no approved version" and wrote nothing.

### A limitation found and pinned, not hidden

**`diff` cannot show an added or removed property under an open content model** — the default.
The compatibility engine records a divergence only where one could affect compatibility, and
under an open model adding a property cannot. So the most common schema change produces two
different schema ids and an empty divergence list.

The first version of the command called that "a formatting change", which is plainly wrong.
It now says exactly what happened and why, and a test pins the behaviour so it cannot quietly
change. Whether the engine should record informational divergences is
[a decision for you](../DECISIONS-PENDING.md).

## M3.2 `concordat infer`

**Done 2026-08-13 · ADR-014 · 29 tests**

- [x] File mode — infer from a corpus of sample payloads (**the default**)
- [x] Queue mode — read-only drain via `basic.get` with requeue
- [x] **Queue mode reordering** — not documented, *enforced*: see below
- [x] Inference: types, required-by-presence, `format` (uuid, date-time, date, email), low-cardinality enums, nullability, nested objects and arrays
- [x] Output is a draft **plus a confidence/ambiguity report** — never auto-registers

### The reordering warning is a flag, not a paragraph

ADR-014 says to document that queue mode can reorder a live queue. Documentation is the wrong
instrument: nobody reads it before running a command, and the cost lands on production traffic.

So queue mode **refuses to run** without
`--i-understand-this-reorders-the-queue`. That turns a side effect somebody discovers into a
decision somebody took. Messages are requeued in a `finally`, after the loop, so every fetched
message goes back even if the drain is cancelled or throws part-way.

`DrainingLosesNothing` asserts the queue depth is unchanged afterwards. That claim is about
what the broker still holds, so it cannot be made against a mock.

### The report is the deliverable

A schema inferred from samples is a well-informed guess, and the guesses are what a reviewer
needs. Findings are ranked worst-first and each says what would happen if the assumption is
wrong — `required` inferred from presence, `integer` narrowed from whole numbers, an enum from
low cardinality, always-null fields, mixed types, empty arrays.

### Two bad inferences caught by running it on realistic data

Neither showed up in a unit test; both were obvious the moment real samples went through:

- **A single repeated value was becoming `enum: ["hello"]`.** Twenty identical samples are not
  a closed set, and that inference would reject the second value the field ever takes — in
  production, long after anyone remembers where the schema came from. An enum now needs at
  least two distinct values, ten observations, and threefold repetition; a constant value gets
  a finding instead.
- **An always-null field was `required` at medium confidence.** "Required, of unknown type" is
  a combination almost nobody means; it usually marks an optional field the samples never
  exercised. Now reported as low with an explanation.

### Deliberately not corpus-pinned

Canonicalisation and compatibility are protocol and every SDK must agree to the byte. Inference
is a drafting aid whose output a human reads and edits before anything is registered, so a
Python implementation guessing slightly differently costs nothing. Pinning it would freeze
heuristics that should be free to improve.

The draft still has to survive the real pipeline, though — one test canonicalises it, and
validates one of the original samples against the result.

### Cost

`RabbitMQ.Client` is now a CLI dependency, for queue mode only. **This is a risk for M3.3's
NativeAOT target** and is called out there.

## M3.3 Distribution

- [ ] NativeAOT binaries: win-x64, linux-x64, linux-arm64, osx-arm64
      — **check `RabbitMQ.Client` first.** M3.2 added it for queue mode, and it is the one
      dependency likely to resist trimming. If it does: publish AOT without queue mode and
      ship queue mode in the JIT/Docker build, rather than dropping AOT for everything
- [ ] **One binary name, `concordat`** ([settled](../DECISIONS-PENDING.md#settled)) — document a
      shell alias rather than shipping a second name into every packaging manifest
- [ ] Docker image
- [ ] GitHub Action wrapping the container
- [ ] Verify: a Python or Go shop can gate CI with **zero .NET installed**

## M3.4 Build-time packages

- [ ] `Concordat.Contracts` — `[ConcordatContract("acme.orders.OrderCreated")]`
- [ ] `Concordat.Contracts.MSBuild` — MSBuild task + Roslyn analyzer; generate a schema per attributed type, diff against checked-in `contracts/`, **error on drift**
- [ ] `Concordat.Contracts.Testing` — `await Concordat.Assert.CompatibleAsync<OrderCreated>(env: "prod")`

---

## Exit

A breaking change to a C# record fails the build locally and fails CI via the GitHub
Action, both naming the exact JSON-Pointer path.

---

← [M2 — .NET client](M2-dotnet-client.md) · [Plan index](../PLAN.md) · [M4 — Web app →](M4-web-app.md)
