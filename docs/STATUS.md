# What is missing

**As of 2026-08-15**, except [the compatibility explanations, 2026-10-14](#compatibility-explanations-visible-text-and-a-disclosure-not-a-tooltip-2026-10-14),
[a tolerant audit read, 2026-10-14](#the-audit-read-returns-an-action-its-build-does-not-know-as-stored-2026-10-14),
[subject prefix search, 2026-10-14](#subjects-are-searched-by-the-start-of-their-name-2026-10-14),
[contract bindings that can be changed and removed, 2026-10-14](#a-contracts-bindings-can-be-changed-and-removed-2026-10-14),
[the policy-change notification, 2026-10-14](#a-policy-change-notifies-subscribers-and-every-subscription-with-no-event-filter-is-one-2026-10-14),
[inheriting and the audited policy change, 2026-10-13](#a-subject-with-no-policy-of-its-own-follows-its-environments-default-and-a-policy-change-is-audited-2026-10-13),
[the subject settings, 2026-10-13](#a-subjects-owner-and-policy-are-editable-and-the-registry-did-three-things-nobody-had-written-down-2026-10-13),
[the create-subject screen, 2026-10-13](#the-create-subject-screen-and-a-500-that-was-not-what-it-looked-like-2026-10-13),
[the schema editor and the web container, 2026-10-13](#the-schema-editor-and-the-web-container-2026-10-13),
[the web app's pages, 2026-10-13](#the-web-apps-pages-2026-10-13) and
[Security hardening, 2026-08-16](#security-hardening-2026-08-16)
below, added later and not re-verified against the rest of this file. Test counts
throughout were re-run and corrected 2026-08-30, and **re-measured 2026-10-14 (again, after the policy-change notification, the unreadable-row fix and the binding editor): 1,762 .NET,
1,504 Angular, 234 browser** (the sections below that name an older figure are that day's snapshot). A consistency audit on **2026-09-24** corrected
the prose that had gone stale against shipped code — four places where this file claimed
something was missing that is not: M4.3's pages, M4.5's write-route test, the `Tenant`
aggregate, and CI's job count — and found three product gaps, recorded below. A companion to
[PLAN.md](PLAN.md), which records what was built, and
[DECISIONS-PENDING.md](DECISIONS-PENDING.md), which records what has not been decided. This
file records what is **not there**, so the gaps are in one place rather than distributed across
ten milestone files as unchecked boxes.

Everything under "verified locally" below was run on this machine today, against the real
container image and a real PostgreSQL. Everything else is claimed on the strength of tests.

---

## A nightly dependency audit, 2026-10-14

`82656ca` fixed one advisory by hand and said the next would recur. The question was whether CI could have caught it, and the
answer is that **CI's restore is already live; its cadence is the hole.** Measured with a one-project probe that references
`System.Security.Cryptography.Pkcs` 10.0.0, a throwaway `NUGET_PACKAGES` and `NUGET_HTTP_CACHE_PATH`, and
`HTTPS_PROXY` pointed at a dead port to tell "re-audited" from "replayed":

| Restore | Result |
|---|---|
| No `obj/`, populated global-packages (what CI does after a cache hit) | audits: NU1902 and NU1903 |
| Immediately again (`All projects are up-to-date for restore`) | **replays** the stored NU1902 and NU1903 from `project.assets.json`, with the network dead and an empty HTTP cache: it did not consult the data |
| `--force`, `--no-cache`, or `obj/` deleted, network dead, HTTP cache empty | tries to fetch the data: **NU1900** (could not load it) |

The vulnerability data (`vuln_index`, `vuln_data_base`, `vuln_data_update`) lives in the HTTP cache beside the package list, not in
the global-packages folder. `ci.yml` caches `~/.nuget/packages` and nothing else, and a fresh checkout has no `obj/`, so a CI
restore is never a no-op and always reads live data; the cache only saves the downloads. What a no-op restore does is hide a new
advisory from a **developer's** machine until `obj/` is cleared or a package changes. Nothing in CI is stale. It simply only
runs when someone pushes.

`.github/workflows/dependency-audit.yml` (nightly, `workflow_dispatch` too; `contents: read`, `issues: write`; the same
pinned actions as `ci.yml`; no cache) runs `scripts/dependency-audit.mjs`, which does `dotnet restore Concordat.slnx --force` and
parses NU1901 to NU1904. On a finding it ensures the `dependency-audit` label exists (`gh label create --force`), looks for an
**open** issue with exactly the same title, and comments on it, or else opens one. The title (`Dependency audit: NU1902, NU1903 in
System.Security.Cryptography.Pkcs, …`) is the idempotency key: it names codes and packages and not versions or advisory ids, so a
second advisory against a package already reported is a comment and a different package is a new issue. The run also exits 1, so
it is red in Actions. NU1900 (audit data unreachable) and any other restore failure exit 2 and open **no** issue: a network blip is
not a vulnerability. `--dry-run` prints instead of calling `gh`; `--from-log FILE` parses a saved restore; `--self-check` restores a
project that references a version with permanent advisories (Pkcs 10.0.0) and fails unless the parser finds them, which is the guard
against NuGet rewording its diagnostic and every later night passing silently (the workflow runs it first).

It is Node (`.mjs`, like `generate-protocol-docs.mjs`): one implementation for `ubuntu-latest` and the Windows development
machine, where bash and PowerShell would be two. 21 `node:test` cases (`scripts/dependency-audit.test.mjs`, run by CI's `protocol docs
gate` job) pin the parser, the title, the log excerpt, the `gh` sequence and the exit codes against output captured from a real
restore, which turned out to differ from the single-project shape in one way the first version missed: a solution restore appends
` [<path>\Concordat.slnx]` to every diagnostic.

**Verified:** `node --test scripts/dependency-audit.test.mjs` 21 of 21; 14 mutants (parser regex, de-duplication, versions in the
title, never commenting, label without `--force`, NU1900 recognition, noise filter, fence, title length, a dry run that calls `gh`,
warnings ignored, project list uncapped, comment wording, exit 0 masking a finding) each killed by a test; the script against a real
`dotnet restore` of the probe (exit 1, the two advisories, the issue it would open), against this repository **before 82656ca's pins
(exit 1, seven findings across Pkcs and Xml, as the commit said)** and **with them applied to a scratch copy (exit 0)**;
`--self-check` green, and red (exit 3) with the regex broken; `actionlint` clean on both workflows. **Not verified:** the workflow
on GitHub (`gh workflow run dependency-audit.yml` then `gh run watch`, once pushed); a real `gh issue create` (the sequence ran against a
fake `gh`, and the flags were checked against `gh --help`, with a read-only `gh issue list --label` confirmed to return `[]` for a label that does not exist yet); `dotnet restore` on
Linux (the parser is tested on Linux-shaped lines, but the real thing has only run on Windows).

---

## Compatibility explanations: visible text and a disclosure, not a tooltip, 2026-10-14

The last open box of M4.4 is closed. The prototype explained each compatibility mode and surface in a hover
tooltip; the web app now explains all seven modes and all three surfaces, and the two directions a finding carries,
**where the screen shows or chooses one**: the subject page's Compatibility cell, the subject policy editor, the
create-subject form, the environment settings (the summary and the form), and the headers and finding lists of the
diff, registration and approval screens. Impact analysis shows no mode or surface, so it has nothing to explain.

**It is not a tooltip, and that is the decision.** A `title` is not reachable from a keyboard and not on a touch
screen, and an overlay that opens on focus covers the next field at 390px. A select has a sentence under it, tied to
it with `aria-describedby` (a description, not a name, so every `getByLabel('Mode')` in the e2e specs still finds
exactly one select), and each screen has a native `<button aria-expanded>` that opens the terms in the flow of the
page. Closed, it puts no token in the DOM, because the e2e specs find a policy by `getByText('FULL_TRANSITIVE',
{ exact: true })`, which matches hidden text. The repo has no tooltip primitive in `shared/ui` (Spartan's brain
package ships one; it is not wired in), so none was added.

**One source of truth.** `COMPATIBILITY_MODE_MEANING`, `COMPATIBILITY_SURFACE_MEANING` and
`COMPATIBILITY_DIRECTION_MEANING` in `domain/registry/compatibility.ts`, each sentence taken from ADR-016, the XML
docs on `CompatibilityMode`/`CompatibilitySurface` and the JSON checker's tests (`NonTransitive_ComparesOnlyAgainst
TheHighestOrdinal`, `Transitive_ComparesAgainstEveryPriorVersion`, `Full_ReportsBothDirections`,
`ModeNone_SkipsCheckingEntirely`, `IntegerWidenedToNumber_PassesWireJsonAndFailsSource`, `ChangingFormat_IsSourceOnly`,
`AWirePolicy_IsEffectivelyNoCheckingForJsonSchema`), which the comments cite. `compatibility.spec.ts` holds each record to
exactly the token list, bounds the length, and pins the claims that were checked against the engine. **The sentence for
`WIRE` says it blocks nothing for a JSON subject**, because that is true and because without it `WIRE` reads as a looser
version of a check that does something.

**What was verified.** Unit specs for the constants, the two shared components and each screen (red first, then
mutation-checked). Browser specs against a private registry and dev server: `settings.spec.ts` runs light and dark at
390px (each select described by its meaning and following the choice; opened with Enter and with Space; every term on
the page with no sideways overflow; text contrast at least 4.5:1, measured by painting the ancestors' backgrounds on a
canvas) and a tap on a touch context; `new-subject.spec.ts` and `registry-data.spec.ts` read the real engine's finding and
the real subject page. Mutations seen red: a panel `min-w-[480px]` (overflow), the text faded to 40% (contrast, both
themes), `aria-expanded` removed with the button made a `div` (focus and tap), `aria-describedby` removed, the panel always
rendered, a changed sentence. Screenshots at 1280 and 390px in both themes were looked at. **No spec creates data.**
**Not covered:** a screen reader (the pattern is a native `button` with `aria-expanded` and `aria-describedby`, and was not
listened to), right-to-left, and the production image (`docker/web*` untouched, `smoke-web-image.sh` not re-run).

**Found, not fixed.** On the subject page at 390px a long mode (`FULL_TRANSITIVE`) in the Compatibility cell runs into the
Content model cell beside it: the cell is half the screen and the token is 16px bold monospace. It predates this change.

---

## The audit read returns an action its build does not know, as stored, 2026-10-14

The hole recorded by ADR-033 and again by ADR-035 is closed ([ADR-036](adr/036-the-audit-read-tolerates-an-action-its-build-does-not-know.md)).
`AuditEntryConfiguration` converted the stored action into an `AuditAction` with a converter that threw for a token the build had
no member for, so **one row written by a newer build failed `GET /v1/audit` for everyone, for as long as it existed**, and the
trail is the one table whose rows cannot be removed through the application. **Measured first, with the previous build against
the real database** (the registry on `:5101`, one row inserted by SQL with `action = 'A_FUTURE_ACTION'`): `GET /v1/audit` answered
**500**, `?env=` for the row's environment the same, `?action=SUBJECT_CREATED` answered `[]` (the row is excluded by SQL before it
is read), `?action=A_FUTURE_ACTION` answered 400. After: **200**, with the row among the others.

**What it does, and the four decisions the ADR defends.** `AuditEntry.ActionToken` is the column as text, with no converter and no
setter; `AuditEntry.Action` is now `AuditAction?`, null for a token the build does not know, so there is no fake enum member and
`WireTokenTests`' enumeration of the enum still means what it says. The wire spells a known action from the catalogue (byte-identical)
and an unknown one as stored. **A filter naming an unknown action is still `400 audit_filter_invalid`** (a typo must not read as
"nothing happened", and accepting a stored unknown token would be an unindexed scan on a request any read scope may make). **Nothing
can write the row back**: no setter, `Record` takes only an `AuditAction`, the repository has `Append` and `QueryAsync`, and the
integration test asserts the row is byte for byte what was inserted after it was read. **The audit page shows it as received,
neutral, with a `title`** ("This version of the app does not know this action…") and none on a known one. No migration: the column
is unchanged. The model snapshot's property name changed in one line (`Action` to `ActionToken`); `dotnet ef migrations
has-pending-model-changes` answers *No changes* before and after.

**What the spec found that reading the code did not:** the table's tone lookup is a plain object, so an action whose token is an
`Object.prototype` member (`constructor`, `toString`, `__proto__`) rendered a badge with **no colour classes at all** (red first: five
cases). It is now an own-property lookup. No registry action is spelt that way; "as received" must not depend on that.

**Visual check, on a private registry** (`:5101`, dev server `:4311`; rows inserted by SQL and removed after, 0 left): the audit page
with `A_FUTURE_ACTION` and `constructor` rows at 1280 and 390 px, light and dark, fresh load, one second settled, no console errors,
no horizontal page overflow. **The unknown row looks exactly like `SERVICE_REGISTERED`** (neutral pill); only its hover text differs.
That is the decision (a guessed severity would be a mislabel), and also the thing most likely to be reversed: a `warning` tone or a
dashed outline would make it seen, at the price of crying wolf on every ordinary new action.

**No browser spec was added.** A row the registry will not write cannot be made by the browser suite, and one inserted by SQL into the
shared database would be a permanent row in a table that is append-only, which is the opposite of what this change is for. The
integration test (real PostgreSQL, deleted in a `finally`), the table and page specs, and the private-stack check above hold it.
The existing `audit.spec.ts` (8 tests) was run against the private stack and passes.

**Found, not fixed (separable):** the other two stored-token converters throw the same way, `DeploymentEvent.Action` (nothing reads
`deployment_event` today: `IDeploymentLog.ReadAsync` has no caller) and `EnforcementViolation.Side`. **It protects a rollback to a
build that contains it, not one to an earlier build.**

### What was verified, and what was not

Run on this machine, on Windows. **Verified:** `Concordat.Domain.Tests` (568), `Concordat.Application.Tests` (236) and
`Concordat.Api.IntegrationTests` (317, Docker running) in full; `dotnet format --verify-no-changes`; the web unit suite (1,512), lint,
prettier, `codes:check`, `monaco:check` and `ng build`; `docs/api/openapi.v1.json` regenerated by the build (one sentence on the
endpoint's description). **Red first:** the integration test answered 500; the web spec failed on the five prototype tokens and the
hint. **Mutation-checked:** the throwing converter put back (the real-PostgreSQL test red), the projection echoing the stored text (the
known-action-in-another-case test red), the projection reading the enum unconditionally (four red), `TryResolve` throwing (eight red
across the domain and handler tests), a setter on `ActionToken` (the no-way-to-rewrite test red), the tone lookup back to a plain
index (five red) and the hint inverted (two red), each restored and confirmed green. **Not run:** the whole .NET suite, the whole
browser suite, the production web image, CI; the other converters above.

---

## Subjects are searched by the start of their name, 2026-10-14

The hole recorded since M1.6 is closed ([ADR-038](adr/038-subject-prefix-search.md)). `GET …/subjects?prefix=` lists the
subjects whose name begins with the prefix, and the web app's search box is that filter. What was wanted was already in the
product and faked: the box filtered the list in memory, by substring and case-insensitively, on the name *or the owner*. The
CLI has no prefix anywhere (`export` wants the whole list), so the box was the only caller.

**The choices, each reversible, and the two that are the maintainer's.**

- **Ordinal and case-sensitive**, because names are (`OrderCreated` and `ordercreated` are two subjects). A prefix is
  **a literal**: it is a valid name, or a valid name and one more dot (`SubjectNamePrefix`), trimmed as a name is. Anything else
  (`%`, a backslash, a hyphen, `..`, a comma, a NUL, a lone surrogate, invalid UTF-8, more than 512 characters) cannot start a name,
  so it is **`200` and an empty list, not `400`**, and the database is never asked. *Maintainer's call:* `400 subject_name_invalid`
  is the other consistent answer; it was rejected because a box typed into a character at a time would show an error banner for
  `acme-`.
- **`starts_with(name, prefix)`, not `LIKE`.** `_` is both a legal name character and a LIKE wildcard, so an escape forgotten
  anywhere would make `a_` find `ab`; `starts_with` takes a literal and has nothing to escape. Reached through a mapped function
  (`ConcordatDbFunctions.StartsWith`) with the column as `EF.Property<string>(s, nameof(Subject.Name))`, which is what the hole row's
  `ComplexProperty` / shadow-column proposals were for, without touching the model or the schema. `StartsWith` itself throws
  through the converter (`InvalidCastException`; measured).
- **No index, no migration.** `EXPLAIN` on the real `subject` table with 65,000 rows (5,000 in the environment): the unfiltered list
  4.7 ms, `starts_with` 1.2 ms (22 rows) and 1.9 ms (1,000 rows) on the existing `(tenant, environment, name)` index, which
  bounds the work to one environment's subjects; with a candidate `varchar_pattern_ops` index 0.075 ms and 1.1 ms. At 100,000
  subjects in one environment the planner chose a sequential scan, 19 ms against 0.05 ms with the index. The DDL for that day is in
  the ADR. A plain btree does not serve `LIKE` here either: the database's collation is `en_US.utf8`, which the planner treats as
  non-C even on the alpine image.
- **The box matches the start of a name, and no longer the middle or the owner.** *Maintainer's call, and a loss of function:*
  the old box found `ordercreated` inside `acme.orders.OrderCreated` and `billing-team` in the owner column. Both are real wants
  and neither can be paged; they are separate features (an owner parameter; a substring match has no index). The box says what it
  matches under itself and in its empty state.

**The screen.** Debounced 250 ms; one pipeline for a load and a search, `switchMap` so a stale request is cancelled (the HTTP call
is unsubscribed, not just ignored) and a reload does not wait out a keystroke; the rows stay on screen, dimmed and `aria-busy`,
while an answer is awaited; **an empty answer to a prefix is its own state** (`noMatches`, with a Clear button and, when no name
could ever match, the character that rules it out) and is not "No subjects yet", which offers to create the first subject and hid the
box; the box lives outside the branches so its own answer never unmounts it (a test holds that it is the same element and keeps
focus). `maxlength` 512, because Kestrel answers `414` past about 8 KB of request line before the route is reached.

### What was verified, and what was not

Run on this machine, on Windows, with `--filter`ed .NET projects only (the full suites run once, at integration). **Verified:**
the new .NET tests (the domain value object, the handler, the repository against real PostgreSQL, and the HTTP tests hosted as
`Development`, the environment a binding failure once hid in), the Domain and Application projects whole, `dotnet format
--verify-no-changes`, the whole web unit suite, lint, prettier, `codes:check`, `monaco:check` and `ng build`, the new browser spec
(12 tests) with the registry-data, new-subject and design-system specs beside it (79 passing, against a private registry and
database and `ng serve`), and the box's states at 1280 and 390 px in light and dark, with no horizontal overflow. The real registry corrected one
test: a 20,000-character prefix is `200` in the in-process server and `414` from Kestrel. **Red first:** the domain, handler,
repository and HTTP tests against stubs that ignore or accept everything (behaviour failures, not compile errors), and the store
spec against a stub store. **Written implementation-first, mutation-checked instead:** `checkSubjectNamePrefix` and the
`listSubjects` parameter. **Mutation-checked:** the grammar (NUL tests red), case-insensitivity, a wildcard `_`, no environment
filter and no tenant filter, `IsNullOrWhiteSpace`, the prefix lower-cased in the store, the debounce, `switchMap`, the box inside
a replaced branch and the prefix pattern. **Not mutation-checked individually:** the browser spec's case and underscore tests
(the same behaviour is mutated at .NET level), and its 390 px and console tests. **Not run:** the production image, the CLI (it
has no prefix flag), two registries of different builds, CI.

---

## A contract's bindings can be changed and removed, 2026-10-14

The hole recorded on 2026-10-13 is closed ([ADR-035](adr/035-contract-bindings-have-an-identity.md)). A binding had no
identity, so there was an `add` for each kind and nothing else, and a binding added **without a precedence could never be
separated from a later overlapping one**: the registry's refusal says to "give the two bindings different precedence
values", and the first had none to change. Every binding now has an `id` (the primary key of its table, returned by every
route that returns a contract), and four routes address it: `PUT` and `DELETE` on `…/publishes/{id}` and
`…/consumes/{id}`, `contract:write`, each answering `200` with the contract. The contract's page offers **Change** and
**Remove** on every row, absent without the scope, each taking two clicks and saying what it does. The counts are
**1,762 .NET** (from 1,710), **1,504 Angular** (from 1,435) and **234 browser across twenty specs** (from 225 across nineteen).

**What can change, and what cannot.** A publish binding's **subjects and precedence**; a consume binding's **subjects**.
The exchange, pattern, virtual host and broker are what a binding *is*, so changing them is a removal and an addition. A
`PUT` replaces, so an absent precedence clears it. **The overlap rule is run again for an edit** (equal precedence with an
overlapping binding, clearing the one that separated two, or pointing one at subjects the other does not carry are each
`409 binding_conflict`, naming a key both match, and leave the binding as it was). **A `PUT` that changes nothing answers `200`
and writes nothing**; the same subjects in another order are a change. An unknown id, or a publish id used on a consume
route, is `404 binding_not_found` (a new code).

**Audited as two new actions, as the caller, in the same unit of work:** `CONTRACT_BINDING_CHANGED` (the detail names only
what changed: `publish orders / orders.#: precedence none -> 1`) and `CONTRACT_BINDING_REMOVED` (the way the entry for adding it
reads). **Nobody is notified**, a decision and not an omission (ADR-035: a new event is a new subscription vocabulary, and
an empty filter means every event). The audit vocabulary is pinned on both sides, so it touched `AuditAction`/`AuditTokens`,
`WireTokenTests`, the web's `AUDIT_ACTIONS` and the audit page's filter (now 27 options).

**The migration changes the key of two tables, and the scaffolded one was wrong for any database with a row in it** (it altered
the integer key to `uuid`, which PostgreSQL refuses). It is written by hand: add the column, give every existing binding a
fresh `gen_random_uuid()`, make it the key, drop the integer. **Measured on the local compose database as it stood** (57 publish
and 28 consume bindings from earlier browser runs): the content of every binding hashed in a fixed order was identical before and
after, the counts unchanged, every id unique. **`Down` was run on a scratch database with rows in it** (up, down, up): the data
survived and the integer identity came back. The ids bindings now have did not exist before, so none is preserved.

**What the real registry and the browser corrected, which reading the code did not:**

- **A refusal left the person on the confirmation.** The first browser run of "refused a change that would undo the
  separation" waited for a Cancel button that was not there: after the registry refused, the panel stayed on "Yes, change the
  binding", so the second click would have resent the same refused request with nothing to edit. A refusal now returns to the
  form with what was typed (held by a spec that goes red without it, and by the browser).
- **The removal sentence was wrong.** It said a route "stops being governed" and, if no other *contract* governs it, resolves as
  ungoverned. The e2e removed the specific binding and the route was **still governed**, by the broader binding of the *same*
  contract. The sentence (and the endpoint's description) now say "another binding of this contract, or another contract", and a
  test holds the case.
- **A resolve in `dev` is not about one contract.** The first draft resolved `orders` / `orders.created` and got every
  contract in the environment that matches it (the other specs' and every earlier run's). The spec uses an exchange of its own
  per run.
- **The SDK keeps what it resolved.** `ContractTtl` is one minute by default (read from the SDK's options, not measured against a
  running service), so a change or removal is not instant for a running service; the confirmations say so.

**Found on the way, not fixed (separable):**

- **The audit read still throws on an action its build does not know.** Rolling back past this change breaks the audit page for
  as long as a `CONTRACT_BINDING_CHANGED`/`…_REMOVED` row exists, and the fix that made the notification pump tolerant of an
  unknown token does not reach it. A tolerant read (show the token as received, as the web app already does) would close this
  one and the `SUBJECT_POLICY_CHANGED` one ADR-033 recorded. *Closed the same day by ADR-036, below.*
- **There is no route that deletes a contract**, so a run of the browser suite leaves its contracts behind. This spec makes
  three per run (see the e2e README), and the first two runs of it, which were red while it was being written, left a few more.
- **A dependency advisory broke a fresh restore** — see the next section and the holes table.
- **The local compose database now holds** eight `e2e-bindings-*` contracts (three per run: two complete runs and three that
  went red part-way while the spec was written), one `probe-shots-*` contract left by the screenshot pass, and 41 contracts in
  all; nothing on `dev`'s defaults, `e2e-inherit`, `e2e-names`, `e2e-empty`, `e2e-follows`' default or the shared fixtures was changed.
  `concordat/api:prev` and `:mutant` still exist.

### A fresh `dotnet restore` failed a day after the same sources had built, 2026-10-14

Rebuilding the registry image for the above died with `NU1902`/`NU1903` on the API and Infrastructure projects. Five
denial-of-service advisories against `System.Security.Cryptography.Pkcs` and `…Xml` (GHSA-846f-f764-76mx, GHSA-2fmv-rv2c-59x7,
GHSA-7hqp-7c5m-4vq6, GHSA-c399-9853-6jmj, GHSA-r955-8q5j-3h83, published 2026-10-13, fixed in 10.0.13) had reached nuget.org's
vulnerability data, and this repository turns an audit finding into an error. Nothing fails until a restore runs, which is why
CI was still green at 07:58 UTC: **any fresh restore, so any push, would have failed.** `dotnet restore --force` on the host
reproduced it. Both packages are transitive (Pkcs 10.0.0 in the API, Pkcs and Xml 10.0.11 in Infrastructure), so they are
**pinned forward** in the transitive-pins group, the way SSH.NET was, not suppressed. It is its own commit (`538e8d9`) and does
not depend on anything else here. **It needs pushing before, or with, anything else**: without it the next CI run fails at restore.

### What was verified, and what was not

Run on this machine, on Windows, only. **Verified:** the full .NET suite (1,762, Docker running, `HeaderSurvival` included);
`dotnet format --verify-no-changes`; the web unit suite, lint, prettier, `codes:check`, `monaco:check` and `ng build`;
`docs/api/openapi.v1.json` regenerated by the build and committed; the migration against the live database and on a scratch
database both ways; the whole browser suite against the dev server **and the production image**, each after a registry restart.
**Red first:** the domain's fifteen new tests against stubs that throw, the handler tests against stub handlers, the HTTP tests
against a model with a pending migration (the registry refuses to start with one), the audit vocabulary pin, and the web specs
as compile and assertion failures. **Mutation-checked:** the domain (overlap check skipped, no-change never reported, removal
removing everything), the handler (a change recorded as an addition), the page (removal opening the change editor), the store (a
change filed under removal) and the edit check (a no-op draft sendable), each killed by the tests that should. **Not run:**
`smoke-web-image.sh`; the CLI, SDKs and GitHub Action (none manages bindings); two editors at once; the SDK's cache against a
live service; CI.

---

## A policy change notifies subscribers, and every subscription with no event filter is one, 2026-10-14

The maintainer decided what ADR-033 left open ([ADR-034](adr/034-a-policy-change-notifies-subscribers.md)): a subject's
policy change stages a notification. A new event, **`SUBJECT_POLICY_CHANGED`** (the eighth; the audit action's own
spelling, pinned equal by `WireTokenTests`), is staged by `SetCompatibilityPolicyHandler` in the **same unit of work** as the
audit entry and **under the same guard**: a change stages one message, a no-op (the policy the subject already has,
including inherit on an inheriting one) and every refused request (half a policy, an unknown subject, a retired one)
stage none. The message names the subject, **the caller** (as the approval, rejection, promotion and breaking-registration
messages do; the deprecation one is the only one that names nobody) and the old and new policy, spelled by the audit detail's
own function. It does not say which is stricter (modes on one axis and surfaces on another are not a total order).
The confirmation on the subject page says who is told. The counts are **1,705 .NET** (from 1,689), **1,435 Angular**
(from 1,425) and **225 browser** (from 223), against the dev server and the production image.

**The behaviour change, and the one to read first. Every existing "all events" subscription starts receiving policy
changes the moment this ships**, because an empty event set means every event (unchanged). A subscription that lists its
events does not. **An operator should check, before upgrading:** `GET /v1/environments/{env}/notifications` for each
environment, and every entry with `"events": []`. On a webhook it is a new `event` value the receiver may not expect; on an
email it is a new kind of message. There is no edit endpoint for a subscription's events, so keeping one out means **mute**
(which stops everything) or **delete and recreate it listing the events you want**, and a recreated webhook is issued **a new
signing secret** the receiver must be given. Policy changes are rare and need `subject:admin`: this is not a new stream.

**Measured against the real registry** (the rebuilt image, a probe environment named by timestamp, three dead-webhook
subscriptions: one listing only the new event, one listing the other seven, one with no filter; a registration, then a policy
change): the registration was attempted at the seven-event and the unfiltered subscription and **not** at the new-event-only
one; the policy change was attempted at the new-event-only and the unfiltered subscription and **not** at the seven-event one
(`Delivering … to … failed`, from the registry's own log). That is the upgrade note above, observed outside the test suite. (Run
twice: the first run's log went with the container when it was recreated for the production-image run, so the second, whose
log was read in full, is the one reported.) The
browser suite does not assert a staged message: the registry's pump drains the outbox every fifteen seconds, so a depth read
from a browser races it, and `notifications.spec.ts` checks the other half instead (the real screen subscribes to the new
event and the registry accepts, stores and returns it).

**The rollback cost is real, was measured, and is worse than the audit one** (ADR-033's, which only broke a page). The previous
image (`concordat/api:prev`) was run against the same database while a probe subscription and an undelivered
`SUBJECT_POLICY_CHANGED` outbox row existed:

- `GET /v1/environments/{env}/notifications` for the environment with the subscription: **500**, and the pump's pass failed
  with `A subscription names event 'SUBJECT_POLICY_CHANGED', which this build does not know.`
- with the subscription removed and only the **outbox row** left: every pass failed with `The outbox contains event
  'SUBJECT_POLICY_CHANGED', which this build does not know.` (`An outbox pass failed. The next pass will retry.`), and **a message
  staged in an unrelated environment with no subscribers, which a healthy pump marks delivered at once, stayed pending** over the
  following passes. So **one undelivered row stops notifications for every tenant** while the registry keeps accepting changes.
- On the new build, with the probe subscriptions deleted, the outbox drained to zero.

**Before rolling back:** delete subscriptions whose events name `SUBJECT_POLICY_CHANGED`, and mark undelivered
`SUBJECT_POLICY_CHANGED` outbox rows delivered (ADR-034 has the statement). Delivered and parked rows are never read by the pump.
The probe also found that this **hazard is not specific to this event**: any future event has it, since the outbox converter
throws on a token the build does not know and the claim query materialises the rows. That is pre-existing; this change is
what first made it reachable by a rollback, and it was listed in the holes table and **closed later the same day** for builds that contain the fix (the claim skips a row it cannot read; the dispatcher fails only the environment whose subscriptions it cannot read). Rolling back to a build without the fix still needs the cleanup above.

### What was verified, and what was not

Run on this machine, on Windows, only; GitHub has not run them. **Verified:** the full .NET suite (1,705, Docker running,
`HeaderSurvival` included); `dotnet format --verify-no-changes`; the web unit suite, lint, prettier, `codes:check`,
`monaco:check` and `ng build`; `docs/api/openapi.v1.json` regenerated by the build (two description strings: the policy `PUT`'s
"Nobody is notified" and the subscription route's "every event" note) and committed; the whole browser suite against the dev
server and the production image, each after a registry restart. **Red first:** the token pin, 7 of the 11 new handler
tests (the other four assert absence and pass before any code stages anything, so each has a mutant below), the HTTP test
that deepens the outbox and the delivery test (the no-op and refusal HTTP tests pass vacuously until a mutant stages), and 8
web specs, against the old sources; the new browser test against the old registry (the registry answered `400` and the
page showed no secret). **Mutation-checked against the final code, each restored and rebuilt:** the handler (built but never
staged: 7 red; staged for a no-op: 4 red, the two audit ones with it; staged before a refusal can happen: the
retired-subject test red; staged before the subject is even found: the unknown-or-invalid-subject test red; staged after the
commit: exactly the unit-of-work test red; wrong event: 2 red; the caller left out of the message: 4 red), the same widening
of the guard against HTTP (the two no-op tests red),
`Wants` reading an empty list as none (the delivery test red) and ignoring the filter (red), and the confirmation losing its
"no event filter" clause (its spec red). Looked at in a real browser, at 1280 and 390 px, light and dark, fresh load each
(`concordat.appearance` set before it): the policy confirmation and the notifications form with the new box ticked; both wrap
cleanly and neither overflows horizontally. **Not run:** `smoke-web-image.sh`; the CLI, SDKs and GitHub Action (they hold no
copy of the event list); a real webhook or SMTP delivery of this event; an environment with no row; CI (nothing pushed).

**What did not exist as the brief described.** No OpenAPI text enumerates the event list (`GovernanceEndpoints.cs` mentions
`ENFORCEMENT_VIOLATION` once, in a violations route, and the notification routes never list events), so the only
OpenAPI strings touched are the two above.

**The local compose database now holds**, from this stretch: three more `probe-*` environments (`probe-policy-…`,
`probe-bystander-…` and `probe-reproof-…`, each with a subject and a version, and no subscription left), the e2e additions (36 `SUBJECT_POLICY_CHANGED` outbox rows exist, all
delivered), and nothing changed on `dev`'s defaults, `e2e-inherit`, `e2e-names`, `e2e-empty`, `e2e-follows`' default or the three
shared fixtures. `concordat/api:prev` (this change's predecessor) and `concordat/api:mutant` still exist.

---

## A subject with no policy of its own follows its environment's default, and a policy change is audited, 2026-10-13

Both registry defects recorded in the section below are **fixed** ([ADR-033](adr/033-inherit-means-the-environments-default.md)),
as two separable changes: **A**, a subject with no policy of its own is judged by its environment's default, and **B**,
a policy change is written to the audit trail. The Angular suite is **1,414** tests (from 1,411), the browser suite
**223** across nineteen specs (from 218 across eighteen), run against the dev server **and the production image**, and the
.NET suite **1,664** (from 1,624). **Measured, production build:** the initial bundle is unchanged at 411.36 kB raw; the
subject page's chunk went 28.39 → 28.57 kB.

### A. Registration, the dry run and the diff read the environment

Registration, the dry-run `check` and the diff now
resolve an inheriting subject's policy against its environment's default, through one function
(`EnvironmentPolicies.EffectiveFor`), as impact analysis and promotion always did.

**Measured, before and after, against the real registry** (new environments and subjects named by timestamp; `dev`,
`e2e-inherit` and the shared fixtures untouched). An environment defaulting to `NONE`, an inheriting subject, v1 then a
breaking v2: **before**, v2 was `AWAITING_APPROVAL`, the dry run said `compatible: false`, and both it and the diff
reported `BACKWARD · WIRE_JSON`; **after**, v2 is `ACTIVE`, the dry run says `compatible: true`, and both report
`NONE · WIRE_JSON`. The pinned subject beside it is unchanged (held, `BACKWARD · WIRE_JSON`). Impact analysis (the surface
of a `BACKWARD · WIRE` environment decided what a consumer pinned to v1 was told) and promotion (into a `NONE`
environment: `ACTIVE`) were right before and are unchanged.

**A behaviour change, and for whom.** Only an environment whose default is not `BACKWARD · WIRE_JSON`: laxer ones start
accepting what the built-in held (and `latest` moves), stricter ones start holding what it accepted, and `concordat check`
in a pipeline follows. **An operator with such an environment should check**, before upgrading, `GET /v1/environments` for
each default and which of its subjects have `compatibilityPolicy: {mode: null, surface: null}`; pin any that were relying
on the built-in. **Nothing already registered or held is judged again** (measured and tested: a version held under the old
default stays held after it is relaxed).

**An environment has no "unset" default.** The brief asked what one resolves to. The registry stores a value: creating an
environment with no mode or surface stores `BACKWARD · WIRE_JSON`, and an update with neither leaves it alone (measured).
The only fallback is for an environment with **no row** (legacy data, or a command built by hand without a name), and it is
the same value. Tested at all three sites.

**Found on the way, not fixed then; both closed later the same day** (see the holes table): **the environment settings
screen could not change a default by changing one select.** It sent only the member that changed and the registry answers 400
"needs both a mode and a surface", so a default could only be changed by choosing a whole pair. Pinned by
`environment-default.spec.ts` so it would go red when fixed, and it was. And the screen's "Not set" default (and
`environment.ts`'s `null` members) described a state the registry never produces; it is gone.

### B. A policy change is in the audit log

`PUT …/compatibility-policy` is a command (`SetCompatibilityPolicyHandler`), so the entry and the save are one unit of
work, and it writes **`SUBJECT_POLICY_CHANGED`**: attributed to the authenticated caller (a `PUT` carries no actor), reading
`policy <old> -> <new>` with each side `MODE/SURFACE` or `inherited`. **Why a new action and not `SUBJECT_UPDATED` with a
detail:** the detail could say it without leaking anything, but the filter by action is how an auditor asks "who made this
stricter", and `SUBJECT_UPDATED` is already the row for an owner change and a deprecation. That touched every place that
pins the vocabulary (`AuditAction`/`AuditTokens`, `WireTokenTests`, the web `AUDIT_ACTIONS` and the audit page's filter, and
the confirmation). **A change that changes nothing writes nothing**, and a refused one (half a policy, an unknown subject, a
retired one) writes nothing; every status and body of the endpoint is what it was (the parity spec runs them all against the
registry). **No notification:** none of the seven `NotificationEvent`s is a policy change, a new one is a decision about who
is told, and *Nobody is notified* stays true, measured by the outbox not deepening. *(Reversed 2026-10-14, the section
above: there is an eighth event now, and the outbox does deepen.)*

**Measured against the real registry** (six `PUT`s, one subject): three entries, `inherited -> FULL/SOURCE`,
`FULL/SOURCE -> BACKWARD/WIRE` (sent as lowercase, stored as the registry's own spelling) and `BACKWARD/WIRE -> inherited`;
the repeated policy and the repeated inherit wrote none; half a policy was `400`, a retired subject `409`, an unknown one
`404`; filtering the audit by the action returned the three.

**The owner-change entry now records both owners** (a separate commit, once asked): `owner <previous> -> <new>`, the new
one as stored after the registry's trim, and `owner a -> b; deprecated` when one request does both. A `PATCH` that changes
nothing kept the old "owner changed" (a hole, pinned so it was not altered by accident; **closed 2026-10-14**, it now writes
nothing). Anything
that parsed the old fixed string will see a different one.

**A cost to know:** the audit table is read by action *token*, and the registry throws on a stored token its build does not
know. Rolling the registry back past this change breaks the audit page for as long as a `SUBJECT_POLICY_CHANGED` row exists.
*(2026-10-14: the notification event of the same name adds a worse one, in the outbox and in subscriptions; see the section above.)*

### What was verified, and what was not

Run on this machine, on Windows, only; GitHub has not run them. **Verified:** the full .NET suite (1,664, Docker running);
the web suite, lint, prettier, `codes:check`, `monaco:check` and `ng build`; `dotnet format --verify-no-changes`; the whole
browser suite against the dev server **and the production image**, each after a registry restart (223 and 223); every new
.NET test red against the code it replaces (handler by handler, a mutant for each of: no entry, wrong actor, entry after
the save, entry for a no-op, wrong action; and one per fixed call site), the new browser specs against the pre-change
registry image (the `inherits` test, the policy journey's audit rows and `environment-default.spec.ts` went red for the right
reasons), and the web specs against the pre-change components (five red). The changed confirmation was looked at in a real
browser at 1280 and 390 px, light and dark, from a fresh page load each; it wraps cleanly and overflows nothing. **Not run:**
`smoke-web-image.sh`, the `concordat` CLI and the GitHub Action against the new registry, an environment with no row (tested
at the application layer only), and a `subject:write`-only account in a browser.

**The local compose database now holds**, from this stretch: 14 `probe-*` environments (all from probing, one of which,
`probe-audit-*`, has a retired subject), `e2e-follows` with 18 subjects (two per run, nine runs including mutation runs), and
about 70 more subjects in `dev` and 14 in `e2e-inherit`, from the suite. Nothing was changed on `dev`'s defaults, `e2e-inherit`,
`e2e-names`, `e2e-empty` or the three shared fixtures. `concordat/api:mutant` was used again and still exists (the exited
`concordat-migrate` container references that tag).

---

## A subject's owner and policy are editable, and the registry did three things nobody had written down, 2026-10-13

The hole recorded below ("No screen edits a subject afterwards") is closed: a subject's page has
**Change owner** (`subject:write` or `subject:admin`) and **Change policy** (`subject:admin` only), each a
panel above the page, each two clicks, each ending in what the registry *answered* and a fresh read of the
subject ([ADR-032](adr/032-subject-settings.md)). Absent, not disabled, for anyone without the scope and for
a retired subject; both present for a deprecated one. The Angular suite was **1,411** tests (from 1,319), the
browser suite **218** across eighteen specs (from 195 across sixteen), run against the dev server **and the
production image**, and the .NET suite **1,624** (from 1,615) *(that day's figures; all three have grown since, see the section above)*. **Measured, production build:** the initial
bundle went 410.89 → 411.36 kB raw (the transferred figure moved less than the ~0.1 kB build-to-build noise);
the subject page's chunk went from 8.79 to 28.39 kB raw (3.16 → 7.36 kB transferred), which is where both
editors live. The create screen's confirmation no longer says "only through the API".

**What the real registry said that reading the C# did not.** Every claim on the screen was sent to the real
endpoint first, and three were different from what the code suggested:

- **An owner change was recorded as made by the *new owner*.** `UpdateSubjectHandler` built the audit actor
  from the owner in the request (`unknown` when there was none), so `bob` took ownership and the trail said
  bob did it. **Fixed** (it records the authenticated caller, like every handler that changes something) with
  four tests, each red on the old behaviour.
- **`{"owner":"   "}` answered 500.** The same line: the actor was built from the text the owner rule had just
  declined to apply, `ActorId.Create("   ")` failed, `.Value` threw. **Fixed by the same change**, with a
  theory over three blank spellings in the application tests and two in the HTTP tests. The web form never
  sends one (a blank field has no request), so this was found by the parity table, not by the screen.
- **A subject with no policy of its own is judged by `BACKWARD · WIRE_JSON`, not by its environment's default.**
  With an environment whose default is `NONE`, a breaking second version of an inheriting subject was *held*.
  `RegisterVersion` (measured), the dry-run `check` and the diff (by reading) call
  `EffectivePolicy(CompatibilityPolicy.Default)`; impact analysis and promotion call it with the environment's.
  So the environment settings screen's "takes effect at once for every subject that has not set its own" is
  false at registration, and one subject has two answers to "what is my policy". **Not fixed**: it changes which
  versions are held in every environment whose default is not the built-in, which is exactly the people who
  set one, and wants its own change. The policy confirmation says what happens today, and
  `e2e/subject-settings.spec.ts` goes red the day it changes, naming the sentence to update. **Fixed later the same day**: see the section above, and ADR-033.
- **A policy change leaves no audit entry** (the endpoint has no audit or outbox call), so the confirmation says
  so. Pinned by an HTTP test and the browser journey. It is the registry's gap and not a decision anyone
  recorded; whether it should be audited is yours, and the day it is both tests go red. **Fixed later the same day**:
  see the section above, and ADR-033.

**What a policy does, measured:** it takes effect for the *next* registration. Tightening it does not judge
versions already registered, and relaxing it does not release a version held at the approval gate (`AWAITING_APPROVAL`
stayed, with `latest` unmoved, after the policy went to `NONE`).

**The forms block on two kinds of thing.** The registry's own refusals (an owner over 256 characters after its
`Trim()`, half a policy), held against `PATCH` and `PUT` by `e2e/subject-settings-parity.spec.ts`; and drafts with
no request to make (a blank owner, or the owner or policy the page already shows). **That second kind is the one
place the client is knowingly stricter than the registry**, only where its snapshot is stale, recorded in the
ADR. Its first draft passed every row, and five mutations each turn it red for the right reason: JavaScript's
`trim()` (U+0085 and U+FEFF), a 255-character limit, no both-or-neither rule, the policy gate admitting
`subject:write`, and a blank owner that sends. Run against the registry *before* the fix it fails on the
whitespace 500, and the audit test fails on `actor: team-after`.

**Found on the way, and not fixed then (the first two were closed 2026-10-14, see the holes table):** retiring a subject records *the subject's owner* as
the actor, the same defect as the one fixed here; a `PATCH` that changes nothing (`{}`, a blank owner) still
writes an entry reading "owner changed"; and the brief's "probe everything the client will check" included the
registry's own scope split, which only an API key can show (no role holds `subject:write` without `subject:admin`),
so it is held through keys and not through a browser.

**Not covered.** Deprecating and retiring a subject, which are on the same `PATCH` and `DELETE` and are left out on
purpose (ADR-032: neither can be undone, one notifies every subscriber). A `subject:write`-only account *in a
browser*, since the sign-in screen only offers roles. Touch input, right-to-left text, a cross-origin registry. A
lone surrogate typed into the owner field, where the form is *looser* (the registry refuses the whole envelope with a
bare 400). `smoke-web-image.sh` was not re-run: `docker/web*` was not touched, though the production image was rebuilt
and the whole browser suite run against it. Run on this machine, on Windows, only; GitHub has not run them. The screen
was looked at in a real browser at 1280 and 390 px, light and dark, from a fresh page load each, which found one
defect the tests had not (a long subject name in the confirmation's heading overflowed its box at phone width; it
wraps now). **The local compose database now holds** 149 subjects in `dev`, of which this stretch made seven per
suite run: 32 `acme.e2e.settings.*`, 18 `acme.e2e.settingsparity.*` and 18 `acme.e2e.settingslife.*` (every
mutation run made its own, and two ran against the pre-fix registry), and the `e2e-inherit` environment, whose
default is `NONE` and must never be changed. From probing rather than the suite: 8 `acme.e2e.probe.*` subjects, one of
them retired, and the `e2e-inherit-probe` environment, which nothing uses. The three probe API keys were revoked. The
`concordat/api:mutant` image tag, built to run the suite against the pre-fix handler, still exists because the exited
`concordat-migrate` container references that image.

---

## The create-subject screen, and a 500 that was not what it looked like, 2026-10-13

**M4's exit criterion is met.** "Create a subject, register a version, view a diff and an impact
report, and approve a pending breaking change — entirely in the UI" had every step but the first;
`/subjects/new-subject` is the first, and `e2e/new-subject.spec.ts` walks it with nothing arranged over
HTTP: a subject made on that screen, then a version registered on the next. The Angular suite is
**1,319** tests (from 1,106), the browser suite **195** across sixteen specs (from 178 across
fourteen), run against the dev server **and the production image**, and the .NET suite **1,615** (the
first pass in this stretch to touch it; it was 1,532). Decisions: [ADR-030](adr/030-create-subject-screen.md)
and [ADR-031](adr/031-unpaired-surrogates-are-refused.md). *(That day's figures: all three have grown since, see the section above.)*

**The screen.** A title suggests the subject name and the name follows it until it is edited by hand
(the prototype's "touched" flag); creating takes two clicks, and the second says that the name, the
format and the content model are permanent and that the owner and compatibility policy can be changed
only through the API (*that day; the section above this one is the screen that changed it*). Every refusal the form makes is the registry's own, held against the real create
endpoint by `e2e/subject-name-parity.spec.ts`; a name that is taken, or differs from one only in case,
is advice and never blocks. It is reached from the subject list's "New subject" and from the empty
list and empty dashboard, **absent without a write scope** and behind `schemaWriteGuard`. The list's
"New subject" had been a button with no handler since M4.3 — pressable by a writer, doing nothing.
**Measured, production build:** the initial bundle went 410.6 → 410.9 kB raw (104.3 → 104.4 kB
transferred); the screen is its own lazy chunk of 18.8 kB raw / 6.0 kB transferred.

**The 500 the editor's parity test found, which was none of the things it was recorded as.** The brief
and this file said *"a bare 500 with no Problem Details body, from the canonical writer"* for
`{"a":"\ud800"}`. Reproduced against the real registry:

- It **has** a Problem Details body — the framework's generic one, with no `concordatCode`.
- It is **not the writer**: `JsonDocument.Parse` accepts the escape, and *reading the string back*
  (`GetString()`, `JsonProperty.Name`) throws, so the first walk of the tree died — the duplicate-key
  check, before the writer was reached.
- It is wider: keys as well as values, Avro (`doc`, `default`, any attribute), a *reversed* pair, and a
  duplicate-key refusal that quoted its own key and would have failed building the response.

It is now `schema_malformed`, from a scan of the escapes as written (`UnpairedSurrogates`) that runs
before the duplicate-key check and never quotes the string. Five conformance fixtures pin it, including
the mirror case a hand-written scanner gets wrong (an escaped backslash before `ud800` is text), the
canonicalisation spec says so (§3.1, §4.2), and the editor says the same with the same code. **The
parity test could not have seen the crash**: it reported a 5xx as "accepted". It fails on any 5xx now,
and a mutation (the editor's change removed) turns it red for the right reason.

**What the real stack corrected, or found, that reading the code did not:**

- **JavaScript's `trim()` is wrong for a subject name in both directions.** The registry trims before it
  validates, with .NET's set: it strips U+0085, which JavaScript does not (so a client using `trim()` is
  *stricter* and blocks a name the registry takes), and it does not strip U+FEFF, which JavaScript does
  (*looser*). Measured character by character; swapping in `trim()` turns the parity spec red for exactly
  those two. I had also half-remembered U+001C–U+001F as stripped; the registry refuses them.
- **Names are case-sensitive and the registry says nothing about it.** `orders.Created` and
  `orders.created` coexist. The form says so, as advice.
- **A subject can only be permanent in the ways it says.** Probed, not read: `PATCH` answers 200 and
  ignores a new name, format or content model; a retired name answers 409 on re-create; the owner and
  the compatibility policy are the only things that change.
- **A third 500, and this one persisted.** `compatibilityMode: "99"` — any integer string — parsed
  (`Enum.TryParse` accepts any number), was **stored**, answered 500 while projecting its own response, and
  then **every `GET …/subjects` in that environment answered 500 for every caller**. One request from
  anyone able to write poisoned an environment's list. I did it to my own `e2e-names` test environment
  while probing, found it when the list would not load, and repaired the one row through the API's own
  `PUT …/compatibility-policy` with nulls. The same pattern was at **six sites** (subject create and
  policy, environment default policy and registration policy, contract enforcement); all fourteen new
  integration cases go red on the old behaviour, so none was a theory. `WireEnums.TryParseDefined` refuses
  any number that is no member, and a comma list (`"Backward,Forward"` was silently `FULL`). A number that
  *is* a member (`"2"` is `BACKWARD`) is still read as it always was. **This was not part of the brief**:
  it is separate, small, and easy to drop from the commit if you would rather take it on its own.
- **The e2e suite had been assuming no subject was owned by its own account.** A subject the browser
  creates is owned by the signed-in account, so that email now sits in a table cell on the list as well as
  in the sidebar, and `getByText(email)` stopped being unique in five places. They look in the sidebar now.
  And an existing authorization test asserted `getByRole('button', { name: 'New subject' })`, which was the
  dead button; it is a link.
- **Port 4300 was another project's.** `npm start -- --port 4300` is what the docs say; on this machine
  Docker Desktop was forwarding 4300 to a different project's container, so the suite ran on 4301 through
  `CONCORDAT_WEB_URL`. Nothing was stopped.

**Not covered.** The new specs have run on this machine only, on Windows; GitHub has not run them. CI retries
a failed test once, and `new-subject.spec.ts` names its subjects at module load, so a retry of a test that had
already created one would meet 409 — not exercised. A *raw* lone-surrogate character (as opposed to an escape)
cannot reach the schema at all: the request envelope is refused with a bare 400 before any rule runs, which
is a refusal and not a crash, carries no code, and the editor does not claim to know it. Protobuf is not JSON
and is unaffected. The screen was looked at in a real browser at desktop and phone width in both themes, not on
a touch device, and not right-to-left. A cross-origin API was not signed into (as ADR-029). `docker/web*` was
not touched, so `smoke-web-image.sh` was not re-run. **No screen edited an owner or a compatibility policy** that day — only
the API did, and one does now (the section above) — and the compatibility tooltips remain unbuilt. **The local compose database now holds** two
environments and subjects that cannot be deleted: `e2e-names` (116 as measured, the parity table's and
the ones I made while probing, one of which was the poisoned row), `e2e-empty` (empty on purpose), and two
`acme.e2e.created.*` per run in `dev` (six at the last count). The `compose_concordat-postgres` volume was
not touched otherwise.

---

## The schema editor and the web container, 2026-10-13

M4.4's last three boxes — Monaco, `ajv`, and the prototype's editing conveniences — shipped on
`NewVersionPage`, and the web app joined the compose stack: `docker compose --profile registry up`
now brings up the registry **and** its UI (`127.0.0.1:5063`). The Angular suite went from 858 to
**1,106** tests and the browser suite from 144 to **178**, the latter run against the dev server **and
against the production image**; the .NET suite was **1,532** and was not touched. *(That day's figures:
all three have grown since — see the section above.)* Decisions:
[ADR-028](adr/028-schema-editor.md), [ADR-029](adr/029-web-container.md).

**The one rule the editor is built on:** a check that can block a registration must be one the
registry would certainly make. Everything it says is a *refusal* (a port of the registry's own
rules, which may disable Register) or *advice* (which never does). The refusals are held against
the registry's own dry-run endpoint, in both directions, over about fifty documents in three
formats — and that test found exactly one disagreement, in the test: JavaScript's `trim()` strips
a byte-order mark that the registry's reader refuses.

**The real stack corrected the first draft five times**, and four of the five could only have been
found by running the production build:

- **The production build ran on a fraction of its stylesheet under the app's own policy.** Angular inlines critical CSS as
  `<link media="print" onload="this.media='all'">`; `script-src 'self'` forbids inline event
  handlers, so the handler was blocked and the full stylesheet never left `print` — only the inlined subset applied, which showed up as a light palette on a page that starts dark. Every browser test
  ran against `ng serve`, which does not do this. The build now does not inline critical CSS, and
  the image's smoke test fails on any inline handler in `index.html`.
- **Monaco rendered unstyled in production and not in development.** The stylesheet a lazy chunk
  imports is emitted and never linked; the dev server injects it from the module. It is a vendored
  file now (`npm run monaco:check`), linked on load, and a test fails the build's fallback path by
  aborting that very request.
- **A subject named `…parity.json` was a 404 through nginx.** The regular expression that serves
  static files by extension outranked the `/v1/` prefix, so any API path ending `.json`, `.js`,
  `.css` or `.txt` never reached the registry. Subjects are dotted; `orders.json` is a plausible
  name. Found by the browser suite's own fixture, fixed with `^~`, and now a line in the smoke test
  that the broken image fails.
- **The accessibility attributes on Monaco's input were never set.** In Chromium Monaco's input is
  an `EditContext` `div`, not a `textarea`, and the code looked for a textarea.
- **The minifier writes `.625rem` for `0.625rem`**, which an exact-string test called a regression;
  and two design-system tests counted icons before the app had rendered them, which the
  production image's one extra request before first paint was enough to lose.

**What the editor is not allowed to claim, and the two registry facts the work surfaced:** the
registry **accepts a schema that is not valid under the 2020-12 metaschema** (`{"type":"bogus"}` is
registered; it only fails when a message is validated against it), which is why `ajv`'s findings
are advice; and it answered **500 to a lone-surrogate escape** (`"\ud800"` in a string), which is
well-formed JSON. *(This paragraph recorded it as "with no Problem Details body", from the canonical
writer. Both were wrong, and it was closed the same day: see the section above and
[ADR-031](adr/031-unpaired-surrogates-are-refused.md).)* The first is still in the holes table below.

**Measured, 2026-10-13, production build.** Initial bundle 405.9 → 410.6 kB raw (102.5 → 104.3 kB
transferred): the editor is not in it. On demand: Monaco 3.32 MB raw / 667 kB transferred, its
worker 307 / 75 kB, the checker's worker (with `ajv`) 136 / 36 kB, the stylesheet 187 kB and the
icon font 153 kB. The screen's own chunk grew from 9.7 to 50.2 kB.

**Not covered.** The prototype's *auto-slugged id* and *compatibility tooltips* — the first belongs to
creating a subject, which no screen did when this was written and one does now (the section above); the
second is still unbuilt. A cross-origin API
(`CONCORDAT_API_BASE_URL` set to another origin) was checked as far as `config.json`, the rendered
header and the validation, **not by signing in across origins**. The CI and `Publish images`
changes parse as YAML and have not been run by GitHub. Right-to-left text, very large documents
that scroll (a 527 KB paste took 0.7 s; reading it back through the DOM is not attempted) and
touch editing were not exercised.

---

## The web app's pages, 2026-10-13

The seven M4.3 screens that were missing shipped in one pass — the version diff, impact analysis,
approvals, contracts, the audit log, the settings split (environment, brokers, members, API
keys) and notification forms — taking the browser suite from 43 to 144 tests and the Angular
suite from 358 to 858. Each was driven in a real browser against the real registry, and **the
real stack corrected the first draft four times**: twice an engine that disagreed with a fixture
written to agree with its author, once a latent defect only nested routes could expose, and once
a budget the suite had quietly outgrown.

- **Impact is judged FORWARD, so a newly required field does not break a reader pinned to the
  old version** — it ignores the extra property. The first impact test expected it to, and the
  engine said otherwise. The suite now holds both a compatible reader and a type change that
  genuinely breaks one.
- **Separating two overlapping contract bindings needs a distinct precedence on *both*.** One
  with a value and one without is still a conflict, and bindings cannot be edited afterwards —
  recorded as a hole below. *(Closed 2026-10-14, ADR-035: a binding can be changed and removed.)*
- **The sidebar was dropping `aria-current`** the moment you moved between two child routes of
  one active row. Angular's `RouterLinkActive` strips the attribute on every navigation unless
  told to manage it with `ariaCurrentWhenActive`, and the hand-bound attribute the sidebar used
  never re-rendered. Nothing exercised it until a screen had tabs; fixed in the sidebar itself.
- **The suite outgrew the auth limiter.** Sign-in is capped at 20 attempts per five minutes, and
  the suite crossed it at ninety-odd tests — an unrelated sign-in near the end failed, and it
  would have failed CI too. Specs that are not about signing in no longer sign in; a reader's
  browser state and the owner's API credential are captured once in global setup, and a run now
  makes thirteen. See `web/e2e/README.md`.

Two decisions worth knowing were made along the way, both for the same reason as ADR-018's
"absent, not disabled": **members and API keys are `org:admin` to read as well as write**, so
they have a route guard and an absent tab rather than a read-only version that would fail every
request; and **deciding an approval needs `subject:admin`, narrower than the write scopes**,
because the author of a breaking change approving it is the arrangement the gate exists to
prevent. A secret the registry returns once — an API key, a webhook signing key — is shown in
one shared panel that stays until dismissed.

What the browser run does not cover: a **reachable** broker (that needs a RabbitMQ the suite does
not start; an unreachable one is real), the registration-policy and compatibility-default
changes on the shared `dev` environment (they would break every other spec, so they are
unit-tested for what is sent and not sent), and a non-empty `parked` count on the outbox banner.
The e2e specs for approvals, contracts, members and API keys each leave something behind — a
subject, a contract, a member, a revoked key — because none can be undone from the UI; the
README says so.

---

## Requirements consistency audit, 2026-09-24

Every requirement source checked against the code it describes: 27 ADRs, ten milestone plans,
DESIGN.md, the four normative protocol specs, and this file. **54 inconsistencies confirmed**,
each re-verified by a second reviewer told to refute it; 226 load-bearing requirements verified
as holding. A gap these documents already recorded counted as consistency, so what follows is
only disagreement between a claim and the code.

**Three were product gaps, all now closed** — they are in the holes table below, with the code:

- **Registration never checked the reference graph.** `ReferenceGraph.DetectCycle` and
  `ReferrersOf` shipped with M1.4 and had no production caller, so an edge naming a subject or
  ordinal that does not exist was accepted and surfaced later as a `bundled` document that was
  not self-contained. M1.6's prose and `BundleSchema`'s own comment had claimed cycles were
  rejected at registration since the day they were written.
- **Nine routes answered anonymously on a claimed instance**, including the environment
  bootstrap route that returns every subject and schema at once. ADR-027 named three of them as
  gated. See [ADR-027](adr/027-read-requires-authentication.md) for how the structural test
  missed them, which is the more useful half of the finding.
- **Signup announced that an address was taken** in its `concordatCode`, title and type URI,
  while the message above them was carefully neutral.

**Two were normative protocol documents specifying the opposite of what ships** — the exact
cross-SDK divergence `docs/protocol/` exists to prevent. `canonicalisation.md` still described
Avro `doc`-stripping that decision #17 reversed on 2026-08-15, citing a fixture that no longer
exists; `envelope.md` §7 still refused generic type names that ADR-025 gave a normative
spelling. An SDK written from either would have computed different schema ids or refused
subjects every other implementation resolves.

**The rest was drift in one direction or the other**: this file understating what the web app
has shipped, protocol gap-lists trailing closed decisions, and ADR-003, `src/README.md` and
DESIGN §7–§8 describing an M1-era layout with a `src/cloud/` and a `Contracts.MSBuild` that were
never built. Nothing found suggested a wrong decision — only records that had stopped matching
what was decided.

---

## Security hardening, 2026-08-16

A production-readiness review of the API, the infrastructure and the deploy path found 36
issues — 2 critical, 4 high, 13 medium, 17 low or code-quality — and every one was fixed and
verified, not just patched and trusted: real Postgres and RabbitMQ via Testcontainers, real
Docker builds of both images, a live migrator run proving the least-privilege database role
actually cannot run DDL, real GitHub API calls to resolve the action and image digests M13
pinned rather than guessed ones.

**The one that changed product behaviour, not just hardened it:** every read endpoint now
requires an authenticated caller, the same as writes have since ADR-018 —
[ADR-027](adr/027-read-requires-authentication.md). Browsing the registry, in either profile,
now needs an account. Discovered the same way `VersionStatus.Dismissed` was in the entry above:
CI's browser suite failed after the backend change shipped, because 24 of its 43 tests had been
built against the original, anonymous-reads behaviour. `web/e2e/README.md` covers what the
suite assumes now.

**Critical, both closed:** an unclaimed self-hosted instance had no way to require a token
before the first `/v1/auth/bootstrap` call, so whoever reached it first — not necessarily the
operator — kept the instance; the shipped `docker-compose.yml` carried default database and
broker credentials.

**High, all four closed:** several `GET` routes had no scope requirement at all, ahead of and
separate from ADR-027's blanket rule; the Cloud-mode outbox pump queried through the tenant
filter and so delivered only one tenant's notifications, ever; the Protobuf parser had no
recursion depth limit; JSON Schema pattern matching had no timeout, so a crafted schema could
hang the delivery path.

**Also closed:** Postgres connections now require a validated certificate chain rather than
encryption alone; the process fails fast on a missing connection string instead of silently
falling back; auth endpoints are rate-limited; the audit trail records the authenticated caller
rather than a client-supplied field; the API serves CSP, HSTS and an explicit CORS policy;
concurrent writes map to 409 instead of a bare 500; outbound webhooks are HMAC-signed per
subscription; GitHub Actions are pinned to commit SHAs and base images to digests; the API now
runs under a least-privilege Postgres role the migrator provisions rather than the admin login
Postgres itself uses; Azure deployments can opt into a fully private-networking path for
PostgreSQL ([decision 28](DECISIONS-PENDING.md), part (b)); and the N+1 schema loads in
registration, promotion, compatibility checking and the bootstrap endpoint — the one whose own
purpose is turning N requests into one — are now one batched query each.

**A NuGet lock file was evaluated for the supply-chain pass and not shipped**: SDK 10.0.301
breaks solution-level restore against `Concordat.slnx` when `RestorePackagesWithLockFile` is on,
reproducibly, for reasons isolated to the SDK's `.slnx` restore-graph construction rather than
this repository. `nuget.config`'s package source pinning shipped instead; `Directory.Build.props`
carries the reason lock files did not.

## What runs today

The whole product path works end to end. Verified 2026-08-14 by building
`docker/api.Dockerfile` — **its first real build outside CI** — and running the compose
`registry` profile:

```
docker build -f docker/api.Dockerfile -t concordat/api:local .
cd deploy/compose && CONCORDAT_IMAGE=concordat/api:local docker compose --profile registry up -d
```

| Step | Result |
|---|---|
| Migrator runs as its own service, API waits for it | exits 0, API starts |
| `GET /health/ready` | `Healthy` |
| Create environment, subject, register a schema | 201, ordinal 1, `ACTIVE` |
| Compatibility engine on a breaking change | `AWAITING_APPROVAL` with a `required_field_added` divergence naming the path |
| Contract + publish binding, then `POST /contracts/resolve` | governed route returns the contract in `ENFORCE`; `order.line.added` returns `{contracts: [], enforcement: "OFF"}` — `*` is one word |
| `PUT …/registration-policy` to `CLOSED`, then register | **403** `registration_policy_forbids` |
| CLI `export` against the live registry | wrote 2 contracts to disk |
| Quickstart sample over real RabbitMQ | valid message accepted, invalid one refused at publish, queue drained clean |
| Container healthcheck | **was broken, now fixed** — see below |

`CONCORDAT__*` configuration binding was listed as an open M1 item and was **stale**: the
container ran on `ConnectionStrings__Concordat` and `Concordat__Profile` that day, and still
does. [M1.8](plan/M1-registry-core.md) now records the prefix as superseded and not built,
rather than as work outstanding.

### The one defect this found

`docker ps` reported the registry **unhealthy while it answered every request correctly**. The
compose healthcheck called `wget`, and the aspnet runtime image has neither wget nor curl — so
the check had never once passed, and `depends_on: condition: service_healthy` against the
registry would have waited forever.

It failed in the worst direction: a working service that looks broken to whoever is evaluating
the product. The image now installs `curl` for this alone. Azure Container Apps was never
affected — its probes are HTTP requests made by the platform, not commands run inside the
container — which is exactly why CI and the Bicep deployment never caught it.

### What the first browser run found

**The subject list was broken against a real registry**, and had been since M7.

`VersionStatus.Dismissed` shipped with M7 and `web/src/app/domain/registry/wire-tokens.ts` never
learned it. The web app's unknown-token guard is strict by design — an unrecognised token means a
newer server, and guessing would be worse — so one dismissed version failed the *entire* page:

> The registry sent 'DISMISSED' for 'status', which this build does not recognise.

**1,489 .NET tests and 187 Angular tests were green throughout.** Each side was correct about
itself; nothing checked that the two agreed at runtime, because nothing loaded a page. The
frontend spec even predicted it in a comment — *"a fourth status added server-side must reach
this list"* — and nothing enforced the prediction. `WireTokenTests` now compares all three shared
vocabularies against the .NET enums.

A second, smaller find from the same run: `/v1/auth/resume` answered **401** when there was no
cookie, so every signed-out page load of a healthy app logged a console error. The app has to
probe on startup — it cannot read an httpOnly cookie to know whether probing is worthwhile — so
"no session" is an ordinary answer, not an authentication failure. It answers 204 now, and 401
only when a cookie was presented and rejected.

### Two things that path does not cover

- **The web app is not in the compose stack.** There is no way to bring up the UI alongside the
  registry with one command. The reason used to be that most of the UI did not exist; it is now
  simply that compose has no web service wired — the registry's own read path is walkable in a
  browser, which is what the 2026-08-15 verification below exercised.
- **The quickstart does not exercise contracts.** It prints `0/0 routes governed` — the sample
  predates contract resolution and declares no topology, so the newest feature in the SDK is
  demonstrated by nothing a user would run.

---

## The numbers, and why the raw count misleads

| Milestone | Done |
|---|---|
| M0 foundations | 22 / 25 |
| M1 registry core | 65 / 68 |
| M2 .NET client | 32 / 41 |
| M3 CLI | **19 / 19** |
| M4 web app | **28 / 30**, counted from [the plan](plan/M4-web-app.md) on 2026-10-13 with its struck-through items left out; the Dashboard is the part-built one of the two still open |
| M5 formats | 14 / 15 |
| M6 SDKs | 5 / 24 |
| M7 governance | **30 / 31** |
| M8 identity | 13 / 15 |
| M9 cloud | 11 / 16 |
| **Total** | **239 / 284** |

*M4's row was re-counted on 2026-10-13, since it had not been since the pages shipped; the others are as of 2026-08-30.*

**Do not read that as a progress bar.** Auditing the 53 remaining boxes found 9 that are not
work at all:

- **M2 has an archived scope list.** `## M2.2 notes (original scope)` preserves the original
  seven-item envelope scope, deliberately left unchecked as a historical record while the
  section above it is marked done. Most of those items shipped — Mode A headers are exercised by
  the quickstart, and the Mode B content-type is written and parsed by `ContentTypeEnvelope`
  under unit tests only, because the sample publishes plain `application/json`. At least one did
  not: see the binary framing gap below.
- **One M2 item is a cross-reference,** not a gap: "binding it to `IReadOnlyBasicProperties`" is
  annotated *"— M2.4"*, where it was in fact done.
- **One M5 item cannot be done** — splitting a `.proto` across files, which [ADR-023](adr/023-no-cross-subject-references-avro-protobuf.md)
  scoped out rather than deferred.

~~Three M4 items are moot — corrections to a React prototype that was never ported.~~ **Removed
from the plan 2026-09-24**, which is why this list is 9 rather than the 12 it counted before:
carrying an item as "not work at all" is a way of saying it should not have been a box, and the
honest fix is to stop counting it rather than to annotate it forever.

Real remaining work was **11 items** when this was written, 4 of them M4; M4 is **2** now (the
part-built Dashboard and the compatibility tooltips), so **9**. The rest of the arithmetic:
**22 are deferred by an ADR** (M6's four SDKs, OIDC, SAML, Helm) and **5 are blocked on an account
or a purchase** you have to make — see *Blocked on you* below. Sorted that way the picture is much
narrower than that: a handful of schema-editor enhancements, and a pile of decisions.

M3 is the first milestone to reach 19/19, closed by `Concordat.Contracts.Testing` (decision 13).
M7 reached 30/31 on 2026-08-15 — not by new work, but by an audit finding its last open box had
described shipped code since decision 25 landed the day before.

---

## Missing product surface

### M4 — the web application

The honest count is **1** — the part-built Dashboard; the one box M4.4 had open (the compatibility
tooltips) is closed ([the first section](#compatibility-explanations-visible-text-and-a-disclosure-not-a-tooltip-2026-10-14)). **No page is missing**, and the schema editor's three boxes
are done ([the section above](#the-schema-editor-and-the-web-container-2026-10-13)). On
2026-10-13 the seven pages that were missing — the version diff, impact analysis, approvals,
contracts, the audit log, the settings split and notification forms — shipped, each driven in a
browser against the real registry, and later the same day so did the screen that creates a subject
([the first section](#the-create-subject-screen-and-a-500-that-was-not-what-it-looked-like-2026-10-13)).

**Genuinely missing:** ~~the prototype's *compatibility tooltips*~~ (built 2026-10-14, as text and a disclosure). The *auto-slugged id with a "touched"
flag* was waiting on a screen that creates a subject, and ~~**no screen creates one**~~ one does now:
M4's exit criterion ("create a subject, register a version, …entirely in the UI"), which had been
recorded as met while its first step was not, **is met**, and `e2e/new-subject.spec.ts` walks the first
two steps in a browser. What is *not* in the UI is editing a subject afterwards — its owner and its
compatibility policy change through the API only — which is recorded in the holes table below.

~~SubjectDetail / VersionDetail / NewVersion pages~~ headed that list until this audit.
**They shipped 2026-08-15 with M4.3** — `subject-detail-page.ts`, `version-detail-page.ts` and
`new-version-page.ts`, routed at `/subjects/:name`, `/subjects/:name/versions/:ordinal` and
`/subjects/:name/versions/new` — and the browser-verification paragraph below has said so since
that day. The 2026-08-30 pass re-ran the test counts and nothing else, so the list outlived the
gap it described.

**Part-built:** the **Dashboard**. A first slice ships on `/` — subject, version and
awaiting-approval counts, and the most recently registered subjects. Every number is derived
from the subject list rather than fetched, which is honest at this size and has to move
server-side the day that list is paginated. Recent breaking changes and enforcement coverage
wait on endpoints that do not exist yet, so it is marked `[~]` rather than done.

**Verified in a browser 2026-08-15**, against the compose `registry` profile on a wiped
database: the subject list, subject detail, version detail and the register form, plus a
version the registry is genuinely holding at the approval gate — a real `AWAITING_APPROVAL`
from the compatibility engine rather than a fixture that agrees with the frontend by
construction. The register form was driven end to end by submitting the document already at
the tip, so the write path is exercised while the suite stays idempotent. Three consecutive
runs leave the registry byte-identical.

That pass cost two defects, both in the suite rather than the product. `ng serve` compiles a
lazy route's chunk on first request, so the first navigation to a new screen took over five
seconds against a 5s assertion timeout and reported a fully-rendered page as missing. And the
held-version fixture grew the subject by two versions per run: re-posting the base schema
while a proposal is pending is a *revert*, so the registry dismissed the pending version and
the next post opened a fresh one — neither call answering 409, because both were legitimate
state changes. Idempotence there had to mean "read the subject first", not "tolerate a
conflict".

**Done 2026-08-15:** the design-system port — the prototype's tokens, a light theme, self-hosted
fonts ([ADR-026](adr/026-self-hosted-web-fonts.md)), the sidebar shell, the icon set and the
cards — and the Playwright suite that now pins it. The E2E suite stands at **234 tests across twenty
specs** as of 2026-10-14, up from the 11 it launched with; `design-system.spec.ts` asserts the palette, both
themes, the typography and the shell as **computed styles rather than pixel screenshots**, which
are exact and identical on Windows and on CI's Linux. It exists because the placeholder palette
survived a whole milestone with every other test green: nothing named a colour, so nothing could
notice they were all wrong.

~~The one M4.5 item still absent — "direct URL to a write route redirects" — is blocked on M4.3
rather than unwritten: there is no write route to paste, and `scopeGuard` is referenced by no
route at all. That also corrected an M4.2 line which had recorded the guard as wired.~~
**Written 2026-08-15**, the same day, once M4.3's pages gave it a route to paste:
`/subjects/:name/versions/new` is the app's first write route and the first thing
`schemaWriteGuard` is attached to, so `authorization.spec.ts` asserts the guard rather than the
`**` wildcard — it renders for a writer, redirects a reader to `/subjects`, and sends a
signed-out visitor to `/sign-in?returnTo=…`.

**Already done but still listed:** `LoginPage` — `sign-in-page.ts` shipped with M8.2.

**Moot — corrections to a prototype that was never ported:**

| Listed | Why it is moot |
|---|---|
| "Collapse the two competing HTTP paths" | There is one. `HttpClient` plus three interceptors, two typed data-access files. |
| "Uncontrolled `defaultValue` forms → reactive forms" | A React idiom. No such forms exist. |
| "Drop the unused dependency surface (React Query, zod, recharts, …)" | None of them are in `package.json`. The Angular app never had them. |

The Monaco item's stated reason had expired — it was framed as fixing the prototype's
`dangerouslySetInnerHTML` XSS hole, and that code was never ported either — and it shipped on its
own merits.

**What exists today:** eighteen screens — sign-in, the dashboard slice on `/`, the subject list, the
create-a-subject form, subject detail, version detail, the register-a-version form, the version
diff, impact analysis, approvals, the contract list and a contract, the audit log, and the five
settings tabs (environment, brokers, members, API keys, notifications) — and the routing, guards and
interceptors under them. Every flow in the exit criterion — create a subject, register a version,
view a diff and an impact report, approve a pending breaking change — is possible entirely in the UI.

### M6 — Tier 2 SDKs

**19 items, deferred on purpose** by [ADR-024](adr/024-v1-ships-dotnet-only.md) until the .NET
SDK has been tested against a real workload. The protocol and the conformance corpus are
published and already caught cross-language defects with no second SDK in existence, which was
the risk the deferral had to answer.

Not forgotten — waiting on your judgement that .NET is proven.

---

## Holes in what is already built

These matter more than the two lists above, because the surface exists and looks finished.

| Gap | Where it bites |
|---|---|
| ~~Mode B binary framing is specified and not implemented~~ | **Closed 2026-08-14** by decision 19 — amended out of v1 rather than built. M2.5 measured every transport it could raise and `concordat-*` headers survived all of them, so framing had no demonstrated need. ADR-010 carries the amendment and says what would bring it back. |
| ~~`ENFORCEMENT_VIOLATION` is a notification event nothing emits~~ | **Closed 2026-08-14** by decision 25 — `POST …/violations`, aggregated and reported by the SDK, upserted by fingerprint, notification on first sight only. **Nothing schedules the flush yet**: a host opts in by wrapping its observer and calling `FlushViolationsAsync` on a timer. |
| ~~An environment with no row has no registration policy~~ | **Closed 2026-08-14** by decision 23. The first write creates the row with the derived id, so the policy applies from the first request rather than from whenever somebody thought to create the environment. |
| ~~Registration never checked the reference graph~~ | **Closed 2026-09-24.** `ReferenceGraph` could detect a cycle and find referrers since M1.4 and nothing called it, so an edge naming a subject or ordinal that does not exist was stored without complaint and failed later, on whoever's `GET …/bundled` asked next. Registration now resolves every edge first, sharing one resolver with the bundler. The transitive re-check DESIGN §4 asked for is moot under pinned references — see §4 — and the cycle check is kept for the assumption it guards. |
| ~~Nine routes answered anonymously on a claimed instance~~ | **Closed 2026-09-24**, thirteen months of exposure after [ADR-027](adr/027-read-requires-authentication.md) said they did not: three governance `GET`s, five read-shaped `POST`s, and `POST …/environments/{env}/bootstrap`, which returns every subject and schema in an environment. `EveryRouteUnderV1DeclaresAScope` now covers reads, not only mutations. |
| ~~Signup named its reason for refusing~~ | **Narrowed 2026-09-24.** The neutral `detail` was the only neutral part; the code, title and type URI all said `user_already_exists`. Now `signup_refused`. Not closed: synchronous provisioning still distinguishes refusal from creation — [M9.2](plan/M9-cloud.md) says what closing it would take. |
| **Approval reviewers do not exist** | Anyone with `subject:admin` can approve anything. A reviewer *set* was deferred to M8 and M8 did not build it. |
| ~~A contract's bindings cannot be edited or removed~~ | **Closed 2026-10-14** ([ADR-035](adr/035-contract-bindings-have-an-identity.md)): every binding has an id, and `PUT`/`DELETE` on `…/publishes/{id}` and `…/consumes/{id}` change its subjects and precedence or remove it, with the overlap rule run again for an edit. A binding added without a precedence can now be given one, and the overlapping binding that was refused is then accepted. What it was: there was an `add` endpoint for each kind and nothing else, so a binding added without a precedence could never be separated from a later overlapping one, and the registry's own refusal says to "give the two bindings different precedence values". Found by the web app's first real overlap test (2026-10-13). |
| ~~Dependency advisories are live, and an audit finding is an error~~ | **Closed 2026-10-14 for detection, not for repair.** Found the same day: NuGet audit runs at restore and `TreatWarningsAsErrors` makes a finding fatal, so an advisory published against any package, transitive ones included, breaks the next fresh restore with no change to the repository, as five against `System.Security.Cryptography.Pkcs`/`.Xml` did (pinned forward, `82656ca`). [`dependency-audit.yml`](../.github/workflows/dependency-audit.yml) now restores the solution every day at 05:41 UTC, with nothing cached, and opens a GitHub issue (or comments on the open one) naming the NU19xx codes and the packages. **It detects; it does not fix.** A human still has to pin the package forward, and an advisory published at 05:42 still waits up to a day for the report, so a push can still be the first thing to find it. **Not verified: the workflow itself has never run on GitHub** (nothing is pushed); the script that does the work was run here against a real restore, the issue sequence only against a fake `gh`. Scheduled workflows are switched off by GitHub after 60 days without repository activity. A lock file is still not an option (`RestorePackagesWithLockFile` breaks solution restore on SDK 10.0.301). |
| ~~The audit read throws on an action its build does not know~~ | **Closed 2026-10-14** by [ADR-036](adr/036-the-audit-read-tolerates-an-action-its-build-does-not-know.md): `AuditEntry` holds the stored token, `Action` is a nullable view of it, and `GET /v1/audit` returns a row it cannot name as stored (measured: 500 before, 200 after, on the real registry with `A_FUTURE_ACTION` inserted by SQL). Pinned by `ARowWhoseActionThisBuildWasNeverTaughtIsReadAsReceivedAndTheRestUnchanged` (real PostgreSQL). **It protects a rollback to a build that contains it**; rolling back to `612f7c8` or earlier keeps the old behaviour (ADR-033 and ADR-035 carry dated amendments). Filtering by an unknown action is still refused, by decision. |
| ~~No screen creates a subject~~ | **Closed 2026-10-13** by `/subjects/new-subject` ([ADR-030](adr/030-create-subject-screen.md)): M4's exit criterion had been recorded as met when its first step was not, and the list's "New subject" was a button with no handler. What is left of it is the next row. |
| ~~No screen edits a subject afterwards~~ | **Closed 2026-10-13** by the panels on a subject's page ([ADR-032](adr/032-subject-settings.md)): Change owner (`subject:write` or `subject:admin`) and Change policy (`subject:admin`), each two clicks, each saying only what the registry was measured to do. Name, format and content model remain permanent (probed: `PATCH` ignores them and answers 200). **Deprecating and retiring are left out on purpose**: neither can be undone, and deprecating notifies every subscriber. The CLI still edits neither. |
| ~~A subject with no policy of its own is judged by `BACKWARD · WIRE_JSON`, not by its environment's default~~ | **Closed 2026-10-13** by [ADR-033](adr/033-inherit-means-the-environments-default.md): registration, the dry-run `check` and the diff read the environment's default, through `EnvironmentPolicies.EffectiveFor`, as impact analysis and promotion always did. **A behaviour change** for every environment whose default is not the built-in (see the section at the top for what to check). Pinned by `InheritedPolicyApiTests`, the three handler test classes, and `e2e/subject-settings.spec.ts` and `e2e/environment-default.spec.ts`. |
| ~~The environment settings screen cannot change a default by changing one select~~ | **Closed 2026-10-13.** The form sent only the member that changed (`changesBetween`) and the registry refuses half a policy: `400` "A compatibility policy needs both a mode and a surface (ADR-016)". Now a change to either select sends both, the unchanged one as the registry holds it, and a change to neither sends neither. Probed first against the real registry (a new environment): mode alone, surface alone and a mode with a null surface were each `400` and stored nothing; both, both null and an unchanged pair were `200`. `e2e/environment-default.spec.ts` had pinned the defect and was inverted (changing only the mode now saves, and the registry reports the new mode beside the old surface); the unit test that asserted the one-member shape was inverted with it. |
| ~~The web app has a "not set" environment default that the registry never produces~~ | **Closed 2026-10-13**, with the row above, by removing it rather than keeping it: `Environment.defaultCompatibility` is now a whole pair (`EnvironmentDefaultPolicy`), the settings form has no "Not set" option, and a `null` member on the wire is refused by name like any token the build does not know. Nothing else read the null members. The wire DTO still mirrors `PolicyResponse`, which is nullable because a *subject's* policy is. |
| ~~The browser job in CI has been red since `803822c`, for a reason that is not the code under test~~ | **Closed 2026-10-14**: the registry now answers an unreadable body `400` in every environment (`RouteHandlerOptions.ThrowOnBadRequest = false` in `Program.cs`; `UnreadableBodyTests` hosts as Development, which `WebApplicationFactory` does by default, and went red with twelve `500`s before the line and green after; the whole browser suite, 223, passed against the registry run with `dotnet run` as Development, which is CI's arrangement). What it was: CI starts the registry with `dotnet run`, which takes `launchSettings.json` and so runs as **Development**; every local run uses the container image, **Production**. In Development ASP.NET Core throws a minimal API's body-binding errors into `UseExceptionHandler`, so a body the registry answers `400` in Production (a lone surrogate, a number where a string goes) comes back `500`. Five parity tests fail on that and nothing else (`schema-parity` json, `subject-name-parity` name, three in `subject-settings-parity`); the other 218 pass. `803822c` had two of them, `f446091` and `e1f8623` five, because each added parity probes; `afe19bc` and everything before was green. Read from the CI registry log (`Hosting environment: Development`, `BadHttpRequestException … Cannot read incomplete UTF-16 JSON text`). Fixed in the registry rather than by running CI's as Production, so a developer's `dotnet run` answers what a deployment does. The other six jobs (`build & test`, `web app`, `contract drift gate`, `protocol docs gate`, `Web container`, `CLI container`) were green on all three. |
| ~~`StompExperiment` published before its subscription existed~~ | **Closed 2026-10-14.** `build & test` failed on two of five consecutive CI runs (`2228d84`, `2ff4ae5`) with "nothing arrived over STOMP" after its 30 s wait, and passed on the rest with nothing in between touching it. A STOMP `SUBSCRIBE` has no reply unless a `receipt` is asked for, and the test published over AMQP the moment it was sent; the broker binds the subscription's queue to `amq.topic` after it has read the frame, so a publish in between has no binding and is dropped. Reproduced against a real broker before changing anything: 2 lost publishes in about 3,400 (one in 60, one in 1,500, none in 300); with the test waiting for the broker's `RECEIPT`, none in 6,000. The sample is small for a one-in-1,700 event, but the mechanism is the documented one and the MQTT experiment, whose subscribe returns on SUBACK, is not exposed. A test-only change; nothing in the registry was involved. |
| ~~A policy change is not in the audit log~~ | **Closed 2026-10-13** by [ADR-033](adr/033-inherit-means-the-environments-default.md): `PUT …/compatibility-policy` is a command and writes `SUBJECT_POLICY_CHANGED` in the same transaction, as the caller, reading `policy <old> -> <new>`; a no-op and a refused change write nothing; nobody is notified. Pinned by `APolicyChangeIsRecordedAsTheCallerWithTheOldAndNewPolicy` (the inverse of `APolicyChangeLeavesNoAuditEntry`) and the policy journey in `e2e/subject-settings.spec.ts`. Rolling the registry back past it breaks the audit page while a row exists. |
| ~~A `PATCH` that changes nothing still writes "owner changed"~~ | **Closed 2026-10-14.** `{}`, a `null` owner and a blank one still answer 200 with the subject as it was, and now write no entry, save nothing and stage nothing: the rule a policy change and an idempotent registration already follow. **Deliberately not widened to an explicit owner the subject already has**, which is a request and still reads `owner a -> a` (pinned, so widening it is a decision); a blank owner beside `deprecate` still deprecates and says only "deprecated". Red first at both levels (`APatchThatChangesNothing_WritesNothingAndSavesNothing`, `APatchThatChangesNothingAnswers200AndWritesNoEntry`, and the parity spec's count of `SUBJECT_UPDATED` rows around the bodies it sends, which gained two before the fix). The old test that pinned "owner changed" so it would not change by accident was replaced. |
| ~~Retiring a subject records the subject's owner as the actor~~ | **Closed 2026-10-14.** `RetireSubjectHandler` takes `ICallerContext` and records the authenticated caller, as `UpdateSubjectHandler` and `SetCompatibilityPolicyHandler` do; the command still carries no actor. Before: retiring someone else's subject was attributed to them (measured in the browser parity spec: the actor was the owner `after-deprecation`, not the account that retired it). A refused retirement (the second one) writes nothing, pinned at both levels. Retiring is still not on any screen. |
| **The registry accepts a schema that is not valid JSON Schema** | `{"type":"bogus"}` registers. Canonicalisation and the portability check never read the document against the 2020-12 metaschema, so it is stored, given an id and offered to consumers — and fails every message validated against it. The web editor reports it as advice (`ajv`); the registry does not. Whether registration should refuse it is a product decision, not a bug fix: refusing changes what existing users can register. |
| ~~A lone-surrogate escape in a schema answers 500~~ | **Closed 2026-10-13** ([ADR-031](adr/031-unpaired-surrogates-are-refused.md)): `schema_malformed`, from registration, the dry run and the lookup, in values and keys and in Avro, pinned by five conformance fixtures. It had been recorded as a bare 500 from the writer; it was a generic Problem Details body from the *reader*, and wider than the one string. |
| ~~An undefined enum number is stored, and then breaks every list that reads it~~ | **Closed 2026-10-13**, at six sites. `compatibilityMode: "99"` parsed (`Enum.TryParse` accepts any integer), was committed, answered 500 while projecting its own response, and every `GET …/subjects` in that environment answered 500 for every caller after it; environment defaults, the registration policy and contract enforcement parsed the same way. `WireEnums.TryParseDefined` refuses it, and a comma list that silently meant a third member. Not part of the brief; found by the probes the brief asked for. A number that *is* a member is still read as it always was. |
| ~~One outbox row the build cannot read stops notifications for every tenant~~ | **Closed 2026-10-14**, for builds that contain the fix. Found while measuring the rollback cost of `SUBJECT_POLICY_CHANGED` ([ADR-034](adr/034-a-policy-change-notifies-subscribers.md)) and not specific to it: the stored event is a token parsed on read, and a build older than the one that wrote it threw while the pump materialised its batch, so one undelivered row failed every pass for every tenant and nothing marked it delivered (a message in an unrelated environment stayed pending behind it, measured). **Two changes.** `ClaimDueAcrossTenantsAsync` filters on the tokens the build knows (`event IN (...)`, in SQL, before the `Take`), so a row it cannot read is not claimed: it stays pending, is still counted by the depth endpoint, and is delivered by whichever build can read it. And `NotificationDispatcher` catches a failed read of an environment's subscriptions (a subscription naming an unknown event) and fails *that environment's* messages (recorded, backed off, eventually parked, never discarded) instead of aborting the pass; the failed read is remembered for the pass, so it costs one query. Not done, on purpose: reading an unknown token "as far as possible" (dropping it from a subscription's list would leave it empty, and empty means every event). **What it does not do:** it protects a rollback *to a build that has it*. Rolling back to `e511c11` or `01bce23` has the old behaviour and still needs ADR-034's cleanup first. `GET …/notifications` still answers 500 for an environment holding such a subscription. Measured on the real registry with `A_FUTURE_EVENT` injected by SQL into one outbox row and one subscription: the bystander environment's message was delivered, the unreadable outbox row stayed pending with 0 attempts, and the subscription's message was failed with the reason recorded, in the same pass. Pinned by two integration tests (real PostgreSQL) and three dispatcher tests, each mutation-checked. |
| **A subscription's events cannot be edited** | There is `POST`, `PUT …/enabled` and `DELETE`, and nothing else, so keeping an existing "all events" subscription out of a newly added event (as `SUBJECT_POLICY_CHANGED`, ADR-034) means muting it or deleting it and creating it again, which issues **a new webhook signing secret** the receiver must be given. Found 2026-10-14. |
| ~~A failing `notifications.spec.ts` leaves its subscriptions behind~~ | **Closed 2026-10-14**: a `test.afterAll` deletes the block's subscriptions by endpoint (`deleteSubscriptionsAt`, `web/e2e/registry.ts`). Verified by failing a test mid-block on purpose: `dev` held no subscription afterwards, and with the `afterAll` removed the same failure left the unfiltered email and both webhooks live. It cannot run if the process is killed. What it was: | The block is serial and its deletes are its last tests, so a red test skips them: the email subscription (no event filter) and the webhook stay live in `dev`, and the registry delivers to them for the rest of the run, and after it. Found 2026-10-14 when the new spec was run red on purpose (they were deleted by hand). A `test.afterAll` that deletes by endpoint would close it; not done here. |
| **Hard delete does not exist** | Soft delete is all there is. The full rule — no registered consumers, force flag, audit entry — is an outstanding commitment. |
| ~~Subject prefix search is not implemented~~ | **Closed 2026-10-14** ([ADR-038](adr/038-subject-prefix-search.md)): `GET …/subjects?prefix=`, ordinal and case-sensitive, a literal and not a pattern, through a mapped `starts_with` and with neither the `ComplexProperty` mapping nor the shadow column the note proposed (so no migration). No index, on measurement (65,000 rows: 1.2 ms against 4.7 ms for the whole list; the DDL for the day an environment holds tens of thousands is in the ADR). The web box is the filter. **It no longer matches the owner or the middle of a name, which it did** (the maintainer's call). Still open: the list is unpaginated, and the CLI has no `--prefix`. |
| ~~Two contracts can govern one route, first-by-name wins~~ | **Closed 2026-08-14** by decision 21 — resolve returns all of them, strictest mode and union of subjects, counted on the client's status. M7.4's impact analysis still attributes a route to one contract. |
| ~~A page reload signs you out~~ | **Closed 2026-08-14** by decision 26 — an httpOnly `SameSite=Strict` cookie and a `/auth/resume` route that is the only thing accepting it. The credential still never touches `localStorage`. |
| ~~`AllowAnonymousUntilClaimed` is on by default~~ | **Still on, and now audible** (decision 27). The API logs a warning naming both ways to close it and repeats hourly until claimed; the web app shows a banner. Verified against a real container. |
| ~~No browser E2E over sign-in + guards~~ | **Closed 2026-08-15.** Playwright, in `web/e2e/` — 234 tests today across twenty specs, up from the 11 it launched with, and run against the production image as well as the dev server. It found the subject list broken on its first run — see below. Both of M4.5's named tests are in: "direct URL to a write route redirects" landed the same day as M4.3's write route. |
| ~~`Tenant` is not an aggregate~~ | **Closed by M9.1**, recorded here 2026-09-24 — `Tenant.Create` carries the name and slug invariants, signup writes a row per organisation, bootstrap writes the self-hosted one, and `CloudTenancyTests` drives two organisations through the real host. Self-hosted still holds exactly one row, `TenantId.SelfHosted`: the profiles differ in how many rows there are, not in whether the table means anything. |
| ~~The derived-environment-id decision is unmade~~ | **Closed 2026-08-14.** Adopted, by creating rows that carry the derived id. No migration, and no orphaned subjects. |

---

## Blocked on you

Nothing here can be done from inside the repository.

| Item | Consequence of waiting |
|---|---|
| **Rename the GitHub repo `Signet` → `Concordat`** | Repo-side is done and pushed; only the two clicks remain. Package metadata is already corrected. |
| **Buy `concordat.dev`** | Problem Details `type` URIs already point at it — as seen in the 403 above. Blocks OAuth redirect URIs. |
| **Publish the GHCR images** | The workflow has never run. The package will be private by default, and Container Apps needs it public or a `registries` block plus a pull secret in the Bicep. |
| **Stripe account** | Columns and an index exist so a webhook has somewhere to land. Nothing charges anyone. |
| **Google / GitHub OAuth clients** | SSO cannot be built without registrations. |
| **Create the `@concordat` npm org** | Only matters at M6. |
| **Confirm the dunning policy** | Past-due currently still allows writes, deliberately. |

---

## What the tests actually cover

Six kinds, not one. Worth knowing which, because "1,762 tests" says nothing about what would
survive being wrong.

| Kind | Where | Tests | What it proves |
|---|---|---|---|
| Domain unit | `Domain.Tests`, three `Formats.*` | ~927 | Invariants in isolation. No I/O |
| Application handler | `Application.Tests` | 232 | Handler refusals and **ordering**, with hand-written fakes |
| HTTP integration | `Api.IntegrationTests` | 309 | Real HTTP against real PostgreSQL, via Testcontainers |
| Conformance corpus | `Conformance` | 104, over 103 fixtures | The protocol as an executable spec |
| Broker end-to-end | `EndToEnd`, `RabbitMq.Tests` | 64 | Publish and consume through real RabbitMQ |
| Empirical measurement | `HeaderSurvival` | 14 | What brokers actually do to headers |
| Browser end-to-end | `web/e2e` | 234 | A real Chromium against the real stack, design system included — and against the production image |

Plus 1,504 Angular unit tests. *(Counts above re-measured 2026-10-14; the unit rows are approximate because
the assemblies that do not fit a row — the client, the CLI and the contract testing — are not in them.)*

**No mocking framework, deliberately.** No Moq, NSubstitute, FluentAssertions or AutoFixture —
`Directory.Packages.props` carries xunit, Testcontainers and `TimeProvider.Testing`. Every double
is hand-written in `TestSupport/Fakes.cs`, which is what lets `Application.Tests` assert things
like *"this refusal cost zero meter calls and staged nothing"* — a property mock-verify tends to
hide rather than express.

**`Application.Tests` is mostly about ordering.** The dominant shape is *"a refusal must happen
before anything is spent"*: no canonicalisation, no billing round trip, no staged schema.

**`HeaderSurvival` does not test our code at all.** It raises three brokers — two for federation
alone — and measures whether `concordat-*` headers survive dead-lettering, shovel, federation,
STOMP, MQTT and the AMQP 1.0 conversion. Those measurements are load-bearing: they are what
scoped binary framing out of v1 (decision 19).

**The corpus is a specification, not a suite.** 103 fixtures, each carrying a `why`, enforced by
`EveryFixtureExplainsWhyItExists`. It is what M6's SDKs will be built against, which is why
recent protocol changes landed there rather than only in C#.

### Where the layers have failed each other

Three structural tests exist because a gap between two correct halves shipped anyway:

- `EveryMutatingRouteDeclaresAScope` — caught unguarded routes twice.
- `TheVocabularyMatchesWhatTheFrontendPublishes` — caught the `ci` scope drifting.
- `VersionStatusesMatchWhatTheFrontendPublishes` — **added 2026-08-15, after a browser found
  the subject list broken.** The pattern was already known and had been applied to scopes only.

### Still thin

- **No load, soak or property-based tests.** The outbox pump and the violation reporter have no
  concurrency test beyond what their unique indexes enforce.
- **Coverage is collected and never read**, which is decision 4 and deliberate.
- **CI is six jobs**: `build & test`, `web app`, a separate `browser end-to-end` that runs the
  API and dev server from source, `protocol docs gate`, `contract drift gate` and
  `CLI container`. A domain-unit change still waits on the broker containers in the first job.

---

## Decisions

**10 open.** The "say yes and I go fix it" group was worked through on 2026-08-14, and the
owner's own-estate questions — #14 exchange rights, #10 generic types, #8 pre-release labels —
plus #17's Avro window were answered and built on 2026-08-15.

What is left splits three ways:

| | |
|---|---|
| **Recent calls of mine, to confirm or overturn** | #30 contract beats local `Mode` · #31 `BasicConsumeAsync` wraps consumers · #32 pinned bindings judge `latest` · #33 `ci` is a marker scope · #34 the policy gates subject creation · #35 `ajv` in a worker, and the refusal/advice line · #36 the web container · #37 the create screen and its slug rule · #38 unpaired surrogates, and **six enum parses that were not in the brief** |
| **Product decisions still genuinely open** | #15 hard-delete semantics · #24 whether the audit trail records refusals |
| **Accounts and admin** | #1 repo rename (two clicks) · #3 reserve the unreserved names · #28 is informational |

**#33 is the one most likely to surprise.** No role grants `ci`, including Owner — so a human
cannot register into a `CI_ONLY` environment however senior, and neither can an unclaimed
instance. Both are intentional; both will read as bugs the first time they are hit.

**#24 got cheaper on 2026-08-14.** Recording refusals needs a write path that is deliberately
*not* transactional with the thing that did not happen — and `DeploymentEvent`, built for
decision 29, is already exactly that shape.

---

## If you want to run it yourself

```bash
# 1. build the image (CI publishes it, but GHCR is empty until the workflow runs)
docker build -f docker/api.Dockerfile -t concordat/api:local .

# 2. bring up PostgreSQL, RabbitMQ, the migrator and the registry
cd deploy/compose
CONCORDAT_IMAGE=concordat/api:local docker compose --profile registry up -d

# 3. the registry is on :5062, RabbitMQ management on :15672
curl localhost:5062/health/ready

# 4. the sample publishes a valid message and then an invalid one (paths are repo-relative)
cd ../..
dotnet run --project samples/Quickstart -c Release

# 5. the web app, on :4300 -- not in the compose stack, see above
cd web && npm start -- --port 4300

# 6. the browser suite, against both of the above
npm run e2e            # or e2e:headed to watch it

# 7. stop it
cd ../deploy/compose && docker compose --profile registry down   # -v drops the database
```

The `concordat-postgres` volume persists between runs, so a second `up` starts with whatever the
first one wrote. The E2E suite claims the instance on first run and is idempotent afterwards; if
you have already claimed it by hand, point the suite at that account with `CONCORDAT_E2E_OWNER`
and `CONCORDAT_E2E_PASSWORD`. See [`web/e2e/README.md`](../web/e2e/README.md).
