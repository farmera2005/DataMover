import json

from lakeflow_fabric.checksum import key_checksum
from lakeflow_fabric.job import process_batch
from lakeflow_fabric.names import bronze_table, silver_table

from conftest import Agent, raw_frame


def live(spark, system, table="dbo.Tickets"):
    rows = spark.table(silver_table(system, table)).filter("NOT _deleted").collect()
    return {(r["_site_id"], r["_key_id"]): r.asDict() for r in rows}


def run(spark, envelopes, as_structs=True):
    return process_batch(spark, raw_frame(spark, envelopes, as_structs), "grain")


def test_inserts_updates_and_deletes_land_as_current_state(spark, agent, system):
    run(spark, [agent.row("snapshot", 1, "a", "1.5"), agent.row("snapshot", 2, "b", "2"), agent.snapshot_complete(1)])
    run(spark, [agent.row("update", 1, "a2", "1.75"), agent.row("insert", 3, "c", "3"), agent.row("delete", 2)])

    rows = live(spark, system)
    assert sorted(k for _, k in rows) == ['{"id":1}', '{"id":3}']
    assert rows[("stmarys", '{"id":1}')]["name"] == "a2"
    assert rows[("stmarys", '{"id":1}')]["qty"] == "1.75"
    assert rows[("stmarys", '{"id":1}')]["_seq"] == 4


def test_numbers_and_text_arrive_exactly_as_the_agent_encoded_them(spark, agent, system):
    run(spark, [agent.row("insert", 1, "é ünïcode", 123456789012.3456)], as_structs=False)
    row = live(spark, system)[("stmarys", '{"id":1}')]
    assert row["qty"] == "123456789012.3456"
    assert row["name"] == "é ünïcode"
    assert row["id"] == "1"


def test_replays_and_out_of_order_duplicates_change_nothing(spark, agent, system):
    first = [agent.row("insert", 1, "a"), agent.row("update", 1, "b"), agent.row("delete", 1), agent.row("insert", 2, "x")]
    run(spark, first)
    # At-least-once: the whole batch again, then the old insert alone (after its delete).
    run(spark, first)
    run(spark, [first[0]])
    rows = live(spark, system)
    assert list(rows) == [("stmarys", '{"id":2}')]
    tombstone = spark.table(silver_table(system, "dbo.Tickets")).filter("_key_id = '{\"id\":1}'").first()
    assert tombstone["_deleted"] and tombstone["_seq"] == 3


def test_several_changes_to_one_key_in_a_batch_keep_the_highest_seq(spark, agent, system):
    run(spark, [agent.row("insert", 1, "first"), agent.row("update", 1, "second"), agent.row("update", 1, "third")])
    assert live(spark, system)[("stmarys", '{"id":1}')]["name"] == "third"


def test_sites_share_a_silver_table_without_mixing(spark, system):
    a = Agent(system, site="stmarys")
    b = Agent(system, site="celina")
    run(spark, [a.row("insert", 1, "stmarys"), b.row("insert", 1, "celina"), b.row("delete", 1)])
    rows = live(spark, system)
    assert list(rows) == [("stmarys", '{"id":1}')]


def test_new_columns_widen_the_table_and_missing_ones_read_as_null(spark, agent, system):
    run(spark, [agent.row("insert", 1, "a", "1"), agent.row("insert", 2, "b", "2")])
    run(spark, [agent.row("update", 1, "a", None, moisture="14.5")])
    # Row 2's update lacks qty (null at the source): it must not keep its old value.
    run(spark, [agent.envelope("update", {"id": 2}, {"id": 2, "name": "b2"})])

    rows = live(spark, system)
    assert rows[("stmarys", '{"id":1}')]["moisture"] == "14.5"
    assert rows[("stmarys", '{"id":1}')]["qty"] is None
    assert rows[("stmarys", '{"id":2}')]["qty"] is None
    assert rows[("stmarys", '{"id":2}')]["moisture"] is None


def test_awkward_source_column_names_become_safe_columns(spark, agent, system):
    run(spark, [agent.envelope("insert", {"id": 1}, {"id": 1, "Net Lbs": 52000, "_rowid": 7, "NAME": "x", "name": "y"})])
    row = live(spark, system)[("stmarys", '{"id":1}')]
    assert row["Net_Lbs"] == "52000"
    assert row["c_rowid"] == "7"
    assert {row["NAME"], row["name_2"]} == {"x", "y"}


def test_snapshot_complete_removes_rows_deleted_while_untracked(spark, agent, system):
    run(spark, [agent.row("snapshot", i, f"t{i}") for i in range(1, 6)] + [agent.snapshot_complete(1)])
    # Resnapshot (e.g. Change Tracking retention lost): rows 2 and 4 were deleted meanwhile and are not re-sent.
    start = agent.seq + 1
    resnapshot = [agent.row("snapshot", i, f"t{i}") for i in (1, 3, 5)] + [agent.snapshot_complete(start)]
    other_site = Agent(system, site="celina")
    run(spark, resnapshot + [other_site.row("insert", 2, "other site keeps its row")])

    rows = live(spark, system)
    assert sorted(k for s, k in rows if s == "stmarys") == ['{"id":1}', '{"id":3}', '{"id":5}']
    assert ("celina", '{"id":2}') in rows


def test_reconcile_marker_compares_silver_at_exactly_its_place_in_the_stream(spark, agent, system):
    rows = [agent.row("insert", i, f"t{i}") for i in range(1, 11)] + [agent.row("delete", 4)]
    keys = [f'{{"id":{i}}}' for i in range(1, 11) if i != 4]
    marker = agent.reconcile(9, key_checksum(keys))
    # Changes after the marker in the same batch must not count.
    later = [agent.row("insert", 11, "after"), agent.row("delete", 1)]

    results = run(spark, rows + [marker] + later)

    assert len(results) == 1
    r = results[0]
    assert (r["source_rows"], r["silver_rows"], r["status"]) == (9, 9, "ok")
    assert r["silver_checksum"] == r["source_checksum"]
    assert len(live(spark, system)) == 9  # 11 added, 1 deleted after the marker
    stored = spark.table("silver.lakeflow_reconciliation").filter(f"source_system = '{system}'").collect()
    assert [s["status"] for s in stored] == ["ok"]


def test_reconcile_reports_mismatches(spark, agent, system):
    run(spark, [agent.row("insert", i) for i in range(1, 1001)])
    keys = [f'{{"id":{i}}}' for i in range(1, 1001)]
    near = run(spark, [agent.reconcile(1001, key_checksum(keys + ['{"id":5000}']))])[0]
    assert (near["row_diff"], near["status"]) == (-1, "within_tolerance")
    far = run(spark, [agent.reconcile(1100, key_checksum(keys))])[0]
    assert far["status"] == "mismatch"
    assert abs(far["mismatch_pct"] - 100 * 100 / 1100) < 1e-9
    same_count = run(spark, [agent.reconcile(1000, key_checksum(keys[:-1] + ['{"id":9999}']))])[0]
    assert (same_count["row_diff"], same_count["status"]) == (0, "within_tolerance")


def test_bronze_keeps_every_change_as_sent(spark, agent, system):
    envelopes = [agent.row("insert", 1, "a"), agent.row("update", 1, "b"), agent.snapshot_complete(1)]
    run(spark, envelopes)
    run(spark, envelopes[:1])  # a duplicate delivery is kept in bronze; history is history
    bronze = spark.table(bronze_table("grain", system, "dbo.Tickets"))
    assert bronze.count() == 4
    assert {r["op"] for r in bronze.collect()} == {"insert", "update", "snapshot_complete"}
    first = bronze.filter("seq = 2").first()
    assert first["after"] == '{"id":1,"name":"b"}'
    assert str(first["capture_date"]) == "2026-10-28"
    assert set(bronze.columns) >= {"site_id", "source_id", "source_system", "table_name", "op", "key_id", "key", "after", "seq", "capture_ts"}


def test_exact_text_json_keeps_numbers_as_written():
    from lakeflow_fabric.silver import exact_text_json
    out = json.loads(exact_text_json('{"a":123456789012.3456,"b":12345678901234567890123,"c":"x","d":null,"e":true,"f":0.10,"g":{"h":1.50}}'))
    assert out == {"a": "123456789012.3456", "b": "12345678901234567890123", "c": "x", "d": None, "e": "true", "f": "0.10", "g": '{"h":1.50}'}
