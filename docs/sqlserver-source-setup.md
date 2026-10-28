# Setting up a SQL Server source

The agent only reads. It needs a dedicated login with SELECT on the tables it captures, nothing else.
Run this on the site server as an administrator, with the vendor's agreement (CompuWeigh, WEM/CPM, Kahler).

```sql
-- Choose a strong password; it is entered once in the console and stored only on the agent (DPAPI).
CREATE LOGIN lakeflow_ro WITH PASSWORD = '<password>', CHECK_POLICY = ON;
USE [<database>];
CREATE USER lakeflow_ro FOR LOGIN lakeflow_ro;
GRANT SELECT ON dbo.<Table1> TO lakeflow_ro;          -- one line per captured table
GRANT SELECT ON dbo.<Table2> TO lakeflow_ro;
-- Only once Change Tracking is approved and enabled on a table:
GRANT VIEW CHANGE TRACKING ON dbo.<Table1> TO lakeflow_ro;
```

## Capture methods

| Method | Needs on the source | Deletes | Notes |
| --- | --- | --- | --- |
| Rowversion polling | A `rowversion` column (ideally indexed) | Nightly key reconciliation | Nothing to enable. Waits while any transaction in the database is open, so a long-running transaction shows as lag. |
| Timestamp polling | A modified-date column (ideally indexed) | Nightly key reconciliation | Re-reads a look-back window (default 5 min) each poll; repeats are dropped. Rows whose modified date is never set, or not updated on change, are missed until a resnapshot. |
| Change Tracking | `ALTER DATABASE ... SET CHANGE_TRACKING = ON` and `ALTER TABLE ... ENABLE CHANGE_TRACKING`, **with the vendor's written OK** | Native | Lakeflow never enables it. If retention passes the agent's checkpoint (e.g. after a long outage), the table is resnapshotted. |

The pilot uses polling until the vendor approves Change Tracking. Switching a table's method triggers a resnapshot.

Without an index on the rowversion or modified-date column, every poll scans the table. Check before choosing
the method, and prefer a longer poll interval on large unindexed tables.

## How the agent connects

- Read-only intent, encrypted connection, application name `Lakeflow Agent` (easy to find in `sys.dm_exec_sessions`).
- One connection per source; 5 s connect timeout, 15 s command timeout.
- `LOCK_TIMEOUT 5000`, `DEADLOCK_PRIORITY LOW`, `READ COMMITTED` (rowversion polling reads without shared locks,
  which is safe because it only reads committed rowversions). A blocked read gives up after 5 s and is retried
  later, so the agent never holds up the scale software.
- Every query is a single bounded `SELECT TOP (n)` paged by key or watermark; the code refuses anything else.
