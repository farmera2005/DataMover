import pytest

from lakeflow_fabric.checksum import checksum_text, key_checksum, key_hash, key_hash_column
from lakeflow_fabric.names import bronze_table, column_name, identifier, raw_table, silver_table


# The same expectations as LakeNamesTests in Lakeflow.Contracts.Tests (C#).
def test_lake_names_match_the_agent():
    assert bronze_table("grain", "CompuWeigh", "dbo.Tickets") == "bronze.grain_compuweigh_tickets_changes"
    assert silver_table("CompuWeigh", "dbo.Tickets") == "silver.compuweigh_tickets"
    assert silver_table("BetaRaven", "audit.Batch Log") == "silver.betaraven_audit_batch_log"
    assert silver_table("agvantage", "ARCUST") == "silver.agvantage_arcust"
    assert raw_table("grain") == "bronze.grain_events"


@pytest.mark.parametrize("text,expected", [("  WEM / CPM  ", "wem_cpm"), ("Ticket#2", "ticket_2")])
def test_identifiers(text, expected):
    assert identifier(text) == expected


def test_empty_identifier_is_rejected():
    with pytest.raises(ValueError):
        identifier("--")


@pytest.mark.parametrize("source,expected", [("Net Lbs", "Net_Lbs"), ("_rowid", "c_rowid"), ("a.b", "a_b"), ("Moisture%", "Moisture%")])
def test_column_names_are_delta_safe(source, expected):
    assert column_name(source) == expected


# The same vectors as KeyChecksumTests in Lakeflow.Contracts.Tests (C#).
@pytest.mark.parametrize("key,expected", [
    ('{"id":1}', 251236297832090819),
    ('{"Site":"AA","TicketId":12345}', 2725067276086842005),
    ('{"name":"é"}', 3393101986252890546),
])
def test_key_hash_matches_the_agent(key, expected):
    assert key_hash(key) == expected


def test_spark_checksum_matches_python(spark):
    keys = [f'{{"id":{i}}}' for i in range(1, 2001)]
    total = (spark.createDataFrame([(k,) for k in keys], "key_id string")
             .agg({"*": "count"}).first())
    assert total[0] == 2000
    spark_sum = (spark.createDataFrame([(k,) for k in keys], "key_id string")
                 .select(key_hash_column(__import__("pyspark.sql.functions", fromlist=["col"]).col("key_id")).alias("h"))
                 .groupBy().sum("h").first()[0])
    assert checksum_text(spark_sum) == str(key_checksum(keys))
    assert int(checksum_text(spark_sum)) < 2**64
