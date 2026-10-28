using Lakeflow.Contracts;
using Xunit;

namespace Lakeflow.Contracts.Tests;

public class ConfigTests
{
    private static AgentConfig Valid() => new()
    {
        AgentId = "agent-stmarys",
        SiteId = "stmarys",
        ConfigVersion = 3,
        EventHubs =
        [
            new EventHubTarget
            {
                Division = Division.Grain,
                FullyQualifiedNamespace = "lakeflow.servicebus.windows.net",
                HubName = "grain",
                SasToken = "SharedAccessSignature sr=...",
                SasExpiresUtc = DateTimeOffset.UtcNow.AddHours(24),
            },
        ],
        Sources =
        [
            new SourceConfig
            {
                SourceId = "compuweigh",
                SourceSystem = "compuweigh",
                Division = Division.Grain,
                Engine = SourceEngine.SqlServer,
                Connection = new ConnectionSettings { Host = "10.1.2.3", Port = 1433, Database = "GMS", UserName = "lakeflow_ro", CredentialName = "compuweigh" },
                Tables =
                [
                    new TableConfig { Table = new TableRef("dbo", "Tickets"), Method = CaptureMethod.ChangeTracking },
                    new TableConfig { Table = new TableRef("dbo", "Customers"), Method = CaptureMethod.TimestampPolling, WatermarkColumn = "ModifiedAt" },
                ],
            },
        ],
    };

    [Fact]
    public void Valid_config_has_no_errors() => Assert.Empty(ConfigValidator.Validate(Valid()));

    [Fact]
    public void Defaults_match_the_spec()
    {
        var config = Valid();
        Assert.Equal(60, config.ConfigPollSeconds);
        Assert.Equal(30, config.HeartbeatSeconds);
        Assert.Equal(10L * 1024 * 1024 * 1024, config.Buffer.CapBytes);
        Assert.Equal(80, config.Buffer.AlertPercent);
        var table = config.Sources[0].Tables[0];
        Assert.Equal(15, table.LagWarningMinutes);
        Assert.Equal(60, table.LagCriticalMinutes);
        Assert.Equal(5, config.Sources[0].Connection.ConnectTimeoutSeconds);
        Assert.Equal(15, config.Sources[0].Connection.CommandTimeoutSeconds);
    }

    [Fact]
    public void Reports_every_problem()
    {
        var source = Valid().Sources[0];
        var bad = Valid() with
        {
            SiteId = "St Marys",
            Sources =
            [
                source with
                {
                    Connection = source.Connection with { CredentialName = null },
                    Tables =
                    [
                        new TableConfig { Table = new TableRef("dbo", "Tickets"), Method = CaptureMethod.Binlog },
                        new TableConfig { Table = new TableRef("dbo", "tickets"), Method = CaptureMethod.TimestampPolling },
                        new TableConfig { Table = new TableRef("dbo", "Bins"), Method = CaptureMethod.ChangeTracking, LagWarningMinutes = 30, LagCriticalMinutes = 30 },
                    ],
                },
                source,
            ],
        };

        var errors = ConfigValidator.Validate(bad);

        Assert.Contains(errors, e => e.StartsWith("site_id 'St Marys'"));
        Assert.Contains(errors, e => e.Contains("credential_name is required"));
        Assert.Contains(errors, e => e.Contains("Binlog capture is not available for SqlServer"));
        Assert.Contains(errors, e => e.Contains("'dbo.tickets' is listed twice"));
        Assert.Contains(errors, e => e.Contains("timestamp polling needs a watermark_column"));
        Assert.Contains(errors, e => e.Contains("lag_critical_minutes must exceed"));
        Assert.Contains(errors, e => e.Contains("source 'compuweigh' is listed twice"));
    }

    [Fact]
    public void Every_source_division_needs_a_hub()
    {
        var config = Valid();
        var agronomy = config.Sources[0] with { SourceId = "kahler", Division = Division.Agronomy };
        Assert.Contains(ConfigValidator.Validate(config with { Sources = [.. config.Sources, agronomy] }),
            e => e.Contains("no event hub for division Agronomy"));
    }

    [Fact]
    public void Table_commands_need_a_target()
    {
        var config = Valid() with
        {
            Commands = [new AgentCommand { CommandId = Guid.NewGuid(), Type = CommandType.ResnapshotTable, SourceId = "compuweigh", IssuedUtc = DateTimeOffset.UtcNow, IssuedBy = "adam" }],
        };
        Assert.Contains(ConfigValidator.Validate(config), e => e.Contains("ResnapshotTable needs table"));
    }

    [Fact]
    public void Config_round_trips_and_sas_token_is_not_in_ToString()
    {
        var config = Valid();
        var copy = EnvelopeJson.Deserialize<AgentConfig>(EnvelopeJson.Serialize(config));
        Assert.Equal(config.Sources[0].Tables[1].WatermarkColumn, copy.Sources[0].Tables[1].WatermarkColumn);
        Assert.Equal(CaptureMethod.TimestampPolling, copy.Sources[0].Tables[1].Method);
        Assert.Contains("\"engine\":\"sql_server\"", EnvelopeJson.Serialize(config));
        Assert.DoesNotContain("SharedAccessSignature", config.EventHubs[0].ToString());
    }

    [Theory]
    [InlineData(SourceEngine.SqlServer, CaptureMethod.ChangeTracking, true)]
    [InlineData(SourceEngine.SqlServer, CaptureMethod.RowVersion, true)]
    [InlineData(SourceEngine.Db2i, CaptureMethod.Journal, true)]
    [InlineData(SourceEngine.Access, CaptureMethod.TimestampPolling, false)]
    [InlineData(SourceEngine.Files, CaptureMethod.FileWatch, true)]
    public void Capture_methods_fit_engines(SourceEngine engine, CaptureMethod method, bool allowed) =>
        Assert.Equal(allowed, ConfigValidator.IsAllowed(engine, method));
}

public class LakeNamesTests
{
    [Fact]
    public void Bronze_and_silver_names_follow_the_spec()
    {
        Assert.Equal("bronze.grain_compuweigh_tickets_changes", LakeNames.BronzeTable(Division.Grain, "CompuWeigh", new TableRef("dbo", "Tickets")));
        Assert.Equal("silver.compuweigh_tickets", LakeNames.SilverTable("CompuWeigh", new TableRef("dbo", "Tickets")));
        Assert.Equal("silver.betaraven_audit_batch_log", LakeNames.SilverTable("BetaRaven", new TableRef("audit", "Batch Log")));
        Assert.Equal("silver.agvantage_arcust", LakeNames.SilverTable("agvantage", new TableRef(null, "ARCUST")));
    }

    [Fact]
    public void Hub_per_division_and_partition_key()
    {
        Assert.Equal("erp", LakeNames.HubName(Division.Erp));
        Assert.Equal("stmarys|dbo.Tickets", LakeNames.PartitionKey("stmarys", "dbo.Tickets"));
    }

    [Theory]
    [InlineData("  WEM / CPM  ", "wem_cpm")]
    [InlineData("Ticket#2", "ticket_2")]
    public void Identifiers_are_cleaned(string input, string expected) => Assert.Equal(expected, LakeNames.Identifier(input));

    [Fact]
    public void Empty_identifier_is_rejected() => Assert.Throws<ArgumentException>(() => LakeNames.Identifier("--"));

    [Fact]
    public void Table_refs_parse() => Assert.Equal(new TableRef("dbo", "Tickets"), TableRef.Parse("dbo.Tickets"));
}
