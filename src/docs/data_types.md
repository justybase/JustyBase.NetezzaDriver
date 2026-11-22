# Data types and CLR mapping

The reader maps Netezza values to stable CLR types. `GetFieldType(ordinal)` is
determined by the column metadata, not by the value in the current row, so a
column never changes type between rows.

## NUMERIC

`NUMERIC`/`DECIMAL` mapping uses the declared precision:

| Declared precision | CLR type | Notes |
|---:|---|---|
| `<= 28` | `decimal` | Exact `System.Decimal` value. |
| `29..38` | `string` | Exact server text, no silent rounding. |

A value outside `System.Decimal` range on a column whose precision was not
known at metadata time throws `InvalidCastException` with a hint to cast the
column to `NUMERIC(38,s)`. Use `GetString` for exact wide values, or
`GetFieldValue<string>` when the metadata advertises a string.

## Text

| Netezza type | CLR type | Notes |
|---|---|---|
| `CHAR`, `VARCHAR` | `string` | DBOS decoding uses `CharVarcharEncoding`; DBOS `CHAR` blank padding is removed. |
| `NCHAR`, `NVARCHAR` | `string` | Always UTF-8. |
| `TEXT`, `NAME`, `CSTRING`, JSON/geometry | `string` | UTF-8 on the text protocol path. |
| `VARBINARY`, `BYTEA` | `byte[]` | Binary copy of the value. |
| `INTERVAL` | `string` | Not a `TimeSpan`: months cannot be represented exactly. |

## Numeric and temporal scalars

| Netezza type | CLR type |
|---|---|
| `BOOLEAN` | `bool` |
| `BYTEINT` | `short` |
| `SMALLINT` | `short` |
| `INTEGER` | `int` |
| `BIGINT` | `long` |
| `REAL` | `float` |
| `DOUBLE PRECISION` | `double` |
| `DATE` | `DateTime` (midnight) |
| `TIME` | `TimeSpan` |
| `TIMESTAMP` | `DateTime` |
| `TIMESTAMPTZ` | `DateTime` |
| `UUID` | `string` |

## NULL handling

A database `NULL` is returned by `GetValue` as `DBNull.Value`. Reading it
through a non-nullable typed getter such as `GetInt32` throws
`InvalidCastException`; check `IsDBNull(ordinal)` first.

## Choosing accessors

- `GetValue` boxes value types.
- `GetFieldValue<T>` and the typed getters (`GetInt64`, `GetString`, ...) avoid
  boxing on the matching type.
- `GetFieldType` and `GetDataTypeName` read metadata and can be called before
  the first `Read()`.
- Named lookup uses `GetOrdinal(name)` once, then index access in the row loop.
