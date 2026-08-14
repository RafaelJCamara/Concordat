# Decisions pending

Everything waiting on you, in one place. Sections are ordered by when the decision starts to
hurt; the numbers are stable labels, not positions, so a later-numbered item can appear first.

Once a decision is made it moves to **[Settled](#settled)** at the bottom and, if it is
architectural, becomes an ADR in [`adr/`](adr/README.md).

---

## Proceeded on my judgement — confirm or overturn

### 17. Avro's Parsing Canonical Form is lossy, and the architecture stores the canonical form

**Option (B) below is implemented and shipped.** M5.2 was blocked on this and you were not
available to decide, so I took the call rather than stall the milestone — and this entry stays
open because it is yours, not mine. **Overturning it is free until the first Avro schema is
stored**, and costs a preimage version bump plus a migration afterwards. It is an ADR-015
amendment if you keep it.

Avro's Parsing Canonical Form is defined by the specification's `[STRIP]` rule: keep only
`type`, `name`, `fields`, `symbols`, `items`, `values` and `size`, and drop everything else.
That deliberately discards **`default` and `aliases`** (and `doc`). DESIGN §4 names PCF as
Avro's canonical form, and `Concordat.Formats.Avro` implements it exactly.

The architecture, settled in M1 when JSON Schema was the only format, stores *the canonical
form*: `CompatibilityEvaluator` canonicalises, then hands the canonical text to
`Schema.Create` as the stored body **and** to `ICompatibilityChecker.Check`, and
`RegisterVersionHandler` builds each `PriorSchema` from that same stored body. The authored
body is never persisted.

For JSON Schema this was invisible, because its canonicalisation is lossless — sort keys,
strip whitespace, normalise `$id`/`$ref`. **Avro is the first format whose canonicalisation is
lossy by design**, and the two attributes it drops are exactly the ones Avro's schema
resolution runs on:

- a reader whose schema has a field the writer lacks **uses that field's `default`**, and
  **signals an error if there is none** — this is what makes "add a field" the safe, ordinary
  Avro change;
- `aliases` on the reader's schema are what let a renamed record or field still resolve.

Two consequences, and the second is worse than the first:

1. **The remaining M5.2 checklist cannot be built correctly.** "Resolution rules: defaults,
   aliases, union widening, enum symbols" — two of those four inputs are gone before the
   checker is ever called. A checker working from PCF has to report every added field as
   backward-breaking, including the ones carrying a default, which is precisely the
   unusable-under-its-own-defaults behaviour Concordat exists to beat Confluent at.
2. **The registry cannot serve a usable Avro schema.** A consumer fetching a schema by id gets
   a body with no defaults, so it cannot read data written under an older version — the one
   job an Avro reader schema exists to do. That is data loss at registration time, not a
   reporting gap.

Underneath both sits an identity question: under PCF a schema **with** a field default and the
same schema **without** one hash to the same id, yet they resolve differently against the same
bytes. Content addressing is supposed to mean *same id ⇒ same meaning*, and for Avro under PCF
it does not.

> **Options:**
>
> - **(A) Store the authored body; canonicalise only to compute the id.** What Confluent does.
>   Fixes serving and checking together, and Avro ids stay exactly as they are today. It does
>   **not** fix the identity question: two bodies differing only in a default still collide on
>   one id, so the registry has to pick one and can hand back defaults the author never wrote.
>   Changes the meaning of `Schema.Body` and the contract of `PriorSchema`, both shared with
>   JSON, and needs a migration.
>
> - **(B) Make Avro canonicalisation lossless-enough — normalise (sort, resolve fullnames,
>   strip whitespace and `doc`) but keep `default` and `aliases`.** One transformation, one
>   stored body, no second column, no collision rule, and the checker gets what it needs.
>   Schemas that resolve differently get different ids, which is what content addressing should
>   mean. The cost is that Concordat's Avro canonical form is no longer the spec's PCF — but
>   Concordat ids were never Avro fingerprints anyway (128-bit truncated SHA-256 over a
>   versioned preimage, versus Avro's 64-bit CRC), so nothing that interoperates today stops.
>
> - **(C) Keep PCF and refuse Avro schemas that use `default` or `aliases`.** Nothing
>   architectural changes; registration rejects them with a clear error, the way M2.3 refused
>   generic type names rather than invent a spelling. But it rules out the ordinary Avro
>   evolution pattern, which leaves Avro support close to decorative.
>
> **Chosen: (B), and built.** It is the only option that makes all three things true at once —
> the checker implements the real resolution rules, the registry serves a schema consumers can
> actually read with, and equal ids mean equal meaning. It was also far cheapest to do
> immediately: no Avro schema has been registered yet (registration still throws
> `NotSupportedException` for Avro, since the reference extractor does not exist), so there was
> no migration and no id churn.
>
> **What shipped:** `doc` is the only attribute stripped. `default`, `aliases`, `logicalType`,
> `order` and any future attribute survive, normalised. For a schema that uses none of the
> attributes PCF would have stripped, the output is byte-identical to PCF, and
> `MatchesParsingCanonicalForm_WhenNothingSemanticWouldBeStripped` pins that so the deviation
> stays confined to where PCF loses information.
>
> **What to weigh if you overturn it:** the cost of (B) is that Concordat's Avro canonical form
> is not the spec's PCF, so anyone comparing Concordat's canonical text against another tool's
> PCF output sees a difference for schemas using defaults or aliases. Schema *ids* were never
> comparable across tools anyway — Concordat uses a 128-bit truncated SHA-256 over a versioned
> preimage, Avro uses a 64-bit CRC — so nothing that interoperates today is affected.

---

## Blocking nothing today, but getting more expensive

### 1. Rename the GitHub repository `Signet` → `Concordat`

The project was renamed in ADR-022 but the repository was not. `Directory.Build.props`
hardcodes `RepositoryUrl` and `PackageProjectUrl` as `github.com/RafaelJCamara/Signet`, and
**those strings get baked into every NuGet package from M2 onward**.

GitHub redirects the old URL, so the change is low-risk. It needs you because it is your
account — or say the word and I'll do it via the API once `gh` is authenticated.

> **Cost of waiting:** after M2 publishes, the wrong URL is in released package metadata.

### 2. What to do with the `docs/design-and-plan` branch

It now carries the entire solution, so the name is inaccurate, and `main` is still the initial
commit. **CI has never run** — with the triggers you chose it fires on pull requests and on
`main`, not on feature-branch pushes.

**Recommendation:** merge to `main`. You are one person; the branch buys nothing and it is the
only thing standing between you and a green CI badge.

### 3. Reserve the names that are still unreserved

Availability is not reservation. From M0.1:

- Buy **`concordat.dev`** — Problem Details `type` URIs point at it, first used in M1.6
- Create the **`@concordat`** npm organisation
- NuGet, PyPI and Maven ids are only claimed on first publish (M2, M3, M6)

---

## Needed before a specific milestone

### 4. External coverage reporting — now live

M0.3 deferred this to "when M1.3 makes the number meaningful". **M1.3 is done**, so it is
decidable: wire up Codecov (needs an account and a token to rotate), or keep the current
coverage artifact and look at it manually.

**Recommendation:** keep the artifact for now. A coverage percentage on a solo project mostly
generates noise; the useful signal is whether the compatibility corpus grows, which a number
does not capture.

### 5. Windows in the CI matrix — now decidable, and now cheaper to skip

M0.3 shipped Ubuntu-only with a note to reconsider at M2, where Testcontainers and the RabbitMQ
tests first make platform differences real. **M2.5 is that point**, and it changed the sum:
the suite raises up to three Linux broker containers, which on `windows-latest` needs
Linux-container support that GitHub's Windows runners do not provide.

**Recommendation:** stay Ubuntu-only. Add Windows later as a *build-and-unit-test-only* job if
you want the platform signal — the container suites cannot follow it there regardless.

### 6. When should the header-survival suite run?

It raises three brokers (two of them for federation alone) and takes ~15 s locally, more on a
cold runner pulling `rabbitmq:4.1-management`. It also almost never changes: it re-measures
broker behaviour, not our code.

**Recommendation:** run it on pull requests anyway, at least until v1. It is the only thing
standing between DESIGN §2 and quiet fiction, and a broker upgrade landing unnoticed is exactly
the scenario it exists to catch. Revisit if CI time becomes a real cost.

### 8. Semver pre-release support

M1.1 rejects `2.0.0-rc.1` with a dedicated code. A team whose pipeline emits pre-release labels
**cannot label a version at all** until this lands. Known gap, not a bug — but if your own
pipeline does that, it moves up.

### 10. Generic message types are unsupported — before v1 ships

M2.3 **refuses** a generic type name rather than inventing a spelling for it, because any
spelling becomes a rule five SDKs must reproduce character for character, and Go and Python
have no CLR generic syntax to reproduce it from.

That is right for the protocol and a hard stop for anyone whose publishers send
`Envelope<OrderCreated>`. Raw RabbitMQ.Client publishers rarely do, which is why this is a
gap rather than a blocker under ADR-020 — but if **your** code does, it moves up.

> **Options:** ship refusing them; require an explicit subject for generic types; or define a
> normative spelling in the corpus and make every SDK implement it.

### 11. Nested and top-level types collide — confirm the trade

`+` → `.` (DESIGN §3) makes `Acme.Orders+OrderCreated` and a top-level
`Acme.Orders.OrderCreated` **the same subject**, silently. The alternative was refusing nested
types outright.

This follows the design as written, and the consequence may not have been visible when it was
written. **Recommendation:** keep it. The collision needs two types whose names collide *after*
the rewrite, which is rare and immediately visible in the registry's subject list; refusing all
nested types would hurt far more publishers.

### 12. `diff` is blind to added properties under an open content model

Found while building M3.1. The compatibility engine records a divergence only where one could
affect compatibility. Under an **open** content model — the default — adding or removing a
property cannot, so it produces no finding. `concordat diff v1 v2` therefore shows two
different schema ids and an empty list for the single most common schema change there is.

`check` is unaffected and correct: the change genuinely is compatible. It is `diff` that
disappoints, because a human reading it wants to know what changed, not only what broke.

> **Options:** leave it and rely on `git diff` of the schema files (what the CLI now tells you
> to do); or have the engine record informational divergences with no surface, which changes
> the meaning of `allDivergences[]` and touches the M1.3 corpus.
>
> **Recommendation:** leave it for v1. The CLI says so explicitly, and widening
> `allDivergences[]` risks the corpus for a reporting nicety.

### 13. `Concordat.Contracts.Testing` — deferred out of M3.4, decide whether v1 needs it

`await ConcordatAssert.CompatibleAsync<OrderCreated>(env: "prod")` was in the M3.4 list and is
not built. The reason is not effort: the obvious implementation reflects over the runtime type
to produce a schema, which would be **a second implementation of the C#-to-JSON-Schema
mapping** alongside the generator's, and the two would drift — the exact failure this milestone
exists to prevent.

The groundwork is done. The generator emits
`[assembly: ConcordatGeneratedSchema(subject, clrType, schema)]`, so the package can read the
compile-time schema instead of recomputing it, and only needs to POST it to the compatibility
endpoint.

> **Recommendation:** build it, small, before v1 — the compile-time check catches drift from
> the *file*, but only a call to the registry catches drift from what is deployed in `prod`.
> Those are different questions and a team needs both.

### 14. The quarantine exchange is declared by the application — confirm

`ConcordatRabbitMqOptions.DeclareQuarantineExchange` defaults to **on**, so the middleware
declares `concordat.quarantine` itself the first time it needs it. The alternative is that the
first quarantine in production fails on a missing exchange — the worst possible moment to
discover a topology gap.

That assumes applications hold `exchange.declare` rights. In estates where topology is owned by
infrastructure-as-code and applications deliberately cannot declare, this must be turned off
and the exchange provisioned ahead of time.

> **Which is your estate?** It changes the recommended default in the deployment docs, not the
> code.

### 19. The envelope spec describes payload framing that does not exist

Found while writing [`docs/protocol/envelope.md`](protocol/envelope.md) — the first time anyone
tried to write the envelope down completely enough to implement from.

ADR-010 and DESIGN §2 both describe **payload framing** as part of the envelope: a
`0x01 | <16-byte id>` prefix Concordat writes, and a read-only `0x00 | <int32 big-endian>`
Confluent-compatible form. **Nothing in `src/` implements either.** The same is true of the
CloudEvents read support DESIGN §2 describes.

The spec now says plainly that they are unimplemented, so nobody builds an SDK from the prose
and finds nothing to interoperate with. But that leaves a normative document and an ADR
describing behaviour the product does not have.

> **Options:** implement framing in v1 — it is the only way to carry identity where headers do
> not survive, which is the whole Mode A/Mode B distinction; or amend ADR-010 to scope it out
> and say what the answer is for brokers that drop headers.
>
> **Recommendation:** amend for now. Every transport measured in M2.5 preserved
> `concordat-*` headers, so framing has no demonstrated need yet — and an unimplemented
> paragraph in a normative document is worse than an honest gap.

### 20. Mode A and Mode B disagree about whitespace and invalid types

Also found writing the envelope spec, and it reads as an implementation inconsistency rather
than a rule anyone chose:

- A padded `properties.type` (`"  acme.X  "`) is **warned about and ignored** on the Mode A
  path, but **trimmed and accepted** on the Mode B path, because `SubjectName.Create` trims.
- An invalid `properties.type` is **warned about** under Mode A and **silently dropped** under
  Mode B.
- A padded `concordat-semver` is accepted for the same reason, which contradicts the reader's
  own stated no-trim rule.

Two paths reaching different verdicts on the same bytes is exactly the class of divergence the
conformance corpus exists to prevent, and no fixture covers it — which is why it survived.

> **Recommendation:** make Mode B match Mode A (warn, do not trim). Trimming is the more
> forgiving behaviour, but it means a subject name's identity depends on which envelope mode a
> publisher happened to use. Whichever wins, it needs corpus fixtures in the same change.

> **Related and smaller:** `envelope_format_mismatch` is in the published `concordatCode`
> catalogue and **nothing emits it**. Either the check it was written for is missing, or the
> code should go.

### 21. Two contracts in one environment can govern the same route, and the first one wins

M7.3 enforces the overlap invariant **within** a contract: two publish bindings that intersect
and carry different subjects are refused unless precedence separates them. Across contracts
there is no such check. Nothing stops `orders-v1` and `orders-legacy` in the same environment
from both binding `orders.created` to different subjects, and `POST /contracts/resolve` answers
with whichever contract sorts first by name.

That is the arbitrary outcome the within-contract invariant exists to prevent, one level up.
It is not a defect in what was built — cross-contract checking was never specified — but the
guarantee is weaker than the DESIGN §4 wording suggests, and a publisher cannot tell.

> **Options:** (a) extend the invariant across the environment, so adding a binding checks
> every contract — correct, but makes contract authoring a global operation and needs a story
> for concurrent writers; (b) keep it per-contract and make resolve return **all** matching
> contracts, letting the SDK refuse on ambiguity — honest, and pushes the decision to where the
> topology is actually known; (c) leave it, and document that overlapping contracts are the
> author's problem.
>
> **Recommendation:** (b). It surfaces the ambiguity at the moment it matters without turning
> every binding write into an environment-wide lock, and the response shape is a superset of
> today's — the field is already `contract`, it would become `contracts`.

**Until this is decided, M7.4's impact analysis inherits the ambiguity**: "who breaks if I
change this subject" is answered from bindings, and a route governed twice will be attributed
to one contract.

### 22. Contract names take anything up to 128 characters

`Subject`, `Environment` and broker names all validate against a grammar. `Contract.Create`
checks only that the name is non-empty and ≤128 characters, so `my contract!! (draft/2)` is a
legal contract name today. Contracts are addressed in URLs
(`/v1/environments/{env}/contracts/{contract}`), which makes that a real interoperability
question rather than a cosmetic one — a name with a `/` or a `%` in it is not reliably
addressable.

> **Recommendation:** apply the same grammar environments use (lowercase, digits, `-`, `_`,
> `.`), before any contract exists to migrate. I did not do it unprompted because a contract is
> a human-facing governance artefact where `Orders — EU` is a reasonable thing to want to
> write, unlike a subject name, which is a wire identifier.

### 15. Hard-delete semantics — before v1 ships

M1.5 implements soft delete (`Subject.Retire()`) and never deletes schemas, which is what
ADR-015 requires. **Hard delete is not implemented**: DESIGN §4 wants "no registered
consumers, an explicit force flag, and an audit entry", and both registered consumers and the
audit log are M7. Confirm that v1 can ship with retire-only, or pull the pieces forward.

---

## Commitments that must not be forgotten

Not decisions — obligations already incurred that land in a later milestone. Each is written
into the milestone that owes it, and collected here because they are the ones that get lost.

| Owed by | Commitment | Why it matters |
|---|---|---|
| ~~M1.6~~ ✅ | ~~Authorise `GET /schemas/{id}` by reachability~~ | **Discharged.** `GetSchemaHandler`, four integration tests including refusal-equals-absence |
| ~~M1.6~~ ✅ | ~~One constructor for `CompatibilityVerdict`, proven by a recording fake~~ | **Discharged.** `ICompatibilityEvaluator` + `Evaluate_AlwaysConsultsTheChecker` |
| **M1.6** | Bundling, deferred from M1.4 | Cannot live at registration: it would make canonicalisation depend on registry state and stop any SDK reproducing an id offline |
| **M1.6** | Subject prefix search needs a `ComplexProperty` mapping or a shadow column | Value converters do not translate `StartsWith` |
| ~~M1.7~~ ✅ | ~~Pin the schema-id preimage bytes, not just ids~~ | **Discharged.** 4 fixtures pin the exact framing; all matched hand-written expectations first run |
| ~~M2~~ ✅ | ~~Run the payload-validation fixtures against a real validator~~ | **Discharged.** NJsonSchema behind `IPayloadValidator`; the corpus caught a real draft-conformance gap on its first run |
| **M7** | Contract-resolution caching, deferred from M2.1 | The 60 s TTL was specified before contracts existed; there is no endpoint to cache until M7 ships one |
| ~~M2.5~~ ✅ | ~~Verify the AMQP 1.0 header conversion~~ | **Discharged.** Measured on `rabbitmq:4.1-management`: `concordat-*` arrive as application-properties, and an `x-`-prefixed control header on the same message is demoted to an annotation, so the prefix rule is load-bearing rather than precautionary |
| **M7** | Hard delete: no registered consumers + force flag + audit entry | Soft delete is all that exists today |
| **M7** | Adopt the derived environment ids, or migrate `subject.environment_id` | `DerivedEnvironmentResolver` hashes the name to a stable id so `/environments/{env}/…` works before environments exist. Real rows will generate their own |
| **M7** | `GET\|PUT …/registration-policy` | Per-environment, and the `Environment` aggregate does not exist yet, so there is nowhere to store it |
| **After M1.5** | Any change to canonicalisation now needs a **preimage version bump and a migration** | Schemas are persisted from here on. The golden id test exists to make such a change impossible to miss |
| **Maintenance** | Drop the `SSH.NET` pin when Testcontainers requires a patched version itself | A stale forward-pin eventually holds a dependency *back* |

---

## Taken on your behalf — object if any is wrong

Reversible, recorded where they were made, listed here so none of them is a surprise later.

| Decision | Where | Reverse cost |
|---|---|---|
| `VersionStatus` gained `Rejected` | [M1.1](plan/M1-registry-core.md), DESIGN §4 | Low, pre-persistence |
| `Result<T>` lives in Domain, not Application | M1.1, DESIGN §8 | Low |
| `Subject.CompatibilityPolicy` nullable = inherit | M1.1 | **Cannot be retrofitted** |
| `SubjectId` and `EnvironmentId` added ahead of M7 | M1.1 | Low now, high after M1.5 |
| Registration idempotent at the tip | M1.1 | Low |
| `Deprecated` accepts versions, `Retired` does not | M1.1 | Low |
| Approval never regresses `LatestPointer` | M1.1 | Low |
| Number literals preserved verbatim, not RFC 8785 | [M1.2](plan/M1-registry-core.md) | **Changes every id** |
| Duplicate JSON keys rejected outright | M1.2 | Low |
| Schema body ceiling of 512 KiB | M1.2 | Low |
| `ContentModel` defaults to `Open` per subject | M1.3 | Low |
| `pattern` changes always read as narrowing | M1.3 | Low |
| Bundling deferred to M1.6, not stored | [M1.4](plan/M1-registry-core.md) | Low |
| No per-file licence headers | [M0.2](plan/M0-foundations.md) | Low |
| Migration runs as a separate process, **not** auto-migrate on startup | [M1.5](plan/M1-registry-core.md) | Low |
| snake_case column names, set explicitly per property | M1.5 | **Renames every column** |
| `SemanticVersion` stored as `MAJOR.MINOR.PATCH` text, not three columns | M1.5 | Low, needs a migration |
| Enums stored as text via `WireTokens`, never `Enum.ToString()` | M1.5 | Low |
| `SSH.NET` pinned forward past a security advisory | M1.5 | Low |
| PostgreSQL image pinned to `17-alpine` in tests | M1.5 | Low |
| Rejected versions are excluded from the compatibility history | [M1.6](plan/M1-registry-core.md) | Low |
| A subject must exist before a version can be registered — no implicit creation | M1.6 | Low, but it is user-visible REST behaviour |
| Schema refusal returns `schema_not_found`, never a distinct "forbidden" | M1.6 | Low |
| Re-registering the tip returns **200**, not 201, since nothing was created | M1.6 | Low, but user-visible |
| A breaking change returns **201**, not 409 — it is a reviewable artifact | M1.6 | Low, but user-visible |
| An unmapped `concordatCode` falls through to HTTP 400 | M1.6 | Low |
| `/health/live` and `/health/ready` are separate, and liveness ignores the database | M1.6 | Low |
| Environment ids are derived by hashing the name until M7 exists | M1.6 | **Needs an M7 migration** |
| With no prior semver label, a breaking change suggests `1.0.0` — an unlabelled history reads as pre-1.0 | M1.6 | Low |
| A 4xx sets `LastFailure` but **not** `IsDegraded`; only 5xx/408/429 count as unreachable | [M2.1](plan/M2-dotnet-client.md) | Low, but it changes what an alert fires on |
| `ResolutionFailures` counts every unenforced *operation*, not distinct causes | M2.1 | Low, but it changes the shape of the metric |
| Fail-closed throws `ConcordatException`; quarantine behaviour is M2.4's, not the client's | M2.1 | Low |
| `SchemaUnresolvable` added to `ConcordatCodes` — a client-raised code in a domain catalogue, because ADR-019 makes the strings normative for every SDK | M2.1 | Low |
| `ConcordatClient` does not dispose the `HttpClient` it is handed | M2.1 | Low |
| `ISubjectResolver` is transport-neutral and lives in the Domain; no `System.Type` on the context | [M2.3](plan/M2-dotnet-client.md) | Low, and it is what keeps .NET's type system out of subject names |
| The separator list is **closed** — `+` and `:` normalise, `/` does not | M2.3 | Low now, **breaks existing subjects** once anyone publishes |
| Subject case is preserved, never folded | M2.3 | **Would merge existing subjects** |
| Hyphens are refused rather than rewritten to underscores | M2.3 | Low |
| The publish side trims surrounding whitespace; envelope reading still does not | M2.3 | Low |
| M2.5 ships a **code** deliverable — findings as assertions — against a brief that said there would be none | [M2.5](plan/M2-dotnet-client.md) | Low. A written report is true on the day it is written and silently becomes fiction at the next broker upgrade |
| The broker image is pinned to `rabbitmq:4.1-management`; findings are stated as true *of that version* | M2.5 | Low, but the pin must be bumped deliberately, not floated |
| Four test-only dependencies added (RabbitMQ.Client, Testcontainers, AMQPNetLite, MQTTnet) | M2.5 | Low; all licence-checked, none reaches a shipped package |
| `ConcordatChannel` is a **full 63-member `IChannel` decorator**, not an extension method | [M2.4](plan/M2-dotnet-client.md) | Low, but the whole point: an opt-in publish method leaves every other `BasicPublishAsync` unenforced |
| `EnforcementMode.Monitor` is the **default**, not `Enforce` | M2.4 | Low, but user-visible. Defaulting to Enforce would let a package reference start rejecting production traffic |
| The envelope is stamped even when the payload is invalid, provided identity resolved | M2.4 | Low. It is what lets consumers read schema ids before publishers are clean |
| When quarantine itself fails, the message is **delivered** to the application, not dropped or requeued | M2.4 | Low, but it means an application can receive a message Concordat knows is invalid |
| An unexpected middleware exception fails **open** | M2.4 | Low |
| Quarantine keeps the original routing key, so operators can bind selectively | M2.4 | Low |
| A format with no registered validator is treated as valid, not invalid | M2.4 | Low, until Avro/Protobuf validators land |
| Quarantine detail is truncated at 4 KiB | M2.4 | Low |
| `./contracts` layout is `<subject>.<ext>` — no manifest, no front-matter | [M3.1](plan/M3-cli.md) | **User-visible convention.** Changing it breaks everyone's repo layout |
| Parse errors are intercepted and mapped to exit **2**, overriding `System.CommandLine`'s default of 1 | M3.1 | Low, and it is what keeps a typo from reading as a contract violation |
| `push` exits **0** on a breaking change; only `check` gates | M3.1 | Low, but it defines how pipelines are wired |
| An empty contracts directory is exit 4, not a vacuous pass | M3.1 | Low |
| `promote` moves one subject, never a whole environment | M3.1 | Low. Bulk promotion reads as atomic and is not |
| `impact` deferred to M7, where registered consumers first exist | M3.1 | Low |
| The CLI talks to the registry directly rather than through the caching `Concordat.Client` | M3.1 | Low, and required: a gate must never answer from cache |
| `Concordat.Cli.Tests` references `Concordat.Api.IntegrationTests` to reuse its harness | M3.1 | Low; extract a shared test-support project if a third consumer appears |
| Queue mode **refuses to run** without `--i-understand-this-reorders-the-queue`, rather than printing a warning | [M3.2](plan/M3-cli.md) | Low, and user-visible. ADR-014 asked for documentation; nobody reads it before running a command |
| A single repeated value is **not** inferred as an enum — needs ≥2 distinct, ≥10 observations, ≥3× repetition | M3.2 | Low |
| Whole numbers narrow to `integer` rather than staying `number` | M3.2 | Low, and reported on every occurrence |
| `format` is inferred (uuid, date-time, date, email); `additionalProperties` never is | M3.2 | Low |
| Inference is **not** corpus-pinned, unlike canonicalisation and compatibility | M3.2 | Low. It is a drafting aid a human edits, not protocol |
| `RabbitMQ.Client` is now a CLI dependency, for queue mode | M3.2 | ~~Possible NativeAOT problem~~ — **resolved in M3.3: it produced no AOT warnings at all.** The warnings were all from our own JSON usage |
| Every `--json` shape has a concrete type and source-generated serialisation | [M3.3](plan/M3-cli.md) | Low, and forced by AOT. Reflection-based output would silently emit `{}` from a trimmed binary |
| The container base is `runtime-deps` (native prerequisites, no .NET runtime) | M3.3 | Low |
| One release runner per architecture instead of cross-compiling | M3.3 | Low; four jobs, but a mis-linked AOT binary builds cleanly and refuses to start |
| The GitHub Action passes config as environment variables, not `args` | M3.3 | Low, and required: a fixed `args` list turns an empty optional input into a parse error |
| The Action wraps `check`/`lint`/`push` only; other commands run the container directly | M3.3 | Low |
| A Roslyn generator, **not** an MSBuild task — no assembly loading | [M3.4](plan/M3-cli.md) | Low, and it removes a whole class of consumer-specific build failure |
| Nullability is the requiredness contract: non-nullable ⇒ required | M3.4 | Low, but **enabling NRT on an existing project changes the schema** |
| Drift is compared structurally, not byte-wise | M3.4 | Low, and required: byte comparison would make the generator and the canonicaliser two implementations of one format |
| The analyzer carries a hand-written JSON parser rather than a dependency | M3.4 | Low. An analyzer sharing the compiler's load context cannot safely bring a JSON library |
| Enums map to their **names**, sorted; properties are camelCased and sorted | M3.4 | **User-visible in every generated schema** |
| Diagnostic ids `CDT001`–`CDT005` are public surface, with release tracking | M3.4 | Low, but a consumer will put them in `<NoWarn>` |
| The generator pins Roslyn **4.14** while the repo resolves 5.x | M3.4 | Low. An analyzer built against a newer Roslyn than the host fails to load |
| `samples/ContractDrift` lives outside the solution | M3.4 | Low; the solution build must not depend on a sample |
| Avro canonicalisation is hand-written against the spec, not delegated to a schema library | [M5.2](plan/M5-formats.md) | Low. Same reasoning as the JSON canonicaliser: ADR-019 needs it reproduced byte-for-byte in every SDK, and a library's own "canonical" output cannot be audited for that |
| `Concordat.Formats.Avro` is registered in DI for canonicalisation and compatibility only, not references or bundling | M5.2 | Low, and deliberate: `ISchemaFormatRegistry` fails loudly for the unimplemented ones rather than silently guessing, so Avro registration is refused rather than half-working while [#16](#16-avro-cross-subject-references-carry-no-version) is open |
| `doc` is the only Avro attribute stripped by canonicalisation | M5.2, and it is [#17](#17-avros-parsing-canonical-form-is-lossy-and-the-architecture-stores-the-canonical-form) | **Free today, needs a preimage bump and a migration once an Avro schema is stored** |
| Four tokens added to `BreakingChangeKinds`: `name_changed`, `fixed_size_changed`, `type_promoted`, `enum_value_defaulted` | M5.2 | Low, and additive — but normative under ADR-019 once published, so a client may branch on them |
| Avro compatibility runs resolution twice with the roles swapped, rather than deriving direction from one comparison | M5.2 | Low, and required: Avro resolution is asymmetric, so `int → long` is genuinely backward-compatible and forward-breaking at once |
| An enum symbol absorbed by the reader's `default` is reported at `WireJson`, not `Wire` | M5.2 | Low. The bytes decode, so it is not a wire break — but the value read is not the value written, which is exactly a broken JSON mapping |
| Numeric **and** `string`↔`bytes` promotions are all reported at `Source` | M5.2 | Low. `string`↔`bytes` is the arguable one: Avro's JSON encoding escapes bytes differently, so a case could be made for `WireJson`. Revisit if it bites |
| Avro paths are name-based (`#/fields/note`), not RFC 6901 index-based like the JSON engine's | M5.2 | Low, but **user-visible in every finding**. Avro matches fields by name, so an index is not a stable identifier — reordering fields is compatible and would renumber every path |
| `ContentModel` is ignored by the Avro checker | M5.2 | Low. Avro records are closed by construction; there is no `additionalProperties` equivalent to honour |
| The `.proto` parser is hand-written rather than taking a dependency | [M5.3](plan/M5-formats.md) | Low, and doubly forced: ADR-019 needs canonicalisation reproduced byte-for-byte in every SDK, and the mature .NET `.proto` parsers are reflection-heavy — which M3.3 established fails *silently* in the CLI's NativeAOT binary |
| The Protobuf canonical form is normalised `.proto` source, not a serialised `FileDescriptorProto` | M5.3 | **Free today, needs a preimage bump and a migration once a Protobuf schema is stored.** DESIGN §4 names the descriptor; a descriptor is the right model but the wrong thing to *serve*, since a consumer wants source it can give to `protoc` — the same lesson as [#17](#17-avros-parsing-canonical-form-is-lossy-and-the-architecture-stores-the-canonical-form) |
| Protobuf canonical output is indented, not minified like the JSON and Avro forms | M5.3 | Low. Determinism and idempotence are what canonicalisation needs; this one is read and compiled by people |
| proto2, groups, `extend`, `service` and aggregate option values are **refused**, not parsed | M5.3 | Low, and user-visible. A parser that silently mis-reads a construct yields a confidently wrong id *and* a confidently wrong verdict |
| Four more `BreakingChangeKinds` tokens: `wire_type_changed`, `field_removed_without_reserved`, `field_number_reused`, `presence_changed` | M5.3 | Low, additive, normative under ADR-019 once published |
| Bidirectional Protobuf breaks are emitted once per direction | M5.3 | Low, and required: Protobuf is not reader/writer asymmetric like Avro, so a single-direction finding would let a `Forward`-only policy miss a wire-type change entirely |
| `int32 → int64` is reported at **`WireJson`**, not `Source` | M5.3 | Low. proto3 JSON encodes 64-bit integers as quoted strings and 32-bit as bare numbers, so the JSON mapping genuinely changes. Still satisfies DESIGN §12's "passes `WIRE`, fails `SOURCE`" |
| Removing a field without `reserved` is reported at `Wire` even though nothing breaks that day | M5.3 | Low. The hazard is a later version reusing the number; flagging it at removal is the only moment it is cheap to fix |
| Protobuf `google/protobuf/*` imports are **allowed** while every other import is refused | [ADR-023](adr/023-no-cross-subject-references-avro-protobuf.md) | Low. They resolve from the runtime, not a registry, so they cannot drift — and refusing them would rule out most real Protobuf, since `google.protobuf.Timestamp` is close to universal |
| `ISchemaBundler` for Avro and Protobuf is the identity function | M5.2, M5.3 | Low, and true by construction while ADR-023 holds: a registered schema has no references, so the bundle is the document. It stops being the identity the moment references are supported |
| Only JSON Schema gets an `ISchemaPortabilityChecker`; the registry returns `null` for the others rather than throwing | [M6.1](plan/M6-sdks.md) | Low, and deliberate. Avro and Protobuf are specified formats with reference implementations; JSON Schema is the one with no compatibility spec and five libraries reading the same text. "No checker" is a statement about the format, not a gap — which is why this is the one registry lookup that does not fail loudly |
| A property literally named `if`/`oneOf`/etc. produces a spurious portability warning | M6.1 | Low. Telling a keyword from a property name means tracking schema position through every applicator; one warning on an unusual property name is far cheaper than missing a real `if` where the tracking got it wrong. Pinned by a test so it is a known trade rather than a bug |
| Regex portability warns on lookaround and backreferences specifically | M6.1 | Low. Go's RE2 cannot compile them **at all**, so a Go consumer loses the payload check entirely rather than disagreeing about it — the sharpest real divergence in ADR-021's validator set |
| Four NJsonSchema divergences from draft 2020-12 are corrected in an adapter rather than the fixtures being relaxed | M6.1, `Draft202012Corrections` | Low, and it follows M2's precedent for `integer`: the corpus is normative, the library is not. **Worth knowing the corrections exist** — if NJsonSchema ever fixes these upstream the corrections become no-ops, not double-corrections, but the tests are what will say so |
| Boolean subschemas are rewritten to `{}` / `{"not":{}}` before compiling | M6.1 | Low, and exact. Applied only in enumerated schema positions, because `{"uniqueItems": true}` is a keyword rather than a subschema and rewriting it would corrupt the schema |
| The missed-violation walk covers `properties`, `items` and `prefixItems` only | M6.1 | Low, and the boundary is deliberate: it stops where `JsonSchemaPortabilityChecker` starts warning that a keyword is outside the interoperable subset, so there is one line to explain rather than two |
| `Concordat.EndToEnd` hosts the registry in-process with `WebApplicationFactory` rather than against a container | Test coverage pass | Low. The HTTP round trip is real — routing, binding, serialisation and Problem Details all run — and the broker, where framing genuinely cannot be faked, *is* a container. Containerising the API too would add a build step per run for no assertion it enables |
| `StackFixture` duplicates `ApiFactory`'s Postgres-plus-host setup instead of sharing it | Test coverage pass | **The trigger recorded at M3.1 has now effectively fired.** That note said to extract a shared test-support project "if a third consumer appears"; this is the third instance of the pattern. It was left duplicated because `StackFixture` also owns a broker and the shapes are only half the same — but the next one should extract rather than copy |
| `Domain.Registry.Environment` deliberately shadows `System.Environment` | [M7.1](plan/M7-governance.md) | Low, but it costs a `using` alias or a qualified name at every use. Renaming it to avoid the clash would let the framework's type dictate the ubiquitous language, which is the wrong way round |
| `prod`, `production` and `live` default to `CiOnly` registration; every other name defaults to `Open` | M7.1 | Low, and user-visible. A name-based guess is crude, but the alternative — every environment open until someone remembers — fails in exactly the environment where it matters |
| A broker's identity is `(host, port, virtual host)`; TLS is derived from the scheme, not stored | M7.1 | Low. Two entries differing only by TLS would be one broker described twice |
| Credentials are encrypted with ASP.NET Core Data Protection, key ring in the database | [M7.2](plan/M7-governance.md) | **Moderate.** The key ring must survive a redeploy or every stored credential is unreadable. Purpose string `Concordat.BrokerCredential.v1` is versioned so a scheme change can be migrated rather than guessed |
| The `Environment` aggregate stores a `CredentialRef`, never a secret | M7.2 | Low, and it is what keeps secrets out of every list response by construction rather than by remembering to redact |
| A new contract is `MONITOR`, never `ENFORCE` | [M7.3](plan/M7-governance.md) | Low, but user-visible. Consistent with M2.4's client-side default, and for the same reason: a contract is authored by guessing about a live topology |
| Binding overlap is decided by pattern **intersection**, not string equality | M7.3, `RoutingKeyPattern.Overlaps` | Low, and it is the invariant. `orders.*` and `*.created` are textually unrelated and both match `orders.created` |
| `Matches` and `Overlaps` share one implementation | M7.3 | Low, and deliberate: a concrete key is just a pattern with no wildcards, so two implementations would be two chances to disagree about `#` |
| A binding must carry at least one subject | M7.3 | Low. An empty binding governs a route while saying nothing about it, which reads as enforcement and is not |
| `resolve` answers an unmatched route with `{contract: null, enforcement: "OFF"}` rather than omitting it | M7.3 | Low, but it is the contract with the SDK: the answers are positional, and "ungoverned" must be distinguishable from "not asked" |
| A binding's subjects are one `subject@selector` text column, not a child table | M7.3, `ContractConfiguration` | Low, needs a migration. The value comparer is load-bearing — without it EF compares by reference and an edited binding is silently never written |
| `resolve` is `POST`, not `GET` with a query string | M7.3 | Low. A whole topology does not fit in a URL, and the request is a body-shaped question even though it is a read |

---

## Settled

| Decision | Outcome | Recorded in |
|---|---|---|
| Milestone order: SDKs before governance (was #7) | **Moot, and answered better than the question asked.** v1 ships the .NET SDK only; M6.2–M6.5 are deferred until the .NET SDK has been tested against a real workload | [ADR-024](adr/024-v1-ships-dotnet-only.md). The original worry was that every milestone the SDKs waited behind was a chance for a .NET assumption to set. M6.1 addressed that directly instead: the protocol is published, the corpus is executable, and both already caught cross-language defects with no second SDK in existence. ADR-021's set and order are untouched |
| The approval gate could be bypassed by resubmitting (was #18) | **Fixed.** Compatibility history is now approved (`Active`) versions only, in one place — `CompatibilityHistory.Of` — used by both registration and the dry-run check | Found by the M6-era test pass. The rule had been written out at both call sites and was wrong identically in both, so the preview agreed with the gate and neither contradicted the other. `AwaitingApproval` counting as history let a proposal justify itself: submit the same breaking schema twice and the second attempt was compared against the first, found no divergence, and registered `Active`, moving `latest` onto a schema incompatible with the last approved version. A retrying CI job did it unaided. The semver suggester deliberately still counts pending labels, because `Subject`'s own increasing-label check does — suggesting a label the aggregate would refuse would be a worse bug |
| JSON Schema keyword coverage (was #9) | **Warn, do not refuse and do not stay silent.** `JsonSchemaPortabilityChecker` reports every keyword the compatibility engine does not compare, with a message saying what it costs — "a change confined to it is reported as compatible even when it is not". Findings ride on the registration response and on `concordat lint` | [M6.1](plan/M6-sdks.md) — this is the option M6.1 already prescribed ("warn at registration when a schema strays outside it"), so implementing it settles the question rather than reopening it. Refusing composition keywords would rule out most mature schemas; staying silent is the under-reporting the decision existed to stop |
| JSON Schema dialect | **draft 2020-12 only, and other dialects are refused** with `schema_dialect_unsupported` | M6.1 — keywords changed meaning between drafts (`items` most visibly), so validating a draft-07 document under 2020-12 rules would apply rules its author never wrote against. The one error-severity portability finding; everything else warns |
| Avro and Protobuf cross-subject references (was #16) | **Refused for v1.** Registration fails with `schema_references_unsupported` naming the type or import. Self-contained schemas — the common shape for both formats — register normally, as do same-document self-references and Protobuf `google/protobuf/*` imports, which the runtime resolves rather than a registry | [ADR-023](adr/023-no-cross-subject-references-avro-protobuf.md) — neither format has anywhere to pin a version, so resolving would bind to whatever the target holds now, which is the silent-behaviour-change ADR-017 exists to prevent. If references return, the out-of-band manifest is the favourite: it is the only mechanism that works identically for both formats |
| Project name | **Concordat**, after rejecting Signet, Hutch, Syngraph, Stipula, Warrenty, Indenture | [ADR-022](adr/022-project-name-concordat.md) |
| CLI binary name | **`concordat` only**, with a shell alias documented for anyone who wants one. No `cdt` | [M3](plan/M3-cli.md) — an alias is *additive*: shipping it later breaks nobody, removing it later breaks every script. That asymmetry says wait. `kubectl`, `terraform`, `docker` and `git` all ship one name and let users abbreviate. If it is ever added, `cdt` needs the same ecosystem-collision check that killed Signet and Hutch |
| JSON Schema validator | **NJsonSchema (MIT)** behind an `IPayloadValidator` port | [M2](plan/M2-dotnet-client.md) — `JsonSchema.Net` publishes its binary under a maintenance-fee agreement that would propagate to our users; `Newtonsoft.Json.Schema` is commercial |
| Default compatibility policy | **`Backward × WireJson`** | [ADR-016](adr/016-two-axis-compatibility.md), DESIGN §7 |
| Schema table scope | **Global**, keyed by `SchemaId` alone | [ADR-015](adr/015-content-addressed-ids.md) |
| Tier 2 SDK set | TypeScript/JavaScript, Python, Go, Java | [ADR-021](adr/021-tier-2-sdk-set.md) |
| Java included | Yes, last in the M6 sequence | ADR-021 |
| CI OS matrix | Ubuntu only for now | [M0.3](plan/M0-foundations.md) |
| CI triggers | Pull request, push to `main`, manual dispatch | M0.3 |
| GitHub issue seeding | Parked; `scripts/seed-github.ps1` is ready and idempotent | — |
