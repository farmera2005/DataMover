"""Raw Eventstream rows -> normalized change rows -> bronze change tables."""
import json

import pandas as pd
from pyspark.sql import DataFrame
from pyspark.sql import functions as F
from pyspark.sql.functions import pandas_udf
from pyspark.sql.types import ArrayType, BinaryType, MapType, StringType, StructField, StructType, TimestampType

from .names import bronze_table

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
