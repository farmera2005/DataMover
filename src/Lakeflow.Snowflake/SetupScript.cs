namespace Lakeflow.Snowflake;

/// <summary>
/// The one-time script a Snowflake administrator runs (Snowsight, a SQL worksheet, as ACCOUNTADMIN): a role that owns
/// the Lakeflow database, an extra-small warehouse that suspends after a minute, a service user that signs in only
/// with Lakeflow's public key, and a reader role for analysts. Lakeflow creates its own schemas and tables.
/// </summary>
public static class SetupScript
{
    public static string For(SnowflakeOptions o, string publicKey)
    {
        string Id(string name) => SnowflakeOptions.IsValidIdentifier(name) ? name.ToUpperInvariant() : Sql.Ident(name);
        var (role, user, warehouse, database) = (Id(o.Role), Id(o.User), Id(o.Warehouse), Id(o.Database));
        var reader = SnowflakeOptions.IsValidIdentifier(o.Database) ? $"{o.Database.ToUpperInvariant()}_READER" : Sql.Ident(o.Database + "_READER");
        return $"""
            -- Lakeflow: one-time setup. Run in Snowsight as ACCOUNTADMIN (Projects -> Worksheets -> + SQL worksheet, Run All).
            USE ROLE ACCOUNTADMIN;

            CREATE ROLE IF NOT EXISTS {role} COMMENT = 'Lakeflow: writes the Lakeflow database';
            CREATE WAREHOUSE IF NOT EXISTS {warehouse}
                WAREHOUSE_SIZE = XSMALL AUTO_SUSPEND = 60 AUTO_RESUME = TRUE INITIALLY_SUSPENDED = TRUE
                COMMENT = 'Lakeflow: builds silver every few minutes';
            GRANT USAGE, OPERATE ON WAREHOUSE {warehouse} TO ROLE {role};

            CREATE DATABASE IF NOT EXISTS {database} COMMENT = 'Site data from Lakeflow: BRONZE (every change), SILVER (current state)';
            GRANT OWNERSHIP ON DATABASE {database} TO ROLE {role} COPY CURRENT GRANTS;

            CREATE USER IF NOT EXISTS {user} TYPE = SERVICE DEFAULT_ROLE = {role} DEFAULT_WAREHOUSE = {warehouse}
                COMMENT = 'Lakeflow forwarder (key-pair sign-in only)';
            ALTER USER {user} SET RSA_PUBLIC_KEY = {Sql.Literal(publicKey)};
            GRANT ROLE {role} TO USER {user};

            -- Readers (analysts, Tableau, Power BI): grant this role to them.
            CREATE ROLE IF NOT EXISTS {reader} COMMENT = 'Lakeflow: reads the Lakeflow database';
            GRANT USAGE ON DATABASE {database} TO ROLE {reader};
            GRANT USAGE ON FUTURE SCHEMAS IN DATABASE {database} TO ROLE {reader};
            GRANT SELECT ON FUTURE TABLES IN DATABASE {database} TO ROLE {reader};
            GRANT USAGE ON WAREHOUSE {warehouse} TO ROLE {reader};
            """;
    }
}
