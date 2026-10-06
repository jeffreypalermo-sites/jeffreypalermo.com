// The site's runtime in one environment (ADR-0007): one container app in the system's Container Apps environment,
// running the image of one release. deploy.ps1 applies this file as the deployment stack stack-<system>-<env>-web
// in the tier's resource group; the system repository creates nothing of it.
targetScope = 'resourceGroup'

@description('The system the site belongs to (jpcom).')
param system string

@description('The environment: tdd, uat or prod.')
param environmentName string

@description('The release to run: the image tag, which /_health/ready then reports.')
param version string

@description('Login server of the system\'s registry, which holds <system>/web:<version>.')
param registryServer string

@description('Resource ID of the Container Apps environment the app runs in.')
param managedEnvironmentId string

@description('Resource ID of the identity that pulls the image; the system gave it AcrPull.')
param pullIdentityId string

@description('The port the container listens on (the Dockerfile sets ASPNETCORE_HTTP_PORTS).')
param port int = 8080

param location string = resourceGroup().location

resource app 'Microsoft.App/containerApps@2025-01-01' = {
  name: 'ca-${system}-${environmentName}-web'
  location: location
  // Not the keys "environment" and "deployable": the system's own probe watches container apps that carry those.
  tags: {
    system: system
    application: 'web'
    stage: environmentName
  }
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${pullIdentityId}': {}
    }
  }
  properties: {
    environmentId: managedEnvironmentId
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
        }
      ]
      // Scale to zero when idle (ADR-0006); one replica is enough for the read-only site.
      scale: {
        minReplicas: 0
        maxReplicas: 1
      }
    }
  }
}

output name string = app.name
output url string = 'https://${app.properties.configuration.ingress.fqdn}'
