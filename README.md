# Lakeflow: site-to-lake data flow

Replicates Mercer Landmark site databases into Microsoft Fabric OneLake. The design is in [SPEC.md](SPEC.md).

## Status

Phase 1 (pilot: one grain site, SQL Server), built step by step:

- [x] 1. Contracts: change envelope, connector interface, config schema, connector contract test suite
- [x] 2. SQL Server connector (Change Tracking, rowversion polling, timestamp polling), integration tests against SQL Server in Docker
- [x] 3. Agent pipeline: SQLite buffer, checkpoints, Event Hubs sender, simulated 24-hour outage
- [x] 4. Control API: enroll, config, heartbeat, commands (see docs/control-api.md)
- [x] 5. Fabric notebooks: bronze-to-silver MERGE, nightly reconciliation (see fabric/README.md)
- [x] 6. Console: sign-in, fleet overview, site detail, source setup wizard (see docs/console.md)
- [ ] 7. Bicep infrastructure and the agent MSI

## Layout

```
src/Lakeflow.Contracts/   envelope, RowChange/ChangeBatch, Checkpoint, ISourceConnector, config DTOs,
                          heartbeat/commands/events, ColumnValues (one value encoding for all connectors),
                          SchemaComparer (drift), ConfigValidator, LakeNames (hub/bronze/silver names)
src/Lakeflow.Connectors.SqlServer/
                          SQL Server connector: Change Tracking, rowversion and timestamp polling;
                          see docs/sqlserver-source-setup.md
src/Lakeflow.Agent/        Windows service: encrypted SQLite buffer (SQLCipher, key under DPAPI), per-table checkpoints and
                          seq, capture workers (throttled, one connection per source, back-off), schema drift
                          handling, nightly reconciliation, Event Hubs delivery with back-off, heartbeat,
                          commands, Serilog JSON logs (14 days)
src/Lakeflow.ControlApi/   ASP.NET Core + EF Core (Azure SQL): enrollment and agent certificates, config with SAS tokens,
                          heartbeat, events, commands, sealed credentials, fleet status, audit; Entra ID for the console
src/Lakeflow.Console.Core/ console logic (API client, view models: fleet, site detail, setup wizard, enrollment), tested on Linux
src/Lakeflow.Console/      WinUI 3 desktop app (MSIX), Windows only: windows/Lakeflow.Console.sln
tests/Lakeflow.Testing/   ConnectorContractTests: the suite every connector must pass, plus an in-memory
                          reference source/connector used to prove the suite and to drive agent tests
tests/Lakeflow.Contracts.Tests/
tests/Lakeflow.Connectors.SqlServer.Tests/   contract suite x3 capture methods + SQL Server specifics
tests/Lakeflow.Agent.Tests/      pipeline on simulated time: 24-hour outage gate, buffer cap, failures, commands; SQL Server end-to-end
tests/Lakeflow.ControlApi.Tests/ the API in-process on SQL Server: enrollment, certificate and role checks, config, audit,
                                 credentials, status; the real agent enrolling and running against it
tests/Lakeflow.Console.Core.Tests/ console view models against the real API and a running agent (setup wizard end to end)
fabric/                   bronze-to-silver MERGE and reconciliation: a Python package tested on local Spark + Delta,
                          and the Fabric notebooks generated from it (see fabric/README.md)
```

## Build and test

Requires the .NET 8 SDK. SQL Server and control API tests need Docker; without `MSSQL_TEST_CONNECTION` they are skipped.

```bash
eval "$(scripts/test-sqlserver.sh)"   # starts SQL Server 2022 in Docker, exports MSSQL_TEST_CONNECTION
dotnet test
```

The WinUI console builds on Windows only (`windows/Lakeflow.Console.sln`). GitHub Actions (`.github/workflows/build.yml`)
builds it and the agent on Windows, and runs every test (with SQL Server as a service container) and the Fabric tests.

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
