# M4 — Angular web app

**Depends on:** [M1](M1-registry-core.md) · **Design refs:** [§9](../DESIGN.md#9-frontend-architecture-angular), decisions 006, 018

Off the critical path — can slip without invalidating M0–M3.

---

## M4.1 Scaffold

- [x] Angular 22 standalone + signals, no NgModules; Angular CLI 21+
- [x] Spartan UI — `@spartan-ng/brain` + `helm` components generated into the repo as source
- [x] Port the prototype's `index.css` token set **verbatim**; add a light theme
- [x] `@ngrx/signals` SignalStore per feature
- [x] Folder structure per DESIGN §9; **ESLint boundaries rule** enforcing it
- [x] `core/` interceptors: auth, tenant, problem-details

## M4.2 Access control

**ADR-018**

Build now, not in M8 — retrofitting across finished screens is how a write path gets missed.

- [x] `canWriteSchemas` computed on the session store, derived from API-returned scopes
- [x] `*cdIfScope` structural directive for affordances
- [x] `scopeGuard` on write routes; direct navigation redirects to the read view
- [x] ~~Stub returning admin~~ — **superseded, and better.** The stub was never built; the
      API's unclaimed-instance caller (M8.2) does the same job on the server, so the UI and
      the server agree by construction rather than by two people remembering to.
- [x] Write affordances **absent, not disabled**, for non-admins

**Delivered across M4.1 and M8.2.** The access-control half deliberately waited for real
scopes rather than shipping against a stub — the sequencing hazard ADR-018 warned about was
retrofitting the *check*, and the check went in with the screens. What arrived late was the
sign-in screen that lets someone pass it, which is recorded as
[decisions-pending #26](../DECISIONS-PENDING.md).

## M4.3 Pages

- [ ] `Dashboard` — environment-scoped overview
- [ ] `SubjectListPage`, `SubjectDetailPage` (`/subjects/:name`, **real routes not query params**), `VersionDetailPage`, `NewVersionPage`
- [ ] `ContractsPage` — bindings, enforcement mode, version selectors
- [ ] `CompatibilityDiffPage`, `ImpactAnalysisPage`
- [ ] `ApprovalsPage` — pending breaking changes with diff and impact; approve/reject (admin only)
- [ ] `AuditLogPage` — `LoginPage` shipped with [M8.2](M8-identity.md) as `sign-in-page.ts`
- [ ] Settings split: `EnvironmentSettings`, `Brokers`, `ApiKeys`, `Members`
- [ ] Notifications as reactive forms that actually persist

## M4.4 Port corrections

**Three of these were moot and are struck through**, audited 2026-08-14. They were written
against a React prototype on the assumption it would be ported file by file. It was not — the
Angular app was written fresh — so the defects they describe were never introduced. Struck
rather than deleted, because "why is there no zod here?" is a question worth answering once.

- [ ] **Monaco for the schema editor.** Listed here as replacing the prototype's
      `dangerouslySetInnerHTML` regex highlighter, which was also never ported — so the XSS
      framing has expired. Monaco is still wanted, on its own merits
- [ ] `ajv` for client-side validation and sample-payload checking
- [ ] Preserve: immutable-id confirmation, auto-slugged id with a touched flag, semver auto-increment seeded from latest, clone-previous-version JSON, compatibility tooltips, "No versions yet" empty state, Format/Validate buttons
- [x] ~~Collapse the two competing HTTP paths into one typed data-access layer~~ — **moot.**
      There is one: `HttpClient` plus three interceptors, and a typed data-access file per feature
- [x] ~~Uncontrolled `defaultValue` forms → reactive forms~~ — **moot.** A React idiom; no such
      forms exist. New pages still use reactive forms, which is a convention, not a correction
- [x] ~~Drop the unused dependency surface (React Query, react-hook-form, zod, recharts, next-themes, cmdk, vaul, embla, input-otp)~~
      — **moot.** None of them are in `package.json`; the Angular app never took them

## M4.5 Tests

- [ ] Playwright E2E against a real API
- [ ] Non-admin E2E: no write affordance renders; direct URL to a write route redirects

---

## Exit

Create a subject, register a version, view a diff and an impact report, and approve a
pending breaking change — entirely in the UI.

---

← [M3 — CLI](M3-cli.md) · [Plan index](../PLAN.md) · [M5 — Formats →](M5-formats.md)
