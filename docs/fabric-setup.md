# Connecting Lakeflow to Microsoft Fabric

Collectors send every change to Event Hubs (one hub per division). Fabric picks the changes up from there, builds the
bronze and silver tables, and checks them against the sources every night. Lakeflow itself never writes to Fabric; the
only connection from the Lakeflow server to Fabric is read-only, to show those nightly checks in the console.

```
Event Hubs hub (per division) ──► Eventstream ──► bronze.<division>_events ──► notebook every 5 min ──► bronze history + silver
                                                                                                     └─► silver.lakeflow_reconciliation
Lakeflow console ◄── reads ── silver SQL analytics endpoint (service principal, read-only)
```

Set up in this order. Allow about an hour. Menu names below are as of late 2026; Microsoft renames things now and then.

## Before you start

- **Event Hubs deployed** with `infra/main.bicep` (see `infra/README.md`), and at least one division's hub entered in the
  console (Configuration, Destinations) so changes are flowing.
- **A Fabric capacity** (an F SKU, or a trial) and permission to create a workspace on it.
- **For the console's reconciliation view** (step 6): someone who can create an app registration in Microsoft Entra ID,
  and a Fabric administrator for one tenant setting.

## 1. Workspace and lakehouses

1. In Fabric, **Workspaces → New workspace**, e.g. `Lakeflow`, on your capacity (Advanced → License mode).
   Limit access to the people who should see the data (spec: named groups only).
2. In the workspace, **New item → Lakehouse**, named exactly `bronze`. Leave **Lakehouse schemas** unticked.
3. Again, a lakehouse named exactly `silver`, schemas unticked.

The notebooks and the console look for these two names.

## 2. One Eventstream per division hub

Repeat for each division you use (`grain`, `feed`, `agronomy`, `erp`, `other`).

1. Get the hub's read key: Azure portal → the Event Hubs namespace → **Event Hubs** → the hub (e.g. `grain`) →
   **Shared access policies** → `fabric-listen` → copy the **Primary key**.
2. In the Fabric workspace, **New item → Eventstream**, e.g. `lakeflow-grain`.
3. **Add source → Azure Event Hubs**:
   - **Connection**: new connection; Event Hub namespace = your namespace name (e.g. `mercer-lakeflow`), Event Hub =
     `grain`, authentication **Shared Access Key**, key name `fabric-listen`, key = the primary key from step 1.
   - **Consumer group**: `fabric`.
   - **Data format**: JSON.
4. **Add destination → Lakehouse**: workspace `Lakeflow`, lakehouse `bronze`, Delta table **`grain_events`** (new;
   the name is `<division>_events`), input data format JSON.
5. **Publish**.

One Eventstream per hub is all there is: new sites, sources and tables flow through it with no change here.

Check: once a collector has sent something for that division, `bronze` shows the table `grain_events` with rows (open
the lakehouse; give it a few minutes).

If you set `allowedIpRanges` on the namespace, Fabric is refused like everyone else; see "Connect Fabric to it" in
`infra/README.md`.

## 3. The notebooks

The two notebooks are in this repository, `fabric/notebooks`.

**With Git (recommended):** workspace → **Workspace settings → Git integration** → GitHub, repository
`farmera2005/DataMover`, the branch you deploy from, **Git folder** `fabric/notebooks` → **Connect and sync**. The
notebooks `lakeflow_bronze_to_silver` and `lakeflow_maintenance` appear in the workspace, and later updates arrive with
**Source control → Update**.

**Without Git:** create two notebooks with those names and paste in the cells of each `notebook-content.py` (the
lines between the `# CELL` markers).

Then in **each** notebook:

1. Open the **Lakehouses** pane (Explorer) → **Add** → existing lakehouse → `bronze`. Make it the **default**
   lakehouse (the notebook keeps its progress in `bronze` under `Files/lakeflow/checkpoints`).
2. **Add** → existing lakehouse → `silver` (as a second, non-default lakehouse).

## 4. First run

Open `lakeflow_bronze_to_silver` and **Run all**. The last cell prints one line per division: rows applied, or
`no raw table` for divisions whose Eventstream is not set up (normal). Then `silver` shows a table per source system
table, e.g. `compuweigh_tickets`.

In silver, always filter `_deleted = false` (deleted rows stay a few days as tombstones). Columns are text exactly as
the source had them; cast in the semantic model or Tableau. See `fabric/README.md` for the details.

## 5. Schedules

- `lakeflow_bronze_to_silver`: **Run → Schedule** → repeat every **5 minutes**, on.
- `lakeflow_maintenance`: monthly (e.g. first Sunday, 03:00). It removes old tombstones and compacts the tables.

Event Hubs keeps changes 7 days: if the schedule stops, restart it within a week and it catches up with nothing lost.

## 6. Reconciliation results in the console

Every night the collectors count each table's rows and checksum its keys; the notebook compares silver at that exact
point and writes the result to `silver.lakeflow_reconciliation`. To see those results (and have a mismatch turn the
site amber), the console reads that table through the silver lakehouse's SQL analytics endpoint, as a service
principal with read access to nothing else.

### 6a. A service principal (Microsoft Entra admin center)

1. **App registrations → New registration**: name `lakeflow-reconciliation`, single tenant, no redirect URI.
2. Copy the **Application (client) ID**.
3. **Certificates & secrets → New client secret**: pick an expiry (e.g. 12 months) and copy the secret's **Value**
   right away (not the Secret ID; the value is shown once). Put the expiry date in a calendar.

No API permissions are needed.

### 6b. Allow service principals in Fabric (Fabric administrator)

**Admin portal → Tenant settings → Developer settings → Service principals can use Fabric APIs**: enabled, ideally for
a security group that contains `lakeflow-reconciliation` rather than the whole organisation.

### 6c. Read access to silver only

In the workspace, on the `silver` lakehouse: **… → Share** → enter `lakeflow-reconciliation` → tick **Read all SQL
endpoint data** → **Grant**. (No workspace role is needed; a workspace Viewer role would also work but sees more.)

### 6d. The endpoint's address

Open `silver`, switch to its **SQL analytics endpoint** (top right), then **Settings** (or **Copy SQL connection
string**). It looks like `xxxxxxxx.datawarehouse.fabric.microsoft.com`.

### 6e. Enter it in the console

Lakeflow console → **Configuration → Destinations → Fabric (reconciliation)**:

| Field | Value |
| --- | --- |
| SQL analytics endpoint | the address from 6d |
| Lakehouse | `silver` |
| Application (client) ID | from 6a |
| Client secret | the value from 6a (stored encrypted; never shown again) |

**Save**, then **Test**:

- *Connected. No reconciliation results yet* — working; the first results arrive after the first night (02:30 at the
  sites, then the next notebook run).
- *Connected; N reconciliation results* — working.

The site page then shows each table's last check, and the fleet view turns a site amber on a mismatch over 0.1%.

### 6f. Network

The Lakeflow server connects out to `*.datawarehouse.fabric.microsoft.com` on **TCP 1433** and to
`login.microsoftonline.com` on 443. Allow both on the firewall (FortiGate FQDN objects).

### Renewing the secret

Before it expires: add a new client secret to the app registration, paste its value in the console (leave the other
fields), **Save**, **Test**, then delete the old secret. Until then the console shows no reconciliation results (the
fleet view keeps working).

## Troubleshooting the Test button

| Message contains | Cause |
| --- | --- |
| `AADSTS7000215` / invalid client secret | the Secret ID was copied instead of the Value, or the secret expired |
| `AADSTS700016` / application not found | wrong client ID, or the app is in another tenant |
| `Login failed for user '<token-identified principal>'` | the tenant setting (6b) is off for this app, or silver is not shared with it (6c) |
| `Cannot open database "silver"` | the lakehouse name is not `silver`, or not shared with the app |
| timeout, `network-related` | the firewall blocks 1433 to `*.datawarehouse.fabric.microsoft.com` (6f) |

## Troubleshooting the data

| Symptom | Check |
| --- | --- |
| no `<division>_events` table in bronze | the console's fleet view: is the site green, Event Hubs reachable? Then the Eventstream: source status, key name `fabric-listen`, consumer group `fabric` |
| `<division>_events` grows, silver does not | the notebook's schedule (Run history); both lakehouses attached, `bronze` as default |
| reconciliation `mismatch` | the site page shows source vs silver counts; a resnapshot of that table (site page) rebuilds it |
| long numbers lose digits in silver | the pilot check in `fabric/README.md` ("does Eventstream keep numbers exact?") |
