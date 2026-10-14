targetScope = 'resourceGroup'

@description('Short name used in resource names. Lowercase letters and numbers only.')
param namePrefix string = 'opsflowdev'

@description('Azure region.')
param location string = resourceGroup().location

@description('Container image tag to deploy. The workflow supplies this; it is not latest by default.')
param imageTag string = 'dev'

@description('SQL admin login used only to bootstrap Azure AD users. Do not reuse it as the application login.')
param sqlAdminLogin string

@secure()
param sqlAdminPassword string

@description('Client IP addresses allowed through the SQL firewall. Empty means no client access. Do not pass 0.0.0.0.')
param allowedSqlIps array = []

@description('When true, Azure services can reach SQL. Leave false until you add the Container Apps outbound IPs or private networking.')
param allowAzureServices bool = false

param sqlSkuName string = 'GP_S_Gen5'
param sqlSkuTier string = 'GeneralPurpose'
param sqlCapacity int = 1
param sqlAutoPauseMinutes int = 60
param sqlMaxSizeBytes int = 2147483648

var acrName = '${namePrefix}acr'
var sqlServerName = '${namePrefix}-sql'
var sqlDatabaseName = '${namePrefix}-db'
var storageName = '${namePrefix}stor'
var logName = '${namePrefix}-logs'
var envName = '${namePrefix}-env'
var apiName = '${namePrefix}-api'
var workerName = '${namePrefix}-worker'

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logName
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${namePrefix}-appi'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
  }
}

resource acr 'Microsoft.ContainerRegistry/registries@2023-11-01-preview' = {
  name: acrName
  location: location
  sku: { name: 'Basic' }
  properties: { adminUserEnabled: false }
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    publicNetworkAccess: 'Enabled'
  }
}

resource attachments 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  name: '${storage.name}/default/attachments'
  properties: { publicAccess: 'None' }
}

resource keys 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  name: '${storage.name}/default/dataprotection'
  properties: { publicAccess: 'None' }
}

resource sql 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: sqlServerName
  location: location
  properties: {
    administratorLogin: sqlAdminLogin
    administratorLoginPassword: sqlAdminPassword
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
  }
}

resource database 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sql
  name: sqlDatabaseName
  location: location
  sku: {
    name: sqlSkuName
    tier: sqlSkuTier
    capacity: sqlCapacity
  }
  properties: {
    maxSizeBytes: sqlMaxSizeBytes
    autoPauseDelay: sqlAutoPauseMinutes
    minCapacity: json('0.5')
  }
}

resource azureServicesRule 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = if (allowAzureServices) {
  parent: sql
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource clientRules 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = [for ip in allowedSqlIps: {
  parent: sql
  name: 'client-${replace(ip, '.', '-')}'
  properties: {
    startIpAddress: ip
    endIpAddress: ip
  }
}]

resource apiIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${namePrefix}-api-id'
  location: location
}

resource workerIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${namePrefix}-worker-id'
  location: location
}

resource migratorIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${namePrefix}-migrator-id'
  location: location
}

resource acrPullApi 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acr.id, apiIdentity.id, 'acrpull')
  scope: acr
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalId: apiIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource acrPullMigrator 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acr.id, migratorIdentity.id, 'acrpull')
  scope: acr
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalId: migratorIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource acrPullWorker 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acr.id, workerIdentity.id, 'acrpull')
  scope: acr
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalId: workerIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource blobDataApi 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, apiIdentity.id, 'blob')
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
    principalId: apiIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

var sqlConnection = 'Server=tcp:${sql.properties.fullyQualifiedDomainName},1433;Initial Catalog=${sqlDatabaseName};Encrypt=True;TrustServerCertificate=False;Authentication=Active Directory Default;'

resource containerAppsEnv 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: envName
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
  name: apiName
  location: location
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${apiIdentity.id}': {} } }
  properties: {
    environmentId: containerAppsEnv.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: { external: true, targetPort: 8080, transport: 'http' }
      registries: [{ server: acr.properties.loginServer, identity: apiIdentity.id }]
    }
    template: {
      containers: [
        {
          name: 'api'
          image: '${acr.properties.loginServer}/opsflow-api:${imageTag}'
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'ConnectionStrings__OpsFlow', value: sqlConnection }
            { name: 'Attachments__Provider', value: 'AzureBlob' }
            { name: 'Attachments__BlobContainerUri', value: 'https://${storage.name}.blob.${environment().suffixes.storage}/attachments' }
            { name: 'DataProtection__BlobUri', value: 'https://${storage.name}.blob.${environment().suffixes.storage}/dataprotection/keys.xml' }
            { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: insights.properties.ConnectionString }
          ]
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 2 }
    }
  }
  dependsOn: [acrPullApi, blobDataApi, database]
}

resource worker 'Microsoft.App/containerApps@2024-03-01' = {
  name: workerName
  location: location
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${workerIdentity.id}': {} } }
  properties: {
    environmentId: containerAppsEnv.id
    configuration: {
      activeRevisionsMode: 'Single'
      registries: [{ server: acr.properties.loginServer, identity: workerIdentity.id }]
    }
    template: {
      containers: [
        {
          name: 'worker'
          image: '${acr.properties.loginServer}/opsflow-worker:${imageTag}'
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: [
            { name: 'ConnectionStrings__OpsFlow', value: sqlConnection }
            { name: 'Smtp__Host', value: 'replace-with-internal-smtp' }
            { name: 'Smtp__Port', value: '25' }
            { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: insights.properties.ConnectionString }
          ]
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 2 }
    }
  }
  dependsOn: [acrPullWorker, database]
}

resource migrationJob 'Microsoft.App/jobs@2024-03-01' = {
  name: '${namePrefix}-migrate'
  location: location
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${migratorIdentity.id}': {} } }
  properties: {
    environmentId: containerAppsEnv.id
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 1800
      replicaRetryLimit: 0
      registries: [{ server: acr.properties.loginServer, identity: migratorIdentity.id }]
    }
    template: {
      containers: [
        {
          name: 'migrate'
          image: '${acr.properties.loginServer}/opsflow-api:${imageTag}'
          command: ['dotnet', 'OpsFlow.Api.dll', '--migrate']
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'ConnectionStrings__OpsFlow', value: sqlConnection }
          ]
        }
      ]
    }
  }
  dependsOn: [database, acrPullMigrator]
}

output apiFqdn string = api.properties.configuration.ingress.fqdn
output acrLoginServer string = acr.properties.loginServer
output sqlServer string = sql.properties.fullyQualifiedDomainName
output sqlDatabase string = sqlDatabaseName
output apiPrincipalId string = apiIdentity.properties.principalId
output workerPrincipalId string = workerIdentity.properties.principalId
output migratorPrincipalId string = migratorIdentity.properties.principalId
output storageAccount string = storage.name
