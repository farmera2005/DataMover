# Lakeflow on Fabric

Turns the change envelopes agents send into bronze history and silver current-state tables, and checks silver
against the sources every night.

```
Event Hubs (one hub per division)
  └─ Eventstream ──► bronze.<division>_events            raw envelopes, one table per hub
                      └─ lakeflow_bronze_to_silver (every 5 min)
                           ├─► bronze.<division>_<source>_<table>_changes   every change, full history, by capture_date
                           ├─► silver.<source>_<table>                      current state per site + key
                           └─► silver.lakeflow_reconciliation               nightly source-vs-silver results
lakeflow_maintenance (monthly): purge old tombstones, OPTIMIZE, VACUUM
```

## Setting up (once per workspace)

Step by step, with the Eventstream and console settings: [Connecting to Microsoft Fabric](../README.md#connecting-to-microsoft-fabric) in the main README. In short:

1. Two lakehouses: `bronze` and `silver`.
2. One Eventstream per division hub (`grain`, `feed`, `agronomy`, `erp`, `other`; only the ones in use) with
   source = the hub (consumer group `fabric`, shared access key `fabric-listen`; both created by `infra/main.bicep`), input format JSON, and destination = lakehouse `bronze`,
   table `<division>_events`. One route per hub: new sites and tables need no Eventstream changes.
3. Import the notebooks in `notebooks/` (Fabric Git integration picks up the `*.Notebook` folders). Attach `bronze`
   as the default lakehouse and `silver` as a second one to both.
4. Schedule `lakeflow_bronze_to_silver` every 5 minutes and `lakeflow_maintenance` monthly.
5. For the console's reconciliation view, share the `silver` lakehouse ("Read all SQL endpoint data") with a service
   principal and enter its SQL analytics endpoint, client ID and secret in the console (Configuration, Destinations).

## Silver tables

- One per source system table, shared by every site. Each row is one key at one site: `_site_id`, `_source_id`,
  `_key_id` (the key as canonical JSON), then one column per source column, then `_seq`, `_op`, `_capture_ts`,
  `_source_ts`, `_merged_at` and `_deleted`.
- **Filter `_deleted = false`.** Deleted rows stay as tombstones for 8 days, so a change replayed late (delivery is
  at-least-once; Event Hubs keeps 7 days) can never bring a deleted row back. Maintenance then removes them.
- A change applies only when its `seq` is higher than the row's, so duplicates and replays change nothing.
- Source columns are text, exactly as the agent encoded them: numbers keep every digit (`123456789012.3456`), dates
  are ISO 8601, binary is base64. Cast in the semantic model or Tableau. A retyped column at the source therefore
  never breaks the table, and a new column is added automatically (older rows read it as null).
- Column names are made Delta-safe: spaces and `,;{}()=.` become `_`, and a leading `_` gets a `c` prefix.
- After a resnapshot the agent sends `snapshot_complete`: rows of that site not re-sent since the snapshot began are
  marked deleted (they were deleted while the table was not tracked).

## Reconciliation

Every night (02:30 site time) the agent counts each table's rows and checksums its keys at the source, and sends a
`reconcile` marker. The notebook applies every change before the marker, compares silver at that exact point, and
appends the result to `silver.lakeflow_reconciliation`: `ok`, `within_tolerance` (counts within 0.1%, or keys differ
with equal counts) or `mismatch` (more than 0.1%, which turns the site amber in the console). The key checksum is
the sum, mod 2^64, of the first 8 bytes of SHA-256 of each `_key_id`; the agent and the notebook share test vectors.

## Check in the pilot: does Eventstream keep numbers exact?

If Eventstream infers JSON numbers as doubles, values with more than about 15 significant digits lose digits before
the notebook sees them (`123456789013.3456` would land as `1.234567890133456E11`). Text and structs inferred as
decimals are exact; the tests cover both. In the pilot, compare a `decimal(18,4)` column end to end. If it is not
exact, land the envelope whole instead (Event Hubs Capture, or a raw `body` column): the notebook reads a `body`
column (text or bytes) exactly, with no other change.

## Developing

The notebooks are generated from `src/lakeflow_fabric`; edit the package, then:

```bash
python fabric/tools/build_notebooks.py
```

Tests run the real code on local Spark 3.5 + Delta 3.2 (Fabric Runtime 1.3) and need Java 11 or 17:

```bash
python3.11 -m venv .venv && .venv/bin/pip install -e 'fabric[test]' pandas pyarrow
.venv/bin/python -m pytest fabric
```

`tests/data/agent_run.json` holds envelopes produced by the real agent (`FabricFixtureTests` in
`Lakeflow.Agent.Tests`, which fails if the agent's output drifts from it); the Fabric tests replay it.
