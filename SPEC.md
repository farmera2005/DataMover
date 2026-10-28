# Site-to-Lake Data Flow — Build Spec

Oct 28, 2026 · @Adam Farmer

## Purpose and goals

Build a system that replicates data from Mercer Landmark's local site databases into Microsoft Fabric OneLake in near real time, managed from one central desktop console.

- **Freshness:** changes land in OneLake within 5 minutes for change-captured sources (target; confirm per source).
- **No data loss:** a site that loses internet buffers locally and catches up on reconnect.
- **Outbound only:** sites never accept inbound connections; no new FortiGate inbound rules.
- **Read-only at the source:** the system never writes to a source database.
- **One pane of glass:** every agent's health, lag and errors visible in the console.
- **Non-goals (v1):** transformations beyond raw landing, writing back to sources, reporting. Tableau and Fabric handle downstream modeling.

## Source environment

Sources span 21 to 40 locations, mostly vendor-managed site systems on local servers backed up with Veeam.

| Source | Where | Engine | Notes |
| --- | --- | --- | --- |
| CompuWeigh (grain) | Grain sites | SQL Server (confirm) | Vendor-managed schema |
| WEM Automation / CPM BetaRaven (feed) | Feed mills | SQL Server (confirm) | Vendor-managed schema |
| Kahler Automation (agronomy) | Agronomy sites | SQL Server (confirm) | Vendor-managed schema |
| AgVantage ERP | MPS Feed - St. Marys | Db2 for IBM i (IBM Power) | Serves MPS Feed and Heartland Feed Services |
| Other SQL | Various | MySQL, PostgreSQL, Microsoft Access | Inventory needed |
| Flat files | Various | CSV and other exports | Drop folders or shares |

Hosted AgVantage (Mercer Landmark's own ERP instance) is out of scope for the agent; it needs a hosted extract or API from AgVantage instead.

## Architecture overview

Three pieces are built — the site agent, the control API and the console — and everything else is managed Azure and Fabric services.

&#91;embedded content: architecture · site agent, Azure, Fabric, console\]

Agents push change batches to Event Hubs and poll the control API for config and commands; the console only ever talks to the API.

## Site agent

Each site runs one agent as a Windows service (.NET 8 worker) that captures changes, buffers them locally and pushes them to Azure.

- **Install:** signed MSI, unattended install with an enrollment token; runs under a dedicated low-privilege service account.
- **Pipeline per source:** capture → serialize (JSON envelope, see below) → local buffer → batch send → checkpoint.
- **Local buffer:** SQLite in WAL mode. Rows are deleted only after Event Hubs acknowledges the send. Cap at a configurable size (default 10 GB) and alert at 80%.
- **Checkpoints:** stored per table (LSN, change-tracking version, binlog position or file offset) so a restart resumes exactly where it stopped.
- **Config:** pulled from the control API every 60 seconds; cached locally so the agent keeps running if the API is down.
- **Heartbeat:** every 30 seconds — agent version, per-table lag, buffer depth, last error.
- **Updates:** the console marks a target version; agents download, verify the signature and self-update in a maintenance window.
- **Footprint:** under 300 MB RAM, throttled reads so site systems (scales especially) are never slowed.

**Change envelope** (one per row change): `site_id`, `source_id`, `table`, `op` (insert/update/delete/snapshot), `key`, `before`/`after` columns, `source_ts`, `capture_ts`, `seq` (monotonic per table).

## Source connectors

Every connector does an initial snapshot, then switches to incremental capture; each implements one interface (`Snapshot`, `ReadChanges(checkpoint)`, `GetSchema`).

| Source | Capture method | Real-time? | Fallback |
| --- | --- | --- | --- |
| SQL Server | Change Tracking (default) or CDC where the vendor allows it | Yes, seconds | Rowversion/timestamp polling |
| Db2 for IBM i | Journal reading (DISPLAY\_JOURNAL table function over ODBC) | Yes, near real time | Timestamp polling every 1–5 min |
| MySQL | Binlog (row format) | Yes | Timestamp polling |
| PostgreSQL | Logical replication (pgoutput) | Yes | Timestamp polling |
| Microsoft Access | Polling by key + hash diff | No, minutes | — |
| CSV / files | Folder watch, file hash, append-offset tracking | On arrival | — |

- **Vendor databases:** enabling Change Tracking or CDC alters the database. Get written OK from CompuWeigh, WEM/CPM and Kahler before turning it on; otherwise use polling.
- **IBM i:** confirm the AgVantage tables are journaled with \*BOTH images. Journal reading needs a dedicated user with read authority to the journals.
- **Deletes:** captured natively by CDC methods; polling sources detect deletes with a periodic key reconciliation (nightly).
- **Schema drift:** a new column is added to the envelope automatically; a dropped or retyped column pauses that table and alerts in the console.

## Transport and landing in Fabric

Agents send change batches over HTTPS (AMQP over WebSockets, port 443) to Azure Event Hubs; Fabric Eventstream lands them as Delta tables in a bronze lakehouse.

- **Event Hubs:** one namespace, one hub per division (grain, feed, agronomy, ERP, other); partition key = `site_id|table` to keep per-table order.
- **Eventstream:** reads each hub and writes to the `bronze` lakehouse, appending every change row (full history).
- **Bronze layout:** `bronze.<division>_<source>_<table>_changes`, partitioned by `capture_date`; columns = envelope fields + payload.
- **Current-state tables:** a scheduled Fabric notebook (every 5 min) MERGEs changes into `silver.<source>_<table>` by key and `seq`, applying deletes.
- **Before building SQL Server connectors:** test Fabric's built-in database mirroring against one site. If it works with the vendor databases and site networks, use it for SQL Server and keep the agent for everything else.
- **Retention:** Event Hubs 7 days (replay window); bronze kept indefinitely, compacted monthly.

## Control plane API

A small ASP.NET Core API on Azure App Service (or Container Apps) with Azure SQL Database holds all configuration and status; agents and the console both talk only to it.

| Endpoint | Caller | Purpose |
| --- | --- | --- |
| `POST /agents/enroll` | Agent | Exchange one-time token for a client certificate |
| `GET /agents/{id}/config` | Agent | Sources, tables, schedules, target hub, desired version |
| `POST /agents/{id}/heartbeat` | Agent | Health, lag, buffer depth, errors |
| `POST /agents/{id}/events` | Agent | Error and audit events |
| `GET/PUT /sites`, `/sources`, `/tables` | Console | Manage configuration |
| `POST /agents/{id}/commands` | Console | Resnapshot table, pause, resume, restart, collect logs |
| `GET /status` | Console | Fleet health rollup |

Commands are queued and picked up on the agent's next config poll, so no inbound connection to a site is ever needed. Every config change is versioned and audited (who, what, when).

## Central console

A Windows desktop app (WinUI 3 / .NET 8, MSIX-packaged) used by the IT team to configure, watch and fix the whole fleet; sign-in with Entra ID.

1. **Fleet overview** — map/list of sites, each green/amber/red; worst lag, buffer depth, last heartbeat.
2. **Site detail** — agent version, sources, per-table lag and rows/min, recent errors, "collect logs" button.
3. **Source setup wizard** — pick engine, enter connection (stored as a Key Vault reference), test connection, browse schema, select tables, choose capture method, start snapshot.
4. **Table detail** — capture method, checkpoint, rows landed today, schema history, resnapshot/pause.
5. **Alerts** — open issues with acknowledge and notes; same rules that send email/Teams alerts.
6. **Enrollment** — generate one-time enrollment tokens and the install command for a new site.
7. **Audit log** — every config change and command.

Roles: Admin (everything), Operator (pause/resume/resnapshot, acknowledge alerts), Viewer (read only).

## Security

The design assumes any one site could be compromised, so each agent can only push its own data and read its own config.

- **Agent identity:** per-agent client certificate issued at enrollment; revocable from the console.
- **Event Hubs access:** per-agent SAS token with Send-only rights, scoped to its hub, rotated every 24 hours by the control API.
- **Source credentials:** read-only database accounts per source; stored encrypted on the agent with Windows DPAPI (machine scope), entered once through the console and never stored in the control plane in clear text.
- **Network:** outbound TLS 1.2+ on 443 only, to an allowlist of Azure endpoints (FortiGate FQDN objects).
- **Console users:** Entra ID with MFA; roles mapped from Entra groups.
- **Secrets in Azure:** Key Vault; API and Fabric use managed identities.
- **Data protection:** encrypted at rest in SQLite buffer (SQLCipher) and in Azure by default; Fabric workspace access limited to named groups.
- **Audit:** all console actions and agent enrollments logged and kept 1 year.

## Reliability, monitoring and alerting

Delivery is at-least-once; the silver MERGE dedupes on `site_id`, `table`, `key` and `seq`, so replays are harmless.

| Alert | Default threshold | Severity |
| --- | --- | --- |
| Agent heartbeat missing | 5 min | Critical |
| Table lag | > 15 min (configurable per table) | Warning; Critical at 60 min |
| Local buffer | > 80% of cap | Warning |
| Source connection failing | 3 consecutive attempts | Critical |
| Schema change blocked a table | Any | Warning |
| Row-count reconciliation mismatch | > 0.1% nightly | Warning |

- **Channels:** console, email and a Teams channel; quiet hours configurable, with harvest-season overrides.
- **Reconciliation:** nightly per-table row counts and key checksums, source vs silver, reported in the console.
- **Telemetry:** agent and API logs to Azure Application Insights; agents also keep 14 days of local rolling logs.
- **DR:** control-plane config is in Azure SQL with point-in-time restore; agents keep running on cached config if Azure SQL is down.

## Phasing

Build in four phases, each ending in a gate that must pass before the next starts; dates to be set once the team is staffed.

1. **Pilot — one grain site, SQL Server.** Agent, buffer, Event Hubs, bronze/silver, control API, basic console (fleet + site detail). Also test Fabric mirroring here.
   - Gate: 2 weeks running with lag under 5 min, nightly reconciliation clean, survives a forced 24-hour internet outage with no loss.
2. **SQL Server rollout — all grain, feed and agronomy sites.** Enrollment flow, setup wizard, alerts, self-update.
   - Gate: all sites green for 7 days; vendor approvals on file.
3. **IBM i / AgVantage.** Journal connector (or polling fallback) for MPS Feed - St. Marys.
   - Gate: journal access confirmed; reconciliation clean for 7 days.
4. **Other sources.** MySQL, PostgreSQL, Access and file connectors as inventoried.
   - Gate: each source signed off by its data owner.

## Open questions

- [ ] Confirm each site system's database engine and version (CompuWeigh, WEM/BetaRaven, Kahler).
- [ ] Get vendor approval (or refusal) to enable Change Tracking/CDC on their databases.
- [ ] Are AgVantage tables on the IBM i journaled with before and after images? Who grants journal read access?
- [ ] How will hosted AgVantage data reach Fabric — AgVantage extract, API, or out of scope?
- [ ] Inventory the MySQL, PostgreSQL, Access and file sources: where, owner, volume.
- [ ] Which tables matter first? Pilot table list per site system.
- [ ] Fabric capacity SKU and workspace ownership.
- [ ] Expected change volume per site (rows/day) to size Event Hubs throughput units.
- [ ] Does Fabric mirroring work against the site SQL Servers through the network?

## Instructions for Claude Code

Build Phase 1 first as a single .NET 8 solution; stop after each step, run its tests and show results before moving on.

**Repo layout**

```
/src
  Lakeflow.Contracts/      # change envelope, config DTOs, connector interface
  Lakeflow.Agent/          # Windows service: pipeline, buffer, checkpoints, heartbeat
  Lakeflow.Connectors.SqlServer/
  Lakeflow.Connectors.Db2i/      # phase 3
  Lakeflow.Connectors.MySql/     # phase 4
  Lakeflow.Connectors.Postgres/  # phase 4
  Lakeflow.Connectors.Files/     # phase 4
  Lakeflow.ControlApi/     # ASP.NET Core + EF Core (Azure SQL)
  Lakeflow.Console/        # WinUI 3 desktop app
/fabric                    # notebooks for silver MERGE, reconciliation
/infra                     # Bicep: Event Hubs, App Service, Azure SQL, Key Vault, App Insights
/installer                 # WiX MSI for the agent
/tests
```

**Build order (Phase 1)**

1. Contracts: envelope, connector interface, config schema.
2. SQL Server connector with Change Tracking + polling fallback; integration tests against SQL Server in Docker.
3. Agent pipeline: SQLite buffer, checkpoints, Event Hubs sender with retry/backoff; test with a simulated 24-hour outage.
4. Control API: enroll, config, heartbeat, commands; Entra ID auth for console routes, cert auth for agents.
5. Fabric notebooks: bronze-to-silver MERGE and nightly reconciliation.
6. Console: sign-in, fleet overview, site detail, source setup wizard.
7. Bicep infrastructure and the agent MSI.

**Rules**

- Never issue a write, DDL or lock-heavy query against a source; connectors open read-only connections.
- No secrets in code or config files; use Key Vault references and DPAPI.
- Every connector must pass the same contract test suite (snapshot, incremental, delete, restart-resume, schema drift).
- Structured logging (Serilog) with `site_id`, `source_id`, `table` on every line.

**Phase 1 is done when** one pilot site replicates its selected tables to silver with lag under 5 minutes, survives a 24-hour outage without loss, and the console shows its health correctly.
