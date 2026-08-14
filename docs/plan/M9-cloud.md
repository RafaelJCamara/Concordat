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
- [x] KMS-backed Data Protection key ring — Azure Key Vault wraps it, and the `Cloud`
      profile **refuses to start without a key URI**

**Persistence and protection are separate decisions.** The key ring lives in PostgreSQL in
both flavours (M7.2) so a second instance can decrypt what the first wrote. What M9.1 adds
is *wrapping* those keys, so the keys that decrypt every broker credential are not sitting
unprotected in the same database as the ciphertext they protect. A Cloud deployment that
believed its keys were wrapped and found out otherwise during an incident is worse off than
one that never claimed it — hence the refusal rather than a warning. Self-hosted is left
free: an operator running one container against their own database has no KMS and ADR-008
promises they do not need one.

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

**A signup is recorded on the deployment trail, not the organisation's** (decision 29). Audit
rows are stamped with the tenant in scope, and at signup nobody has authenticated — the scope is
whatever an anonymous caller resolves to, which is not the organisation being created, so a row
there would land in someone else's trail.

The fix is **not** a cross-tenant audit write. Creating an organisation is something the
*operator's deployment* did, not something the organisation did to itself, and it wants a
different retention and a different reader; bending the tenant-scoped trail to hold it is how
that trail stops being the thing people trust. `DeploymentEvent` is the one table with no tenant
filter, and `TenantId` on it is data — *which* organisation the event concerns — rather than
scope. Staged on the same change tracker, so an organisation and the record of its creation
commit together or not at all.

**It has no HTTP reader, and that is deliberate rather than unfinished.** The rows span tenants
and there is no operator role to gate an endpoint with: in self-hosted the instance owner is the
operator, in Cloud they emphatically are not, so one gate cannot be correct in both profiles.

## M9.3 Billing

- [x] `Subscription`, `PlanLimits`, `UsageMeter`, `IBillingGate`
- [x] Metering: subjects, versions/month, environments, seats — **not API requests**
- [x] Tiers: Free (1 env, 10 subjects) → Team → Business → Enterprise
- [x] `GET /v1/usage` reports what is used against what the plan allows
- [ ] Stripe adapter and `Invoice` — needs an account. The provider's customer and
      subscription ids already have columns and an index, so the webhook has somewhere to land.
- [ ] `BillingPage` in the web app — nothing to render until there is a checkout to start

**Usage is measured by query, not by counter.** Every figure is derived from rows that
already exist, so there is no meter to increment, nothing to drift and no reconciliation
job. A counter would have to stay correct across a failed transaction, a restore from backup
and a retried request; a `COUNT(*)` is correct by construction and cheap at the sizes these
limits describe.

**API requests are deliberately not metered**, though DESIGN §10 lists them. Counting a
request per request is a write on the read path, and the SDK's schema lookups are the
highest-volume traffic this registry sees — turning each into a row would cost more than the
thing being measured. It needs sampling or an aggregation pipeline, which is a different
design, so it is recorded as not done rather than approximated badly.

**The gate stops creation and nothing else.** Reads are never refused, existing resources
never stop working, and no message is ever rejected for a billing reason. The registry sits
on the delivery path: a plan limit that could stop a consumer resolving a schema would turn
a billing dispute into a production outage, which is a far larger event than an unpaid
invoice. `BeingOverALimitNeverBreaksReads` drives that through the SDK's own startup path.

**A past-due subscription still allows everything.** A card that expired over a weekend must
not stop a team registering a schema. Only a *cancelled* subscription refuses creation.

**A downgrade is never refused for being over the new limit.** Refusing would strand an
organisation on a plan they no longer want to pay for, with the only way out being to delete
things — and deletion here means retiring subjects other teams depend on. Being over a limit
stops you creating more; it never takes away what you have.

**`plan_limit_reached` is 402, not 403.** The caller has every right to do this and their
plan does not stretch to it — a distinction a client can act on by upgrading, rather than by
asking an admin for a scope they already hold.

**A missing subscription row allows everything, rather than falling back to Free.** The row
is created inside the same transaction as the organisation; a missing one means provisioning
went wrong, and the response to an internal inconsistency should not be to silently downgrade
a paying customer.

## M9.4 Deployment

- [x] **Azure Container Apps** — `deploy/azure/main.bicep`, which compiles and lints:
      Container Apps, PostgreSQL Flexible Server, a Key Vault key, Log Analytics, and a
      managed identity granted *Key Vault Crypto User* and nothing else
- [x] The same image serves both profiles — one `Concordat__Profile` environment variable,
      and `CloudTenancyTests` runs the real host in the other one
- [ ] Helm chart — for self-hosted Kubernetes users, and nobody has asked for one. Cloud
      does not need it.

**`minReplicas` is 1, and that is not a capacity decision.** Container Apps scales to zero
by default and the registry carries an in-process background worker: at zero replicas
nothing polls the outbox, so a notification is staged inside the change's own transaction —
correctly — and then delivered whenever the next HTTP request happens to wake the app.
**Alerts stop arriving and nothing reports an error**, because from the registry's point of
view every message is still pending and will be retried. A quiet weekend looks exactly like
a working system.

**What the template deliberately does not do** is written down in
[`deploy/azure/README.md`](../../deploy/azure/README.md) rather than left to be discovered:
no VNet integration, no custom domain, no database HA.

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

**Second half half-met.** Usage is metered and enforced — `BillingApiTests` drives a Free
organisation into its environment and subject limits and asserts a 402 — but nothing reaches
Stripe yet, because that needs an account. The numbers an invoice would be built from are
the part that has to be right regardless, and they are the part that exists.

---

← [M8 — Identity](M8-identity.md) · [Plan index](../PLAN.md)
