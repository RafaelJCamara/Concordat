# Deploying Concordat to Azure Container Apps

One stateless container plus PostgreSQL, a Key Vault key to wrap the Data Protection key
ring, and Log Analytics. `main.bicep` provisions all of it.

Container Apps rather than AKS: the registry is one container, and a cluster would be
infrastructure to operate rather than infrastructure that earns its keep. The trade is that
a couple of things a cluster gives you for free have to be asked for — see
[Scale to zero](#scale-to-zero-and-the-outbox) below, which is the one that bites silently.

---

## What you need first

- An Azure subscription and `az login`
- The registry image pushed somewhere Container Apps can pull from — GHCR is fine for a
  public image, ACR for a private one
- `az bicep install` (once)

## Deploy

```bash
az group create --name concordat --location westeurope

az deployment group create \
  --resource-group concordat \
  --template-file deploy/azure/main.bicep \
  --parameters image=ghcr.io/rafaeljcamara/concordat:latest \
               databasePassword='<a long random password>'
```

The template outputs the registry URL and the database host.

## Migrate before the first start

**The API does not migrate on startup, deliberately** ([M1.5](../../docs/plan/M1-registry-core.md)):
auto-migration means every replica races to alter the schema, and a failed migration takes
the app down rather than failing a step somebody is watching.

Run the migrator against the database the template created:

```bash
ConnectionStrings__Concordat="Host=<databaseHost>;Database=concordat;Username=concordat;Password=<password>;SSL Mode=Require" \
  dotnet run --project src/hosts/Concordat.Migrator
```

In a pipeline this is a Container Apps **job** using the same image, run before the revision
is promoted.

## Claim the instance

A fresh deployment has no accounts, and until it does it answers every request as an owner
(M8.2) — which is what makes it usable immediately and also means **it is open to anyone who
can reach it**. Close it straight away:

```bash
curl -X POST https://<url>/v1/auth/signup \
  -H 'Content-Type: application/json' \
  -d '{"organisationName":"Acme","email":"you@example.com","password":"a long password"}'
```

In the `Cloud` profile, `signup` is the way in. On `SelfHosted`, use `/v1/auth/bootstrap`.

---

## Scale to zero, and the outbox

`minReplicas` is **1**, and that is not a capacity decision.

The registry runs an in-process background worker: `OutboxPump` drains staged notifications
on a timer (M7.5). At zero replicas nothing polls — so a breaking change lands, its
notification is written to the outbox inside the same transaction *correctly*, and is then
delivered whenever the next HTTP request happens to wake the app.

The failure that produces is the worst kind: **alerts stop arriving and nothing reports an
error**, because from the registry's own point of view every message is still pending and
will be retried. A quiet weekend looks exactly like a working system.

One always-on replica is the cheap fix. If that idle cost ever matters, the alternative is
to move the pump into a Container Apps job on a cron schedule and let the API scale to zero
— recorded in [decisions-pending](../../docs/DECISIONS-PENDING.md) rather than pre-emptively
built.

## Key wrapping is required in Cloud

`Concordat__KeyProtection__KeyUri` points at the Key Vault key the template creates, and the
app **refuses to start in the `Cloud` profile without it**. Without wrapping, the key ring
sits unwrapped in the same database as the broker credentials it protects, so a database
dump is enough to read every tenant's broker passwords.

The container authenticates with its system-assigned managed identity, granted **Key Vault
Crypto User** — wrap and unwrap, nothing else. The app never needs the key material and Data
Protection is designed so that it cannot obtain it.

Purge protection is on. This key is the only thing that can decrypt every stored credential,
and a deleted key that cannot be recovered is a permanent outage.

## What this template does not do

Named so nobody assumes otherwise:

- **No VNet integration.** Container Apps egresses from shared Azure IPs, so PostgreSQL is
  reached through the allow-Azure-services firewall rule. A VNet-integrated environment with
  a private endpoint is the right answer for production; it is not the default here because
  it roughly triples the resources somebody has to understand on a first deployment.
- **No custom domain or certificate.** Container Apps issues a `*.azurecontainerapps.io`
  hostname; a custom domain is a portal step or a few more lines once the DNS name exists.
- **No high availability on PostgreSQL.** `Standard_B1ms`, burstable, single zone. Fine for
  an evaluation, not for something people depend on.
- **No autoscale on the database.** Storage grows manually.
