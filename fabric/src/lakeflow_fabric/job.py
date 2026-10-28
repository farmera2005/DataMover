"""The scheduled job: each division's raw Eventstream table -> bronze change tables and silver, incrementally."""
import logging

from pyspark.sql import DataFrame, SparkSession
from pyspark.sql import functions as F

from .bronze import append_bronze, normalize
from .names import DIVISIONS, raw_table, silver_table
from .silver import apply_changes

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
