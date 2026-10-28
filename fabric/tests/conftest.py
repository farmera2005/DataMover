import json
import os
import shutil
import sys
import tempfile
import uuid

import pytest
from delta import configure_spark_with_delta_pip
from pyspark.sql import SparkSession


# Spark's Python workers must run the same Python as the driver.
os.environ.setdefault("PYSPARK_PYTHON", sys.executable)
os.environ.setdefault("PYSPARK_DRIVER_PYTHON", sys.executable)
# Workers import the package too (pandas UDFs).
_SRC = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "src")
os.environ["PYTHONPATH"] = os.pathsep.join(p for p in (_SRC, os.environ.get("PYTHONPATH")) if p)
# Spark 3.5's Arrow needs Java 11 or 17 (Fabric Runtime 1.3 runs 11); use a local 17 or 11 when there is one.
for _java in ("/usr/lib/jvm/java-17-openjdk-amd64", "/usr/lib/jvm/java-11-openjdk-amd64"):
    if os.path.isdir(_java):
        os.environ["JAVA_HOME"] = _java
        break
_ARROW_OPENS = "--add-opens=java.base/java.nio=ALL-UNNAMED -Dio.netty.tryReflectionSetAccessible=true"
if _ARROW_OPENS not in os.environ.get("JAVA_TOOL_OPTIONS", ""):
    os.environ["JAVA_TOOL_OPTIONS"] = (os.environ.get("JAVA_TOOL_OPTIONS", "") + " " + _ARROW_OPENS).strip()


@pytest.fixture(scope="session")
def spark():
    warehouse = tempfile.mkdtemp(prefix="lakeflow-warehouse-")
    builder = (SparkSession.builder.master("local[2]").appName("lakeflow-fabric-tests")
               .config("spark.sql.extensions", "io.delta.sql.DeltaSparkSessionExtension")
               .config("spark.sql.catalog.spark_catalog", "org.apache.spark.sql.delta.catalog.DeltaCatalog")
               .config("spark.sql.warehouse.dir", warehouse)
               .config("spark.driver.memory", "2g")
               .config("spark.sql.shuffle.partitions", "4")
               .config("spark.default.parallelism", "4")
               .config("spark.ui.enabled", "false")
               .config("spark.sql.session.timeZone", "UTC"))
    session = configure_spark_with_delta_pip(builder).getOrCreate()
    session.sparkContext.setLogLevel("ERROR")
    session.sql("CREATE DATABASE IF NOT EXISTS bronze")
    session.sql("CREATE DATABASE IF NOT EXISTS silver")
    yield session
    session.stop()
    shutil.rmtree(warehouse, ignore_errors=True)


@pytest.fixture
def system():
    """A source system name unique to the test, so tests never share tables."""
    return "sys" + uuid.uuid4().hex[:8]


class Agent:
    """Writes envelopes the way the C# agent serializes them (snake_case, key_id = compact key JSON)."""

    def __init__(self, system, site="stmarys", source="compuweigh", table="dbo.Tickets"):
        self.system, self.site, self.source, self.table = system, site, source, table
        self.seq = 0

    def envelope(self, op, key, after=None, seq=None, site=None, source=None):
        if seq is None:
            self.seq += 1
            seq = self.seq
        return {
            "envelope_version": 1, "site_id": site or self.site, "source_id": source or self.source,
            "source_system": self.system, "table": self.table, "op": op, "key": key,
            "key_id": json.dumps(key, separators=(",", ":")), "before": None, "after": after,
            "source_ts": None, "capture_ts": "2026-10-28T12:00:00.0000000+00:00", "seq": seq,
            "partition_key": f"{site or self.site}|{self.table}",
        }

    def row(self, op, id_, name=None, qty=None, **extra):
        after = None if op == "delete" else {"id": id_, "name": name, "qty": qty, **extra}
        return self.envelope(op, {"id": id_}, after)

    def snapshot_complete(self, start_seq):
        return self.envelope("snapshot_complete", {}, {"snapshot_start_seq": start_seq})

    def reconcile(self, row_count, checksum):
        return self.envelope("reconcile", {}, {"row_count": row_count, "key_checksum": str(checksum)})


@pytest.fixture
def agent(system):
    return Agent(system)


def raw_frame(spark, envelopes, as_structs=True):
    """
    Raw rows as Eventstream lands them. With as_structs, the schema is inferred from the JSON (nested objects become
    structs, unioned across every table in the batch), like Eventstream's JSON inference.
    """
    lines = [json.dumps(e) for e in envelopes]
    if as_structs:
        return spark.read.json(spark.sparkContext.parallelize(lines, 2))
    flat = [{**e, "key": json.dumps(e["key"]), "after": None if e["after"] is None else json.dumps(e["after"]),
             "before": None} for e in envelopes]
    schema = ("envelope_version int, site_id string, source_id string, source_system string, table string, op string, "
              "key string, key_id string, before string, after string, source_ts string, capture_ts string, seq long, "
              "partition_key string")
    return spark.createDataFrame(flat, schema)
