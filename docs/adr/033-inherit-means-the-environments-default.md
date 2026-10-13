# ADR-033: A subject with no policy of its own is judged by its environment's default

- **Status:** Accepted
- **Date:** 2026-10-13
- **Deciders:** Rafael Camara

## Context

[ADR-032](032-subject-settings.md) put an owner and policy editor on the subject's page and, to say
only what the registry was measured to do, ended up describing a registry defect in a confirmation
rather than fixing it. It was left as a decision for the maintainer. This is the decision: fix it, and
make the sentence say what is now true.

`Subject.CompatibilityPolicy` is nullable so that *inheriting* and *choosing the current default* stay
distinguishable ([M1.1](../plan/M1-registry-core.md)). `Subject.EffectivePolicy(environmentDefault)`
resolves it, `Environment.SetDefaultCompatibilityPolicy` says a change "takes effect immediately for every
subject that has not set its own", [ADR-016](016-two-axis-compatibility.md) says the pair is "configured per
environment and overridable per subject", and `CompatibilityPolicy.Default`'s own comment says it is "the
*environment* seed, not a subject fallback". Five call sites resolve the policy; two of them agreed with all
of that and three did not:

| Call site | Passed to `EffectivePolicy` | Before this change, measured |
|---|---|---|
| `POST …/versions` (`RegisterVersionHandler`) | `CompatibilityPolicy.Default` | `NONE` environment, inheriting subject, breaking v2 → **`AWAITING_APPROVAL`** (it should have been `ACTIVE`) |
| `POST …/compatibility` (`CheckCompatibilityHandler`) | `CompatibilityPolicy.Default` | the same subject: `compatible: false`, `policy: BACKWARD · WIRE_JSON` |
| `GET …/versions/1/diff/2` (`DiffVersionsHandler`) | `CompatibilityPolicy.Default` | the same subject: `policy: BACKWARD · WIRE_JSON` |
| `GET`/`POST …/impact` (`AnalyseImpactHandler`) | the environment's | right: in a `BACKWARD · WIRE` environment a consumer pinned to v1 was *not* reported broken by an `id: string → integer` change, in a `NONE · WIRE_JSON` one it was (the surface decides, and it was the environment's) |
| `POST …/promote` (`PromoteVersionHandler`) | the environment's | right: a version promoted into a `NONE` environment onto an inheriting subject landed `ACTIVE` |

So one subject had two answers to "what is my policy", and the dry run CI calls before pushing disagreed
with the registration it was a rehearsal for. A pinned subject was never affected: it never reads the
environment. The probe ran against the real registry in new environments and subjects named by timestamp,
and was run again after the change; the table above is the "before", and the "after" is in the section
below.

**There is no "unset" default.** The brief asked what an environment whose default is not set resolves to.
The registry has no such state: `Environment.DefaultCompatibilityPolicy` is a non-nullable value, creating an
environment with both members absent stores `BACKWARD · WIRE_JSON` (`Environment.Create` substitutes
`CompatibilityPolicy.Default`), and an update with both absent leaves the stored value alone. Measured:
`POST /v1/environments {"name":…}` answers `defaultCompatibilityPolicy: {BACKWARD, WIRE_JSON}`, and so does a
`PATCH` that sends two nulls. (The web app's environment model has a "not set" case and the settings screen
an option for it; the registry cannot produce one. See "Found, not fixed".) The only way to reach a subject
with no environment row is legacy data from before environments were rows, or a command built by hand without
a name; that is the one place a fallback is needed, and it is `CompatibilityPolicy.Default`.

## Decision

Registration, the dry-run check and the diff resolve an inheriting subject's policy against the environment's
default, through one function, `EnvironmentPolicies.EffectiveFor(subject, environment)`, so that there is one
rule and not three copies of it. Registration already holds the environment (`RegistrationGate` loads it,
and creates it on the first write, which is why the new row's default is the built-in); the check and the diff
take only an `EnvironmentId`, so they load it with `IEnvironmentRepository.FindAsync(EnvironmentId)`, the same
call the gate makes, rather than `RequireAsync` by name as impact and promotion do. Those two are *about*
environments and refuse an unknown one (`environment_not_found`); the check and the diff are about subjects,
and a subject can exist in an environment with no row (the same legacy case), where refusing would turn a read
that works today into a 404.

**A missing environment row falls back to `CompatibilityPolicy.Default`, and only that.** It is the value
every environment is created with, so for an environment nobody has configured it is also what "the
environment's default" would have said. Pinned by a test at each of the three sites.

**Not re-judged.** Versions already registered are not judged again, and a version held at the approval gate
stays held when a default is relaxed. That is the rule ADR-032 measured for a subject's own policy and it holds
for an environment's default too: a registration is judged once, when it is made, by whatever the policy was
then. It is tested (a held version stays held after the environment's default goes to `NONE`).

**What this changes, for whom.** Only an environment whose default is **not** `BACKWARD · WIRE_JSON` has
inheriting subjects that now behave differently, and only from their next registration:

- an environment whose default is **laxer** (`NONE`, `FORWARD`, `BACKWARD · WIRE`): a version the built-in would
  have held now goes in `ACTIVE`, and `latest` moves;
- an environment whose default is **stricter** (`FULL`, any `*_TRANSITIVE`, `· SOURCE`): a version the built-in
  let through is now held at the approval gate, and `latest` does not move;
- `concordat check` and the dry-run endpoint change with it, so a pipeline gate that was red for an inheriting
  subject in a `NONE` environment goes green, and one that was green in a `FULL` environment can go red. A gate
  and a registration now agree, which is the point.

**What an operator with such an environment should check**, before upgrading: `GET /v1/environments` for each
environment's `defaultCompatibilityPolicy`; and for each one that is not `BACKWARD · WIRE_JSON`, which subjects
have `compatibilityPolicy: {mode: null, surface: null}` (those inherit). Nothing needs migrating, nothing
already registered changes, and a subject pinned to a policy never changes. If an inheriting subject was
relying on being judged by the built-in default in an environment whose default is something else, pin it
(`PUT …/compatibility-policy`).

**After the change, measured** (the same probe, against the rebuilt registry): in the `NONE · WIRE_JSON`
environment the inheriting subject's breaking v2 is `ACTIVE`, its dry run says `compatible: true` under
`NONE · WIRE_JSON`, and its diff names `NONE · WIRE_JSON`; the pinned subject beside it is still held and still
reports `BACKWARD · WIRE_JSON`; in the `BACKWARD · WIRE` environment the inheriting subject reports
`BACKWARD · WIRE`; impact and promotion answer as they did.

**The web app.** The subject page's inherit confirmation says what is now true: that from the next version the
subject is judged by its environment's default, and that registering, checking, comparing, impact analysis and
promotion all read it. The "As this registry stands…" sentence and `BUILT_IN_POLICY` are gone (nothing else read
it). The environment settings screen's "takes effect at once for every subject here that has not set its own" is
now true at registration, and says what "at once" is: the next version registered, checked or compared; versions
already registered or held are not judged again. A new spec, `environment-default.spec.ts`, holds it against an
environment of its own (`e2e-follows`) and moves nothing shared.

## Alternatives considered

- **Weaken the claims instead** (make the docs and the screen say "inherit means the built-in"). Rejected by the
  maintainer, and rightly: it would leave a per-environment default that does nothing at the one place it
  matters, which is the only reason to have one.
- **Re-judge held and registered versions when a default or policy changes.** Rejected: a registration is judged
  once, a held version is a decision a reviewer may already be making, and re-judging the past makes the audit
  trail disagree with the data (`Environment.SetPreReleasePolicy` says the same of labels).
- **`RequireAsync` by name in the check and the diff, like impact and promotion.** Needs the route's name in two
  more queries and turns a legacy environment-without-a-row from a working read into a 404. The id is what the
  query already holds.
- **Copy the environment's default onto a subject when it is created.** Destroys the distinction between
  inheriting and pinned (`ANullPolicyIsStoredAsNull…`), and is the thing nullable policy exists to avoid.

## Consequences

- **Positive:** a subject has one policy. The dry run, the registration, the diff, the impact report and a
  promotion agree, in every environment, and ADR-016's "configured per environment and overridable per subject"
  is true of the code.
- **Positive:** the confirmation no longer describes a registry defect. The sentence was pinned by tests that went
  red on purpose; they are now inverted and pin the new behaviour.
- **Negative: a behaviour change for any environment whose default is not `BACKWARD · WIRE_JSON`**, described above
  with what to check. Nothing already registered or held changes.
- **Negative:** the dry run and the diff each cost one more read (the environment row).
- **Not covered:** an environment with no row, in the browser or over HTTP (only legacy data produces one; it is
  tested at the application layer, once per call site); the `concordat` CLI and the GitHub Action, which call the
  same endpoints and so change with them, but were not run for this.
- **Found, not fixed** (separable, all in [STATUS.md](../STATUS.md)): **the environment settings screen cannot change
  a default by changing one select** — it sends only the member that changed and the registry refuses half a
  policy (`400`, "needs both a mode and a surface"), so a default is changed by choosing a whole pair; pinned by
  `environment-default.spec.ts`. The screen and the web model also carry a "not set" default that the registry
  never produces. A `PATCH` that changes nothing still writes "owner changed". Retiring a subject records the
  subject's owner as the actor.

## References

- [ADR-016](016-two-axis-compatibility.md), [ADR-017](017-gated-latest-pointer.md),
  [ADR-028](028-schema-editor.md), [ADR-032](032-subject-settings.md)
- [STATUS.md](../STATUS.md), [DECISIONS-PENDING.md](../DECISIONS-PENDING.md) decision 39
- `src/core/Concordat.Application/Registry/` (`EnvironmentCommands.cs`: `EnvironmentPolicies`;
  `RegisterVersion.cs`, `SubjectQueries.cs`, `DiffAndLifecycle.cs`), `web/e2e/environment-default.spec.ts`,
  `web/e2e/subject-settings.spec.ts`, `tests/Concordat.Api.IntegrationTests/InheritedPolicyApiTests.cs`
