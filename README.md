# Lakeflow: site-to-lake data flow

Replicates Mercer Landmark site databases into Microsoft Fabric OneLake. The design is in [SPEC.md](SPEC.md).

## Status

Phase 1 (pilot: one grain site, SQL Server), built step by step:

- [x] 1. Contracts: change envelope, connector interface, config schema, connector contract test suite
- [x] 2. SQL Server connector (Change Tracking, rowversion polling, timestamp polling), integration tests against SQL Server in Docker
- [ ] 3. Agent pipeline: SQLite buffer, checkpoints, Event Hubs sender, simulated 24-hour outage
- [ ] 4. Control API: enroll, config, heartbeat, commands
- [ ] 5. Fabric notebooks: bronze-to-silver MERGE, nightly reconciliation
- [ ] 6. Console: sign-in, fleet overview, site detail, source setup wizard
- [ ] 7. Bicep infrastructure and the agent MSI

## Layout

```
src/Lakeflow.Contracts/   envelope, RowChange/ChangeBatch, Checkpoint, ISourceConnector, config DTOs,
                          heartbeat/commands/events, ColumnValues (one value encoding for all connectors),
                          SchemaComparer (drift), ConfigValidator, LakeNames (hub/bronze/silver names)
src/Lakeflow.Connectors.SqlServer/
                          SQL Server connector: Change Tracking, rowversion and timestamp polling;
                          see docs/sqlserver-source-setup.md
tests/Lakeflow.Testing/   ConnectorContractTests: the suite every connector must pass, plus an in-memory
                          reference source/connector used to prove the suite and to drive agent tests
tests/Lakeflow.Contracts.Tests/
tests/Lakeflow.Connectors.SqlServer.Tests/   contract suite x3 capture methods + SQL Server specifics
```

## Build and test

Requires the .NET 8 SDK. SQL Server integration tests need Docker; without `MSSQL_TEST_CONNECTION` they are skipped.

```bash
eval "$(scripts/test-sqlserver.sh)"   # starts SQL Server 2022 in Docker, exports MSSQL_TEST_CONNECTION
dotnet test
```

## Connector contract

Each connector + capture method gets a subclass of `ConnectorContractTests` with an `IContractTestBed` that can
write to a real source. The suite checks: schema and key discovery, bounded snapshot batches, inserts/updates,
deletes (natively, or by key reconciliation through `IKeyReader` for polling methods), changes made during a
snapshot, resuming an interrupted snapshot, resuming incremental capture after a restart (checkpoint round-tripped
through JSON), an added column captured automatically, and dropped/retyped columns reported as breaking.
