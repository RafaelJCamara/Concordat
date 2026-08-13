# M8 — Identity, RBAC, API keys

**Depends on:** [M7](M7-governance.md) · **Unlocks:** [M9](M9-cloud.md) · **Design refs:** [§4 Context E](../DESIGN.md#context-e--identity--access), decisions 008, 018

---

## M8.1 Identity model

- [ ] `Tenant`, `User`, `Membership`, `Role`
- [ ] `ApiKey`, hashed at rest, with scopes
- [ ] Scopes: `subject:read|write|admin`, `contract:*`, `env:*`, `broker:*`, `org:admin`
- [ ] Local accounts; **OIDC optional** (ADR-008 — no third-party dependency required to run Indenture)

## M8.2 Authorization (ADR-018)

- [ ] `subject:write` and `subject:admin` granted to admin roles only; non-admin membership carries `subject:read`
- [ ] Every mutating subject/version endpoint checks scope server-side → `403 insufficient_scope`
- [ ] Approve/reject is admin-only — keeps the author of a breaking change from waving it through
- [ ] Replace [M4.2](M4-web-app.md#m42-access-control-adr-018)'s admin stub with real role resolution

## M8.3 Tests

- [ ] Every mutating endpoint rejects a `subject:read` principal — for API keys and sessions alike
- [ ] The read surface stays fully available to non-admins
- [ ] E2E as a non-admin: no write affordance renders; direct URL to a write route redirects

---

## Exit

A non-admin can browse everything and change nothing, and that holds against curl, not
just the UI.

---

← [M7 — Governance](M7-governance.md) · [Plan index](../PLAN.md) · [M9 — Cloud →](M9-cloud.md)
