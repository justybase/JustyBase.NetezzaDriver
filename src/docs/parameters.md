# Parameterized Queries

`JustyBase.NetezzaDriver` supports both **named** (`:name`, `@name`) and **positional** (`?`) parameter placeholders.

## How it works

Parameters are **rendered inline** into the SQL text before execution. The driver parses the SQL, replaces placeholders with properly escaped C#-to-SQL literal representations, and sends the final SQL to the server. There is no binary Bind message — this matches the approach used by the Python `nzpy_extended` driver.

## Named parameters

```csharp
using var cmd = conn.CreateCommand(
    "SELECT id, name FROM users WHERE age > :minAge AND city = :city");
cmd.Parameters.AddWithValue(":minAge", 18);
cmd.Parameters.AddWithValue(":city", "New York");

using var reader = cmd.ExecuteReader();
while (reader.Read())
    Console.WriteLine($"{reader.GetInt32(0)}: {reader.GetString(1)}");
```

Both `:name` and `@name` prefixes are supported:

```csharp
cmd.Parameters.AddWithValue(":id", 42);   // colon prefix
cmd.Parameters.AddWithValue("@name", "Alice");  // @ prefix
```

## Positional parameters

Use `?` placeholders and mark each parameter with `IsPositional = true`:

```csharp
using var cmd = conn.CreateCommand("SELECT * FROM users WHERE status = ? AND age > ?");
cmd.Parameters.Add(new NzParameter { Value = "active", IsPositional = true });
cmd.Parameters.Add(new NzParameter { Value = 18, IsPositional = true });

using var reader = cmd.ExecuteReader();
```

**Important:** All positional parameters must have `IsPositional = true`. Named parameters must have `IsPositional = false` (default). Mixing named and positional in a single query is not supported.

## C# to SQL type mapping

| C# type | SQL literal | Example |
|---------|-------------|---------|
| `null` / `DBNull` | `NULL` | `NULL` |
| `bool` | `TRUE` / `FALSE` | `TRUE` |
| `int`, `long`, `short` | number literal | `42` |
| `float`, `double` | number literal | `3.14` |
| `decimal` | number literal | `3.14` |
| `string` | `'escaped'` | `'hello'` |
| `DateTime` | `'yyyy-MM-dd HH:mm:ss.ffffff'` | `'2024-01-15 10:30:00.000000'` |
| `DateOnly` | `'yyyy-MM-dd'` | `'2024-01-15'` |
| `TimeOnly` | `'HH:mm:ss'` | `'14:30:00'` |
| `TimeSpan` | `'HH:mm:ss'` | `'14:30:00'` |
| `byte[]` | `x'hex'` | `x'deadbeef'` |
| `Guid` | `'guid'` | `'xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx'` |
| `char` | `'c'` (escaped) | `'a'` |

String values are escaped by doubling single quotes (`'` → `''`). Byte arrays are rendered as hex literals (`x'abcd'`).

## Using ADO.NET standard API

```csharp
using var cmd = conn.CreateCommand("SELECT :val::INTEGER");
var p = cmd.CreateParameter();
p.ParameterName = ":val";
p.Value = 7;
cmd.Parameters.Add(p);
using var reader = cmd.ExecuteReader();
```

## Parameters are not persisted

Each execution rebuilds the SQL with current parameter values. Parameters are cleared after execution, so you must re-add them for each `Execute*` call.

## SQL parsing details

The `NzParameterHelper.SubstituteParameters` method uses a state-machine parser that:
- Handles string literals (`'single quoted'`)
- Handles identifiers (`"quoted"`, `"escaped""quote"`)
- Handles line comments (`--`) and block comments (`/* */`)
- Only replaces `:name`, `@name`, and `?` outside of literals and comments
