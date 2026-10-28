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

from .bronze import DATA_OPS, _Number, dump_exact
from .checksum import checksum_text, key_hash_column
from .names import column_name

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
