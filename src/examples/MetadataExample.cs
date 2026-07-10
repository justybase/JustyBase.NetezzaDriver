// Example: Using NzMetadata for catalog introspection

using JustyBase.NetezzaDriver;

await using var conn = new NzConnection("user", "password", "host", "database");
await conn.OpenAsync();

var meta = conn.Meta;

// List schemas
var schemas = await meta.GetSchemasAsync();
Console.WriteLine("Schemas: " + string.Join(", ", schemas));

// List tables in ADMIN schema
var tables = await meta.GetTablesAsync("ADMIN");
foreach (var t in tables.Take(5))
    Console.WriteLine($"  TABLE: {t.Schema}.{t.TableName} (rows: {t.RowCount})");

// Get columns of a specific table
var columns = await meta.GetColumnsAsync("DIMDATE", "ADMIN");
foreach (var c in columns)
    Console.WriteLine($"  {c.ColumnName} ({c.DataType}), nullable={c.IsNullable}");

// Get distribution key
var distKey = await meta.GetDistributionKeyAsync("DIMDATE", "ADMIN");
Console.WriteLine($"Distribution key: {string.Join(", ", distKey)}");

// Search for objects
var results = await meta.SearchObjectsAsync("DIM");
Console.WriteLine($"Found {results.Count} objects matching 'DIM'");
