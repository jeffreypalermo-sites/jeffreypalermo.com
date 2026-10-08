// The DNS zone of jeffreypalermo.com (ADR-0016), as an Azure DNS zone in the tier's resource group. deploy.ps1 applies
// this file as a deployment stack of its own, stack-<system>-<env>-dns, apart from the site's stack: that stack
// deletes what leaves its template, and a zone that holds the mail records must not go because a line was removed.
// The stack of this file detaches what leaves it and lets nobody but the deploy identity delete what it holds.
//
// The zone holds every record that was read from public DNS on 2026-10-08 and must survive
// (tests/contract/dns-inventory.tsv; DnsZoneTemplateTests holds the two equal), and what the Front Door needs for each
// of the environment's host names. Creating it changes nothing for anyone: the registrar names other name servers
// until a person enters this zone's there (docs/runbooks/dns-cutover.md).
targetScope = 'resourceGroup'

@description('The zone: jeffreypalermo.com. The records below are that domain\'s.')
param zoneName string

@description('The system the site belongs to (jpcom).')
param system string

@description('The environment whose settings name the zone: prod.')
param environmentName string

@description('The environment\'s host names in this zone, each as its name in the zone: "@" for the zone\'s own name, else the part before it ("www", "feeds"). Each is answered by the Front Door. None: every name is answered as it was on 2026-10-08.')
param hostLabels array = []

@description('Resource ID of the Front Door endpoint: what the zone\'s own name is an alias of, when it is a host name.')
param frontDoorEndpointId string = ''

@description('The endpoint\'s azurefd.net name: what the other host names are a CNAME of.')
param frontDoorHostName string = ''

@description('The host names the Front Door has given a validation token for, as their names in the zone, and the tokens in the same order. The zone gives each back as the TXT record _dnsauth.<name>, so nobody copies a token by hand.')
param validationLabels array = []
param validationTokens array = []

// How long a resolver may keep an answer.
// - One hour for mail: these records do not change with the move, and the old zone gave them one hour.
// - Five minutes for what the move changes, and for the tokens, which change when a certificate is renewed: going
//   back, or a new token, is everywhere within five minutes.
var mailTtl = 3600
var siteTtl = 300

var tags = {
  system: system
  application: 'web'
  stage: environmentName
}

resource zone 'Microsoft.Network/dnsZones@2018-05-01' = {
  name: zoneName
  location: 'global'
  tags: tags
  properties: {
    zoneType: 'Public'
  }
}

// ---- Mail and what proves mail: exactly as read. A name in a record's value is a full name here, as everywhere in
// ---- Azure DNS, with or without a dot at its end; they are written without.

// Mail for the domain goes to GoDaddy.
resource mail 'Microsoft.Network/dnsZones/MX@2018-05-01' = {
  parent: zone
  name: '@'
  properties: {
    TTL: mailTtl
    MXRecords: [
      {
        preference: 0
        exchange: 'smtp.secureserver.net'
      }
      {
        preference: 10
        exchange: 'mailstore1.secureserver.net'
      }
    ]
  }
}

// SPF, as it is: it names WordPress.com's mail servers only. Changing it is a decision of its own.
resource spf 'Microsoft.Network/dnsZones/TXT@2018-05-01' = {
  parent: zone
  name: '@'
  properties: {
    TTL: mailTtl
    TXTRecords: [
      {
        value: [
          'v=spf1 include:_spf.wpcloud.com ~all'
        ]
      }
    ]
  }
}

resource dmarc 'Microsoft.Network/dnsZones/TXT@2018-05-01' = {
  parent: zone
  name: '_dmarc'
  properties: {
    TTL: mailTtl
    TXTRecords: [
      {
        value: [
          'v=DMARC1;p=none;'
        ]
      }
    ]
  }
}

// DKIM of the mail WordPress.com sends for the site. They stay while the WordPress.com site exists.
resource dkim1 'Microsoft.Network/dnsZones/CNAME@2018-05-01' = {
  parent: zone
  name: 'wpcloud1._domainkey'
  properties: {
    TTL: mailTtl
    CNAMERecord: {
      cname: 'wpcloud1._domainkey.wpcloud.com'
    }
  }
}

resource dkim2 'Microsoft.Network/dnsZones/CNAME@2018-05-01' = {
  parent: zone
  name: 'wpcloud2._domainkey'
  properties: {
    TTL: mailTtl
    CNAMERecord: {
      cname: 'wpcloud2._domainkey.wpcloud.com'
    }
  }
}

// ---- The three names of the site. Each is answered as on 2026-10-08 until it is one of the environment's host
// ---- names; from then on it is the site's own.

// The zone's own name cannot be a CNAME, and the Front Door gives no address: an alias of the endpoint.
resource top 'Microsoft.Network/dnsZones/A@2018-05-01' = {
  parent: zone
  name: '@'
  properties: contains(hostLabels, '@')
    ? {
        TTL: siteTtl
        targetResource: {
          id: frontDoorEndpointId
        }
      }
    : {
        TTL: siteTtl
        // WordPress.com.
        ARecords: [
          {
            ipv4Address: '192.0.78.168'
          }
          {
            ipv4Address: '192.0.78.213'
          }
        ]
      }
}

resource wwwAsItWas 'Microsoft.Network/dnsZones/CNAME@2018-05-01' = if (!contains(hostLabels, 'www')) {
  parent: zone
  name: 'www'
  properties: {
    TTL: siteTtl
    CNAMERecord: {
      cname: zoneName
    }
  }
}

// FeedBurner's old proxy, which answers 404 today. Kept as it was until feeds. is a host name of the site.
resource feedsAsItWas 'Microsoft.Network/dnsZones/CNAME@2018-05-01' = if (!contains(hostLabels, 'feeds')) {
  parent: zone
  name: 'feeds'
  properties: {
    TTL: siteTtl
    CNAMERecord: {
      cname: '1i4ygfi.feedproxy.ghs.google.com'
    }
  }
}

// Every host name but the zone's own: a CNAME of the endpoint. Front Door renews such a name's certificate by itself.
resource hostNames 'Microsoft.Network/dnsZones/CNAME@2018-05-01' = [
  for label in hostLabels: if (label != '@') {
    parent: zone
    name: label
    properties: {
      TTL: siteTtl
      CNAMERecord: {
        cname: frontDoorHostName
      }
    }
  }
]

// What proves each host name to the Front Door: the token it gave, as it gave it.
resource validations 'Microsoft.Network/dnsZones/TXT@2018-05-01' = [
  for (label, i) in validationLabels: {
    parent: zone
    name: label == '@' ? '_dnsauth' : '_dnsauth.${label}'
    properties: {
      TTL: siteTtl
      TXTRecords: [
        {
          value: [
            validationTokens[i]
          ]
        }
      ]
    }
  }
]

// Not in the zone, on purpose (ADR-0016): the wildcard that answered every other name, and WordPress.com's own
// _domainconnect record. And no record for the name servers: Azure DNS writes the zone's own.

// Entering these four at the registrar is the move.
output nameServers array = zone.properties.nameServers
output zoneId string = zone.id
