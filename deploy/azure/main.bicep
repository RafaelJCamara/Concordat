// Concordat on Azure Container Apps (M9.4).
//
// Container Apps rather than AKS: the registry is one stateless container plus PostgreSQL, and
// a cluster would be infrastructure to operate rather than infrastructure that earns its keep.
// The trade is that some things a cluster gives you for free have to be asked for here — see
// `minReplicas` below, which is load-bearing rather than a capacity choice.

@description('Where to deploy. Container Apps and Flexible Server must agree.')
param location string = resourceGroup().location

@description('Distinguishes resource names within the resource group.')
@minLength(3)
@maxLength(20)
param name string = 'concordat'

@description('''
The registry image to run, including its tag. No default, deliberately: `:latest` moves, and a
production deployment that floats on it cannot state which build is actually running, cannot be
rolled back to a known-good version, and re-deploys something different every time this template
re-runs without anyone changing it. Pin to a commit-SHA tag from the Publish images workflow, for
example ghcr.io/rafaeljcamara/concordat-api:<sha>.
''')
@minLength(1)
param image string

@description('The PostgreSQL administrator login.')
param databaseAdministrator string = 'concordat'

@description('''
The PostgreSQL administrator password. Used to provision the database and, by whoever runs the
migration job, to apply schema changes — never by the running API. See databaseAppPassword.
''')
@secure()
param databasePassword string

@description('''
The password for `concordat_app`, a login the migration job creates with CRUD on the schema and
nothing else — no DDL, no ownership, no grants on other databases (Migrator/Program.cs). This is
what the running API authenticates as, so a bug that lets an attacker run arbitrary SQL through
it cannot alter the schema, create a new login, or read another database on the same server, the
way it could running under `databaseAdministrator`. Generate independently, the same way as
bootstrapToken.
''')
@secure()
@minLength(20)
param databaseAppPassword string

@description('''
The shared secret POST /v1/auth/bootstrap must present to create the first owner. Required
because this deployment is internet-reachable the moment it comes up: without a token, an
unclaimed instance answers every request as owner, and whoever calls /bootstrap first keeps the
instance and locks you out. Generate one with `openssl rand -hex 32` and use it as the `token`
field in your first bootstrap request; nothing else needs it again.
''')
@secure()
@minLength(20)
param bootstrapToken string

@description('SelfHosted or Cloud. Cloud refuses to start without a Key Vault key.')
@allowed(['SelfHosted', 'Cloud'])
param profile string = 'Cloud'

@description('''
Off by default, matching "What this template does not do" in the README: PostgreSQL reachable
only through a VNet, instead of the public endpoint behind the AllowAllAzureServices firewall
rule. The app's own ingress is unaffected either way — this is about the database hop, not
whether the API is internet-facing. Flip to true for a deployment where "PostgreSQL has a
public endpoint at all, even one only Azure services can reach" is not an acceptable answer.

This changes how the PostgreSQL server is provisioned (delegated subnet instead of a public
endpoint), which Azure only supports at create time — flipping it on an existing deployment
recreates the server rather than reconfiguring it in place, which means a new empty database.
Decide before the first deployment, not after.
''')
param usePrivateNetworking bool = false

var databaseName = 'concordat'
var suffix = uniqueString(resourceGroup().id)

// Must match the role name Concordat.Migrator/Program.cs creates — the two are not wired
// together by anything Bicep can check, so a rename needs both sides updated by hand.
var appRoleName = 'concordat_app'

// ---------------------------------------------------------------------------- observability

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${name}-logs'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

// ------------------------------------------------------------------------------- networking
//
// Only provisioned when usePrivateNetworking is true. Two subnets in one VNet: Postgres
// requires a subnet delegated exclusively to it, and Container Apps' own infrastructure subnet
// cannot be that same subnet, so they cannot share one even though nothing else uses either.

resource vnet 'Microsoft.Network/virtualNetworks@2024-05-01' = if (usePrivateNetworking) {
  name: '${name}-vnet'
  location: location
  properties: {
    addressSpace: { addressPrefixes: ['10.0.0.0/16'] }
    subnets: [
      {
        // Sized for a Consumption-only managed environment (minimum /27); generous headroom
        // costs nothing since these are private RFC 1918 addresses, not a public allocation.
        name: 'apps'
        properties: {
          addressPrefix: '10.0.0.0/23'
          delegations: [
            {
              name: 'Microsoft.App.environments'
              properties: { serviceName: 'Microsoft.App/environments' }
            }
          ]
        }
      }
      {
        // Postgres flexible server's delegation requirement: this subnet may contain nothing
        // else, ever — not even another delegated resource of a different type.
        name: 'postgres'
        properties: {
          addressPrefix: '10.0.2.0/24'
          delegations: [
            {
              name: 'Microsoft.DBforPostgreSQL.flexibleServers'
              properties: { serviceName: 'Microsoft.DBforPostgreSQL/flexibleServers' }
            }
          ]
        }
      }
    ]
  }
}

// Flexible server with a delegated subnet resolves its own hostname through this rather than
// the public DNS Azure manages for publicly-accessible servers — required, not optional, for
// the private-access mode this VNet exists to enable.
resource postgresDns 'Microsoft.Network/privateDnsZones@2024-06-01' = if (usePrivateNetworking) {
  name: 'privatelink.postgres.database.azure.com'
  location: 'global'
}

resource postgresDnsLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = if (usePrivateNetworking) {
  parent: postgresDns
  name: '${name}-vnet-link'
  location: 'global'
  properties: {
    virtualNetwork: { id: vnet.id }
    registrationEnabled: false
  }
}

// resourceId() rather than referencing vnet's own subnets output: that reference would require
// Bicep to prove the (conditional) vnet resource exists wherever it is used, including inside
// the unconditional `postgres` and `environment` resources below, whose *properties* differ by
// usePrivateNetworking but which are themselves deployed either way. Building the ID as a
// string sidesteps that — it is only ever consumed when the condition that guarantees the
// subnet exists is also true.
var appsSubnetId = resourceId('Microsoft.Network/virtualNetworks/subnets', '${name}-vnet', 'apps')
var postgresSubnetId = resourceId('Microsoft.Network/virtualNetworks/subnets', '${name}-vnet', 'postgres')

// ---------------------------------------------------------------------------------- database

resource postgres 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: '${name}-pg-${suffix}'
  location: location
  sku: {
    name: 'Standard_B1ms'
    tier: 'Burstable'
  }
  properties: {
    version: '17'
    administratorLogin: databaseAdministrator
    administratorLoginPassword: databasePassword
    storage: { storageSizeGB: 32 }
    backup: {
      backupRetentionDays: 7
      geoRedundantBackup: 'Disabled'
    }
    highAvailability: { mode: 'Disabled' }
    // Mutually exclusive with the public-access + firewall-rule path below: a server created
    // with a delegated subnet has no public endpoint at all, so there is nothing for a firewall
    // rule to gate.
    network: usePrivateNetworking
      ? {
          delegatedSubnetResourceId: postgresSubnetId
          privateDnsZoneArmResourceId: postgresDns.id
        }
      : null
  }
  // Explicit, not inferred: postgresSubnetId is built with resourceId() rather than a symbolic
  // vnet.properties.subnets[...] reference specifically so this resource can stay unconditional
  // while vnet is conditional (see the comment on appsSubnetId/postgresSubnetId above) — but
  // that sidestep also means Bicep cannot see the dependency and add it automatically, so vnet
  // has to be listed here by hand alongside the DNS zone and its VNet link.
  dependsOn: usePrivateNetworking ? [vnet, postgresDnsLink] : []
}

resource database 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  parent: postgres
  name: databaseName
  properties: {
    charset: 'UTF8'
    collation: 'en_US.utf8'
  }
}

// Container Apps egresses from shared Azure IPs unless a VNet is attached, so by default the
// registry reaches PostgreSQL through this Azure-services rule rather than a firewall entry per
// replica. Set usePrivateNetworking instead for a server with no public endpoint at all — a
// server provisioned that way has no firewall to add a rule to, so this is skipped entirely.
resource allowAzure 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = if (!usePrivateNetworking) {
  parent: postgres
  name: 'AllowAllAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

// ------------------------------------------------------------------------------- key wrapping

resource vault 'Microsoft.KeyVault/vaults@2024-11-01' = {
  name: '${name}-kv-${suffix}'
  location: location
  properties: {
    sku: { family: 'A', name: 'standard' }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enableSoftDelete: true
    // Not optional, and not a default worth overriding: purge protection is what stops a
    // deleted key from being permanently gone, and this key is the only thing that can decrypt
    // every stored broker credential.
    enablePurgeProtection: true
    softDeleteRetentionInDays: 90
  }
}

resource wrappingKey 'Microsoft.KeyVault/vaults/keys@2024-11-01' = {
  parent: vault
  name: 'data-protection'
  properties: {
    kty: 'RSA'
    keySize: 2048
    keyOps: ['wrapKey', 'unwrapKey']
  }
}

// ------------------------------------------------------------------------------- the registry

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${name}-env'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
    // internal stays false either way: this is what lets the reach to PostgreSQL be private
    // without also taking the API itself off the public internet — ingress and the database
    // hop are independent, and only the latter is what usePrivateNetworking is about.
    vnetConfiguration: usePrivateNetworking
      ? {
          infrastructureSubnetId: appsSubnetId
          internal: false
        }
      : null
  }
  // appsSubnetId is a resourceId() string (see the comment above it), which carries no
  // implicit dependency the way a symbolic reference would — vnet has to be named explicitly
  // or this can deploy before the subnet it points at exists.
  dependsOn: usePrivateNetworking ? [vnet] : []
}

resource api 'Microsoft.App/containerApps@2024-03-01' = {
  name: name
  location: location
  identity: { type: 'SystemAssigned' }
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
      }
      secrets: [
        {
          // VerifyFull, not Require: Require encrypts the connection but never checks the
          // server certificate, so an on-path attacker between Container Apps and Postgres
          // could present any certificate and harvest this password. VerifyFull pins the
          // hostname and chain against the CA bundle the aspnet base image already carries.
          //
          // Username is concordat_app, not databaseAdministrator: the running API authenticates
          // as the least-privilege role the migration job provisions (CRUD only, no DDL — see
          // databaseAppPassword above), never as the admin login that can alter the schema.
          name: 'connection-string'
          value: 'Host=${postgres.properties.fullyQualifiedDomainName};Database=${databaseName};Username=${appRoleName};Password=${databaseAppPassword};SSL Mode=VerifyFull'
        }
        {
          name: 'bootstrap-token'
          value: bootstrapToken
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'api'
          // Public on GHCR, so Container Apps pulls it without a registry credential. A
          // private package needs a `registries` block here and a secret to go with it.
          image: image
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: [
            { name: 'ConnectionStrings__Concordat', secretRef: 'connection-string' }
            { name: 'Concordat__Profile', value: profile }
            { name: 'Concordat__KeyProtection__KeyUri', value: wrappingKey.properties.keyUriWithVersion }
            { name: 'Concordat__Authentication__BootstrapToken', secretRef: 'bootstrap-token' }
            { name: 'ASPNETCORE_URLS', value: 'http://+:8080' }
          ]
          probes: [
            {
              type: 'Liveness'
              httpGet: { path: '/health/live', port: 8080 }
              periodSeconds: 30
            }
            {
              type: 'Readiness'
              httpGet: { path: '/health/ready', port: 8080 }
              periodSeconds: 10
            }
          ]
        }
      ]
      scale: {
        // ONE, NOT ZERO, AND THIS IS NOT A CAPACITY DECISION.
        //
        // Container Apps scales to zero by default, and the registry carries an in-process
        // background worker: OutboxPump drains staged notifications on a timer (M7.5). At zero
        // replicas nothing polls, so a breaking change lands, its notification is written to
        // the outbox inside the same transaction — correctly — and is then delivered whenever
        // the next HTTP request happens to wake the app.
        //
        // The failure that produces is the worst kind: alerts stop arriving and nothing reports
        // an error, because from the registry's point of view every message is still pending
        // and will be retried. A quiet weekend looks exactly like a working system.
        //
        // Raising minReplicas to 1 is the cheap fix and costs one always-on replica. The
        // alternative is moving the pump out to a Container Apps *job* on a cron schedule,
        // which lets the API scale to zero and is the right answer if that idle cost ever
        // matters — recorded in decisions-pending rather than pre-emptively built.
        minReplicas: 1
        maxReplicas: 10
        rules: [
          {
            name: 'http'
            http: { metadata: { concurrentRequests: '50' } }
          }
        ]
      }
    }
  }
}

// The managed identity's access to the wrapping key. 'Key Vault Crypto User' grants wrap and
// unwrap and nothing else — the app never needs to read the key material, and Data Protection
// is designed so that it cannot.
var keyVaultCryptoUser = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '12338af0-0e69-4776-bea7-57ae8d297424'
)

resource keyAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: vault
  name: guid(vault.id, api.id, keyVaultCryptoUser)
  properties: {
    roleDefinitionId: keyVaultCryptoUser
    principalId: api.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

@description('The registry base URL.')
output url string = 'https://${api.properties.configuration.ingress.fqdn}'

@description('The PostgreSQL host, for running migrations before the first start.')
output databaseHost string = postgres.properties.fullyQualifiedDomainName
