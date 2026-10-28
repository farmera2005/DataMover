using Lakeflow.Contracts;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Lakeflow.Connectors.SqlServer.Tests;

public class SqlTextTests
{
    [Theory]
    [InlineData("Tickets", "[Tickets]")]
    [InlineData("odd]name", "[odd]]name]")]
    [InlineData("has space", "[has space]")]
    public void Quotes_like_QUOTENAME(string input, string expected) => Assert.Equal(expected, SqlText.Quote(input));

    [Fact]
    public void Qualifies_tables() => Assert.Equal("[dbo].[Tickets]", SqlText.Table(new TableRef("dbo", "Tickets")));

    [Fact]
    public void Keyset_predicate_orders_columns()
    {
        var values = new[] { new SqlParameter("@a", 1), new SqlParameter("@b", 2), new SqlParameter("@c", 3) };
        Assert.Equal("(([x] > @a) OR ([x] = @a AND [y] > @b) OR ([x] = @a AND [y] = @b AND [z] > @c))",
            SqlText.After(["[x]", "[y]", "[z]"], values));
    }

    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("  select TOP (5) [a;b] FROM [t] WHERE x = 'semi;colon'")]
    public void Allows_single_selects(string sql) => Assert.Equal(sql, SqlText.ReadOnly(sql));

    [Theory]
    [InlineData("UPDATE t SET a = 1")]
    [InlineData("SELECT 1; DELETE t")]
    [InlineData("EXEC sp_who")]
    [InlineData("WITH x AS (SELECT 1) DELETE t")]
    public void Refuses_anything_else(string sql) => Assert.Throws<InvalidOperationException>(() => SqlText.ReadOnly(sql));
}
