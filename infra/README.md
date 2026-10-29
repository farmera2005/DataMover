# Azure: Event Hubs

Lakeflow's only Azure resources besides Fabric: one Event Hubs namespace with a hub per division. The control API,
console, database and collector run in the Docker stack on-prem (see the main README).

## What `main.bicep` creates

| Resource | Settings |
| --- | --- |
| Event Hubs namespace | Standard, 1 throughput unit, auto-inflate up to 4, TLS 1.2 minimum |
| Hubs `grain`, `feed`, `agronomy`, `erp`, `other` | 4 partitions, 7 days retention (changes are keyed by site and table, so each table stays in order) |
| Per hub: policy `lakeflow-send` | **Send** only. Its key stays in the control API, which hands collectors 24-hour SAS tokens signed with it |
| Per hub: policy `fabric-listen` | **Listen** only, for Fabric Eventstream |
| Per hub: consumer group `fabric` | what Eventstream reads with |
| Optional IP firewall | only the addresses in `allowedIpRanges` (your Lakeflow server's outbound address) |

There is no namespace-wide key: a key can send to its own division's hub only (CI checks the compiled template for this).

## Deploy

In the Azure portal, open **Cloud Shell** (Bash) and upload `main.bicep` (or clone the repository), then:

```bash
az group create -n rg-lakeflow -l eastus2
```

```bash
az deployment group create -g rg-lakeflow -f main.bicep -p namespaceName=mercer-lakeflow
```

Without a command line: each CI run publishes the compiled template as the `azuredeploy` artifact; in the portal choose
**Deploy a custom template → Build your own template in the editor → Load file**, pick `azuredeploy.json`, and fill in
the namespace name.

Sizing: one throughput unit takes 1 MB/s or 1,000 events/s in; auto-inflate adds more under load (a resnapshot of a
big table, or catch-up after an outage). Raise `capacity` once the per-site volumes are known (open question in
SPEC.md).

## Connect Lakeflow to it

For each division in use, copy the hub's **lakeflow-send** connection string and paste it in the console under
**Configuration → Destinations** (or in the setup wizard). In the portal: the namespace → Event Hubs → the hub → Shared
access policies → lakeflow-send → *Connection string–primary key*. Or in Cloud Shell (the deployment output lists this
command per hub):

```bash
az eventhubs eventhub authorization-rule keys list -g rg-lakeflow --namespace-name mercer-lakeflow --eventhub-name grain -n lakeflow-send --query primaryConnectionString -o tsv
```

The collectors connect out on 443 (AMQP over WebSockets) to `<namespace>.servicebus.windows.net`; allow that name on
the firewall (FortiGate FQDN object).

## Connect Fabric to it

One Eventstream per hub in use (step by step: "Connecting to Microsoft Fabric" in the main README): source Azure Event Hubs, the hub, consumer group `fabric`,
shared access key `fabric-listen`.

With `allowedIpRanges` set, the namespace refuses everyone else, Fabric included. Either leave the list empty (keys
are still required, and the send keys only send) or give Fabric a way in (a managed private endpoint from the Fabric
workspace) before turning it on.

## Rotating keys

Regenerate a hub's `lakeflow-send` key in the portal, paste the new connection string in the console; collectors get
tokens signed with it within a minute (old tokens stop working when they expire, at most 24 hours). For
`fabric-listen`, update the Eventstream source.
