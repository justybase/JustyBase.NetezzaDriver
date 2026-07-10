# Catalog Introspection (Metadata API)

The `NzConnection.Meta` property provides async access to Netezza system catalog views — tables, columns, views, procedures, distribution keys, storage stats, sessions, and more.

All queries run against the **current connection's database**. Connect to the `SYSTEM` database for system-wide objects.

## Quick start

```csharp
var meta = conn.Meta;

// Schemas & databases
var schemas = await meta.GetSchemasAsync();
var databases = await meta.GetDatabasesAsync();

// Tables
var tables = await meta.GetTablesAsync("ADMIN");

// Columns of a specific table
var columns = await meta.GetColumnsAsync("DIMDATE", "ADMIN");

// Views
var views = await meta.GetViewsAsync();

// Stored procedures
var procedures = await meta.GetProceduresAsync();

// Distribution key
var distKey = await meta.GetDistributionKeyAsync("DIMDATE", "ADMIN");

// Table sizes
var sizes = await meta.GetTableSizesAsync();

// Active sessions
var sessions = await meta.GetSessionsAsync();

// Search across tables, views, procedures
var results = await meta.SearchObjectsAsync("DIM%");
```

## Method reference

| Method | Returns | Notes |
|--------|---------|-------|
| `GetSchemasAsync()` | `IReadOnlyList<string>` | All schemas in current database |
| `GetDatabasesAsync()` | `IReadOnlyList<string>` | All databases visible to user |
| `GetTablesAsync(schema, pattern?)` | `IReadOnlyList<NzTableInfo>` | Tables with schema, owner, row count |
| `GetColumnsAsync(table, schema?)` | `IReadOnlyList<NzColumnInfo>` | Columns with name, ordinal, data type, nullable |
| `GetViewsAsync(schema?)` | `IReadOnlyList<NzViewInfo>` | Views with schema, name, owner, definition |
| `GetProceduresAsync(schema?)` | `IReadOnlyList<NzProcedureInfo>` | Procedures with signature, returns, source |
| `GetTableSizesAsync(schema?)` | `IReadOnlyList<NzTableSizeInfo>` | Storage stats with used/allocated bytes, skew |
| `GetSessionsAsync()` | `IReadOnlyList<NzSessionInfo>` | Active sessions |
| `SearchObjectsAsync(pattern, schema?)` | `IReadOnlyList<NzObjectInfo>` | Search across tables, views, procedures |
| `GetDistributionKeyAsync(table, schema?)` | `IReadOnlyList<string>` | Distribution column names (empty = RANDOM) |

## Record types

```csharp
public sealed record NzTableInfo(string Schema, string TableName, string? Owner,
    string? ObjType, long? ObjId, long? RowCount);

public sealed record NzColumnInfo(string ColumnName, int Ordinal, string DataType,
    bool IsNullable, long? ObjId);

public sealed record NzViewInfo(string Schema, string ViewName, string? Owner,
    long? ObjId, string? Definition);

public sealed record NzProcedureInfo(string Schema, string ProcName, string? Owner,
    long? ObjId, string? Signature, string? Returns, bool? IsBuiltin, string? Source);

public sealed record NzTableSizeInfo(string Schema, string TableName, long? UsedBytes,
    long? AllocatedBytes, long? SizeMb, double? Skew);

public sealed record NzSessionInfo(long SessionId, string? Username, string? Database,
    DateTime? ConnectTime, string? Priority, string? Status, string? ClientType,
    string? ClientOsUser);

public sealed record NzObjectInfo(string Schema, string Name, string Type,
    string? Owner, long? ObjId);
```

## Examples

### List tables with row counts

```csharp
var tables = await meta.GetTablesAsync("ADMIN");
foreach (var t in tables.Take(10))
    Console.WriteLine($"{t.Schema}.{t.TableName}  ({t.RowCount} rows)");
```

### Get column details for a table

```csharp
var columns = await meta.GetColumnsAsync("DIMDATE", "ADMIN");
foreach (var c in columns)
    Console.WriteLine($"  {c.ColumnName,-30} {c.DataType,-20} nullable={c.IsNullable}");
```

### Search for objects matching a pattern

```csharp
var results = await meta.SearchObjectsAsync("SALES");
foreach (var r in results)
    Console.WriteLine($"{r.Type}: {r.Schema}.{r.Name}");
```

### Get distribution key

```csharp
var distKey = await meta.GetDistributionKeyAsync("FACT_SALES", "ADMIN");
Console.WriteLine(distKey.Count == 0 ? "RANDOM" : string.Join(", ", distKey));
```

## Notes

- All methods are async-only (use `.GetAwaiter().GetResult()` if needed from sync context)
- The underlying queries target Netezza system views (`_v_table`, `_v_relation_column`, `_v_view`, `_v_procedure`, `_v_session`, `_v_table_storage_stat`, `_v_table_dist_map`)
- System schemas (`DEFINITION_SCHEMA`, `INZA`, `NZ_QUERY_HISTORY`) are excluded from table listings
