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
- `az bicep install` (once)

`image` has no default and the deployment below will fail without it — pass a SHA-tagged image
from the **Publish images** workflow, for example `ghcr.io/rafaeljcamara/concordat-api:<sha>`.
`latest` moves, and a deployment that follows it is one whose version nobody can state
afterwards, so the template refuses to guess.

## Deploy

```bash
az group create --name concordat --location westeurope

# Generate this once and keep it — it is what proves you, not a stranger who found the URL
# first, get to claim the instance below.
BOOTSTRAP_TOKEN=$(openssl rand -hex 32)

# The password the running API itself authenticates with. Deliberately not the admin
# password below: the migration job creates a `concordat_app` login scoped to CRUD on the
# schema — no DDL, no other database — and this is its password. Keep it, the migration job
# needs it too.
DATABASE_APP_PASSWORD=$(openssl rand -hex 32)

az deployment group create \
  --resource-group concordat \
  --template-file deploy/azure/main.bicep \
  --parameters databasePassword='<a long random password>' \
               databaseAppPassword="$DATABASE_APP_PASSWORD" \
               bootstrapToken="$BOOTSTRAP_TOKEN" \
               image=ghcr.io/rafaeljcamara/concordat-api:<sha>
```

The template outputs the registry URL and the database host.

## Migrate before the first start

**The API does not migrate on startup, deliberately** ([M1.5](../../docs/plan/M1-registry-core.md)):
auto-migration means every replica races to alter the schema, and a failed migration takes
the app down rather than failing a step somebody is watching.

**The migrator ships in the same image**, which is deliberate: published separately it could
be built from a different commit than the API, and the mismatch would not surface until a
request touched the changed table. Override the entrypoint to run it:

```bash
az containerapp job create \
  --name concordat-migrate \
  --resource-group concordat \
  --environment concordat-env \
  --trigger-type Manual \
  --image ghcr.io/rafaeljcamara/concordat-api:<sha> \
  --command dotnet Concordat.Migrator.dll \
  --secrets conn="<admin connection string>" approlepwd="$DATABASE_APP_PASSWORD" \
  --env-vars "ConnectionStrings__Concordat=secretref:conn" \
             "Concordat__Provisioning__AppRolePassword=secretref:approlepwd"

az containerapp job start --name concordat-migrate --resource-group concordat
```

The connection string here uses the *admin* login — applying schema migrations needs DDL
rights the API's own login (below) deliberately does not have. `Concordat__Provisioning__
AppRolePassword` is what tells the migrator to also (re)create `concordat_app` with the
password from [Deploy](#deploy) and grant it CRUD — set it every run, not just the first:
a run with nothing to migrate still needs to apply that password if it changed.

Passed as secrets and referenced with `secretref:`, not as plain `--env-vars` values: a
Container Apps job's environment variables are returned in cleartext by `az containerapp job
show`, an ARM export, or the portal to anyone with Reader on the resource group — a secret is
not.

Locally, the same thing is one `docker run` — **unless `usePrivateNetworking=true`**, in which
case PostgreSQL has no public endpoint for a laptop to reach at all, and the
`az containerapp job` form above is the only way to run it: a job in the same environment
shares its VNet integration, a `docker run` on your machine does not.

```bash
docker run --rm -e ConnectionStrings__Concordat='<admin connection string>' \
  -e Concordat__Provisioning__AppRolePassword="$DATABASE_APP_PASSWORD" \
  --entrypoint dotnet ghcr.io/rafaeljcamara/concordat-api:<sha> Concordat.Migrator.dll
```

## Claim the instance

A fresh deployment has no accounts, and until it does it answers every request as an owner
(M8.2) — which is what makes it usable immediately. In the `Cloud` profile the way in is
`signup`, which needs no token because it always creates a brand-new organisation rather than
claiming a shared one:

```bash
curl -X POST https://<url>/v1/auth/signup \
  -H 'Content-Type: application/json' \
  -d '{"organisationName":"Acme","email":"you@example.com","password":"a long password"}'
```

On `SelfHosted`, use `/v1/auth/bootstrap` instead, with the `BOOTSTRAP_TOKEN` generated during
[Deploy](#deploy) — the template wires it in as `Concordat__Authentication__BootstrapToken`, and
the endpoint answers 401 without it:

```bash
curl -X POST https://<url>/v1/auth/bootstrap \
  -H 'Content-Type: application/json' \
  -d "{\"email\":\"you@example.com\",\"password\":\"a long password\",\"token\":\"$BOOTSTRAP_TOKEN\"}"
```

Either way, do it immediately after deploying: `/v1/auth/bootstrap` remains reachable by anyone
who has the token, and the certificate for a fresh `*.azurecontainerapps.io` hostname lands in
public transparency logs within minutes of the deployment finishing.

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

- **No VNet integration by default.** Container Apps egresses from shared Azure IPs, so
  PostgreSQL is reached through the allow-Azure-services firewall rule. Set
  `usePrivateNetworking=true` for a VNet-integrated environment where PostgreSQL has no public
  endpoint at all instead — it is not the default because it roughly triples the resources
  somebody has to understand on a first deployment, and because it can only be chosen before
  the first deployment: PostgreSQL's public-vs-private networking mode is fixed at server
  creation (see the parameter's own description in `main.bicep`).
- **No custom domain or certificate.** Container Apps issues a `*.azurecontainerapps.io`
  hostname; a custom domain is a portal step or a few more lines once the DNS name exists.
- **No high availability on PostgreSQL.** `Standard_B1ms`, burstable, single zone. Fine for
  an evaluation, not for something people depend on.
- **No autoscale on the database.** Storage grows manually.
