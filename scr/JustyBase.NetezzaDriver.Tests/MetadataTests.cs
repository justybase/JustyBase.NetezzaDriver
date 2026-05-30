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
}
