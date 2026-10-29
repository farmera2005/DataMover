// Optional: a Microsoft Fabric capacity (F SKU) for the Lakeflow workspace, if you do not already have one.
// A capacity bills for every hour it runs, whether used or not; pause it in the Azure portal when it is not needed.
//
//   az deployment group create -g rg-lakeflow -f infra/fabric-capacity.bicep \
//     -p capacityName=mercerlakeflow administrators='["adam@mercerlandmark.com"]'

targetScope = 'resourceGroup'

@description('Capacity name: 3-63 lower-case letters and digits, unique in the region, e.g. mercerlakeflow.')
@minLength(3)
@maxLength(63)
param capacityName string

@description('Azure region. Use the region of your Fabric home tenant if you can (Fabric admin portal, About Fabric).')
param location string = resourceGroup().location

@description('Size. F4 is a reasonable start for the 5-minute notebook and a few Eventstreams; watch the Fabric Capacity Metrics app and resize.')
@allowed([
  'F2'
  'F4'
  'F8'
  'F16'
  'F32'
  'F64'
])
param sku string = 'F4'

@description('Capacity administrators: user principal names (e.g. adam@mercerlandmark.com).')
@minLength(1)
param administrators array

param tags object = {
  application: 'Lakeflow'
}

resource capacity 'Microsoft.Fabric/capacities@2023-11-01' = {
  name: capacityName
  location: location
  tags: tags
  sku: {
    name: sku
    tier: 'Fabric'
  }
  properties: {
    administration: {
      members: administrators
    }
  }
}

output capacityName string = capacity.name
