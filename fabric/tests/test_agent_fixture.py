"""
Replays envelopes produced by the real C# agent (tests/data/agent_run.json, written by FabricFixtureTests in
Lakeflow.Agent.Tests) through the notebook code, in uneven batches with redeliveries, and checks silver against the
source's final rows and the reconcile marker's numbers.
"""
import json
from pathlib import Path

import pytest

from lakeflow_fabric.job import process_batch
from lakeflow_fabric.names import silver_table
from lakeflow_fabric.bronze import _Number
from lakeflow_fabric.bronze import dump_exact as _dump
from lakeflow_fabric.silver import _value_text

FIXTURE = Path(__file__).parent / "data" / "agent_run.json"


def load_fixture():
    return json.loads(FIXTURE.read_text(encoding="utf-8"), parse_float=_Number, parse_int=_Number)


def with_system(envelope, system):
    return {**envelope, "source_system": system}


def string_frame(spark, envelopes):
    rows = [{
        "envelope_version": int(e["envelope_version"]), "site_id": e["site_id"], "source_id": e["source_id"],
        "source_system": e["source_system"], "table": e["table"], "op": e["op"], "key": _dump(e["key"]),
        "key_id": e["key_id"], "before": None, "after": None if e["after"] is None else _dump(e["after"]),
        "source_ts": e["source_ts"], "capture_ts": e["capture_ts"], "seq": int(e["seq"]),
    } for e in envelopes]
    schema = ("envelope_version int, site_id string, source_id string, source_system string, table string, op string, "
              "key string, key_id string, before string, after string, source_ts string, capture_ts string, seq long")
    return spark.createDataFrame(rows, schema)


def struct_frame(spark, envelopes):
    # Like Eventstream inferring a schema from the JSON; decimals inferred as decimals keep their digits.
    lines = [_dump(e) for e in envelopes]
    return spark.read.option("prefersDecimal", "true").json(spark.sparkContext.parallelize(lines, 3))


def body_frame(spark, envelopes):
    # The whole envelope as bytes, as Event Hubs Capture or a Kafka reader delivers it.
    return spark.createDataFrame([(bytearray(_dump(e).encode("utf-8")),) for e in envelopes], "Body binary")


@pytest.mark.parametrize("frame", [string_frame, struct_frame, body_frame], ids=["json-text", "inferred-structs", "body"])
def test_agent_run_lands_in_silver_exactly(spark, system, frame):
    fixture = load_fixture()
    envelopes = [with_system(e, system) for e in fixture["envelopes"]]

    # Uneven micro-batches, with a stretch delivered twice (at-least-once) and an old batch replayed at the end.
    batches, start = [], 0
    for size in (7, 150, 3, 40, 1000):
        batches.append(envelopes[start:start + size])
        start += size
    batches.insert(3, envelopes[100:160])
    batches.append(envelopes[:20])
    results = []
    for batch in filter(None, batches):
        results += process_batch(spark, frame(spark, batch), "grain")

    silver = {r["_key_id"]: r.asDict() for r in spark.table(silver_table(system, "dbo.Tickets")).filter("NOT _deleted").collect()}
    expected = fixture["expected"]
    assert sorted(silver) == sorted(expected)
    for key, row in expected.items():
        for column, value in row.items():
            assert silver[key][column] == _value_text(value), f"{key}.{column}"

    reconcile = [r for r in results if r["site_id"] == "stmarys"]
    assert reconcile, "the run's reconcile marker should have been checked"
    assert all(r["status"] == "ok" for r in reconcile), reconcile
