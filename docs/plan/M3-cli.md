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
- [x] `impact` — **delivered in M7.4**, where registered consumers first exist. Originally It answers "who consumes this", and registered consumers
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

**Done 2026-08-13 · AOT verified by a real Linux build**

- [x] NativeAOT binaries: win-x64, linux-x64, linux-arm64, osx-arm64 — one runner per target
- [x] **One binary name, `concordat`** ([settled](../DECISIONS-PENDING.md#settled))
- [x] Docker image — `docker/cli.Dockerfile`, **8.8 MB**, no .NET runtime
- [x] GitHub Action wrapping the container — `action.yml`
- [x] Verify: a Python or Go shop can gate CI with **zero .NET installed**

### `RabbitMQ.Client` was not the AOT problem. My own code was.

M3.2 flagged RabbitMQ.Client as the likely blocker. **That was wrong, and worth correcting
plainly:** it produced no AOT warnings at all. Every one of the dozen `IL2026`/`IL3050`
warnings came from this project's JSON usage — anonymous types in `--json` output,
reflection-based `JsonContent.Create` and `ReadFromJsonAsync`, and generic `JsonArray.Add<T>`
in the inferrer.

That matters beyond bookkeeping: **the failure would have been silent.** Reflection-based
serialisation compiles, passes every JIT test, and then emits `{}` from a trimmed binary. A
pipeline parsing `--json` would have got an empty object and no error.

The fix was `JsonSerializerContext` source generation, which meant giving every `--json` shape
a real type. That is what the shape deserved anyway: it is a published interface under ADR-019
that other languages' pipelines parse, and it had been defined only by whichever anonymous
object happened to sit at the call site.

### The claims are asserted in CI, not stated

A `cli-container` job builds the image on every pull request and then checks three things a
JIT test cannot:

- **No .NET runtime in the image** — `command -v dotnet` must fail.
- **`--json` still has a payload under AOT** — the exact regression trimming would cause.
- **Exit codes survive containerisation** — 2 for a usage error and 3 for an unreachable
  registry, never 1.

### Content addressing holds across platform and compilation mode

The Linux AOT binary computes `f90f434be8410abb8d9c9e54e7aacc92` for the same schema the
Windows JIT build did. ADR-015 claims an id is reproducible offline in any implementation;
this is the first evidence across two platforms and two compilation modes.

### One runner per target, and every binary is executed

NativeAOT cross-compilation needs a matching cross-linker and sysroot, and getting it wrong
produces a binary that builds cleanly and refuses to start. A runner per architecture costs
four jobs and removes the failure mode entirely — and the release workflow runs `--help` on
each artifact before uploading it, because argument parsing is where a trimmed-away type
shows up.

### The Action passes configuration as environment, not arguments

A Docker action's `args` list is fixed: every entry is always passed. An empty optional input
would arrive as an empty-string argument and be rejected as an unknown token — exit 2 on a
step the user thought they had left unconfigured. Environment variables simply go unset, and
the CLI already reads `CONCORDAT_REGISTRY`, `CONCORDAT_ENV` and `CONCORDAT_API_KEY` natively.

It is a Docker action rather than a composite one because a composite would have to install a
.NET SDK on the runner — the exact dependency this milestone exists to remove.

## M3.4 Build-time packages

**Done 2026-08-13 · 22 tests + a worked sample verified end to end**

- [x] `Concordat.Contracts` — `[ConcordatContract("acme.orders.OrderCreated")]`
- [x] Roslyn generator: a schema per attributed type, diffed against checked-in `contracts/`, **error on drift**
- [x] `samples/ContractDrift` — the feature as a runnable example, gated in CI
- [x] `Concordat.Contracts.Testing` — built 2026-08-14 (decision 13). The deferral's reason
      stands and shaped it: it reads the generator's emitted schema and never derives one

### No MSBuild task. A generator instead.

The plan said "MSBuild task + Roslyn analyzer". It is only the analyzer, because the task would
have had to load the compiled assembly to reflect over it — resolving the consumer's entire
dependency graph inside the build process, and failing in ways that depend on their package set
rather than on anything Concordat controls. Reading Roslyn symbols needs nothing but the
compilation already in memory.

### Nullability *is* the requiredness contract

A non-nullable member is required; a nullable one is optional. No second annotation, because a
second annotation is a second thing to keep in sync and it falls out of sync immediately.

Worth stating plainly: **enabling nullable reference types on an existing project changes the
generated schema**, and the drift check will say so.

### The comparison is structural, and that is load-bearing

Byte-comparing the generated schema against the checked-in file would make this generator and
the CLI's canonicaliser two implementations of one format that must agree exactly — the
divergence this project spends most of its effort preventing. Parsing both and comparing shapes
means whitespace, key order and number spelling cannot cause a false failure. A test pins it:
a reformatted, reordered contract file is **not** drift.

The comparer carries its own hand-written JSON parser. An analyzer ships into the compiler's
own load context, where dragging in a JSON library invites a version conflict with whatever the
host already loaded — which the consumer sees as "the analyzer crashed", with nothing to act on.

### The message names the member, not the mechanism

The first version said *"the file has Array where the type produces String"* — accurate and
useless. It now shows the values:

```
error CDT003: 'ContractDrift.OrderCreated' has drifted from contracts/acme.orders.OrderCreated.json.
At #/properties/note/type: the file has ["string","null"], the type produces "string".
```

Which reads as *you removed a `?`*.

### Five diagnostics, and the one that matters most is a warning

| Id | Severity | Meaning |
|---|---|---|
| CDT001 | Error | The subject name breaks the ADR-011 grammar |
| CDT002 | Warning | A member has no JSON Schema mapping and is emitted unconstrained |
| CDT003 | **Error** | The checked-in contract has drifted from the type |
| CDT004 | **Warning** | The type has **no** contract file, so nothing is being checked |
| CDT005 | Error | Two types declare the same subject |

CDT004 exists because a drift check with nothing to compare against passes vacuously — green
build, unguarded contract. It is a warning rather than an error only because a type must be
allowed to exist before its contract is written.

### Roslyn 4.14, deliberately older than the repository uses

Central package management resolves one version repo-wide, and EF Core's Design package
requires Roslyn 5. The generator overrides *down* to 4.14 with `VersionOverride`, and the
direction matters: an analyzer built against a Roslyn **newer** than the host compiler fails to
load outright. A consumer on the .NET 8 SDK must be able to run a generator this repository
builds on .NET 10.

### `Concordat.Contracts.Testing` is deferred, on purpose

`ConcordatAssert.CompatibleAsync<T>(env: "prod")` needs the schema for `T` at test time. The
obvious implementation — reflect over the runtime type — would be **a second implementation of
the C#-to-JSON-Schema mapping**, and the two would drift apart exactly as this milestone's
whole subject warns.

The groundwork is in place: the generator already emits
`[assembly: ConcordatGeneratedSchema(subject, clrType, schema)]`, so the testing package can
read the compile-time schema rather than recomputing it. That is a small, correct package to
build next, and building it wrong would be worse than not having it.

---

## Exit

A breaking change to a C# record fails the build locally and fails CI via the GitHub
Action, both naming the exact JSON-Pointer path.

---

← [M2 — .NET client](M2-dotnet-client.md) · [Plan index](../PLAN.md) · [M4 — Web app →](M4-web-app.md)
