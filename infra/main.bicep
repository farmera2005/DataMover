// Lakeflow's Azure side: the Event Hubs namespace collectors send changes to, with one hub per division.
// Everything else (control API, console, database, collector) runs in the Docker stack on-prem; see docs/decisions.md.
//
//   az group create -n rg-lakeflow -l eastus2
//   az deployment group create -g rg-lakeflow -f infra/main.bicep -p namespaceName=mercer-lakeflow
//
// Then, for each division, paste its hub's "lakeflow-send" connection string into the console
// (Configuration, Destinations). See infra/README.md.

targetScope = 'resourceGroup'

@description('Event Hubs namespace name, unique across Azure (6-50 letters, digits and hyphens), e.g. mercer-lakeflow.')
@minLength(6)
@maxLength(50)
param namespaceName string

@description('Azure region.')
param location string = resourceGroup().location

@description('Standard suits the expected volume (21-40 sites); Premium for isolation or retention beyond 7 days.')
@allowed([
  'Standard'
  'Premium'
])
param sku string = 'Standard'

@description('Throughput units (Standard: 1 MB/s in each; Premium: processing units).')
@minValue(1)
@maxValue(16)
param capacity int = 1

@description('Standard only: let Event Hubs add throughput units under load, up to this many (0 turns it off).')
@minValue(0)
@maxValue(20)
param autoInflateMaximum int = 4

@description('One hub per division; names must match the divisions in the console (grain, feed, agronomy, erp, other).')
param divisions array = [
  'grain'
  'feed'
  'agronomy'
  'erp'
  'other'
]

@description('Partitions per hub. Changes are keyed by site and table, so a table\'s changes stay in order.')
@minValue(1)
@maxValue(32)
param partitionCount int = 4

@description('Days a change stays in the hub (Standard: at most 7). Fabric must read it within this time.')
@minValue(1)
@maxValue(90)
param retentionDays int = 7

@description('Optional: public addresses or CIDR ranges allowed to reach the namespace (your Lakeflow server\'s outbound address). Empty allows any address; SAS tokens are required either way. Fabric Eventstream also has to reach the namespace: with a list here, give Fabric a way in (see infra/README.md).')
param allowedIpRanges array = []

@description('Consumer group Fabric Eventstream reads with.')
param fabricConsumerGroup string = 'fabric'

@description('Also create, on each hub, the snowflake consumer group and a snowflake-listen (Listen only) policy for Lakeflow\'s Snowflake forwarder (Configuration, then Snowflake).')
param snowflake bool = false

param tags object = {
  application: 'Lakeflow'
}

var standard = sku == 'Standard'

resource namespace 'Microsoft.EventHub/namespaces@2024-01-01' = {
  name: namespaceName
  location: location
  tags: tags
  sku: {
    name: sku
    tier: sku
    capacity: capacity
  }
  properties: {
    minimumTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    // Collectors send with short-lived SAS tokens the control API signs with each hub's Send-only key.
    disableLocalAuth: false
    isAutoInflateEnabled: standard && autoInflateMaximum > 0
    maximumThroughputUnits: standard && autoInflateMaximum > 0 ? max(autoInflateMaximum, capacity) : 0
  }
}

resource network 'Microsoft.EventHub/namespaces/networkRuleSets@2024-01-01' = if (!empty(allowedIpRanges)) {
  parent: namespace
  name: 'default'
  properties: {
    defaultAction: 'Deny'
    publicNetworkAccess: 'Enabled'
    trustedServiceAccessEnabled: true
    ipRules: [for range in allowedIpRanges: {
      ipMask: range
      action: 'Allow'
    }]
    virtualNetworkRules: []
  }
}

resource hubs 'Microsoft.EventHub/namespaces/eventhubs@2024-01-01' = [for division in divisions: {
  parent: namespace
  name: division
  properties: {
    partitionCount: partitionCount
    messageRetentionInDays: standard ? min(retentionDays, 7) : retentionDays
  }
}]

// Per hub, never on the namespace: a key can only send to its own division's hub.
resource sendPolicies 'Microsoft.EventHub/namespaces/eventhubs/authorizationRules@2024-01-01' = [for (division, i) in divisions: {
  parent: hubs[i]
  name: 'lakeflow-send'
  properties: {
    rights: [
      'Send'
    ]
  }
}]

resource listenPolicies 'Microsoft.EventHub/namespaces/eventhubs/authorizationRules@2024-01-01' = [for (division, i) in divisions: {
  parent: hubs[i]
  name: 'fabric-listen'
  properties: {
    rights: [
      'Listen'
    ]
  }
}]

resource consumerGroups 'Microsoft.EventHub/namespaces/eventhubs/consumergroups@2024-01-01' = [for (division, i) in divisions: {
  parent: hubs[i]
  name: fabricConsumerGroup
  properties: {}
}]

resource snowflakeListenPolicies 'Microsoft.EventHub/namespaces/eventhubs/authorizationRules@2024-01-01' = [for (division, i) in divisions: if (snowflake) {
  parent: hubs[i]
  name: 'snowflake-listen'
  properties: {
    rights: [
      'Listen'
    ]
  }
}]

resource snowflakeConsumerGroups 'Microsoft.EventHub/namespaces/eventhubs/consumergroups@2024-01-01' = [for (division, i) in divisions: if (snowflake) {
  parent: hubs[i]
  name: 'snowflake'
  properties: {}
}]

@description('Fully qualified namespace, as the console shows it for each hub.')
output namespaceHost string = '${namespace.name}.servicebus.windows.net'

@description('Per division: the hub, and the command that prints the connection string to paste into the console.')
output hubs array = [for (division, i) in divisions: {
  division: division
  hub: hubs[i].name
  sendPolicy: sendPolicies[i].name
  listenPolicy: listenPolicies[i].name
  consumerGroup: consumerGroups[i].name
  sendConnectionStringCommand: 'az eventhubs eventhub authorization-rule keys list -g ${resourceGroup().name} --namespace-name ${namespace.name} --eventhub-name ${division} -n lakeflow-send --query primaryConnectionString -o tsv'
  snowflakeListenConnectionStringCommand: snowflake ? 'az eventhubs eventhub authorization-rule keys list -g ${resourceGroup().name} --namespace-name ${namespace.name} --eventhub-name ${division} -n snowflake-listen --query primaryConnectionString -o tsv' : ''
}]
