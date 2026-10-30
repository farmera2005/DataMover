# Lakeflow: site-to-lake data flow

Replicates Mercer Landmark site databases into Microsoft Fabric OneLake. The brief is [SPEC.md](SPEC.md); where the build
departs from it (one Docker stack set up in the browser, a central collector instead of per-site agents) is in
[docs/decisions.md](docs/decisions.md).

```
                         ┌──────────────── Docker stack on an on-prem server ────────────────┐
site SQL Servers ◄─SQL── │ built-in collector ──► control API + web console ──► PostgreSQL   │
                         └────────│──────────────────────▲─────────────────────────────────────┘
                                  │ HTTPS (Send-only)     │ browser
                                  ▼                       │
                             Event Hubs ──► Fabric (bronze → silver)
```

## Install and first run

On a Linux server inside the WAN (one that can reach the sites' SQL Servers), with Docker and Docker Compose:

```bash
docker compose up -d --build
```

Then open **https://&lt;server&gt;:8443** (or http://&lt;server&gt;:8090) and follow the setup wizard. That is the only
command: there is no config file and nothing to set in environment variables.

The wizard creates the administrator (a local account that always signs in; within an hour of the stack starting, so
nobody else can claim a fresh install), takes each division's Event Hub connection string (can be skipped), and creates
the first site, read by the stack's built-in collector, which enrolls on its own. Then the source wizard tests the
connection and lists the tables. Everything else is under **Configuration**: sites and collectors, destinations (Event
Hubs, Fabric), users, directory (AD/LDAP) sign-in, and the server's certificate. See [docs/console.md](docs/console.md).

Azure and Fabric, once: deploy Event Hubs with [infra/README.md](infra/README.md), then connect Fabric as described in
[Connecting to Microsoft Fabric](#connecting-to-microsoft-fabric) below.

The browser warns about the HTTPS certificate until you either trust the Lakeflow CA on your PCs (downloadable under
Configuration, then Server & certificate) or upload a certificate for the server's name there.

**If the build fails:** it downloads .NET packages from NuGet and base images from Microsoft's registry. Run it again (a
second run reuses what downloaded). Behind a proxy, configure Docker's proxy for builds. If the network inspects HTTPS
to NuGet, the build cannot restore packages; ask for prebuilt images instead of
disabling certificate checks.

**Ports** (optional to change, e.g. `LAKEFLOW_HTTPS_PORT=9443 docker compose up -d`): 8090 console over HTTP, 8443
console over HTTPS, 8444 collectors. **Volumes:** `pgdata` (database), `api-data` (master key, CA; back it up with the
database), `secrets` (database password), `collector-data` and `collector-secrets` (the built-in collector's buffers
and key), `bootstrap` (only while the built-in collector enrolls).

## Connecting to Microsoft Fabric

Collectors send every change to Event Hubs (one hub per division). Fabric picks the changes up from there, builds the
bronze and silver tables, and checks them against the sources every night. Lakeflow itself never writes to Fabric; the
only connection from the Lakeflow server to Fabric is read-only, to show those nightly checks in the console.

```
Event Hubs hub (per division) ──► Eventstream ──► bronze.<division>_events ──► notebook every 5 min ──► bronze history + silver
                                                                                                     └─► silver.lakeflow_reconciliation
Lakeflow console ◄── reads ── silver SQL analytics endpoint (service principal, read-only)
```

Set up in this order; allow an afternoon the first time. Menu names are as of late 2026 (Microsoft renames things now
and then; look for the closest match). What you create, all in one Fabric workspace:

| Item | Name | Created in |
| --- | --- | --- |
| Fabric capacity (F SKU) or trial | any, e.g. `mercerlakeflow` | step 0 |
| Workspace | e.g. `Lakeflow` | step 1 |
| Lakehouse | `bronze` (exactly) | step 2 |
| Lakehouse | `silver` (exactly) | step 2 |
| Eventstream, one per division in use | e.g. `lakeflow-grain` | step 3 |
| Cloud connection to each hub | e.g. `lakeflow-grain-hub` | step 3 |
| Notebook | `lakeflow_bronze_to_silver` | step 4 |
| Notebook | `lakeflow_maintenance` | step 4 |
| Two schedules | on the notebooks | step 6 |
| App registration (Entra ID), shared on `silver` | `lakeflow-reconciliation` | step 7 |

### Before you start

- **Event Hubs deployed** with `infra/main.bicep` (see [infra/README.md](infra/README.md)), and at least one
  division's hub entered in the console (Configuration, Destinations), so changes are flowing.
- **Who does what:** a Fabric administrator (tenant settings, step 0 and 7b), an Azure subscription owner or contributor
  (only if you create the capacity in Azure), someone who can register applications in Microsoft Entra ID (step 7a),
  and you as workspace admin for the rest.

### 0. Fabric itself: enabled, and a capacity

1. **Fabric is on for your users.** Fabric administrator: [app.fabric.microsoft.com](https://app.fabric.microsoft.com)
   → gear icon → **Admin portal → Tenant settings → Microsoft Fabric → Users can create Fabric items**: enabled
   (for the whole organisation or a security group that includes the people setting this up).
2. **A capacity** runs the Eventstreams and notebooks. Pick one:
   - **An existing F-SKU capacity** your organisation already has: ask its admin to let you assign workspaces to it
     (Admin portal → Capacity settings → the capacity → Contributor permissions).
   - **A new one in Azure:** Azure portal → **Create a resource** → search **Microsoft Fabric** → **Create**:
     subscription, resource group (e.g. `rg-lakeflow`), capacity name (lower-case letters and digits, e.g.
     `mercerlakeflow`), region (preferably your Fabric home region: Fabric → **?** → About Fabric), size **F4** to start,
     capacity administrator (you) → **Review + create**. Or deploy `infra/fabric-capacity.bicep`:

     ```bash
     az deployment group create -g rg-lakeflow -f infra/fabric-capacity.bicep -p capacityName=mercerlakeflow administrators='["you@mercerlandmark.com"]'
     ```

     A capacity bills every hour it runs; **Pause** it in the Azure portal when not needed (paused, the notebooks and
     Eventstreams stop; Event Hubs keeps changes 7 days, so resume within a week). Size it later from the **Fabric
     Capacity Metrics** app.
   - **A trial**, to try things out: Fabric → your account picture (top right) → **Start trial** (60 days, then the
     workspace must move to a paid capacity).

### 1. The workspace

1. Fabric → **Workspaces** (left rail) → **+ New workspace**.
2. **Name** `Lakeflow`; description e.g. "Site data from Lakeflow".
3. **Advanced → License mode**: **Fabric capacity**, and choose the capacity from step 0 (or **Trial**).
4. **Apply**.
5. **Access**: in the workspace, **Manage access** → **+ Add people or groups**. Add security groups rather than
   people: data engineers as **Member** or **Contributor**, readers of the tables as **Viewer**. Only you need **Admin**.

To change the capacity later: workspace → **Workspace settings → License info → Edit**.

### 2. The two lakehouses

1. In the workspace, **+ New item** → search **Lakehouse** → **Lakehouse**.
2. **Name** `bronze` (exactly; the notebooks and the console use these names). Leave **Lakehouse schemas** unticked.
   **Create**.
3. Back in the workspace, **+ New item → Lakehouse** again: `silver`, schemas unticked, **Create**.

Each lakehouse also gets a **SQL analytics endpoint** with the same name automatically (used in step 7). Both start
empty: the tables appear as data arrives.

### 3. An Eventstream per division hub

Repeat for each division you use (`grain`, `feed`, `agronomy`, `erp`, `other`); the example is `grain`.

1. **The hub's read key.** Azure portal → your Event Hubs namespace (e.g. `mercer-lakeflow`) → **Entities → Event
   Hubs** → `grain` → **Settings → Shared access policies** → `fabric-listen` → copy the **Primary key**.
2. **The Eventstream.** Fabric workspace → **+ New item** → search **Eventstream** → **Eventstream**; **Name**
   `lakeflow-grain` → **Create**. (If asked for "enhanced capabilities", keep them on.)
3. **Its source.** **Add source** (or **Connect data sources**) → **Azure Event Hubs** → **Connect**:
   - **Connection** → **New connection**:
     - **Event Hub namespace**: `mercer-lakeflow` (the name, not the full address)
     - **Event Hub**: `grain`
     - **Connection name**: `lakeflow-grain-hub`
     - **Authentication kind**: Shared Access Key; **Shared Access Key Name** `fabric-listen`; **Shared Access Key**
       the primary key from step 1
     - **Connect**.
   - **Consumer group**: `fabric` (not `$Default`).
   - **Data format**: **Json**.
   - **Next → Add**.
4. **Its destination.** On the canvas, **Add destination → Lakehouse**:
   - **Destination name**: `bronze-grain`
   - **Workspace**: `Lakeflow`; **Lakehouse**: `bronze`
   - **Delta table**: **Create new** → `grain_events` (always `<division>_events`)
   - **Input data format**: **Json**
   - **Save**.
5. **Publish** (top right). The source and destination turn **Active** within a minute or two.
6. **Check.** Open the source node: **Data preview** shows the change envelopes (JSON with `site_id`, `table`, `op`, ...)
   once a collector has sent something for this division. A few minutes later `bronze` lists the table `grain_events`.

One Eventstream per hub is all there is: new sites, sources and tables flow through it with no change here.

If you set `allowedIpRanges` on the namespace, Fabric is refused like everyone else; see "Connect Fabric to it" in
[infra/README.md](infra/README.md).

### 4. The two notebooks

The notebooks come from this repository, generated from the tested code in `fabric/src` (never edit them in Fabric;
re-import or update after a change).

**Import (simplest):**

1. Download `fabric/import/lakeflow_bronze_to_silver.ipynb` and `fabric/import/lakeflow_maintenance.ipynb` from the
   repository.
2. Fabric workspace → **Import** (toolbar) → **Notebook** → **From this computer** → **Upload** → pick both files.
3. They appear as `lakeflow_bronze_to_silver` and `lakeflow_maintenance` (keep these names).

To update later, delete the two notebooks (their schedules go with them; set them again in step 6) and import again.

**Or with Git integration** (updates with one click; the repository folder must hold only the notebooks):
workspace → **Workspace settings → Git integration** → **GitHub** → add your GitHub account (a fine-grained personal
access token with read access to this repository's contents) → repository `farmera2005/DataMover`, branch, **Git
folder** `fabric/notebooks` → **Connect and sync**. Later: **Source control → Updates → Update all**. Fabric also lists
the lakehouses and Eventstreams as uncommitted changes; leave them uncommitted.

**Then, in each notebook** (open it):

1. Left side, **Explorer** (or **Lakehouses**) → **+ Data items** / **Add** → **Existing lakehouse** → tick `bronze` →
   **Add**. It becomes the **default** lakehouse (pin icon); the notebook keeps its progress there under
   `Files/lakeflow/checkpoints`.
2. **+ Data items / Add** again → `silver` → **Add** (a second lakehouse; leave `bronze` as default).
3. Top of the first cell: the language is **PySpark (Python)**. Nothing else to change: the settings cell already names
   `bronze` and `silver`.

### 5. First run

Open `lakeflow_bronze_to_silver` → **Run all**. The first run takes a few minutes while Spark starts. The last cell
prints one line per division: what was applied, or `no raw table` for divisions without an Eventstream (normal). Then
`silver` shows a table per source system table, e.g. `compuweigh_tickets`, and `bronze` a change table per source
table, e.g. `grain_compuweigh_tickets_changes`.

In silver, always filter `_deleted = false` (deleted rows stay a few days as tombstones). Columns are text exactly as
the source had them; cast in the semantic model or Tableau. See [fabric/README.md](fabric/README.md) for the details.

### 6. Schedules

1. Open `lakeflow_bronze_to_silver` → **Run** (ribbon) → **Schedule** (or the workspace list: **...** → **Schedule**).
2. **Scheduled run: On**; **Repeat**: by the minute, every **5** minutes; start now; your time zone → **Apply**.
3. Same for `lakeflow_maintenance`: **Repeat** monthly (e.g. first Sunday, 03:00) → **Apply**.

Each run's result shows under the notebook's **Recent runs** (or **Monitor** in the left rail). Event Hubs keeps
changes 7 days: if the schedule stops, turn it back on within a week and it catches up with nothing lost.

### 7. Reconciliation results in the console

Every night the collectors count each table's rows and checksum its keys; the notebook compares silver at that exact
point and writes the result to `silver.lakeflow_reconciliation`. To see those results (and have a mismatch turn the
site amber), the console reads that table through the silver lakehouse's SQL analytics endpoint, as a service
principal with read access to nothing else.

#### 7a. A service principal (Microsoft Entra admin center)

1. [entra.microsoft.com](https://entra.microsoft.com) → **Identity → Applications → App registrations** → **+ New
   registration**.
2. **Name** `lakeflow-reconciliation`; **Supported account types**: *Accounts in this organizational directory only*;
   **Redirect URI**: leave empty → **Register**.
3. On its **Overview**, copy the **Application (client) ID**.
4. **Certificates & secrets → Client secrets → + New client secret**: description `Lakeflow console`, expiry (e.g. 12
   months) → **Add**. Copy the **Value** column right away (not the Secret ID; the value is shown only once). Put the
   expiry date in a calendar.
5. Nothing under **API permissions** is needed (remove the default *User.Read* if you like).
6. **A security group** for step 7b: **Identity → Groups → + New group**, type Security, name e.g. `Fabric service
   principals`, **Members** → add `lakeflow-reconciliation` → **Create**.

#### 7b. Allow service principals in Fabric (Fabric administrator)

Fabric → gear icon → **Admin portal → Tenant settings → Developer settings → Service principals can use Fabric APIs**:
**Enabled**, **Specific security groups** → `Fabric service principals` → **Apply**. (Changes can take up to 15 minutes.)

#### 7c. Read access to silver only

1. In the `Lakeflow` workspace, on the `silver` lakehouse (the lakehouse, not its SQL analytics endpoint) → **...**
   → **Manage permissions** (or **Share**).
2. **+ Add user** → search `lakeflow-reconciliation` (the app's name).
3. Tick **Read all SQL endpoint data** only (not "Read all Apache Spark", not "Build reports") → **Grant**.

No workspace role is needed; a workspace Viewer role would also work but sees every item.

#### 7d. The endpoint's address

1. In the workspace, open the **SQL analytics endpoint** named `silver` (type *SQL analytics endpoint*, listed under
   the lakehouse), or open the lakehouse and switch from **Lakehouse** to **SQL analytics endpoint** (top right).
2. **Settings** (gear) → **SQL endpoint** → **Copy** the **SQL connection string**, or on the endpoint's ribbon
   **Copy SQL connection string**.
3. It looks like `xxxxxxxx-xxxx.datawarehouse.fabric.microsoft.com`: that is the server. The database is the lakehouse
   name, `silver`.

#### 7e. Enter it in the console

Lakeflow console → **Configuration → Destinations → Fabric (reconciliation)**:

| Field | Value |
| --- | --- |
| SQL analytics endpoint | the address from 7d |
| Lakehouse | `silver` |
| Application (client) ID | from 7a |
| Client secret | the value from 7a (stored encrypted; never shown again) |

**Save**, then **Test**:

- *Connected. No reconciliation results yet* — working; the first results arrive after the first night (02:30 at the
  sites, then the next notebook run).
- *Connected; N reconciliation results* — working.

The site page then shows each table's last check, and the fleet view turns a site amber on a mismatch over 0.1%.

#### 7f. Network

The Lakeflow server connects out to `*.datawarehouse.fabric.microsoft.com` on **TCP 1433** and to
`login.microsoftonline.com` on 443. Allow both on the firewall (FortiGate FQDN objects).

#### Renewing the secret

Before it expires: add a new client secret to the app registration, paste its value in the console (leave the other
fields), **Save**, **Test**, then delete the old secret. Until then the console shows no reconciliation results (the
fleet view keeps working).

### Troubleshooting the Test button

| Message contains | Cause |
| --- | --- |
| `AADSTS7000215` / invalid client secret | the Secret ID was copied instead of the Value, or the secret expired |
| `AADSTS700016` / application not found | wrong client ID, or the app is in another tenant |
| `Login failed for user '<token-identified principal>'` | the tenant setting (7b) is off for this app, or silver is not shared with it (7c) |
| `Cannot open database "silver"` | the lakehouse name is not `silver`, or not shared with the app |
| timeout, `network-related` | the firewall blocks 1433 to `*.datawarehouse.fabric.microsoft.com` (7f) |

### Troubleshooting the data

| Symptom | Check |
| --- | --- |
| no `<division>_events` table in bronze | the console's fleet view: is the site green, Event Hubs reachable? Then the Eventstream: source status, key name `fabric-listen`, consumer group `fabric` |
| `<division>_events` grows, silver does not | the notebook's schedule (Run history); both lakehouses attached, `bronze` as default |
| reconciliation `mismatch` | the site page shows source vs silver counts; a resnapshot of that table (site page) rebuilds it |
| long numbers lose digits in silver | the pilot check in `fabric/README.md` ("does Eventstream keep numbers exact?") |

## Connecting to Snowflake

Optional, alongside Fabric or instead of it. Nothing extra is installed: the Lakeflow server reads each division's
Event Hub with its own consumer group (`snowflake`), streams every change into `BRONZE.EVENTS` with Snowpipe
Streaming, and builds the `SILVER` tables (current state, one per source table, same rules as Fabric's silver) on a
schedule with SQL. The console shows what it is doing and, nightly, the reconciliation results.

### Before you start

- A Snowflake account (any edition, any cloud) and someone who can use the **ACCOUNTADMIN** role once.
- Your account identifier: in Snowsight, click your name (bottom left), then **Account**, then hover the account and
  **Copy account identifier**. It looks like `MYORG.MYACCOUNT`; Lakeflow wants it as `myorg-myaccount` (dot made a hyphen).
- The Lakeflow server must be able to connect out on **443** to `myorg-myaccount.snowflakecomputing.com` and to the
  Snowpipe Streaming ingest host that account hands out (also under `*.snowflakecomputing.com`). If your firewall
  inspects HTTPS, allow `*.snowflakecomputing.com` through without inspection, or Snowflake will refuse the connection.
- Each division's Event Hub already set up in **Configuration → Destinations** (the collectors are sending).

### 1. Listen keys and consumer groups on the hubs

Deploy (or re-deploy) the Bicep template with `snowflake=true`:

```bash
az deployment group create -g rg-lakeflow -f infra/main.bicep -p infra/main.example.bicepparam -p snowflake=true
```

That adds, on every hub, a consumer group `snowflake` and a shared access policy `snowflake-listen` with only
**Listen**. Its output prints, per hub, the command that shows the connection string. Without the template: in the
Azure portal, open the namespace, then the hub, then **Consumer groups → + Consumer group** (`snowflake`), then
**Shared access policies → + Add** (`snowflake-listen`, tick only **Listen**), then open the policy and copy
**Connection string–primary key**.

### 2. Account and key pair (console)

**Configuration → Snowflake**: enter the account identifier and keep the default user, role, warehouse and database
names unless you have a naming standard. **Save**, then **Generate key pair**. The private key stays on the Lakeflow
server (encrypted with its master key); the page shows the public key and a setup script containing it.

### 3. Run the setup script (Snowflake)

Copy the script from the page. In Snowsight: **Projects → Worksheets → + → SQL worksheet**, paste, and **Run All**
(top right, the arrow menu) as ACCOUNTADMIN. It creates:

| Object | What it is |
| --- | --- |
| role `LAKEFLOW_FORWARDER` | owns the `LAKEFLOW` database; nothing else in the account |
| warehouse `LAKEFLOW_WH` | X-Small, suspends after 60 seconds; runs only while silver is built |
| database `LAKEFLOW` | Lakeflow creates its `BRONZE` and `SILVER` schemas and tables itself |
| user `LAKEFLOW_FORWARDER` | `TYPE = SERVICE`: signs in only with the key pair, no password |
| role `LAKEFLOW_READER` | grant it to analysts and BI tools: read access to everything Lakeflow writes |

Back in the console, **Test connection** should say "Connected as LAKEFLOW_FORWARDER ...".

### 4. Hubs to forward, then turn it on (console)

Still on **Configuration → Snowflake**: for each division, paste the `snowflake-listen` connection string and
**Save** (the hub must be the same one the collectors send to; the page checks). Then tick **Forward changes to
Snowflake**. Within a minute the status table shows rows sent per hub partition, and silver is built every 5
minutes (change it on the page; the warehouse runs only then).

### What lands where

| Table | Contents |
| --- | --- |
| `BRONZE.EVENTS` | every change, one row each: site, source, table, operation, key, sequence, times; key, before and after as exact JSON text (`KEY_JSON`, `BEFORE_JSON`, `AFTER_JSON`) and `AFTER_VALUES` (a VARIANT with every value as text) |
| `SILVER.<SOURCESYSTEM>_<SCHEMA>_<TABLE>` | current state of each source table: source columns as text, plus `_SITE_ID`, `_SOURCE_ID`, `_KEY_ID`, `_SEQ`, `_OP`, `_DELETED`, `_CAPTURE_TS`, `_SOURCE_TS`, `_MERGED_AT`. Deleted rows stay as tombstones (`_DELETED = TRUE`) for 8 days |
| `SILVER.LAKEFLOW_RECONCILIATION` | nightly source vs silver row counts and checksums, shown in the console |
| `BRONZE.LAKEFLOW_PROGRESS`, `BRONZE.LAKEFLOW_BATCH` | Lakeflow's own bookkeeping; leave them alone |

Bronze rows are written exactly once per hub position (the channel's offset token is the Event Hubs sequence
number), so a restart neither loses nor repeats changes. Silver is rebuilt idempotently from what bronze holds.

### Cost

The forwarder itself uses no warehouse: Snowpipe Streaming is billed by data ingested. The warehouse runs a few
seconds every silver build. An X-Small warehouse costs 1 credit per hour running, billed at least 60 seconds each
time it resumes, so a 5-minute schedule is about 288 one-minute runs, roughly 5 credits a day; every 60 minutes is
under half a credit a day. Pick the interval to match how fresh reports need to be.

### Troubleshooting

| Symptom | Check |
| --- | --- |
| Test: "No such account" | the account identifier: `myorg-myaccount`, not the URL, not the locator without its region |
| Test: "refused the key" | the setup script's `ALTER USER ... SET RSA_PUBLIC_KEY` was not run with the key now shown (a new key pair needs it again) |
| Test: "Could not reach" | outbound 443 to `*.snowflakecomputing.com`, without HTTPS inspection |
| status: setup error | the role cannot create schemas in the database: run the setup script again (the `GRANT OWNERSHIP` line) |
| a partition shows an error, rows stop | the `snowflake-listen` key (regenerated in Azure?) and the `snowflake` consumer group exist on that hub |
| silver behind, bronze growing | the silver error under Status; the warehouse is not suspended by a resource monitor |

## Status

Phase 1 (pilot: one grain site, SQL Server), built step by step:

- [x] 1. Contracts: change envelope, connector interface, config schema, connector contract test suite
- [x] 2. SQL Server connector (Change Tracking, rowversion polling, timestamp polling), integration tests against SQL Server in Docker
- [x] 3. Agent pipeline: SQLite buffer, checkpoints, Event Hubs sender, simulated 24-hour outage
- [x] 4. Control API: enroll, config, heartbeat, commands (see docs/control-api.md)
- [x] 5. Fabric notebooks: bronze-to-silver MERGE, nightly reconciliation (see fabric/README.md)
- [x] 6. Console: sign-in, fleet overview, site detail, source setup wizard; web app (see docs/console.md)
- [x] Central collector serving many sites, in Docker (see docs/collector.md, docs/decisions.md)
- [x] One Docker stack, set up entirely in the browser (setup wizard, local + LDAP sign-in, settings in the console)
- [x] 7. Azure: Event Hubs namespace and hubs (Bicep, see infra/README.md); the site-agent MSI (see docs/collector.md)
- [x] Snowflake as a destination ("Connecting to Snowflake" above; forwarder built into the server)

Phase 2 (SQL Server rollout to all grain, feed and agronomy sites):

- [x] 1. Alerts: the spec's rules raise and clear alerts; console Alerts page with acknowledge and notes (see docs/console.md)
- [ ] 2. Alert delivery: email (Microsoft Graph) and a Teams channel, quiet hours, harvest-season overrides
- [ ] 3. Self-update: signed MSI uploaded in the console, target version, install in the site's maintenance window
- [ ] 4. Rollout aids: bulk site import, division filters

## Layout

```
src/Lakeflow.Contracts/   envelope, RowChange/ChangeBatch, Checkpoint, ISourceConnector, config DTOs,
                          heartbeat/commands/events, ColumnValues (one value encoding for all connectors),
                          SchemaComparer (drift), ConfigValidator, LakeNames (hub/bronze/silver names)
src/Lakeflow.Connectors.SqlServer/
                          SQL Server connector: Change Tracking, rowversion and timestamp polling;
                          see docs/sqlserver-source-setup.md
src/Lakeflow.Agent/        the collector (Docker on Linux centrally; Windows service at a site): one pipeline per assigned
                          site, each with an encrypted SQLite buffer (SQLCipher; key under DPAPI or a master key file), per-table checkpoints and
                          seq, capture workers (throttled, one connection per source, back-off), schema drift
                          handling, nightly reconciliation, Event Hubs delivery with back-off, heartbeat,
                          commands, Serilog JSON logs (14 days)
src/Lakeflow.ControlApi/   ASP.NET Core + EF Core (PostgreSQL): setup, sign-in (local + LDAP), users, settings, enrollment
                          and collector certificates, config with SAS tokens, heartbeat, events, commands, sealed
                          credentials, fleet status, audit; serves the console (see docs/control-api.md)
src/Lakeflow.Console.Core/ console logic (API client, view models: fleet, site detail, setup wizard, sites and collectors)
src/Lakeflow.Console.Web/  the web console (Blazor WebAssembly), served by the control API at /console
compose.yaml              the stack: init (database password), db, api, collector
deploy/api/               the control API image (with the console)
deploy/collector/         the collector image, and a compose file for a collector on another server
infra/                    Bicep for the Azure side: the Event Hubs namespace, a hub per division, Send/Listen policies
installer/                WiX MSI for an agent at a site the server cannot reach (Windows service, virtual account)
tests/Lakeflow.Testing/   ConnectorContractTests: the suite every connector must pass, plus an in-memory
                          reference source/connector used to prove the suite and to drive agent tests
tests/Lakeflow.Contracts.Tests/
tests/Lakeflow.Connectors.SqlServer.Tests/   contract suite x3 capture methods + SQL Server specifics
tests/Lakeflow.Agent.Tests/      pipeline on simulated time: 24-hour outage gate, buffer cap, failures, commands; SQL Server end-to-end
tests/Lakeflow.ControlApi.Tests/ the API in-process on PostgreSQL: setup and its window, sign-in, users, write-only
                                 settings, every route walked for its role, enrollment and CA pinning, config, audit,
                                 credentials, status; the real collector enrolling and running against it
tests/Lakeflow.Console.Core.Tests/ console view models against the real API and a running collector (setup wizard end to
                                   end); browser sealing (seal.js under Node) opened by the collector's C# code
fabric/                   bronze-to-silver MERGE and reconciliation: a Python package tested on local Spark + Delta,
                          and the Fabric notebooks generated from it (see fabric/README.md)
```

## Build and test

Requires the .NET 8 SDK. The control API tests need PostgreSQL and the connector tests SQL Server, both in Docker;
without `POSTGRES_TEST_CONNECTION` / `MSSQL_TEST_CONNECTION` they are skipped.

```bash
eval "$(scripts/test-postgres.sh)"    # starts PostgreSQL 16 in Docker, exports POSTGRES_TEST_CONNECTION
eval "$(scripts/test-sqlserver.sh)"   # starts SQL Server 2022 in Docker, exports MSSQL_TEST_CONNECTION
dotnet test
```

GitHub Actions (`.github/workflows/build.yml`) runs every test (with PostgreSQL and SQL Server as service containers)
and the Fabric tests, runs `docker compose up -d --build` and checks the stack comes up unconfigured with the built-in
collector enrolled, compiles and checks the Bicep template, and builds the site-agent MSI, installs it on a Windows
runner, checks the service, its account and its folder and registry permissions, and uninstalls it.

## Connector contract

Each connector + capture method gets a subclass of `ConnectorContractTests` with an `IContractTestBed` that can
write to a real source. The suite checks: schema and key discovery, bounded snapshot batches, inserts/updates,
deletes (natively, or by key reconciliation through `IKeyReader` for polling methods), changes made during a
snapshot, resuming an interrupted snapshot, resuming incremental capture after a restart (checkpoint round-tripped
through JSON), an added column captured automatically, and dropped/retyped columns reported as breaking.

## Agent behaviour worth knowing

- **No loss:** changes and the checkpoint that follows them are committed to the local buffer in one transaction;
  rows leave the buffer only when Event Hubs acknowledges them. Delivery is at-least-once, in `seq` order per table.
- **Outages:** while Event Hubs is unreachable the agent keeps capturing into the buffer and retries with back-off
  (1 s doubling to 5 min). At the buffer cap (default 10 GB, alert at 80%) capture pauses and the data waits at the
  source; the cap can be exceeded by at most one 1000-row read.
- **Resnapshots** (command, capture-method change, or Change Tracking retention passing the checkpoint) end with a
  `snapshot_complete` envelope carrying `snapshot_start_seq`. Silver must delete the site's rows of that table whose
  latest `seq` is lower: they were deleted while the table was not being tracked.
- **Nightly reconciliation** (02:30 site time): every table's keys are read once; polling tables get their deletes
  from it, and a `reconcile` envelope carries the source row count and key checksum for silver to compare against.
- **Schema drift:** added columns flow on (older rows simply lack them); a dropped or retyped column blocks the table
  until an operator resumes it (accepting the new schema) or resnapshots it.
