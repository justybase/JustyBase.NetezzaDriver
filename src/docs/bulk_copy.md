# Bulk inserts (`NzBulkCopy`)

`NzBulkCopy` inserts rows through Netezza's external-table transfer protocol:
it generates `INSERT INTO <table> (...) SELECT c0, c1, ... FROM EXTERNAL '<marker>' (...) USING (...)`
and streams the encoded rows over the existing connection as `DATA` frames followed
by `DONE`. No local file, staging table or second connection is created.

Bulk copy is **explicit**. The driver does not detect repeated `ExecuteNonQuery`
calls and does not rewrite multi-statement SQL. Call `CreateBulkCopy()` and
`WriteToServer(...)` to opt in.

## Basic usage

```csharp
var bulk = connection.CreateBulkCopy();
bulk.DestinationTableName = "ADMIN.TARGET";
bulk.ColumnNames.Add("ID");
bulk.ColumnNames.Add("NAME");
bulk.ColumnNames.Add("CREATED");

object?[][] rows =
[
    [1, "one", new DateOnly(2026, 11, 22)],
    [2, "zażółć 😀", new DateOnly(2026, 11, 23)]
];

int inserted = bulk.WriteToServer(rows);                 // sync
int insertedAsync = await bulk.WriteToServerAsync(rows); // async
```

`DestinationTableName` accepts a schema-qualified name. Identifiers are quoted
when they are not simple identifiers; already-quoted identifiers are preserved.
`ColumnNames` is optional: when empty, values are assigned in the table's
physical column order. Every row must have the same number of values, and the
count must match `ColumnNames` when that list is set.

## Overloads

| Method | Source |
|---|---|
| `WriteToServer(IEnumerable<object?[]>)` | positional rows |
| `WriteToServer(DataTable)` | all non-deleted rows in column order |
| `WriteToServer(IDataReader)` | all remaining rows in field order |
| `WriteToServerAsync(IEnumerable<object?[]>, CancellationToken)` | positional rows |
| `WriteToServerAsync(DataTable, CancellationToken)` | all rows in column order |
| `WriteToServerAsync(DbDataReader, CancellationToken)` | all remaining rows asynchronously |

The return value is the number of rows inserted. Source rows are enumerated
before the statement starts, because the driver must infer external-column
widths and decide whether fallback is needed before sending anything.

## Value rendering

Values are converted to the external-table text format using invariant culture:

| .NET value | Rendered as |
|---|---|
| `null` / `DBNull` | `\N` (external `NULLVALUE`) |
| `bool` | `TRUE` / `FALSE` |
| integers, `decimal`, `float`, `double` | invariant numeric text (finite values only) |
| `string`, `char` | UTF-8 text; `\`, tab, CR and LF are backslash-escaped |
| `DateOnly` | `yyyy-MM-dd` |
| `TimeOnly`, `DateTime`, `TimeSpan` | `HH:mm:ss.ffffff`, `yyyy-MM-dd HH:mm:ss.ffffff`, `hh:mm:ss.ffffff` |
| `Guid` | `D` format |

The external table uses `REMOTESOURCE 'jdbc'`, `DELIMITER '\t'`,
`ESCAPECHAR '\'`, `NULLVALUE '\N'` and `ENCODING 'UTF8'`. Text columns are
typed `nvarchar(n)` up to 16,000 characters per value and `varchar(n)` above
that; total rendered row width is limited to 64,000 bytes.

## External-table path and fallback

- **Two or more rows** with supported values use one external-table transfer.
- **One row** always uses a regular row-by-row `INSERT`.
- If any value cannot be represented safely (for example `byte[]`, non-finite
  floating-point values, unsupported control characters or invalid UTF-16),
  the whole batch uses row-by-row inserts instead. That decision is made before
  the statement is sent.
- `BulkInsert = false` in `NzConnectionStringBuilder` (or `BulkInsert=False` in
  the connection string) forces the row-by-row path for all `WriteToServer`
  calls on that connection. The option defaults to `true` and is included in
  `ConnectionString` / `SafeConnectionString`.

The external-table transfer is a single server statement, so a load failure is
atomic at the statement level. Row-by-row fallback follows the connection's
normal transaction and autocommit behavior. Command timeout and cancellation
tokens apply to both paths.

## Limitations

- Binary/LOB values are not part of the text import format and use fallback.
- Values containing characters below `0x20` other than tab, CR and LF use fallback.
- Rows are buffered in memory before transfer; this is intentional so column
  widths and fallback eligibility can be determined up front.
- Server log payloads (`.nzlog` / `.nzbad`) are drained without creating local
  files during a bulk transfer, so a load failure is reported through the
  normal statement error.
*** End Patch