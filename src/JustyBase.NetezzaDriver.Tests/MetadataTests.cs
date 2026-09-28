namespace JustyBase.NetezzaDriver.Tests;

[Collection("Sequential")]
[Trait("Category", "Integration")]
public class MetadataTests : IDisposable
{
    private readonly NzConnection _conn;

    public MetadataTests()
    {
        _conn = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName, Config.Port);
        _conn.Open();
    }

    public void Dispose() => _conn.Dispose();

    [Fact]
    public async Task GetSchemasAsync_ReturnsSchemas()
    {
        var meta = _conn.Meta;
        var schemas = await meta.GetSchemasAsync();
        Assert.NotEmpty(schemas);
        Assert.Contains(schemas, s => s.Equals("ADMIN", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetDatabasesAsync_ReturnsDatabases()
    {
        var meta = _conn.Meta;
        var databases = await meta.GetDatabasesAsync();
        Assert.NotEmpty(databases);
    }

    [Fact]
    public async Task GetTablesAsync_ReturnsTables()
    {
        var meta = _conn.Meta;
        var tables = await meta.GetTablesAsync("ADMIN");
        Assert.NotEmpty(tables);
        var table = tables.First();
        Assert.Equal("ADMIN", table.Schema, ignoreCase: true);
        Assert.NotNull(table.TableName);
    }

    [Fact]
    public async Task GetColumnsAsync_ReturnsColumns()
    {
        var meta = _conn.Meta;
        var columns = await meta.GetColumnsAsync("DIMDATE", "ADMIN");
        Assert.NotEmpty(columns);
        var first = columns.First();
        Assert.NotNull(first.ColumnName);
        Assert.True(first.Ordinal >= 1);
    }

    [Fact]
    public async Task GetViewsAsync_ReturnsViews()
    {
        var meta = _conn.Meta;
        var views = await meta.GetViewsAsync();
        Assert.NotEmpty(views);
    }

    [Fact]
    public async Task GetProceduresAsync_ReturnsProcedures()
    {
        var meta = _conn.Meta;
        var procs = await meta.GetProceduresAsync();
        Assert.NotNull(procs);
    }

    [Fact]
    public async Task GetTableSizesAsync_ReturnsSizes()
    {
        var meta = _conn.Meta;
        var sizes = await meta.GetTableSizesAsync();
        Assert.NotNull(sizes);
    }

    [Fact]
    public async Task GetSessionsAsync_ReturnsSessions()
    {
        var meta = _conn.Meta;
        var sessions = await meta.GetSessionsAsync();
        Assert.NotEmpty(sessions);
    }

    [Fact]
    public async Task SearchObjectsAsync_ReturnsResults()
    {
        var meta = _conn.Meta;
        var results = await meta.SearchObjectsAsync("DIM");
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Type == "TABLE");
    }

    [Fact]
    public async Task GetDistributionKeyAsync_ReturnsColumns()
    {
        var meta = _conn.Meta;
        var keys = await meta.GetDistributionKeyAsync("DIMDATE", "ADMIN");
        Assert.NotNull(keys);
    }

    [Fact]
    public async Task ExtendedMetadata_ReconstructsExistingTable()
    {
        var meta = _conn.Meta;
        Assert.False(string.IsNullOrWhiteSpace(await meta.GetCurrentDatabaseAsync()));
        var columns = await meta.GetDetailedColumnsAsync("DIMDATE", "ADMIN");
        Assert.NotEmpty(columns);
        var ddl = await meta.GetTableDdlAsync("DIMDATE", "ADMIN");
        Assert.Contains("CREATE TABLE", ddl);
        Assert.Contains("DIMDATE", ddl);
        var batch = await meta.GetTablesDdlAsync("ADMIN", tables: ["DIMDATE"]);
        Assert.Single(batch);
        Assert.Null(batch[0].Error);
    }

    [Fact]
    public async Task DdlHelpers_RoundTripAllCatalogObjectKinds()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var table = "JB_DDL_T_" + suffix;
        var view = "JB_DDL_V_" + suffix;
        var procedure = "JB_DDL_P_" + suffix;
        var synonym = "JB_DDL_S_" + suffix;
        var external = "JB_DDL_E_" + suffix;
        void Execute(string sql)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        void Cleanup()
        {
            foreach (var sql in new[]
            {
                $"DROP VIEW {view}", $"DROP PROCEDURE {procedure}()", $"DROP SYNONYM {synonym}",
                $"DROP TABLE {external}", $"DROP TABLE {table}"
            })
            {
                try { Execute(sql); } catch { /* unique test object may not exist */ }
            }
        }

        Cleanup();
        try
        {
            Execute($"CREATE TABLE {table}(\"SELECT\" INTEGER) DISTRIBUTE ON (\"SELECT\")");
            Execute($"CREATE VIEW {view} AS SELECT \"SELECT\" FROM {table}");
            Execute($"COMMENT ON VIEW {view} IS 'DDL round-trip view comment'");
            Execute($"COMMENT ON COLUMN {view}.\"SELECT\" IS 'DDL round-trip view column comment'");
            Execute($"CREATE OR REPLACE PROCEDURE {procedure}() RETURNS INTEGER EXECUTE AS OWNER LANGUAGE NZPLSQL AS BEGIN_PROC BEGIN RETURN 1; END; END_PROC;");
            Execute($"COMMENT ON PROCEDURE {procedure}() IS 'DDL round-trip comment'");
            Execute($"CREATE SYNONYM {synonym} FOR {table}");
            Execute($"COMMENT ON SYNONYM {synonym} IS 'DDL round-trip comment'");
            Execute($"CREATE EXTERNAL TABLE {external}(ID INTEGER, LABEL CHAR(10), EVENT_DATE DATE) USING (DATAOBJECT('/tmp/{external}.csv') FORMAT 'FIXED' RECORDLENGTH 24 RECORDDELIM '\r\n' LAYOUT (BYTES 4, BYTES 10, DATE YMD ' ' BYTES 10))");

            var meta = _conn.Meta;
            var tableDdl = await meta.GetTableDdlAsync(table, "ADMIN");
            var viewDdl = await meta.GetViewDdlAsync(view, "ADMIN");
            Assert.Contains("DDL round-trip view comment", viewDdl);
            Assert.Contains("DDL round-trip view column comment", viewDdl);
            var viewBatch = await meta.GetViewsDdlAsync("ADMIN", views: [view]);
            Assert.Single(viewBatch);
            Assert.Contains("DDL round-trip view comment", viewBatch[0].Ddl);
            Assert.Contains("DDL round-trip view column comment", viewBatch[0].Ddl);
            var procedureDdl = await meta.GetProcedureDdlAsync(procedure, "ADMIN");
            var synonymDdl = await meta.GetSynonymDdlAsync(synonym, "ADMIN");
            var externalDdl = await meta.GetExternalTableDdlAsync(external, "ADMIN");
            Cleanup();
            foreach (var ddl in new[] { tableDdl, viewDdl, procedureDdl, synonymDdl, externalDdl }) Execute(ddl);
        }
        finally { Cleanup(); }
    }
}
