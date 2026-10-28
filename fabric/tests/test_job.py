import uuid

from lakeflow_fabric.job import run
from lakeflow_fabric.maintenance import compact, purge_tombstones
from lakeflow_fabric.names import silver_table

from conftest import Agent, raw_frame


def _append_raw(spark, bronze, envelopes):
    (raw_frame(spark, envelopes).write.format("delta").mode("append").option("mergeSchema", "true")
     .saveAsTable(f"{bronze}.grain_events"))


def test_scheduled_runs_process_only_new_events_and_survive_new_columns(spark, tmp_path, system):
    bronze = "bronze_" + uuid.uuid4().hex[:6]
    spark.sql(f"CREATE DATABASE {bronze}")
    agent = Agent(system)
    checkpoints = str(tmp_path / "checkpoints")

    _append_raw(spark, bronze, [agent.row("snapshot", i, f"t{i}", "1.0") for i in range(1, 51)] + [agent.snapshot_complete(1)])
    summary = run(spark, checkpoints, divisions=["grain", "feed"], bronze=bronze)
    assert summary["feed"] == "no raw table"
    silver = silver_table(system, "dbo.Tickets")
    assert spark.table(silver).filter("NOT _deleted").count() == 50

    # Nothing new: a run changes nothing and appends nothing to bronze.
    bronze_rows = spark.table(f"{bronze}.grain_{system}_tickets_changes").count()
    run(spark, checkpoints, divisions=["grain"], bronze=bronze)
    assert spark.table(f"{bronze}.grain_{system}_tickets_changes").count() == bronze_rows

    # Eventstream lands rows with a column the raw table has not seen (a source added a column).
    _append_raw(spark, bronze, [agent.row("update", 1, "t1", "2.0", moisture="15.1"), agent.row("delete", 2)])
    run(spark, checkpoints, divisions=["grain"], bronze=bronze)
    current = {r["_key_id"]: r for r in spark.table(silver).filter("NOT _deleted").collect()}
    assert len(current) == 49
    assert current['{"id":1}']["moisture"] == "15.1"
    assert spark.table(f"{bronze}.grain_{system}_tickets_changes").count() == bronze_rows + 2


def test_maintenance_purges_only_old_tombstones_and_compacts(spark, system):
    from lakeflow_fabric.job import process_batch
    agent = Agent(system)
    process_batch(spark, raw_frame(spark, [agent.row("insert", 1), agent.row("insert", 2), agent.row("delete", 1)]), "grain")
    silver = silver_table(system, "dbo.Tickets")
    assert spark.table(silver).filter("_deleted").count() == 1

    assert purge_tombstones(spark)[silver] == 0  # too recent
    spark.sql(f"UPDATE {silver} SET _merged_at = current_timestamp() - INTERVAL 9 DAYS WHERE _deleted")
    assert purge_tombstones(spark)[silver] == 1
    assert [r["_key_id"] for r in spark.table(silver).collect()] == ['{"id":2}']

    done = compact(spark)
    assert silver in done
    assert f"bronze.grain_{system}_tickets_changes" in done
