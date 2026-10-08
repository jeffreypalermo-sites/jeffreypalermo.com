// The custom domain of an environment's Front Door (ADR-0014): for every host name a custom domain with a certificate
// the Front Door manages, and the routes that answer for the names. main.bicep deploys this file only when the
// environment's settings list a host name; docs/runbooks/dns-cutover.md is the day that happens for production.
targetScope = 'resourceGroup'

@description('Name of the environment\'s Front Door profile.')
param profileName string

@description('Name of the profile\'s endpoint.')
param endpointName string

@description('Name of the origin group the routes send to: every region of the environment, in rotation.')
param originGroupName string

@description('Host names that the site answers with its pages: the canonical host.')
param hostNames array

@description('Host names that the site answers with a redirect to the canonical host: www. and feeds. of it.')
param redirectHostNames array

@description('What the route of the names with pages keeps at the edge: what the endpoint\'s own route keeps (ADR-0013).')
param routeCache object

// The names with pages first: the routes below find a name's domain by its place in this list.
var allHostNames = concat(hostNames, redirectHostNames)

resource profile 'Microsoft.Cdn/profiles@2024-02-01' existing = {
  name: profileName
}

resource endpoint 'Microsoft.Cdn/profiles/afdEndpoints@2024-02-01' existing = {
  parent: profile
  name: endpointName
}

resource originGroup 'Microsoft.Cdn/profiles/originGroups@2024-02-01' existing = {
  parent: profile
  name: originGroupName
}

// One custom domain per host name, each with a certificate the Front Door issues and renews. A domain serves only
// after its owner proved it: a TXT record _dnsauth.<host name> with the token the outputs give.
resource customDomains 'Microsoft.Cdn/profiles/customDomains@2024-02-01' = [
  for host in allHostNames: {
    parent: profile
    name: replace(host, '.', '-')
    properties: {
      hostName: host
      tlsSettings: {
        certificateType: 'ManagedCertificate'
        minimumTlsVersion: 'TLS12'
      }
    }
  }
]

// The names the site answers with pages get a route like the endpoint's own (the route "web" of main.bicep, which
// stays as it is for the endpoint's own name): the same regions, the same cache.
resource pageHostsRoute 'Microsoft.Cdn/profiles/afdEndpoints/routes@2024-02-01' = if (!empty(hostNames)) {
  parent: endpoint
  name: 'web-hosts'
  properties: {
    customDomains: [
      for (host, i) in hostNames: {
        id: customDomains[i].id
      }
    ]
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
    linkToDefaultDomain: 'Disabled'
    enabledState: 'Enabled'
    cacheConfiguration: routeCache
  }
}

// www. and feeds. are answered with a redirect where the canonical host is answered with the page. Their route has
// no cache at all: the edge can keep nothing for them, and can give them nothing it keeps for another name. This
// does not rest on what the key of the Front Door's cache is made of, which its documentation does not say.
resource redirectHostsRoute 'Microsoft.Cdn/profiles/afdEndpoints/routes@2024-02-01' = if (!empty(redirectHostNames)) {
  parent: endpoint
  name: 'web-redirects'
  properties: {
    customDomains: [
      for (host, i) in redirectHostNames: {
        id: customDomains[length(hostNames) + i].id
      }
    ]
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
    linkToDefaultDomain: 'Disabled'
    enabledState: 'Enabled'
  }
}

// What DNS needs for every host name: the TXT record that proves the name is the owner's, and where the name's
// address record points.
output hostNames array = [
  for (host, i) in allHostNames: {
    hostName: host
    kept: i < length(hostNames)
    validationState: customDomains[i].properties.domainValidationState
    validationRecord: '_dnsauth.${host}'
    validationToken: customDomains[i].properties.?validationProperties.?validationToken ?? ''
    validationExpires: customDomains[i].properties.?validationProperties.?expirationDate ?? ''
    target: endpoint.properties.hostName
  }
]
