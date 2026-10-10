// The site's runtime in one environment (ADR-0007, ADR-0008): one container app per region, each in a Container Apps
// express environment of its own, running the image of one release; and, where the environment asks for it, an
// Azure Front Door in front of them all. deploy.ps1 creates the express environments (a template deployment cannot,
// see there) and applies this file as the deployment stack stack-<system>-<env>-web in the tier's resource group.
targetScope = 'resourceGroup'

@description('The system the site belongs to (jpcom).')
param system string

@description('The environment: tdd, uat or prod.')
param environmentName string

@description('The release to run: the image tag, which /_health/ready then reports.')
param version string

@description('Login server of the system\'s registry, which holds <system>/web:<version>.')
param registryServer string

@description('Resource ID of the identity that pulls the image; the system gave it AcrPull.')
param pullIdentityId string

@description('Where the site runs in this environment: { location, code, managedEnvironmentId } per region. The code is short and becomes part of the app\'s name.')
param regions array

@description('True: an Azure Front Door profile in front of every region of the environment.')
param frontDoor bool = false

@description('The port the container listens on (the Dockerfile sets ASPNETCORE_HTTP_PORTS).')
param port int = 8080

@description('Host names of the environment that the site answers with its pages: the canonical host (ADR-0014). None: the Front Door has no custom domain.')
param hostNames array = []

@description('Host names that the site answers with a redirect to the canonical host: www. and feeds. of it.')
param redirectHostNames array = []

@description('True: the Front Door\'s access log is kept in a Log Analytics workspace (ADR-0023). Needs the Front Door.')
param edgeLogs bool = false

@description('How many days the workspace keeps a line of the access log before it deletes it.')
@minValue(30)
@maxValue(730)
param edgeLogsRetentionDays int = 30

@description('How many GB of log the workspace takes in a day. Beyond it the workspace takes nothing more until its day starts again.')
@minValue(1)
param edgeLogsDailyCapGb int = 1

// What the Front Door compresses for a reader whose browser accepts it (ADR-0013): the site's text. Pictures, fonts
// and video are compressed already. An answer is compressed only when it is between 1 KB and 8 MB and came from the
// app with its length; the app sends its pages that way.
var compressedContentTypes = [
  'text/html'
  'text/css'
  'text/plain'
  'application/rss+xml'
  'application/atom+xml'
  'application/xml'
  'application/json'
  'image/svg+xml'
]

// What a route of the Front Door keeps at the edge (ADR-0013). The edge keeps what the app allows it to keep, for as
// long as the app says: the app sends Cache-Control with every answer, and "no-store" with the health, version and
// build answers. No rule here overrides it. deploy.ps1 empties the cache after every deployment.
var routeCache = {
  // The query string is part of the address: /?p=123 is a post, /?s=onion a search, /search/?q=onion&page=2 its
  // second page. Each is kept by itself.
  queryStringCachingBehavior: 'UseQueryString'
  compressionSettings: {
    isCompressionEnabled: true
    contentTypesToCompress: compressedContentTypes
  }
}

// Not the keys "environment" and "deployable": the system's own probe watches container apps that carry those.
var tags = {
  system: system
  application: 'web'
  stage: environmentName
}

// Standard tier: a base fee per profile and month, whatever the number of regions behind it.
resource profile 'Microsoft.Cdn/profiles@2024-02-01' = if (frontDoor) {
  name: 'afd-${system}-${environmentName}'
  location: 'global'
  tags: tags
  sku: {
    name: 'Standard_AzureFrontDoor'
  }
  properties: {
    // A region that scaled to zero starts when its first request arrives: give it time before Front Door gives up.
    originResponseTimeoutSeconds: 120
  }
}

resource apps 'Microsoft.App/containerApps@2025-01-01' = [
  for region in regions: {
    name: 'ca-${system}-${environmentName}-web-${region.code}'
    location: region.location
    tags: union(tags, { region: region.code })
    identity: {
      type: 'UserAssigned'
      userAssignedIdentities: {
        '${pullIdentityId}': {}
      }
    }
    properties: {
      environmentId: region.managedEnvironmentId
      configuration: {
        activeRevisionsMode: 'Single'
        ingress: {
          external: true
          targetPort: port
          // An express environment has no HTTP/2.
          transport: 'http'
          allowInsecure: false
        }
        // An express environment keeps no registry on the app: it must come with every request that names the image.
        registries: [
          {
            server: registryServer
            identity: pullIdentityId
          }
        ]
      }
      template: {
        containers: [
          {
            name: 'web'
            image: '${registryServer}/${system}/web:${version}'
            resources: {
              cpu: json('0.5')
              memory: '1Gi'
            }
            // Behind Front Door the app believes the forwarded host only from this profile (FrontDoorHostMiddleware).
            env: frontDoor
              ? [
                  {
                    name: 'Site__FrontDoorId'
                    value: profile!.properties.frontDoorId
                  }
                ]
              : []
          }
        ]
        // Scale to zero when idle; one replica per region is enough for the read-only site.
        scale: {
          minReplicas: 0
          maxReplicas: 1
        }
      }
    }
  }
]

resource endpoint 'Microsoft.Cdn/profiles/afdEndpoints@2024-02-01' = if (frontDoor) {
  parent: profile
  name: '${system}-${environmentName}'
  location: 'global'
  tags: tags
  properties: {
    enabledState: 'Enabled'
  }
}

// Round robin over every region, and no health probes (ADR-0008).
// - No health probe settings: Front Door sends no probes, so a region with no visitors scales to zero. It also means
//   Front Door cannot take a failed region out by itself.
// - Equal weights, and the widest latency tolerance: without probes Front Door has no latency to prefer a region by,
//   and every region is in the rotation wherever the visitor is.
resource originGroup 'Microsoft.Cdn/profiles/originGroups@2024-02-01' = if (frontDoor) {
  parent: profile
  name: 'web'
  properties: {
    loadBalancingSettings: {
      sampleSize: 4
      successfulSamplesRequired: 3
      additionalLatencyInMilliseconds: 1000
    }
    sessionAffinityState: 'Disabled'
  }
}

resource origins 'Microsoft.Cdn/profiles/originGroups/origins@2024-02-01' = [
  for (region, i) in regions: if (frontDoor) {
    parent: originGroup
    name: region.code
    properties: {
      hostName: apps[i].properties.configuration.ingress.fqdn
      // The container app answers to its own name only; the visitor's host travels in X-Forwarded-Host.
      originHostHeader: apps[i].properties.configuration.ingress.fqdn
      httpPort: 80
      httpsPort: 443
      priority: 1
      weight: 1000
      enabledState: 'Enabled'
      enforceCertificateNameCheck: true
    }
  }
]

resource route 'Microsoft.Cdn/profiles/afdEndpoints/routes@2024-02-01' = if (frontDoor) {
  parent: endpoint
  name: 'web'
  dependsOn: [
    origins
  ]
  properties: {
    originGroup: {
      id: originGroup.id
    }
    supportedProtocols: [
      'Http'
      'Https'
    ]
    patternsToMatch: [
      '/*'
    ]
    forwardingProtocol: 'HttpsOnly'
    httpsRedirect: 'Enabled'
    linkToDefaultDomain: 'Enabled'
    enabledState: 'Enabled'
    cacheConfiguration: routeCache
  }
}

// The access log of the Front Door (ADR-0023): what a reader got at the edge, one line per request. A workspace of the
// environment's own, and the one diagnostic setting that sends the log there. Only the access log: the Front Door
// sends no probes (ADR-0008) and has no firewall, so those two logs would stay empty, and no metric is exported.
// Deployed only when the environment's settings ask for it. Switched off, both conditions are false and this
// template deploys what it deployed without them (EdgeLogsTemplateTests proves it on the compiled template); the
// stack then deletes the workspace, and with it the lines it holds.
var edgeLog = frontDoor && edgeLogs

resource edgeLogWorkspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = if (edgeLog) {
  name: 'log-${system}-${environmentName}-edge'
  // Where the lines are stored: the region of the tier's resource group.
  location: resourceGroup().location
  tags: tags
  properties: {
    // Paid by the GB taken in; no commitment.
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: edgeLogsRetentionDays
    // A flood of requests must not become a bill: beyond this the workspace takes nothing more that day.
    workspaceCapping: {
      dailyQuotaGb: edgeLogsDailyCapGb
    }
    // Nobody reads or writes with the workspace's keys: a reader signs in to Azure, and the Front Door writes as
    // the platform. No key leaves this template.
    features: {
      disableLocalAuth: true
    }
  }
}

resource edgeLogSetting 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (edgeLog) {
  scope: profile
  name: 'access-log'
  properties: {
    workspaceId: edgeLogWorkspace.id
    logs: [
      {
        category: 'FrontDoorAccessLog'
        enabled: true
      }
    ]
  }
}

// The custom domain (ADR-0014): everything a host name needs is in custom-domains.bicep, and is deployed only when
// the environment's settings list a host name. With none, the condition is false, nothing of that file exists, and
// this template deploys what it deployed without it (CustomDomainTemplateTests proves it on the compiled template).
// A custom domain needs the Front Door; deploy.ps1 refuses host names for an environment without one.
var customDomain = frontDoor && !empty(concat(hostNames, redirectHostNames))

module customDomains 'custom-domains.bicep' = if (customDomain) {
  // Environments of one tier share a resource group: the deployment's name is the environment's own.
  name: 'custom-domains-${environmentName}'
  dependsOn: [
    origins
    route
  ]
  params: {
    profileName: profile!.name
    endpointName: endpoint!.name
    originGroupName: originGroup!.name
    hostNames: hostNames
    redirectHostNames: redirectHostNames
    routeCache: routeCache
  }
}

output regions array = [
  for (region, i) in regions: {
    code: region.code
    location: region.location
    app: apps[i].name
    url: 'https://${apps[i].properties.configuration.ingress.fqdn}'
  }
]
output frontDoorUrl string = frontDoor ? 'https://${endpoint!.properties.hostName}' : ''
// What deploy.ps1 empties after a deployment: the cache of this endpoint.
output frontDoorEndpointId string = frontDoor ? endpoint!.id : ''
// What DNS needs for every host name, which deploy.ps1 prints: the TXT record that proves the name is the owner's
// (while the validation has not passed), and where the name's address record points. No host name: an empty list.
output hostNames array = customDomain ? customDomains!.outputs.hostNames : []
// Where the access log of the Front Door is (ADR-0023), which deploy.ps1 prints: the workspace's resource ID. Not its
// key. No access log: an empty text.
output edgeLogWorkspaceId string = edgeLog ? edgeLogWorkspace.id : ''
