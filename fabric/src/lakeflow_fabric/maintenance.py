"""Housekeeping: purge old tombstones, compact bronze monthly, vacuum."""
from pyspark.sql import SparkSession

from .silver import RECONCILIATION_TABLE

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
