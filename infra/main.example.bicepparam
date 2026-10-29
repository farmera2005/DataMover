// Example parameters: copy, adjust, and deploy with
//   az deployment group create -g rg-lakeflow -f infra/main.bicep -p infra/main.example.bicepparam
using 'main.bicep'

param namespaceName = 'mercer-lakeflow'
param capacity = 1
param autoInflateMaximum = 4
param partitionCount = 4
param retentionDays = 7
// The Lakeflow server's outbound public address, if the namespace should accept only it (see infra/README.md).
param allowedIpRanges = []
