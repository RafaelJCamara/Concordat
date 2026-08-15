# Decisions pending

Everything waiting on you, in one place. Sections are ordered by when the decision starts to
hurt; the numbers are stable labels, not positions, so a later-numbered item can appear first.

**A settled decision is struck through in place, not moved.** Its heading gets `~~strikethrough~~`
and the body is rewritten to say what was decided and why. Keeping the reasoning next to the
decision is the point: a one-line row in a table at the bottom loses exactly the part somebody
needs when they come back in six months asking why. Scan for a heading without strikethrough to
see what is still open.

The **[Settled](#settled)** table at the bottom holds the older entries, from before that
convention, plus decisions that were answered by an ADR rather than by an edit here. Anything
architectural still becomes an ADR in [`adr/`](adr/README.md) either way.

---

## Proceeded on my judgement — confirm or overturn

### 33. `ci` is a marker scope, and no role grants it

**Implemented.** `RegistrationPolicy.CiOnly` has to tell a build pipeline apart from a running
producer, and **nothing in the system could**: both authenticate with an API key carrying
`subject:write`, and `ApiKeyKind` records how a credential was issued, not what holds it.

So `ci` is a scope that grants nothing, is implied by nothing, and is read by exactly one rule.
The alternatives I rejected: a third `ApiKeyKind` member, which mixes lifetime with purpose on
one axis; and a boolean column on `ApiKey`, which adds a second authorisation axis alongside
scopes for a single consumer.

**The consequence worth your attention: no role grants `ci`, including Owner.** It belongs on a
key issued to a pipeline, not on a human's role — an administrator who inherited it could walk
straight through the control that keeps production clean. Two things follow, and neither is
obviously right:

- **A human cannot register into a `CI_ONLY` environment at all**, however senior, without being
  issued a key carrying `ci`. That is the intent, and it will read as a bug the first time
  somebody hits it in the UI.
- **An unclaimed instance cannot either**, because `Caller.Unclaimed` holds the Owner scopes.
  Create an environment called `prod` on a fresh self-hosted registry and it refuses everything
  until an account and a CI key exist. Defensible — `prod` defaulting to `CiOnly` is deliberate —
  but it is a sharper first-run experience than it was yesterday.

> **If you overturn this**, the cheapest variants are: grant `ci` to `Role.Owner`; or narrow the
> default so only an explicitly-set `CI_ONLY` enforces and the name-based guess merely warns.
> Nothing is persisted that a change would have to migrate.

### 34. The policy gates subject creation as well as version registration

**Implemented.** `RegistrationPolicy` is documented as "whether an SDK may register schemas
directly", which reads as versions only. I extended it to `CreateSubject` because gating only
versions would let a misconfigured producer fill a closed environment with empty subjects —
permanent clutter, through the exact door the policy exists to shut, reported as success.

The argument the other way: creating a subject is a deliberate act, usually by a person, and a
team setting up a new service in a locked-down environment now needs CI to do it for them.

### 30. A governing contract beats local `Mode` in both directions — except `Off`

**Implemented and shipped** as part of contract resolution in the SDK. It is the load-bearing
call in that work and it is yours to confirm.

When a contract governs a route, its enforcement decides and the client's configured `Mode` is
ignored. `Mode` governs only routes no contract covers.

The alternative was **stricter of the two**, which sounds safer and is not. Under it an operator
could switch enforcement **on** centrally but never **off**: any service configured locally to
`Enforce` would keep refusing traffic after the contract had been set to `OFF`. An off switch
that does not switch anything off is worse than none, because it is believed. That asymmetry is
what decided it.

**The exception, and it is an inconsistency worth seeing plainly:** local `Mode = Off` cannot be
overridden by a contract, because `ConcordatChannel` short-circuits on it before resolving
anything. The justification is that `Off` means "Concordat does nothing in this process", and
honouring that must not depend on the registry being reachable — it is also what lets `Off` cost
nothing. But it does mean an operator cannot switch enforcement *on* for a service that has
opted out locally, and there is no signal anywhere telling them so.

> **If you overturn this:** the change is confined to `SchemaEnforcer.Decision` and two
> short-circuits. Nothing is persisted, so there is no migration either way.

### 31. `BasicConsumeAsync` now wraps the application's consumer

**Implemented.** `ConcordatChannel` decorated `BasicPublishAsync` and passed `BasicConsumeAsync`
straight through, so a codebase could publish under enforcement and consume without it. That
contradicts the stated reason the channel is a decorator at all — "enforcement you can bypass by
forgetting is the failure mode this product exists to prevent" — so I closed it rather than
record it.

It is here because **it changes behaviour for existing users without them asking.** Anyone on a
`ConcordatChannel` today gets consume-side enforcement they did not previously have, including
quarantining in `Enforce`. Double-wrapping is guarded, so a manual `ConcordatConsumer` is left
alone, and `Mode = Off` still bypasses everything.

> **Cost of waiting:** none — but the longer it stands, the more likely someone depends on the
> old asymmetry without knowing it. Worth a line in the release notes at v1 either way.

### 32. Publish-side version conformance judges `latest`, not the pinned version

**Implemented.** When a binding pins `orders.created@3` and the subject's latest is 7, the SDK
reports `contract_version_not_permitted` rather than fetching version 3 and validating against it.

The reasoning: a publisher stamps the tip, so the honest question is whether the route accepts
the tip — and it needs no version-by-ordinal fetch on the publish path. The alternative reading
is that a pinned binding *instructs* the publisher to send version 3, which would mean validating
against it and stamping it. That is a coherent design and a different product decision.

> **If you want the other reading**, `GET /subjects/{s}/versions/{ordinal}` already exists; the
> work is a `GetVersionAsync` on the client and a branch in `SchemaEnforcer`.

### 17. ~~Avro's Parsing Canonical Form is lossy~~ — hash the full document

**Resolved 2026-08-15 by the owner: hash the whole thing.** Option (B) had already stopped
stripping `default` and `aliases`; `doc` was the last attribute still dropped, and it is dropped
no longer. The Avro canonical form is now pure normalisation — ordering, whitespace, fullname
resolution — and nothing is removed.

The reasoning that carried it: **an id is a claim about a document, not about the subset of it
this build considers meaningful.** Once one attribute is dropped for being presentational, every
later attribute needs the same judgement made about it, by five SDKs, identically, forever.
"Everything" is the only rule that does not require a committee.

**The cost is real and was accepted:** editing a comment mints a new schema id and therefore a
new version. That version is compatible with its predecessor so nothing breaks, but the history
carries an entry whose only change is prose. `SchemasDifferingOnlyInDoc_HaveDifferentIds` pins it
so nobody rediscovers it as a bug.

**No migration and no id churn**, because nothing has registered an Avro schema yet — which is
exactly the window this decision was recorded to be settled inside. The preimage version is
unbumped deliberately: `format` is part of the preimage, so JSON Schema ids are untouched.

### 1. Rename the GitHub repository `Signet` → `Concordat` — one step left, and it is yours

**Repository side done, 2026-08-14.** `RepositoryUrl` and `PackageProjectUrl` in
`Directory.Build.props` now say `github.com/RafaelJCamara/Concordat`, as does `NOTICE`.
Those strings are baked into every NuGet package from M2 onward, which is why they had to
change before the first publish rather than after it.

**Remaining:** the rename itself, on GitHub — *Settings → General → Repository name*.
I could not do it: `gh` is not installed on this machine and no `GH_TOKEN` is in the
environment, so there is no credential I can legitimately reach. Installing and
authenticating `gh` costs more of your time than the two clicks do.

Afterwards, `git remote set-url origin https://github.com/RafaelJCamara/Concordat.git`.
Not urgent — GitHub redirects the old URL indefinitely and pushes keep working — but a
redirect is a thing to remember, and the point of the rename is to stop remembering it.

Two things the rename does **not** touch, recorded so neither looks like an oversight:

- **The GHCR image paths.** `publish-images.yml` derives them from `github.repository_owner`
  and a literal image name, never the repository name, so `ghcr.io/rafaeljcamara/concordat-api`
  is already correct and stays correct.
- **The local directory,** still `Projects\Signet`. Git identifies a repository by its remote;
  the folder name is cosmetic. Renaming it mid-session would invalidate every absolute path in
  flight, so it is left for you to do between sessions if it bothers you.

> **What is still deliberately named Signet:** ADR-022, M0.1 and the `## Settled` table below
> record *why the name was rejected* — an active `bytepunx/signet-proto` publishing the exact
> package ids ADR-021 depends on. That history is the reason the ADR exists. It stays.

### 2. ~~What to do with the `docs/design-and-plan` branch~~ — merged

**Resolved 2026-08-14: it had already resolved itself.** By the time this was actioned the branch
was fully merged into `main` and 33 commits behind it — work had been landing on `main` for some
time. The branch is deleted, locally and on the remote.

CI has been running on `main` throughout, so the concern behind this entry — "CI has never run" —
no longer applies either.

### 3. Reserve the names that are still unreserved

Availability is not reservation. From M0.1:

- Buy **`concordat.dev`** — Problem Details `type` URIs point at it, first used in M1.6
- Create the **`@concordat`** npm organisation
- NuGet, PyPI and Maven ids are only claimed on first publish (M2, M3, M6)

---

## Needed before a specific milestone

### 4. ~~External coverage reporting~~ — kept as an artifact

**Confirmed 2026-08-14.** No Codecov. The coverage artifact stays and is read by hand when
somebody wants it. A percentage on a solo project mostly generates noise, and the signal that
matters here is whether the conformance corpus grows — which no coverage number captures.
Revisit if a second contributor arrives: the number is worth more as a conversation between
people than as a gate.

### 5. ~~Windows in the CI matrix~~ — Ubuntu only

**Confirmed 2026-08-14.** The suite raises up to three Linux broker containers and GitHub's
Windows runners do not provide Linux-container support, so the container suites cannot follow
there whatever the matrix says. A Windows *build-and-unit-test-only* job stays available if the
platform signal is ever wanted; it is not today.

### 6. ~~When should the header-survival suite run?~~ — on pull requests

**Confirmed 2026-08-14, and decision 19 is why it earned its place.** It raises three brokers and
almost never changes, because it measures broker behaviour rather than our code — which is
exactly the argument for running it. A broker upgrade that quietly stopped preserving
`concordat-*` headers is the scenario it exists to catch, and that upgrade will not arrive with a
code change to trigger it. Its measurements are now load-bearing on a decision, too: they are
what scoped binary framing out of v1.

### 8. ~~Semver pre-release support~~ — per environment

**Resolved 2026-08-15 by the owner: configurable, off by default.** Some teams treat a release
candidate as a real contract their consumers build against; others want production carrying
released labels only. Both are reasonable, which is why this is a setting rather than a rule.

- **`SemanticVersion` parses pre-releases everywhere**, with full SemVer 2.0.0 precedence — a
  pre-release precedes its own release, numeric identifiers compare numerically so `rc.10`
  follows `rc.9`, and more identifiers beat fewer. Folding the policy into the parser was the old
  behaviour and meant a team emitting `-rc` labels could not label a version *at all, anywhere*.
- **`Environment.AllowPreReleaseVersions` decides**, off by default. The natural shape is dev and
  staging permitting them and production not, so a candidate is exercised everywhere it should be
  and cannot reach production still wearing its suffix.
- **Turning it off does not invalidate labels already registered.** Versions are immutable and
  their labels are history; re-judging the past on a policy change would make the audit trail
  disagree with the data.
- **A message may always carry one.** What an environment *accepts at registration* and what a
  message *carries on the wire* are different questions — the envelope reader now reads
  `2.0.0-rc.1` rather than warning about it.

**Build metadata is still refused**, with its own code. SemVer ignores it for precedence, so
`1.0.0+a` and `1.0.0+b` compare equal while being different strings — and this registry requires
each label to increase on the last. A grammar that can express something the ordering cannot see
is a trap, not a feature.

> **The UI toggle is owed.** The setting is on `POST`/`PATCH /v1/environments` and on the
> environment response; the checkbox lands with M4.3's settings pages, which do not exist yet.

### 10. Generic message types are unsupported — before v1 ships

M2.3 **refuses** a generic type name rather than inventing a spelling for it, because any
spelling becomes a rule five SDKs must reproduce character for character, and Go and Python
have no CLR generic syntax to reproduce it from.

That is right for the protocol and a hard stop for anyone whose publishers send
`Envelope<OrderCreated>`. Raw RabbitMQ.Client publishers rarely do, which is why this is a
gap rather than a blocker under ADR-020 — but if **your** code does, it moves up.

> **Options:** ship refusing them; require an explicit subject for generic types; or define a
> normative spelling in the corpus and make every SDK implement it.

### 11. ~~Nested and top-level types collide~~ — kept

**Confirmed 2026-08-14, unchanged.** `+` → `.` (DESIGN §3) means `Acme.Orders+OrderCreated` and a
top-level `Acme.Orders.OrderCreated` are the same subject.

Kept because the collision needs two types whose names match *after* the rewrite — rare, and
immediately visible in the registry's subject list — while refusing all nested types would hurt
far more publishers, every day, for a case most estates never hit.

### 12. ~~`diff` is blind to added properties under an open content model~~ — left

**Confirmed 2026-08-14: left as is for v1.** Under an open content model — the default — adding
or removing a property cannot affect compatibility, so the engine records no divergence and
`concordat diff v1 v2` shows two different schema ids and an empty list for the most common
schema change there is.

`check` is unaffected and correct: the change genuinely *is* compatible. It is `diff` that
disappoints, and the CLI already says so and points at `git diff` of the schema files. Widening
`allDivergences[]` to carry informational findings would change what that field means and touch
the M1.3 corpus — a protocol change bought for a reporting nicety.

### 13. ~~`Concordat.Contracts.Testing`~~ — built

**Resolved 2026-08-14: built, before v1, as recommended.** `ConcordatAssert.CompatibleAsync<T>`
and `AllCompatibleAsync(assembly)` ask a live registry whether a contract type still fits what
that environment is serving.

**It answers a question the build-time check cannot.** `concordat check` and the M3.4 analyser
compare a type against the schema file beside it — they catch drift from the *file*, in the pull
request that caused it, and neither knows what is deployed. This catches drift from what is
running in `prod`, which is the failure that pages somebody. A test pins exactly that case: the
type is unchanged and its file is unchanged, so both build-time checks are green, and the
assertion still fails because the registry moved underneath them.

**It reads the generator's emitted schema and never derives one.** The reason this was deferred
in the first place stands: reflecting over the runtime type would be a second implementation of
the C#-to-JSON-Schema mapping, the two would drift, and *a drift detector that drifts* is worse
than none — it reports failures nobody can reproduce and, far worse, passes while the real
mapping has changed. The only source is `ConcordatGeneratedSchemaAttribute`.

Four defaults chosen so the check does not get deleted by whoever is waiting for the build:

- **An unknown subject is compatible**, by default. A team adding a new type has not broken
  anything, and a red test between writing the type and CI first pushing it teaches them the
  check cries wolf. Turn it off in a suite whose job is to prove every contract is registered.
- **An unreachable registry is not an incompatibility.** A test that fails identically either way
  trains a team to rerun it rather than read it.
- **A ten-second timeout**, not the hundred-second default.
- **No test-framework dependency.** It throws, and xunit, NUnit and MSTest all report that as a
  failure; inheriting from one framework's assertion base would make the package unusable in the
  other two.

Tested against the real API rather than a fake handler, which is what the `Handler` option on
`ConcordatTestOptions` is for — and it doubles as the seam a corporate proxy or a self-signed
registry certificate needs.

### 14. ~~The quarantine exchange is declared by the application~~ — confirmed, and documented

**Resolved 2026-08-15 by the owner: applications have exchange rights, and the documentation must
say so plainly.** The default stands, and it is now stated as a *requirement* rather than left as
an assumption.

[`docs/BROKER-PERMISSIONS.md`](BROKER-PERMISSIONS.md) is new and linked first from the README,
from the quickstart, and from `DeclareQuarantineExchange` itself. It gives the exact
`set_permissions` invocation, says what Concordat never needs so nobody grants more than
necessary, and covers the IaC-owned case — where turning the flag off **and** provisioning the
exchange are both required, because doing only the first looks fine until the first violation.

The argument for the default, written down where somebody deciding will see it: quarantine only
runs once a message has already violated its contract under `ENFORCE`. So an undeclared exchange
is discovered *during an incident*, in a path nobody has exercised — a second failure stacked on
the one being handled. Declaring costs one idempotent `exchange.declare` per process.

### 19. ~~The envelope spec describes payload framing that does not exist~~ — amended

**Resolved 2026-08-14: amended, not built.** The [ADR-010 amendment](adr/010-header-envelope.md)
scopes both binary forms and the CloudEvents read support out of v1, and DESIGN §2 now strikes
them through and points at it.

**The ADR listed the negative and M2.5 went and measured it.** "Headers may not survive every
hop" was the recorded risk; the header-survival suite found `concordat-*` survived every
transport it could raise — direct publish, dead-lettering, shovel, federation, the AMQP 1.0
conversion. Framing exists to carry identity where headers do not, so building it now would ship
a second wire format on a hypothesis the evidence contradicts.

The answer for a broker that *does* drop headers is the content-type token, which is implemented
and tested and needs only `content-type` to survive — a far weaker requirement than a header
table. **What would bring framing back:** a transport that drops both. The `v1` token exists so
that stays possible without breaking anyone.

### 20. ~~Mode A and Mode B disagree about whitespace and invalid types~~ — done

**Resolved 2026-08-14 with the recommendation: Mode B matches Mode A — warn, do not trim.** Both
paths now share `EnvelopeReader.ValidateSubject`, and a padded `concordat-semver` is refused for
the same reason. A subject's identity must not depend on which envelope mode a publisher happened
to use.

Three corpus fixtures pin it, which is what was actually missing: no fixture covered the two
paths against the same input, and that is exactly why the divergence survived to be found by
reading the code rather than by running it.

The related note is also closed — `envelope_format_mismatch` was a published code nothing
emitted, and it turned out not to be dead but *unwritten*: a message declaring `json` for a
schema the registry holds as `avro` means the producer and the registry disagree about what was
sent. The schema id is content-addressed so the registry wins and validation is unaffected; what
was missing was saying so rather than quietly validating past it.

### 21. ~~Two contracts in one environment can govern the same route~~ — done

**Resolved 2026-08-14 with option (b).** `POST /contracts/resolve` returns **every** matching
contract rather than whichever sorted first by name. The field is `contracts` and it is a list;
an ungoverned route sends an empty one.

Option (a) — checking across contracts when a binding is added — was the alternative and was
rejected for the reason recorded when the decision was written: it makes authoring a contract an
environment-wide operation and needs a story for concurrent writers, to prevent a condition that
is rare and now visible.

**Strictest enforcement, union of subjects.** That combination fails safe in both directions
while the ambiguity stands: taking the loosest mode would let an authoring mistake quietly
switch enforcement off, and intersecting the subjects would refuse a publisher that one contract
plainly permits. Neither picks a winner.

**Reported once per route, not once per message.** `ConcordatClientStatus.AmbiguousRoutes` counts
them and `ToString()` names the condition when it is non-zero. Ambiguity is a property of the
topology, so a per-message signal would say the same thing a million times.

`ResolvedRoute.Contract` is now null when *several* contracts match as well as when none do, so
nothing in the SDK can name one of a colliding pair as though it were the answer.

> **Still inherited by M7.4's impact analysis**, which attributes a route to a contract when it
> answers "who breaks if I change this subject". It now has a list to work from; using it is not
> done.

### 22. ~~Contract names take anything up to 128 characters~~ — done

**Resolved 2026-08-14 with the recommendation.** Contract names now use the grammar environments
use, widened by `_` and `.` because a contract is a governance artefact and `payments.eu_west` is
a reasonable thing to want: lowercase letters, digits, `-`, `_`, `.`, starting and ending
alphanumeric, no repeated separator.

Folded to lowercase, like an environment name and unlike a subject name — a subject comes from a
message type where `OrderCreated` and `ordercreated` are genuinely different types, while a
contract name is typed by a human into a URL. `my contract!! (draft/2)` was legal until this, and
a name carrying `/` or `%` is not reliably addressable at
`/v1/environments/{env}/contracts/{contract}`.

### 23. ~~A subject can be registered in an environment that does not exist~~ — done

**Resolved 2026-08-14 with option (a).** The row is created on the first write to an
environment, carrying the **derived** id — so it is not a migration, it is filling in the record
the hashed id always implied, and every subject already pointing at that id is adopted rather
than orphaned. `RegistrationGate` does it, shared by subject creation and version registration.

Two things fell out of it that are worth knowing:

- **It closed the registration-policy hole.** A never-created environment had no row, therefore
  no policy, therefore admitted everyone — so `prod` was open precisely until somebody thought
  to create it. The first write now materialises the row, the `CiOnly` default applies
  immediately, and a non-CI caller is refused.
- **A refused first write leaves nothing behind.** The row is staged, not committed; the handler
  returns before saving, so the refusal does not quietly create the environment it just refused.

It also discharged the standing M7 commitment to *"adopt the derived environment ids or migrate
`subject.environment_id`"*, which had been deferred twice.

### 24. The audit trail records successful changes only

By construction: entries are appended to the same unit of work as the change, so a refused
request — which never reaches a commit — leaves nothing behind. That is what makes the trail
incapable of disagreeing with the data, and it is the right trade today.

**It stops being the right trade in [M8](plan/M8-identity.md).** An authorization denial is
exactly what an auditor opens the log to find, and `403 insufficient_scope` will produce no row
at all. Recording refusals needs a second write path that is explicitly *not* transactional
with the thing that did not happen, which is a different mechanism, not a bigger switch
statement.

> **Recommendation:** decide it with M8 rather than now, but decide it *deliberately* — the
> tempting shortcut is to append refusals on the same path, which reintroduces exactly the
> "audit row survives a rolled-back change" failure this design avoids.

### 25. ~~`ENFORCEMENT_VIOLATION` is a notification event nothing emits~~ — done

**Resolved 2026-08-14 with option (a).** `POST /v1/environments/{env}/violations`, reported by
`Concordat.RabbitMq` through a `ViolationReportingObserver` that decorates whatever observer the
host already wired up rather than replacing it.

The shape, all of which follows from "this must not touch the delivery path":

- **Counted locally, sent on a timer.** `RecordViolation` does one dictionary update and returns.
- **Aggregated by fingerprint** — environment, side, route, subject, code — not queued per
  message. A broken publisher emits thousands a second, and a queue of individual violations
  would post a batch big enough to hurt the registry: a denial of service written by our own SDK.
- **Bounded and dropping**, at 256 distinct violations between flushes, counted in `Dropped`. A
  client that exhausts its own memory recording that something else is broken has made the
  outage worse.
- **Upserted at the registry**, one row per distinct violation with `FirstSeenAt`, `LastSeenAt`
  and a count. The unique index is what stops every replica of a broken service opening its own
  row and firing its own notification.
- **The notification fires on first sight only.** "This started happening" is the alert; "this is
  still happening" is the counter. Staging one per report would page somebody every reporting
  window for as long as the fault lasted.
- **Unenforced is not a violation** and is never reported — a brownfield estate would otherwise
  report every message it sends.
- **Opt-in on `ServiceName`,** the same rule service registration uses: a table of violations
  reported by "unknown" names a problem and nobody to talk to about it.

> **Not yet wired by default.** `ViolationReportingObserver` and `FlushViolationsAsync` exist and
> are tested end to end, but nothing schedules the flush — a host opts in by wrapping its observer
> and calling flush on a timer. Putting a background timer inside a client library that hosts
> already own the lifetime of is the next decision, not this one.

### 26. A reload signs you out — ~~fixed~~; the browser E2E is still missing

**The reload half is resolved, 2026-08-14, with option (a).** `/v1/auth/signin` now sets an
`HttpOnly`, `SameSite=Strict` cookie alongside the credential, and `POST /v1/auth/resume` trades
it back for one at startup. The credential still lives in memory only — `localStorage` is
readable by any script on the page and ADR-006 already declined that trade.

**CSRF is structural here, not mitigated.** `/resume` is the *only* route that accepts the
cookie, and its entire power is handing back a credential the browser already holds. Every
mutating route still requires an `Authorization` header a cross-site request cannot set; a test
pins that the cookie alone is refused everywhere else. `SameSite=Strict` is a second lock on a
door already bolted.

Three details worth knowing:

- **`Secure` follows the request rather than being forced on.** Forcing it would make the cookie
  silently not-set on a self-hosted registry served over plain HTTP — a sign-in that appears to
  work while only the reload keeps failing.
- **`__Host-` is deliberately not used.** It is stronger and would make the cookie unusable at
  `http://localhost:5062`, which is the ordinary evaluation path. A security feature that breaks
  the quickstart gets turned off rather than obeyed.
- **Sign-out needs the server**, because script cannot delete an `HttpOnly` cookie. The API key
  is still left to expire on its own.

`SessionApi` lives in `core/auth`, not the identity feature: the shell and the app initializer
are the callers and neither may import a feature. The boundaries lint caught that, which is what
it is for.

> **Still open: there is no browser E2E.** `if-scope.spec.ts` and `scope-guard.spec.ts` cover
> each half in isolation and the API's `AuthorizationTests` cover the server. Nothing drives a
> real browser through "sign in as a reader, confirm the button is absent, paste the URL
> anyway". That needs Playwright, which this project has never had — a dependency decision
> rather than an oversight, and it is [M4.5](plan/M4-web-app.md)'s to make.

### 27. ~~`AllowAnonymousUntilClaimed` is on by default~~ — done

**Resolved 2026-08-14 with option (a) plus the banner.** The default stands: an unclaimed
instance answers an unauthenticated request as an owner, so `docker compose up` gives you
something usable and an upgrade locks nobody out. What changed is that it now **says so**.

- **`UnclaimedInstanceWarning`** logs it as a warning naming both ways to close it, and repeats
  hourly until claimed. **Repeated rather than logged once at startup**, because a line at boot
  is exactly as invisible as no line by the time it matters — the container has been up three
  weeks and that message scrolled away on day one. It stops the moment somebody claims the
  instance, so the noise ends by being acted on.
- **The web app already had the banner**, shipped with M8.2 and never noted here. It had no test;
  it does now, including that it stays silent while `claimed` is still `null` — flashing a
  security warning on every page load of a correctly configured registry is how people learn to
  ignore it.

Verified by running the real container against a real database and reading the log, not only by
test. Option (c), binding the unclaimed caller to loopback, stays rejected: the common evaluation
path is a container, where nothing is loopback.

### 28. Cloud is Azure, on Container Apps — what that settles and what it does not

**Settled by you:** Azure, deployed to Azure Container Apps.

That resolves two of the four blockers and changes a third:

| Was blocked on | Now |
|---|---|
| A KMS for the key ring | **Done.** Azure Key Vault wraps the Data Protection key ring, and the `Cloud` profile refuses to start without a key URI |
| A cluster for M9.4 | **Done differently.** Container Apps, not AKS — `deploy/azure/main.bicep`, which compiles and lints. The Helm chart M9.4 asks for is for self-hosted Kubernetes users, and nobody has asked for one |
| Google / GitHub OAuth clients (M9.2) | Still yours. Needs redirect URIs on a real domain, which is [#3](#3-reserve-the-names-that-are-still-unreserved) |
| A Stripe account (M9.3) | Still yours. Metering needs nothing from Stripe and is the obvious next build |

> **Two things worth deciding soon, both consequences of Container Apps rather than of Azure:**
>
> **(a) The outbox pump and scale-to-zero.** `minReplicas` is pinned to 1 because `OutboxPump`
> is an in-process background worker: at zero replicas nothing polls, so notifications are
> staged correctly and then delivered whenever the next HTTP request wakes the app. Alerts stop
> arriving and *nothing reports an error* — from the registry's point of view every message is
> still pending and will be retried. One always-on replica is the cheap fix. Moving the pump
> into a Container Apps job on a cron schedule would let the API scale to zero; it is the right
> answer if that idle cost matters and the wrong one to build before it does.
>
> **(b) VNet integration.** The template reaches PostgreSQL through the allow-Azure-services
> firewall rule, because a VNet-integrated environment with a private endpoint roughly triples
> the resources on a first deployment. That is fine for an evaluation and not fine for
> production, and the gap should close before anyone real depends on it.

### 29. ~~A signup writes no audit entry~~ — done

**Resolved 2026-08-14 with option (b).** A separate `DeploymentEvent` trail, not tenant-scoped
and not tenant-filtered, holding what happened above the tenant line: `ORGANISATION_CREATED` and
`INSTANCE_CLAIMED` today.

Option (a) — letting `IAuditLog.Append` take an explicit tenant — would have worked and was
rejected for the reason recorded when the decision was written: it makes cross-tenant audit
writes possible from anywhere, and these are not the same kind of record anyway. Operator events
want a different retention and a different reader from "who changed this subject".

`TenantId` on the row is **data, not scope** — which organisation the event concerns — and the
row is staged on the same change tracker as the organisation, so a refused signup leaves no event
behind and a successful one cannot exist without its record.

> **No HTTP reader, deliberately.** The rows span tenants and there is no operator role to gate
> an endpoint with: in self-hosted the instance owner *is* the operator, in Cloud they are not,
> so one gate cannot be right in both profiles. `IDeploymentLog.ReadAsync` exists so tests and an
> eventual operator console read the same way. **Building that role is the follow-up**, and it is
> also what tenant suspension and billing events will need.

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
| ~~M7~~ ✅ | ~~Contract-resolution caching, deferred from M2.1~~ | **Discharged.** `ContractCache` at the specified 60 s, keyed by topology rather than subject; batch resolve at warm-up, on-demand for anything undeclared, stale-on-failure. The oldest outstanding commitment in this register, and closing it turned `/contracts/resolve` from an endpoint nothing called into the feature it was built to be |
| ~~M2.5~~ ✅ | ~~Verify the AMQP 1.0 header conversion~~ | **Discharged.** Measured on `rabbitmq:4.1-management`: `concordat-*` arrive as application-properties, and an `x-`-prefixed control header on the same message is demoted to an annotation, so the prefix rule is load-bearing rather than precautionary |
| **M7** | Hard delete: no registered consumers + force flag + audit entry | Soft delete is all that exists today |
| ~~M7~~ ✅ | ~~Adopt the derived environment ids, or migrate `subject.environment_id`~~ | **Discharged by decision 23.** The row is now created on first write *carrying the derived id*, so nothing migrates and no subject is orphaned — the id was always deterministic, and this fills in the record it always implied |
| ~~M7~~ ✅ | ~~`GET\|PUT …/registration-policy`~~ | **Discharged, and the routes were the smaller half.** `Environment.RegistrationPolicy` had been stored since M7.1, defaulted to `CiOnly` for production-sounding names, and documented in three places as "enforced server-side, which is the whole point" — while no handler read it. Now enforced on subject creation and version registration, refused with 403 `registration_policy_forbids`. See decision #33 for how a pipeline is told apart from a producer |
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
| Impact analysis is evaluated **FORWARD**, not under the subject's own policy | [M7.4](plan/M7-governance.md) | Low, and it is the feature. Registration asks "can the new schema read old data"; impact asks the opposite question about a different party. Getting this backwards would report every added required field as breaking every consumer |
| The surface comes from the subject's effective policy; only the mode is forced | M7.4 | Low. A subject governed at `WIRE` tolerates JSON-name changes by design, and reporting its consumers as broken by one would contradict the verdict the registry gave the change |
| A `latest` consumer is reported as `FOLLOWS_LATEST`, never as safe or broken | M7.4 | Low, but user-visible: it is a third answer, and tools have to handle it |
| A range selector is judged at its floor | M7.4 | Low. `>=1` claims to handle version 1 onward, so version 1 is the reader that has to survive |
| A service registration is keyed on (environment, name); instances are not recorded | M7.4 | Low. Recording instances would turn a rolling deploy into fifty rows that all say the same thing |
| Registrations go stale after **30 days**, reported not hidden | M7.4 | Low, and it is a reporting hint only — never a reason to drop a consumer from the report |
| Audit entries are written in the same transaction as the change; refusals are not recorded | M7.4 | **Revisit at M8** — see #24 |
| Health checks are deliberately not audited | M7.4 | Low. A probe on a timer would produce most of the rows in the table |
| A breaking promotion lands as `AWAITING_APPROVAL` rather than being refused | M7.4 | Low, but user-visible. ADR-017 applied consistently; refusing would make promotion the one operation whose breaking changes cannot be reviewed |
| Promotion **creates** the target subject when absent, carrying the source's format, owner and content model | M7.4 | Low, and it contradicts M1.6's "no implicit creation" on purpose: promotion is precisely the flow where the target legitimately does not exist yet. It also removes a real bug — the CLI's client-side promotion hard-coded JSON |
| A schema-id mismatch across environments **throws** rather than returning a failure | M7.4 | Low. It means content addressing is broken, which is a defect in the registry, not something the caller can fix by asking differently |
| `VersionStatus.Dismissed` is a fourth state, not a reuse of `Rejected` | M7.4 | Low, needs a migration. Rejection is a reviewer's judgement; a dismissal says only that nobody is asking any more, and it names no decider |
| Dismissed **and** rejected semver labels are excluded from the increasing-label check | M7.4 | Low, and required: a withdrawn `2.0.0` would otherwise strand the subject's version line forever |
| `Subject.Revision` exists to dirty the root row so `xmin` engages | M7.4 | Low, needs a migration. It closed a hole that already existed: `Reject` touches only a child row and slipped past optimistic concurrency entirely |
| A CLI `4xx` is now exit **1**, not exit 3 | M7.4, `RegistryApi.EnsureAsync` | Low, but it changes what a pipeline does. Exit 3 means "retry, the registry is down"; a deliberate refusal was telling CI to retry until timeout |
| `concordat impact` gates by default; `--warn-only` opts out | M7.4 | Low, and it matches `check`. A warning in a log nobody reads is not a gate |
| **MailKit** added for SMTP delivery | [M7.5](plan/M7-governance.md) | Low. MIT, confined to Infrastructure, reaches no shipped client package. It is what Microsoft's own `System.Net.Mail.SmtpClient` documentation points at — that type is explicitly not recommended for new development and cannot negotiate STARTTLS properly |
| Notification delivery is **at-least-once**; every message carries an id to deduplicate on | M7.5 | **Cannot be tightened later without a receiver-side contract change.** Exactly-once would need an acknowledgement protocol the receivers do not have |
| A message with no matching subscription is marked **delivered** | M7.5 | Low, and required: nobody subscribing is the commonest configuration, and treating it as undelivered grows the table forever |
| Partial delivery counts as success; the healthy subscriber gets a duplicate on retry | M7.5 | Low, given at-least-once. The alternative is silence for the broken subscriber |
| Failed messages are **parked** after 5 attempts, never deleted | M7.5 | Low. A message nobody could deliver is evidence about the channel |
| Backoff doubles from one minute, capped by the attempt limit | M7.5 | Low. Deliberately coarse — an endpoint that is down stays down for minutes |
| `http://` webhooks are refused outright | M7.5 | Low, and user-visible. A webhook body names subjects, versions and reviewers |
| An unknown event token in a subscription is refused, not ignored | M7.5 | Low. The alternative is a subscription that is configured, enabled, and silently delivers nothing |
| The pump is in-process on every instance, with no leader election | M7.5 | Low today, and it is why at-least-once is the stated contract. Cloud (M9) may want a single writer; the receivers' contract does not change either way |
| Ownership changes are audited but not notified; only deprecation is | M7.5 | Low. Deprecation is a signal to every consuming team; an owner change is not |
| The API integration harness removes `OutboxPump` from the host | M7.5, `ApiFactory` | Low, and necessary: a background timer draining the outbox mid-assertion makes every count a race. Notification tests pump deliberately |
| Three roles — `READER`, `ADMIN`, `OWNER` — rather than a permission matrix | [M8.1](plan/M8-identity.md) | Low. Per-subject grants are a reasonable later refinement (`Subject.Owner` exists for it), but shipping one before anyone has enough subjects to need it makes "can this person change a contract?" a longer question than it deserves |
| `org:admin` does **not** imply `subject:write` | M8.1 | Low, and deliberate: acquiring schema authority by managing the org would be a way around ADR-018 |
| Passwords have a 12-character minimum and no character classes | M8.1 | Low. Composition rules measurably push people towards predictable substitutions; NIST dropped them for that reason |
| Password hashing delegated to `PasswordHasher<T>` from `Microsoft.Extensions.Identity.Core` | M8.1 | Low. None of Identity's stores, managers or UI are used. Argon2id is defensible and would mean a third-party cryptographic dependency replacing a reviewed in-framework one |
| API key secrets are SHA-256, not a slow KDF | M8.1 | Low, and correct: a slow KDF protects a *low-entropy* secret, and this is 256 bits from a CSPRNG. It would add milliseconds to every authenticated request and buy nothing |
| A credential is `cdt_<keyId>_<secret>`, both halves alphanumeric | M8.1 | **User-visible format.** Changing it invalidates every issued key. Not base64url: that alphabet contains the `_` separator |
| Authentication failures are indistinguishable, and a failed sign-in still runs a key derivation | M8.1 | Low, and both are the point: the distinction is worth nothing to a legitimate caller and enumerates accounts for everyone else |
| A key may not grant more than its issuer holds | M8.1 | Low, and load-bearing: without it, reaching the issue endpoint is a privilege escalation |
| A browser session is a short-lived API key, not a second token format | [M8.2](plan/M8-identity.md) | Low. One thing to verify, one thing to revoke; session keys are excluded from the key listing so they cannot bury the standing ones |
| `AllowAnonymousUntilClaimed` defaults to **on**, and disables itself once an account exists | M8.2 | **See #27.** Never applies to a request that presented a credential and failed to verify |
| Scope enforcement is an endpoint filter with a structural test, not a check in each handler | M8.2 | Low, and it is what makes the convention real: a forgotten check is silent and works for everyone |
| `StampTenant` now stamps **shadow** properties only | M8.1, `ConcordatDbContext` | Low, and a fix: M8's `Membership` and `ApiKey` carry a real strongly-typed `TenantId` with the same name, and matching on the name alone threw on the first sign-in |
| A key stores the actor it attributes requests to, rather than deriving one | [M8.2](plan/M8-identity.md) | Low, needs a migration. Deriving would mean a user lookup on every authenticated request — the SDK's hot path — or string-munging a label, which breaks the first time somebody names a key `session for the demo` |
| M7.4's `unknown` audit actor replaced by the real caller wherever one exists | M8.2 | Low, and the point of M8: those handlers said `unknown` because there was nobody to name |
| The web app probes `/v1/auth/status` in an app initializer, and a failure is swallowed | M8.2 | Low. Without the probe the app cannot tell "signed out" from "nobody has signed up"; refusing to render when the registry is not up yet leaves the user with nothing |
| The sign-in screen doubles as first-run setup | M8.2 | Low. The two differ by one fact the server already knows, and a separate `/setup` route would be reachable after setup and have to redirect |
| Sign-out drops the credential locally and does not revoke the session key | M8.2 | Low. It expires on its own; revoking would mean one row deleted per tab closed. A user who needs it gone sooner revokes it from the keys screen |
| `*cdIfScope` and `scopeGuard` live in `core/auth`, not `shared/ui` | M8.2 | Low, and forced by the boundary lint: `shared` may not depend on `core`, and both read the session store |
| An unrecognised scope from a newer server is dropped client-side | M8.2, `AuthApi` | Low, and the safe direction: it hides an affordance rather than showing one. The server remains the authority |
| The tenant is in the derived environment-id preimage, except for `TenantId.SelfHosted` | [M9.1](plan/M9-cloud.md) | **Cannot be changed for Cloud once an organisation exists.** The self-hosted exception is what makes it a zero-migration upgrade: existing ids were computed without a tenant segment, and changing them would point every subject at an environment that no longer exists |
| `ITenantContext` is scoped in **both** profiles | M9.1 | Low. A singleton for self-hosted would be a lifetime somebody has to revisit the day Cloud is switched on, under time pressure, and getting it wrong means a request holding another request's tenant |
| `IEnvironmentResolver` is now scoped, not singleton | M9.1 | Low, and forced: it reads the current tenant, and a singleton would answer with whichever organisation was in scope when it was first resolved |
| Sign-in takes an optional organisation slug and refuses an ambiguous one | M9.1 | Low, and user-visible only for someone in several organisations. Choosing for them would drop somebody into the wrong one silently |
| An anonymous Cloud caller resolves to `TenantId.SelfHosted` — an organisation nobody belongs to | M9.1 | Low, and deliberate: an empty view beats a full one. It is not a real organisation in a Cloud deployment |
| `Tenant` has an immutable slug and a mutable name | M9.1 | Low. The slug is a DNS label and appears in URLs; renaming it breaks links somebody has already sent |
| The Cloud test fixture sets the profile with `UseSetting`, not `ConfigureAppConfiguration` | M9.1, `ApiFactory` | Low, and a trap worth knowing: under minimal hosting the app reads configuration and builds before any `ConfigureAppConfiguration` callback runs, so the value is present and was never seen. It failed silently as a profile that stayed self-hosted while the test believed it was Cloud |
| Usage is measured by `COUNT(*)`, not by an incrementing meter | [M9.3](plan/M9-cloud.md) | Low today, and revisit at scale. A counter has to stay correct across a failed transaction, a restore and a retry; a count is correct by construction. If the counts ever get slow, a materialised view is a smaller change than a counter |
| **API requests are not metered** | M9.3 | Low, and stated rather than approximated. A write per request on the SDK's hot read path would cost more than the thing measured. Needs sampling or an aggregation pipeline |
| Plan limits are in code, not configuration | M9.3 | Low. A limit editable per deployment is a limit no invoice can be reconciled against. Enterprise is unlimited precisely so "negotiated" does not mean "someone typed a number into a config file" |
| `plan_limit_reached` is **402**, not 403 | M9.3 | Low, and user-visible: it is the difference between "upgrade" and "ask an admin for a scope you already have" |
| A past-due subscription still allows creation; only a cancelled one refuses | M9.3 | **Deliberate and worth confirming.** A card that expired over a weekend must not stop a team registering a schema. If you want a stricter dunning policy, this is the line to move |
| A downgrade is never refused for being over the new limit | M9.3 | Low, and it protects the customer: refusing strands them on a plan they no longer want, and the only escape is retiring subjects other teams depend on |
| A missing subscription row allows everything rather than defaulting to Free | M9.3 | Low, and it fails open on purpose — the row is created in the same transaction as the organisation, so a missing one is an internal inconsistency, not a free-tier customer |

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
