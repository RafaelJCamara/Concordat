# Notes for integration — M4.1 (scaffold)

Everything in this milestone package is under `web/`. Nothing outside it was touched: no
`docs/`, no `src/`, no `tests/`, no `.github/`, no `Concordat.slnx`. Where something outside
`web/` needs to change, it is written down here instead of done.

---

## 1. What was built

**Workspace.** Angular 22.1 (CLI 22.1.4), standalone, zoneless, signals, no NgModules.
Vitest as the unit runner (the CLI's default in 22), Tailwind 4 via `@tailwindcss/postcss`.

**Spartan UI.** `@spartan-ng/brain` 1.3.1 as a dependency; `button`, `card`, `badge`,
`table`, `skeleton`, `alert`, `separator` and the shared `utils` generated into
`src/app/shared/ui/` as source, per ADR-006. `components.json` records the generator
settings so a later run is reproducible.

**Tokens.** `src/styles/tokens.css` — a complete light and dark palette on the
shadcn/Spartan token contract, plus `--success`/`--warning`/`--chart-*`. **Placeholder; see
§3.1.**

**State.** `@ngrx/signals` SignalStores: `SessionStore`, `ThemeStore`,
`ActiveEnvironmentStore` (root-provided, in `core/`) and `SubjectListStore` (provided by its
route, as the reference shape for M4.3).

**Folder structure and its enforcement.** The DESIGN §9 tree, with an
`eslint-plugin-boundaries` policy set in `eslint.config.mjs`. **Verified to actually fail —
see §2.**

**Interceptors.** `core/http/` holds `tenantInterceptor`, `authInterceptor` and
`problemDetailsInterceptor`, wired in that order in `app.config.ts`. Ordering is load-bearing
and commented there: Angular runs the response chain in reverse, so problem-details is
registered last in order to be the _first_ to see an error, and everything upstream deals in
`ConcordatError` rather than `HttpErrorResponse`.

**Problem Details typing.** `ConcordatCode` is a real union, not `string`. The domain half is
**generated** from `src/core/Concordat.Domain/Results/ConcordatCodes.cs` by
`tools/generate-concordat-codes.mjs` into `concordat-codes.generated.ts` (38 codes), and
`npm run codes:check` fails when the two drift — the same shape as M3.4's build-time contract
drift detection. The transport-only codes (`invalid_request`, `insufficient_scope`,
`registry_unreachable`, `registry_refused`) are listed by hand in `concordat-codes.ts` with
the reason each cannot be generated.

**One vertical slice.** `features/registry/` — `SubjectsApi` (data-access, the only
`HttpClient`), `SubjectListStore` (application), `SubjectTable` (ui), `SubjectListPage`
(routed). It exists because "SignalStore per feature" and a boundaries rule cannot be
verified against an empty tree, and because M4.3 needs a shape to copy. It is deliberately
one screen and no more.

---

## 2. What was actually run

Every command below was executed in `web/` and the stated result observed.

| Command                  | Result                                                                              |
| ------------------------ | ----------------------------------------------------------------------------------- |
| `npm install`            | 557 packages, `npm audit` → **0 vulnerabilities**                                   |
| `npx ng build`           | Success. 337 kB initial / 90 kB transfer; `subject-list-page` in its own lazy chunk |
| `npx ng lint`            | **All files pass linting**, no warnings                                             |
| `npx ng test`            | 1 file, **5 tests passed**                                                          |
| `npm run codes:check`    | Matches `ConcordatCodes.cs` (38 codes)                                              |
| `npx prettier --check .` | All matched files conform                                                           |
| `npx ng serve`           | Served `index.html` on a local port; dev build completed, watch mode entered        |

**The boundaries rule was verified by breaking it.** Six deliberately violating files were
added, `ng lint` was run, every one was reported, and then they were deleted and lint
re-run clean. The violations, and the message each produced:

1. `domain/registry/*` importing `@angular/core` → _"'domain/' is framework-free TypeScript
   and may not import a framework"_
2. `domain/registry/*` importing `features/registry/data-access` → boundary error
3. `features/registry/feature/*` importing its own `data-access` → boundary error
4. `features/registry/ui/*` importing its own `application` store → boundary error
5. `shared/pipes/*` importing `@angular/common/http` → _"HttpClient belongs in a feature's
   data-access layer or in core/http"_
6. `src/app/stray/*`, in no layer at all → `boundaries/no-unknown-files`

That exercise was worth doing. **The rule was silently inert on the first two attempts**, in
two different ways, and both are now defended against in the config with a comment:

- Without a TypeScript-aware import resolver the plugin cannot resolve `.ts` files or
  `tsconfig` path aliases, so every dependency classified as "unknown" and no policy ever
  matched — zero violations reported, rule looking healthy. Fixed by
  `eslint-import-resolver-typescript`, and `boundaries/no-unknown-dependencies` is now on so
  the same failure cannot recur quietly.
- `boundaries/dependencies` defaults `checkAllOrigins: false`, which skips every
  `node_modules` import — so the "domain may not import Angular" policy, the one DESIGN §9
  asks for by name, was doing nothing. Fixed by `checkAllOrigins: true`.

**Contrast** was checked offline with a throwaway script converting each OKLCH value to sRGB
and computing WCAG 2.1 ratios. Every text-on-surface pair passes AA (4.5:1) in both themes;
`--input` clears 3:1 against both `--background` and `--card` (WCAG 1.4.11). Tightest pairs:
light `warning-foreground` on `warning` at 4.8:1, dark `destructive-foreground` on
`destructive` at 5.1:1.

**Not verified.** Nothing was rendered in a browser, so the visual result, the theme toggle's
behaviour, focus order and screen-reader output are all unverified beyond static markup and
lint's accessibility rules. The app has never been run against a live API — the data-access
layer matches `Concordat.Api/Contracts.cs` by reading it, not by exchanging a request.

---

## 3. Decisions for the project owner

### 3.1 The token set is a placeholder — this is the big one

M4.1 says to port the prototype's `index.css` **verbatim**. The prototype is not in this
repository, so that was not possible. `src/styles/tokens.css` is a stand-in built to the same
contract and carries a banner saying so.

Replacing it should be a values-only edit to that one file: no component names a colour, and
the file uses the shadcn shape (`:root` for light, `.dark` for dark) that the prototype
already ships, so the swap is a paste rather than a translation. Three things to know when
you do it:

- The prototype is dark-only. The light block has no counterpart to copy and must be
  re-derived from the incoming dark values, then re-checked for contrast.
- `--success`, `--warning` and `--chart-*` are additions the prototype will not have.
  `--success`/`--warning` are not decoration: this UI is mostly status (`ACTIVE`,
  `AWAITING_APPROVAL`, `REJECTED`, `DEPRECATED`, compatible/breaking), and without tokens for
  them those states get expressed as ad-hoc palette classes that drift screen to screen.
  Keep them and give them values consistent with the incoming palette.
- `--border` and `--input` are deliberately different weights — decorative versus a control's
  boundary, which WCAG 1.4.11 requires at 3:1. If the prototype gives them the same value,
  that is a bug worth keeping fixed rather than porting.

### 3.2 `@ngrx/signals` is on a release candidate

`@ngrx/signals@21.1.1` peers on `@angular/core@^21`; the only build that peers on Angular 22
is `22.0.0-rc.0`, which is what is pinned. The alternative was `--legacy-peer-deps`, which
would have hidden a real incompatibility rather than avoided one. The API surface used
(`signalStore`, `withState/Computed/Methods/Props/Hooks`, `patchState`, `rxMethod`) is the
stable one. Move to `^22.0.0` when it ships.

### 3.3 The API contradicted itself on one wire token — **fixed**

`CompatibilitySurface.WireJson` serialised **two different ways** depending on where it
appeared: `PolicyResponse` used `enum.ToString().ToUpperInvariant()` and produced `WIREJSON`,
while `BreakingChangeResponse` used an explicit switch and produced `WIRE_JSON`. The
transitive modes came out as `BACKWARDTRANSITIVE`. The write side accepted either, so only
reads were inconsistent.

**Resolved in M6.1.** `WireTokens` in the Domain — which already existed for `SchemaFormat`,
carrying a comment that these strings must not be `Enum.ToString()` because a C# rename must
not silently change the wire format — now covers `CompatibilityMode`,
`CompatibilitySurface`, `SubjectLifecycle`, `ContentModel` and `VersionStatus`. Every
projection routes through it and `WireTokenTests` pins the tokens as literals.

**`WIRE_JSON` and `BACKWARD_TRANSITIVE` won.** The underscored spelling was already what
`BreakingChangeResponse` emitted and what the request side documents, so the alternative would
have broken more.

This build targets the corrected protocol: `compatibility.ts` spells the tokens with
underscores and `toSurface` no longer strips them. The leniency was removed deliberately —
keeping it would mean a future divergence passed silently, which is how this one survived five
milestones.

> Worth recording why this was found at all: a single implementation never notices that it
> disagrees with itself. It took a second client reading the same API.

### 3.4 `insufficient_scope` is not in the catalogue

DESIGN §5 (Context E) and ADR-018 both name `concordatCode: "insufficient_scope"` as the
answer to an unauthorised mutation, but it is not in `ConcordatCodes.cs` — reasonably, since
authorisation is M8. It is declared client-side in `concordat-codes.ts` as a transport code so
M4.2 can name the refusal. When M8 adds it to the .NET catalogue, delete it from that list or
the union will carry it twice.

`invalid_request` has the same shape: the API raises it in `SubjectEndpoints` but the domain
catalogue does not contain it. Worth deciding whether request-validation codes belong in
`ConcordatCodes` too, given ADR-019 makes the strings normative for every SDK.

### 3.5 The tenant header name is a guess

`tenantInterceptor` sends `X-Concordat-Tenant` when `config.tenant` is set, and nothing at all
in the self-hosted profile (where the server binds `TenantId.Default` and there is nothing for
the client to say). No endpoint reads that header today. The name needs agreeing with M8, and
the server must treat it as a _selector among the caller's authorised tenants_, never as the
tenant itself — `ITenantContext`'s own doc comment already says as much.

### 3.6 Credential storage

`SessionStore` holds the bearer credential **in memory only** — not `localStorage`, not
`sessionStorage`. A token in web storage is readable by any script on the page, and this
project has already declined to ship one XSS hole (ADR-006). **Closed 2026-08-14 by decision
26**: an httpOnly `SameSite=Strict` session cookie whose only power is handing back this same
credential via `POST /v1/auth/resume`, called by `SessionApi.resume()` on startup. A reload no
longer signs the user out, and the credential still never touches web storage.

### 3.7 The toolchain's Node floor is above the machine's Node

`@angular/cli@22` requires Node `^22.22.3 || ^24.15.0 || >=26`. The Node on this machine is
**v22.17.1**, which the CLI refuses outright. Everything reported in §2 was run against a
portable Node 22.23.2 unpacked into a scratch directory, not against the installed one.
`package.json` declares the same `engines` range so the failure is a clear message rather than
a puzzle. **The installed Node needs upgrading before anyone can run `npm start` here.**

### 3.8 Things outside `web/` that will need doing

- **CI.** `.github/workflows/ci.yml` has no web job. It wants `npm ci`, `npm run lint`,
  `npm run build`, `npm test` and — importantly — `npm run codes:check`, which is the
  drift gate and is worthless if it only ever runs locally.
- **`.dockerignore`.** DESIGN §10 says self-hosted is one image serving the API _and_ the
  embedded SPA. When that Dockerfile is written, `web/node_modules`, `web/dist` and
  `web/.angular` need excluding from the build context, the same way `**/bin/` and `**/obj/`
  already are.
- **The SPA is same-origin by default.** `provideConcordatConfig()` defaults `apiBaseUrl` to
  `''`, which is correct for that one-image deployment and means no CORS configuration and no
  cross-origin request that could carry a credential. A deployment that serves the SPA from a
  different origin overrides it at bootstrap; nothing about the API location is baked into the
  bundle.
- **Root `.editorconfig`.** The one at the repository root is `root = true` and already sets
  UTF-8, LF and 2-space indent for `[*]`. The Angular generator also writes a
  `web/.editorconfig` declaring `root = true` again, which would sever that; it was not
  carried over, so the repository's own settings apply here.

---

## 4. Where the spec was ambiguous, and what was chosen

**"Port the prototype's `index.css` verbatim"** — impossible, see §3.1.

**"`core/` interceptors: auth, tenant, problem-details"** — DESIGN does not say what the
tenant interceptor is _for_ in a deployment with one implicit tenant. It is implemented as a
tenant _selector_ that is a documented no-op self-hosted, and both it and the auth interceptor
gate on `isApiUrl()` so that no header ever attaches to a request going somewhere else. That
gate is the substantive part: an interceptor without it eventually hands a registry bearer
token to a CDN.

**Empty folders.** DESIGN §9 lists `shared/directives/`, `domain/topology/`,
`domain/billing/`. They are **absent**, not stubbed:

- `shared/directives/` is where M4.2's `*cdIfScope` goes. Creating a stub now would collide
  with that work. The ESLint config already covers the path.
- `domain/topology/` and `domain/billing/` would mean inventing wire types for contracts (M2)
  and billing (Cloud) before those APIs exist. Types invented ahead of their API go stale
  before they are used.
- `domain/identity/` **is** present — it holds `Scope`, `SCHEMA_WRITE_SCOPES` and `grants()`
  from DESIGN §5, which are settled and which M4.2 needs.

Git cannot track an empty directory anyway, so a `.gitkeep` would be the artefact, not the
structure.

**Domain vocabulary = wire vocabulary.** `domain/registry/` uses the API's own tokens
(`'ACTIVE'`, `'AWAITING_APPROVAL'`, `'BACKWARD_TRANSITIVE'`) rather than a prettier
client-side spelling. ADR-019 guarantees those tokens will not be renamed, so a second
vocabulary would buy nicer templates at the cost of a two-way mapping table to get wrong.
Display formatting is a presentation concern and belongs in a pipe. The _shapes_ are still
mapped at the boundary — dates parsed, policies normalised, unknown tokens rejected.

**Unknown tokens throw.** `subject-dtos.ts` refuses a status/format/lifecycle it does not
recognise instead of coercing. A registry newer than the bundle that gains a fourth version
status would otherwise have it silently rendered as `ACTIVE`, and a UI that mislabels the
approval gate is worse than one that declines to draw the row. `concordatCode` and
`BreakingChange.kind` go the other way — both are open-ended and tolerated — and the reasons
for the difference are written next to each.

**Zoneless.** Angular 22's default for a new workspace, and the right fit for a
signals-and-SignalStore app. `zone.js` is not a dependency.

**Component prefix `cd`.** Chosen because DESIGN §9 names the M4.2 directive `*cdIfScope`,
which fixes the prefix. Enforced by `@angular-eslint/component-selector`.

**Feature context naming.** DESIGN says `features/<ctx>/`; the contexts are named A–G in §4.
`features/registry/` uses Context A's name. The others follow the same convention.

**Type-aware linting is on.** It costs a TypeScript program per lint run and buys
`@angular-eslint/no-uncalled-signals`, which catches a signal read without `()` in a template
— a mistake that renders the function's source text into the page and is invisible in review.

**`angular-eslint` `tsRecommended`, not `tsAll`.** `tsAll` plus a dozen rules switched back
off means every angular-eslint minor release can break the build with a rule nobody chose.
Three extra rules are enabled explicitly instead.

**`@spartan-ng/cli` is not a devDependency.** It depends on the entire Nx toolchain, which
brought **12 high-severity advisories** into `npm audit` — all dev-only and all unrelated to
anything shipped, but enough to make an audit gate permanently red. The helm components are
already source in the repo, so the generator is scaffolding (like `ng new`), not a
dependency. `README.md` documents the install-generate-uninstall cycle. Audit is currently
clean at zero.

**One test file exists.** M4.5 owns testing, and this is not it. `subject-dtos.spec.ts` is
there because an unrun test target is another piece of unverified scaffolding, and because
the surface spelling in §3.3 is exactly the kind of thing that gets "cleaned up" by someone
who does not know why it is there. It now pins the _corrected_ protocol, including a negative
case asserting the old spelling is refused rather than quietly accepted.

**A known, accepted flash.** `main.ts` applies the stored theme before `bootstrapApplication`,
which is flash-free in practice because the body is empty until Angular renders. Making it
flash-free by construction would need an inline boot script (and therefore a CSP nonce) or
SSR. Neither seemed worth it at M4.1; both are still open.

---

## 5. Seams left for later milestones

### M4.2 — access control (ADR-018)

- `SessionStore` carries `scopes: readonly Scope[]`. **`canWriteSchemas` is deliberately
  not there.** Stubbing it `true` now is how a write path ships ungated, and DESIGN §9 asks
  for one source of truth rather than two. It is a one-line `computed` over
  `grants(scopes(), SCHEMA_WRITE_SCOPES)`.
- `domain/identity/scope.ts` already holds the scope list, `SCHEMA_WRITE_SCOPES` and
  `grants()`, so the directive, the guard and the store cannot disagree about spelling.
- `shared/directives/` is the home for `*cdIfScope`; the boundaries config already covers it,
  and `shared/` may not reach into `core/`, so the directive should take the decision as an
  input or read a store injected by its host — worth thinking about before writing it.
- `scopeGuard` goes on the write routes in `app.routes.ts`. The comment there records that
  resource identity belongs in the path, never a query parameter.
- The auth interceptor already clears the session on 401 and deliberately leaves 403 alone —
  a 403 means the credential is fine and the scope is not, and signing the user out would hide
  the read surface ADR-018 says they are entitled to. **Redirecting to `LoginPage` is not
  wired**, because there is no `LoginPage` until M4.3.
- `SubjectListPage` carries a comment marking where the gated "New subject" affordance goes,
  and that it must be _absent_ rather than disabled.

### M4.3 — pages

- `withComponentInputBinding()` is on, so `/subjects/:name` screens can use
  `name = input.required<string>()` instead of subscribing to `ActivatedRoute`.
- `ActiveEnvironmentStore` exists and is root-provided; the header shows the active
  environment but there is **no environment switcher UI** yet. `SubjectListPage` already
  reloads on a change via an effect, so a switcher is a component, not a rewrite.
- `SubjectsApi` has `listSubjects` and `getSubject`. Register / approve / reject / diff /
  policy all belong on it — not on a second service, and not on a store.
- `SubjectListStore` is the reference store shape: state, one computed view, one `rxMethod`
  per thing the screen can ask for, errors captured as state rather than rethrown.

### M4.4 — port corrections

- **Monaco and `ajv` are not installed.** Both are M4.4's, and adding an unused dependency
  now would contradict the milestone's own instruction to drop the unused dependency surface.
- The prototype's regex highlighter is not ported and never should be (XSS, ADR-006).
- The `--font-mono` token is already defined and the subject table already uses `font-mono`
  for schema text, so Monaco has a font stack to match.

### M4.5 — tests

- Nothing E2E exists. Vitest is configured and running; Playwright is not installed.
- The non-admin E2E case in M4.5 depends entirely on M4.2's stub being switchable — worth
  keeping in mind when writing it, since a stub hardcoded to admin makes that test
  unwritable.
