# Fabric notebook source

# METADATA ********************

# META {
# META   "kernel_info": {
# META     "name": "synapse_pyspark"
# META   },
# META   "dependencies": {}
# META }

# CELL ********************

# Settings. Attach the "bronze" lakehouse (default) and the "silver" lakehouse to this notebook.
BRONZE = "bronze"
SILVER = "silver"

# METADATA ********************

# META {
# META   "language": "python",
# META   "language_group": "synapse_pyspark"
# META }

# CELL ********************

# Generated from fabric/src/lakeflow_fabric by fabric/tools/build_notebooks.py: edit the package, not this cell.

# ---- lakeflow_fabric/names.py ----
"""Lake names, the same rules as Lakeflow.Contracts.LakeNames (C#)."""
import re

DIVISIONS = ("grain", "feed", "agronomy", "erp", "other")


def identifier(text: str) -> str:
    """Lower case, letters/digits/underscores only, no repeated or edge underscores."""
    out = []
    for ch in text.strip().lower():
        c = ch if (ch.isascii() and ch.isalnum()) else "_"
        if c == "_" and (not out or out[-1] == "_"):
            continue
        out.append(c)
    ident = "".join(out).rstrip("_")
    if not ident:
        raise ValueError(f"'{text}' has no usable characters for a lake name.")
    return ident


def table_part(table: str) -> str:
    """'dbo.Tickets' -> 'tickets'; any schema other than dbo is kept: 'audit.Log' -> 'audit_log'."""
    schema, dot, name = table.partition(".")
    if not dot:
        schema, name = "", schema
    if not schema or schema.lower() == "dbo":
        return identifier(name)
    return f"{identifier(schema)}_{identifier(name)}"


def raw_table(division: str, bronze: str = "bronze") -> str:
    """Where Eventstream lands one division hub."""
    return f"{bronze}.{identifier(division)}_events"


def bronze_table(division: str, source_system: str, table: str, bronze: str = "bronze") -> str:
    return f"{bronze}.{identifier(division)}_{identifier(source_system)}_{table_part(table)}_changes"


def silver_table(source_system: str, table: str, silver: str = "silver") -> str:
    return f"{silver}.{identifier(source_system)}_{table_part(table)}"


_INVALID_COLUMN = re.compile(r"[ ,;{}()\n\t=.]")


def column_name(source_column: str) -> str:
    """
    A source column as a Delta column without column mapping: characters Delta rejects become '_', and a leading
    '_' gets a 'c' prefix so source columns never collide with Lakeflow's own '_' columns.
    """
    name = _INVALID_COLUMN.sub("_", source_column)
    return "c" + name if name.startswith("_") or not name else name

# ---- lakeflow_fabric/checksum.py ----
"""Key checksum, the same as Lakeflow.Contracts.KeyChecksum (C#)."""
import hashlib

from pyspark.sql import Column
from pyspark.sql import functions as F

MOD = 2**64


def key_hash(key_id: str) -> int:
    """First 8 bytes (big-endian) of SHA-256 of the key's canonical text."""
    return int(hashlib.sha256(key_id.encode("utf-8")).hexdigest()[:16], 16)


def key_checksum(key_ids) -> int:
    """Sum of key hashes mod 2^64: order does not matter."""
    return sum(key_hash(k) for k in key_ids) % MOD


def key_hash_column(key_id: Column) -> Column:
    """key_hash as a Spark expression (decimal(20,0), as values reach 2^64)."""
    return F.conv(F.substring(F.sha2(key_id, 256), 1, 16), 16, 10).cast("decimal(20,0)")


def checksum_text(total) -> str:
    """Sum (any size) reduced mod 2^64, as text like the agent sends it."""
    return str(int(total or 0) % MOD)

# ---- lakeflow_fabric/bronze.py ----
"""Raw Eventstream rows -> normalized change rows -> bronze change tables."""
import json

import pandas as pd
from pyspark.sql import DataFrame
from pyspark.sql import functions as F
from pyspark.sql.functions import pandas_udf
from pyspark.sql.types import ArrayType, BinaryType, MapType, StringType, StructField, StructType, TimestampType


DATA_OPS = ("insert", "update", "delete", "snapshot")
MARKER_OPS = ("snapshot_complete", "reconcile")

# Columns of every bronze change table: the envelope, with key/before/after kept as JSON text so any schema change
# at the source lands without touching the table.
BRONZE_COLUMNS = (
    "site_id", "source_id", "source_system", "table_name", "op", "key_id", "key", "before", "after",
    "source_ts", "capture_ts", "seq", "envelope_version", "enqueued_utc", "capture_date",
)


_BODY_FIELDS = ("envelope_version", "site_id", "source_id", "source_system", "table", "op", "key_id", "key", "before",
                "after", "source_ts", "capture_ts", "seq")
_BODY_SCHEMA = StructType([StructField(f, StringType()) for f in _BODY_FIELDS])


class _Number(str):
    """A JSON number, kept as the exact text it was written as."""


def dump_exact(value) -> str:
    """JSON text with numbers written exactly as they were read (see _Number)."""
    if isinstance(value, _Number):
        return str(value)
    if isinstance(value, dict):
        return "{" + ",".join(f"{json.dumps(k, ensure_ascii=False)}:{dump_exact(v)}" for k, v in value.items()) + "}"
    if isinstance(value, list):
        return "[" + ",".join(dump_exact(v) for v in value) + "]"
    return json.dumps(value, ensure_ascii=False)


def split_body(body) -> dict:
    """One envelope's JSON text -> its fields as text, objects as exact JSON text."""
    if isinstance(body, (bytes, bytearray)):
        body = body.decode("utf-8")
    envelope = json.loads(body, parse_float=_Number, parse_int=_Number, parse_constant=_Number)
    return {f: None if envelope.get(f) is None
            else dump_exact(envelope[f]) if isinstance(envelope[f], (dict, list))
            else str(envelope[f]) for f in _BODY_FIELDS}


@pandas_udf(_BODY_SCHEMA)
def _split_bodies(bodies: pd.Series) -> pd.DataFrame:
    return pd.DataFrame([split_body(b) for b in bodies], columns=list(_BODY_FIELDS))


def _from_body(raw: DataFrame, body_column: str) -> DataFrame:
    """Raw rows that carry the whole envelope as text or bytes (Event Hubs Capture, a Kafka reader): parsed exactly."""
    parts = raw.withColumn("_e", _split_bodies(F.col(body_column)))
    return parts.select(*[F.col(f"_e.{f}").alias(f) for f in _BODY_FIELDS],
                        *[c for c in raw.columns if c == "EventEnqueuedUtcTime"])


def normalize(raw: DataFrame) -> DataFrame:
    """
    Accepts the envelope as Eventstream lands it, whether nested objects arrive as JSON text or as structs (re-encoded
    to JSON; null fields drop out, which downstream reads as null anyway), or as one `body` column holding the whole
    envelope (text or bytes), and returns typed change rows.

    Exactness: with JSON text or a body, numbers keep their exact text. If Eventstream infers JSON numbers as doubles,
    values with more than ~15 significant digits lose digits before this code sees them; check this in the pilot and
    land bodies instead if it happens (see fabric/README.md).
    """
    body = next((c for c in raw.columns if c.lower() == "body"), None)
    if body is not None and "site_id" not in raw.columns:
        raw = _from_body(raw, body)
    types = {f.name: f.dataType for f in raw.schema.fields}

    def json_text(name):
        if name not in types:
            return F.lit(None).cast(StringType())
        if isinstance(types[name], (StructType, MapType, ArrayType)):
            return F.to_json(F.col(name))
        return F.col(name).cast(StringType())

    def timestamp(name):
        if name not in types:
            return F.lit(None).cast(TimestampType())
        if isinstance(types[name], TimestampType):
            return F.col(name)
        return F.to_timestamp(F.col(name))

    enqueued = timestamp("EventEnqueuedUtcTime") if "EventEnqueuedUtcTime" in types else timestamp("capture_ts")
    return raw.select(
        F.col("site_id").cast("string").alias("site_id"),
        F.col("source_id").cast("string").alias("source_id"),
        F.col("source_system").cast("string").alias("source_system"),
        F.col("table").cast("string").alias("table_name"),
        F.col("op").cast("string").alias("op"),
        F.col("key_id").cast("string").alias("key_id"),
        json_text("key").alias("key"),
        json_text("before").alias("before"),
        json_text("after").alias("after"),
        timestamp("source_ts").alias("source_ts"),
        timestamp("capture_ts").alias("capture_ts"),
        F.col("seq").cast("long").alias("seq"),
        (F.col("envelope_version").cast("int") if "envelope_version" in types else F.lit(1)).alias("envelope_version"),
        enqueued.alias("enqueued_utc"),
    ).withColumn("capture_date", F.to_date("capture_ts"))


def append_bronze(changes: DataFrame, division: str, source_system: str, table: str, bronze: str = "bronze") -> str:
    """Appends every change row, full history, partitioned by capture date."""
    name = bronze_table(division, source_system, table, bronze)
    (changes.select(*BRONZE_COLUMNS).write.format("delta").mode("append")
        .option("mergeSchema", "true").partitionBy("capture_date").saveAsTable(name))
    return name

# ---- lakeflow_fabric/silver.py ----
"""
Changes -> silver current-state tables.

One silver table per source system table, shared by every site: rows are matched on (_site_id, _source_id, _key_id)
and a change applies only if its seq is higher than the row's, so replays and duplicates (delivery is at-least-once)
change nothing. Deletes leave a tombstone (_deleted = true) so an older change replayed later cannot bring the row
back; the maintenance notebook purges tombstones once no replay can still arrive. Readers filter _deleted = false.

Source columns are kept as text, exactly as the agent encoded them (numbers exact, ISO dates): typing is left to the
consumers, so a retyped column at the source never breaks the table.
"""
import json
from datetime import datetime, timezone

import pandas as pd

from delta.tables import DeltaTable
from pyspark.sql import DataFrame, SparkSession, Window
from pyspark.sql import functions as F
from pyspark.sql.functions import pandas_udf
from pyspark.sql.types import MapType, StringType


META_COLUMNS = ("_site_id", "_source_id", "_key_id", "_seq", "_op", "_deleted", "_capture_ts", "_source_ts", "_merged_at")
RECONCILIATION_TABLE = "lakeflow_reconciliation"
MISMATCH_TOLERANCE_PCT = 0.1


def _value_text(value):
    if value is None:
        return None
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, (dict, list)):
        return dump_exact(value)
    return str(value)


def exact_text_json(text: str | None) -> str | None:
    """
    Re-encodes a JSON object with every value as a string, numbers keeping their exact original text. Spark's own
    JSON functions read numbers as doubles (123456789012.3456 becomes 1.234567890123456E11), which would not do for
    weights and money.
    """
    if text is None:
        return None
    obj = json.loads(text, parse_float=_Number, parse_int=_Number, parse_constant=_Number)
    return json.dumps({k: _value_text(v) for k, v in obj.items()}, ensure_ascii=False)


@pandas_udf(StringType())
def _exact_text_json(texts: pd.Series) -> pd.Series:
    return texts.map(exact_text_json)


def _table_exists(spark: SparkSession, name: str) -> bool:
    return spark.catalog.tableExists(name)


def _latest_per_key(rows: DataFrame) -> DataFrame:
    """Within one batch, only the highest seq per key matters."""
    window = Window.partitionBy("site_id", "source_id", "key_id").orderBy(F.col("seq").desc())
    return rows.withColumn("_rank", F.row_number().over(window)).filter("_rank = 1").drop("_rank")


def _flatten(rows: DataFrame) -> tuple[DataFrame, list[str]]:
    """Spreads each change's after-image into one text column per source column."""
    with_map = rows.withColumn("_after", F.from_json(_exact_text_json("after"), MapType(StringType(), StringType())))
    source_columns = sorted(r[0] for r in with_map.select(F.explode(F.map_keys("_after"))).distinct().collect())
    names: dict[str, str] = {}
    used: set[str] = set()
    for source in source_columns:
        name = column_name(source)
        base, n = name, 2
        while name.lower() in used:  # Spark column names are case-insensitive
            name, n = f"{base}_{n}", n + 1
        used.add(name.lower())
        names[source] = name
    flat = with_map.select(
        F.col("site_id").alias("_site_id"),
        F.col("source_id").alias("_source_id"),
        F.col("key_id").alias("_key_id"),
        F.col("seq").alias("_seq"),
        F.col("op").alias("_op"),
        (F.col("op") == "delete").alias("_deleted"),
        F.col("capture_ts").alias("_capture_ts"),
        F.col("source_ts").alias("_source_ts"),
        F.current_timestamp().alias("_merged_at"),
        *[F.col("_after").getItem(source).alias(name) for source, name in names.items()],
    )
    return flat, list(names.values())


def merge_rows(spark: SparkSession, rows: DataFrame, silver_name: str) -> None:
    """Applies data changes (insert/update/snapshot/delete) to a silver table, creating or widening it as needed."""
    rows = rows.filter(F.col("op").isin(*DATA_OPS))
    if rows.isEmpty():
        return
    flat, columns = _flatten(_latest_per_key(rows))

    if not _table_exists(spark, silver_name):
        (flat.write.format("delta").option("delta.enableChangeDataFeed", "true").saveAsTable(silver_name))
        return

    target_columns = [f.name for f in spark.table(silver_name).schema.fields]
    existing = {c.lower() for c in target_columns}
    new = [c for c in columns if c.lower() not in existing]
    if new:
        spark.sql(f"ALTER TABLE {silver_name} ADD COLUMNS ({', '.join(f'`{c}` STRING' for c in new)})")
    data_columns = [c for c in target_columns if c not in META_COLUMNS] + new
    present = {c.lower() for c in columns}
    # An after-image is the whole row: a column missing from it is null now (or did not exist yet), never "unchanged".
    source = flat.select(
        *META_COLUMNS,
        *[F.col(f"`{c}`") if c.lower() in present else F.lit(None).cast(StringType()).alias(c) for c in data_columns],
    )
    every = {c: f"s.`{c}`" for c in (*META_COLUMNS, *data_columns)}
    tombstone = {c: f"s.`{c}`" for c in ("_seq", "_op", "_deleted", "_capture_ts", "_source_ts", "_merged_at")}
    (DeltaTable.forName(spark, silver_name).alias("t")
        .merge(source.alias("s"), "t._site_id = s._site_id AND t._source_id = s._source_id AND t._key_id = s._key_id")
        .whenMatchedUpdate(condition="s._seq > t._seq AND s._deleted", set=tombstone)
        .whenMatchedUpdate(condition="s._seq > t._seq AND NOT s._deleted", set=every)
        .whenNotMatchedInsert(values=every)
        .execute())


def apply_snapshot_complete(spark: SparkSession, silver_name: str, site_id: str, source_id: str, start_seq: int) -> None:
    """Rows of the site not re-sent since the snapshot started no longer exist at the source."""
    if not _table_exists(spark, silver_name):
        return
    DeltaTable.forName(spark, silver_name).update(
        condition=(F.col("_site_id") == site_id) & (F.col("_source_id") == source_id)
        & (F.col("_seq") < start_seq) & ~F.col("_deleted"),
        set={"_deleted": F.lit(True), "_op": F.lit("delete"), "_merged_at": F.current_timestamp()},
    )


def silver_counts(spark: SparkSession, silver_name: str, site_id: str, source_id: str) -> tuple[int, str]:
    """Live rows and key checksum of one site's slice of a silver table."""
    if not _table_exists(spark, silver_name):
        return 0, "0"
    row = (spark.table(silver_name)
           .filter((F.col("_site_id") == site_id) & (F.col("_source_id") == source_id) & ~F.col("_deleted"))
           .agg(F.count(F.lit(1)).alias("n"), F.sum(key_hash_column(F.col("_key_id"))).alias("total"))
           .first())
    return int(row["n"]), checksum_text(row["total"])


def record_reconciliation(spark: SparkSession, silver_db: str, silver_name: str, marker, silver_rows: int, silver_checksum: str) -> dict:
    after = marker["after"]
    source_rows = int(after["row_count"])
    source_checksum = str(after["key_checksum"])
    diff = silver_rows - source_rows
    pct = 0.0 if source_rows == 0 and silver_rows == 0 else abs(diff) * 100.0 / max(source_rows, 1)
    status = ("ok" if diff == 0 and silver_checksum == source_checksum
              else "within_tolerance" if pct <= MISMATCH_TOLERANCE_PCT
              else "mismatch")
    result = {
        "checked_at": datetime.now(timezone.utc).replace(tzinfo=None),
        "site_id": marker["site_id"], "source_id": marker["source_id"], "source_system": marker["source_system"],
        "table_name": marker["table_name"], "silver_table": silver_name, "as_of_seq": int(marker["seq"]),
        "capture_ts": marker["capture_ts"], "source_rows": source_rows, "silver_rows": silver_rows,
        "source_checksum": source_checksum, "silver_checksum": silver_checksum, "row_diff": diff,
        "mismatch_pct": float(pct), "status": status,
    }
    schema = ("checked_at timestamp, site_id string, source_id string, source_system string, table_name string, "
              "silver_table string, as_of_seq long, capture_ts timestamp, source_rows long, silver_rows long, "
              "source_checksum string, silver_checksum string, row_diff long, mismatch_pct double, status string")
    spark.createDataFrame([result], schema).write.format("delta").mode("append").saveAsTable(f"{silver_db}.{RECONCILIATION_TABLE}")
    return result


def apply_changes(spark: SparkSession, changes: DataFrame, silver_name: str, silver_db: str = "silver") -> list[dict]:
    """
    Applies one table's changes from one micro-batch in seq order around its markers: for each marker, the changes
    before it (for that site and source) are merged first, then the marker is applied, so a reconcile marker compares
    silver with the source at exactly the point in the stream where the agent counted. Returns reconciliation results.
    """
    markers = (changes.filter(F.col("op").isin("snapshot_complete", "reconcile"))
               .withColumn("after", F.from_json("after", MapType(StringType(), StringType())))
               .orderBy("seq").collect())
    data = changes.filter(F.col("op").isin(*DATA_OPS))
    if not markers:
        merge_rows(spark, data, silver_name)
        return []

    results = []
    merged_up_to: dict[tuple[str, str], int] = {}
    for marker in markers:
        site_source = (marker["site_id"], marker["source_id"])
        lower = merged_up_to.get(site_source, -1)
        merge_rows(spark, data.filter(
            (F.col("site_id") == site_source[0]) & (F.col("source_id") == site_source[1])
            & (F.col("seq") > lower) & (F.col("seq") < marker["seq"])), silver_name)
        merged_up_to[site_source] = marker["seq"]
        if marker["op"] == "snapshot_complete":
            apply_snapshot_complete(spark, silver_name, *site_source, int(marker["after"]["snapshot_start_seq"]))
        else:
            rows, checksum = silver_counts(spark, silver_name, *site_source)
            results.append(record_reconciliation(spark, silver_db, silver_name, marker, rows, checksum))

    # Everything after the last marker of each site and source, and every other site and source.
    bounds = spark.createDataFrame([(s, src, seq) for (s, src), seq in merged_up_to.items()],
                                   "b_site string, b_source string, b_seq long")
    rest = (data.join(bounds, (F.col("site_id") == F.col("b_site")) & (F.col("source_id") == F.col("b_source")), "left")
            .filter(F.col("b_seq").isNull() | (F.col("seq") > F.col("b_seq")))
            .drop("b_site", "b_source", "b_seq"))
    merge_rows(spark, rest, silver_name)
    return results

# ---- lakeflow_fabric/job.py ----
"""The scheduled job: each division's raw Eventstream table -> bronze change tables and silver, incrementally."""
import logging

from pyspark.sql import DataFrame, SparkSession
from pyspark.sql import functions as F


log = logging.getLogger("lakeflow")


def process_batch(spark: SparkSession, raw: DataFrame, division: str, bronze: str = "bronze", silver: str = "silver") -> list[dict]:
    """One micro-batch of raw events: append to bronze, then apply to silver, table by table."""
    changes = normalize(raw).persist()
    try:
        results = []
        tables = changes.select("source_system", "table_name").distinct().collect()
        for t in tables:
            subset = changes.filter((F.col("source_system") == t["source_system"]) & (F.col("table_name") == t["table_name"]))
            append_bronze(subset, division, t["source_system"], t["table_name"], bronze)
            results += apply_changes(spark, subset, silver_table(t["source_system"], t["table_name"], silver), silver)
        return results
    finally:
        changes.unpersist()


def run(spark: SparkSession, checkpoint_root: str, divisions=DIVISIONS, bronze: str = "bronze", silver: str = "silver") -> dict:
    """
    Processes everything new in each division's raw table, then stops (Structured Streaming, availableNow): run it on a
    schedule. The checkpoint makes each raw row processed once per run history; a rerun after a failure repeats at most
    one micro-batch, which the seq rules make harmless.
    """
    spark.conf.set("spark.databricks.delta.schema.autoMerge.enabled", "false")
    summary = {}
    for division in divisions:
        source = raw_table(division, bronze)
        if not spark.catalog.tableExists(source):
            summary[division] = "no raw table"
            continue
        results: list[dict] = []
        query = (spark.readStream.format("delta").option("maxFilesPerTrigger", 200).table(source)
                 .writeStream
                 .foreachBatch(lambda df, _id, d=division: results.extend(process_batch(spark, df, d, bronze, silver)))
                 .option("checkpointLocation", f"{checkpoint_root.rstrip('/')}/{division}")
                 .trigger(availableNow=True)
                 .start())
        query.awaitTermination()
        mismatches = [r for r in results if r["status"] == "mismatch"]
        summary[division] = {"reconciled": len(results), "mismatches": len(mismatches)}
        for r in mismatches:
            log.warning("Reconciliation mismatch %s/%s %s: source %s rows, silver %s rows",
                        r["site_id"], r["source_id"], r["table_name"], r["source_rows"], r["silver_rows"])
    return summary

# ---- lakeflow_fabric/maintenance.py ----
"""Housekeeping: purge old tombstones, compact bronze monthly, vacuum."""
from pyspark.sql import SparkSession


# Replays reach silver at most 7 days late (Event Hubs retention); after that a tombstone has done its job.
TOMBSTONE_DAYS = 8


def purge_tombstones(spark: SparkSession, silver: str = "silver", days: int = TOMBSTONE_DAYS) -> dict:
    purged = {}
    for t in spark.catalog.listTables(silver):
        if t.name == RECONCILIATION_TABLE:
            continue
        name = f"{silver}.{t.name}"
        if "_deleted" not in spark.table(name).columns:
            continue
        before = spark.table(name).filter("_deleted").count()
        spark.sql(f"DELETE FROM {name} WHERE _deleted AND _merged_at < current_timestamp() - INTERVAL {int(days)} DAYS")
        purged[name] = before - spark.table(name).filter("_deleted").count()
    return purged


def compact(spark: SparkSession, bronze: str = "bronze", silver: str = "silver", vacuum_hours: int = 168) -> list[str]:
    """OPTIMIZE and VACUUM every bronze change table and silver table."""
    done = []
    for db, keep in ((bronze, lambda n: n.endswith("_changes")), (silver, lambda n: True)):
        for t in spark.catalog.listTables(db):
            if not keep(t.name):
                continue
            name = f"{db}.{t.name}"
            spark.sql(f"OPTIMIZE {name}")
            spark.sql(f"VACUUM {name} RETAIN {int(vacuum_hours)} HOURS")
            done.append(name)
    return done

# METADATA ********************

# META {
# META   "language": "python",
# META   "language_group": "synapse_pyspark"
# META }

# CELL ********************

print("tombstones purged:", purge_tombstones(spark, SILVER))
print("compacted:", compact(spark, BRONZE, SILVER))

# METADATA ********************

# META {
# META   "language": "python",
# META   "language_group": "synapse_pyspark"
# META }
