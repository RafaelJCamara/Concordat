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

@description('The registry image to run, including its tag.')
param image string

@description('The PostgreSQL administrator login.')
param databaseAdministrator string = 'concordat'

@description('The PostgreSQL administrator password.')
@secure()
param databasePassword string

@description('SelfHosted or Cloud. Cloud refuses to start without a Key Vault key.')
@allowed(['SelfHosted', 'Cloud'])
param profile string = 'Cloud'

var databaseName = 'concordat'
var suffix = uniqueString(resourceGroup().id)

// ---------------------------------------------------------------------------- observability

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${name}-logs'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

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
  }
}

resource database 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  parent: postgres
  name: databaseName
  properties: {
    charset: 'UTF8'
    collation: 'en_US.utf8'
  }
}

// Container Apps egresses from shared Azure IPs unless a VNet is attached, so the registry
// reaches PostgreSQL through the Azure-services rule rather than a firewall entry per replica.
// A VNet-integrated environment is the right answer for production and is deliberately not the
// default here: it triples the resources somebody has to understand on a first deployment.
resource allowAzure 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = {
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
  }
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
          name: 'connection-string'
          value: 'Host=${postgres.properties.fullyQualifiedDomainName};Database=${databaseName};Username=${databaseAdministrator};Password=${databasePassword};SSL Mode=Require'
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'api'
          image: image
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: [
            { name: 'ConnectionStrings__Concordat', secretRef: 'connection-string' }
            { name: 'Concordat__Profile', value: profile }
            { name: 'Concordat__KeyProtection__KeyUri', value: wrappingKey.properties.keyUriWithVersion }
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
