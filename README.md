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

The browser warns about the HTTPS certificate until you either trust the Lakeflow CA on your PCs (downloadable under
Configuration, then Server & certificate) or upload a certificate for the server's name there.

**If the build fails:** it downloads .NET packages from NuGet and base images from Microsoft's registry. Run it again (a
second run reuses what downloaded). Behind a proxy, configure Docker's proxy for builds. If the network inspects HTTPS
to NuGet (as it does for npm, see GrainTime), the build cannot restore packages; ask for prebuilt images instead of
disabling certificate checks.

**Ports** (optional to change, e.g. `LAKEFLOW_HTTPS_PORT=9443 docker compose up -d`): 8090 console over HTTP, 8443
console over HTTPS, 8444 collectors. **Volumes:** `pgdata` (database), `api-data` (master key, CA; back it up with the
database), `secrets` (database password), `collector-data` and `collector-secrets` (the built-in collector's buffers
and key), `bootstrap` (only while the built-in collector enrolls).

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
- [ ] 7. Azure: Event Hubs namespace and hubs (Bicep), the optional site-agent MSI

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
collector enrolled, and publishes the site agent for Windows.

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
