# M9 — Concordat Cloud

**Depends on:** [M8](M8-identity.md) · **Design refs:** [§10](../DESIGN.md#10-deployment-flavours), decisions 009

Same image, `CONCORDAT__PROFILE=Cloud`. Everything here is Apache-2.0 too (ADR-009) — Cloud
competes on managed upgrades, backups, HA, SLA and support, not on withheld code.

---

## M9.1 Tenancy

- [x] `ConcordatProfile` swaps `ITenantContext` **at the composition root** — read in exactly
      one place, and no `if (cloud)` anywhere below it
- [x] `Tenant` aggregate, and a real row in both flavours
- [x] Multi-tenant row-level isolation via the EF global query filters wired in
      [M1.5](M1-registry-core.md#m15-persistence)
- [ ] KMS-backed Data Protection key ring — deferred. The key ring is already an
      abstraction (`PersistKeysToDbContext`); pointing it at a KMS is a provider choice and
      credentials, neither of which exists yet, and a test would be mocking the cloud.

**The tenant comes from the credential, never from the request.** A header, a path segment
or a subdomain are all caller-supplied, and this value is the sole input to every global
query filter — a wrong answer silently returns another organisation's data rather than
failing. An API key names its tenant because it was issued inside one; a session names the
tenant its membership is in.

**The environment-id collision was real, and it was recorded before it was hit.**
Environment ids are derived by hashing the name, so two organisations both naming an
environment `prod` derived the same id. `CreateEnvironmentHandler` called this out as an M9
constraint when the aggregate landed; M9.1 puts the tenant in the preimage.
`TenantId.SelfHosted` keeps the original preimage, because every id an existing install has
stored was computed without one and changing it would point every subject at an environment
that no longer exists — silently, since nothing joins on it.

**Sign-in discovers the organisation rather than assuming it.** The earlier version took the
ambient tenant, which is correct when there is one and wrong the moment there are two: at
sign-in nobody has authenticated yet, so the ambient tenant is whatever an *anonymous*
caller resolves to. A user who belongs to exactly one organisation — everybody, on a
self-hosted deployment — needs to name nothing; anyone in several must, because choosing for
them would drop somebody into the wrong organisation and the first sign would be data they
did not expect.

**An anonymous caller in Cloud resolves to a tenant nobody is a member of**, so the read
surface is empty rather than everything. A filter that matches nothing beats one that
matches everything. The unclaimed-instance owner (M8.2) is a self-hosted first-run
affordance and has no equivalent here.

## M9.2 Signup and identity

- [x] Org signup — `POST /v1/auth/signup` creates an organisation and its first owner
- [ ] Google / GitHub SSO — needs OAuth client registrations, which are yours to create.
      ADR-008 already makes OIDC optional and the seam is the same one local accounts use.
- [ ] SAML on the top tier — a tier that does not exist until M9.3 prices one

**Signup and bootstrap are deliberately different commands.** Bootstrap claims a deployment
and can only ever run once; signup creates one organisation among many and runs forever.
Sharing a handler would mean one code path whose safety depends on a profile flag, which is
the shape DESIGN §10 rejects.

**Signup does not say whether an email is already known.** A form open to the internet that
distinguishes "taken" from "invalid" is an account enumeration oracle, so it answers the
same way for both.

**A signup writes no audit entry, and that is a limitation rather than an omission.** Audit
rows are stamped with the tenant in scope, and at signup nobody has authenticated — the
scope is whatever an anonymous caller resolves to, which is not the organisation being
created. A row in the wrong organisation's trail is worse than no row. The record of a
signup is the tenant's own `CreatedAt` and its owner's membership, both queryable; see
decisions-pending.

## M9.3 Billing

- [ ] Stripe: `Subscription`, `Plan`, `UsageMeter`, `Invoice`
- [ ] Metering: subjects, versions/month, API requests, environments, seats
- [ ] Tiers: Free (1 env, 10 subjects) → Team → Business → Enterprise
- [ ] `BillingPage` in the web app

## M9.4 Self-hosted parity

- [ ] Helm chart
- [ ] Verify the same image serves both profiles

---

## Exit

Two tenants on one deployment cannot see each other's subjects, proven by test, and
metered usage reaches Stripe.

**First half met.** `CloudTenancyTests` hosts the API as `ConcordatProfile.Cloud` over its
own database and drives two real organisations through signup, sign-in and writes: neither
sees the other's environments, subjects, audit trail, members or keys; both can own an
environment called `prod` and a subject of the same name; and a key issued in one gets a
404 — not a 403 — for a subject that exists in the other, because saying "forbidden" would
confirm it exists somewhere.

The profile is exercised as a *configuration value*, not by substituting `ITenantContext` in
the test container. Reaching in would prove something weaker than the thing that ships.

**Second half not started.** M9.3 needs a Stripe account.

---

← [M8 — Identity](M8-identity.md) · [Plan index](../PLAN.md)
