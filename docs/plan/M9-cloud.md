# M9 — Signet Cloud

**Depends on:** [M8](M8-identity.md) · **Design refs:** [§10](../DESIGN.md#10-deployment-flavours), decisions 009

Same image, `SIGNET__PROFILE=Cloud`. Everything here is Apache-2.0 too (ADR-009) — Cloud
competes on managed upgrades, backups, HA, SLA and support, not on withheld code.

---

## M9.1 Tenancy

- [ ] `SignetProfile` swaps `ITenantResolver`, `IBillingGate`, `IIdentityProvider` **at the composition root** — no `if (cloud)` scattered around
- [ ] Multi-tenant row-level isolation via the EF global query filters wired in [M1.5](M1-registry-core.md#m15-persistence)
- [ ] KMS-backed Data Protection key ring

## M9.2 Signup and identity

- [ ] Org signup
- [ ] Google / GitHub SSO
- [ ] SAML on the top tier

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

---

← [M8 — Identity](M8-identity.md) · [Plan index](../PLAN.md)
