# JustyBase.NetezzaDriver 
JustyBase.NetezzaDriver is a .NET library for interacting with IBM Netezza Performance Server databases. It provides a set of classes and methods to facilitate database connections, command execution, and data retrieval.
Code is based on [nzpy](https://github.com/IBM/nzpy) and [npgsql](https://github.com/npgsql/npgsql)


## Features
* Connect to Netezza databases using NzConnection.
* Execute SQL commands and queries with NzCommand.
* Read data using NzDataReader.
* Async ADO.NET API (`OpenAsync`, `ExecuteReaderAsync`, `ReadAsync`, `ExecuteScalarAsync`, `CloseAsync`).
* Support for various Netezza data types.
* Secure connections with SSL/TLS.

## ADO.NET support snapshot
- `NzConnection.BeginTransaction()` supports `IsolationLevel.ReadCommitted` (`IsolationLevel.Unspecified` is accepted and treated as `ReadCommitted`).
- `NzConnection.DataSource` is implemented.
- `NzConnection.ChangeDatabase(...)` is intentionally not supported (create a new connection).
- `NzCommand` supports `CommandType.Text`; parameter APIs (`DbParameterCollection`, `CreateDbParameter`) are not supported.
- `NzDataReader.GetBytes(...)` and `NzDataReader.GetChars(...)` are implemented.

## Behavioral Changes (from v1.4.0)
Starting from version 1.4.0, the behavior when attempting to retrieve a `NULL` column value as a specific data type (e.g., `string`, `Int16`) has changed. Previously, such retrieval was possible, and users were required to explicitly check for `DBNull` using `IsDBNull`. Now, attempting to retrieve a `NULL` value as a non-nullable type will result in an `InvalidCastException`. This change ensures stricter type enforcement and aligns with common ADO.NET practices. Users should adjust their code to handle `NULL` values appropriately, for example, by checking `IsDBNull` before attempting to cast, or by using nullable types.

## Requirements
* .NET 8, .NET 9 or .NET 10
* C# 12.0
## Installation
JustyBase.NetezzaDriver is available as a NuGet package. You can install it using the following command:
```bash
dotnet add package JustyBase.NetezzaDriver
```

## Usage
### Connecting to a Database
To connect to a Netezza database, create an instance of NzConnection and open the connection:
```c#
using JustyBase.NetezzaDriver;

var connection = new NzConnection("username", "password", "host", "database");
connection.Open();
```

### Async Usage
```c#
using JustyBase.NetezzaDriver;

await using var connection = new NzConnection("username", "password", "host", "database");
await connection.OpenAsync();

await using var command = connection.CreateCommand("SELECT * FROM my_table");
await using var reader = await command.ExecuteReaderAsync();
while (await reader.ReadAsync())
{
    Console.WriteLine(reader.GetValue(0));
}
```

### Async best practices
- Przekazuj `CancellationToken` do `OpenAsync`, `ExecuteReaderAsync`, `ReadAsync`, `ExecuteScalarAsync`.
- Używaj `await using` dla `NzConnection`, `NzCommand` i `NzDataReader`.
- Nie mieszaj sync i async w tej samej ścieżce wykonania zapytania.

### Executing a Command

```c#
var command = new NzCommand(connection)
{
    CommandText = "SELECT * FROM my_table"
};

using var reader = command.ExecuteReader();
while (reader.Read())
{
    for (int i = 0; i < reader.FieldCount; i++)
    {
        var value = reader.GetValue(i);
        Console.WriteLine(value);
    }
}

```
### Handling Transactions
To handle transactions, use the BeginTransaction, Commit, and Rollback methods:

```c#
        using NzConnection connection = new NzConnection("username", "password", "host", "database");
        connection.Open();
        using var nzCommand = connection.CreateCommand();

        connection.AutoCommit = false; // autocommit is on by default. It can be turned off by using the autocommit property of the connection.

        nzCommand.CommandText = "DROP TABLE T2 IF EXISTS";
        nzCommand.ExecuteNonQuery();
        nzCommand.CommandText = "create table t2(c1 numeric (10,5), c2 varchar(10),c3 nchar(5))";
        nzCommand.ExecuteNonQuery();
        nzCommand.CommandText = "insert into t2 values (123.54,'xcfd','xyz')";
        nzCommand.ExecuteNonQuery();
        connection.Rollback();
        nzCommand.CommandText = "DROP TABLE T5 IF EXISTS";
        nzCommand.ExecuteNonQuery();
        nzCommand.CommandText = "create table t5(c1 numeric (10,5), c2 varchar(10),c3 nchar(5))";
        nzCommand.ExecuteNonQuery();
        nzCommand.CommandText = "insert into t5 values (123.54,'xcfd','xyz')";
        nzCommand.ExecuteNonQuery();
        connection.Commit();

        nzCommand.CommandText = "SELECT * FROM T2";
        Assert.Throws<NetezzaException>(() => nzCommand.ExecuteNonQuery());
        try
        {
            nzCommand.CommandText = "SELECT * FROM T5";
            nzCommand.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Assert.Fail( $"Expected no exception, but got: {ex.Message}");
        }
```
### Secure Connections
TLS certificate validation is strict by default: certificates with TLS policy errors are rejected.

To establish a secure connection, provide the SSL certificate file path when creating the NzConnection instance:
```c#
var connection = new NzConnection("username", "password", "host", "database", securityLevel: SecurityLevelCode.OnlySecuredSession, sslCerFilePath: @"C:\path\to\certificate.pem");
connection.Open();
```
### Testing
- Unit tests only:
```bash
dotnet test .\scr\JustyBase.NetezzaDriver.Tests\JustyBase.NetezzaDriver.Tests.csproj --filter "Category=Unit"
```

- Integration tests only:
```bash
dotnet test .\scr\JustyBase.NetezzaDriver.Tests\JustyBase.NetezzaDriver.Tests.csproj --filter "Category=Integration"
```
- Integration configuration uses environment variables: `NZ_DEV_HOST`, `NZ_DEV_PORT`, `NZ_DEV_DB`, `NZ_DEV_USER`, `NZ_DEV_PASSWORD`.
- CI/CD workflow is intentionally not included in this repository.

### Benchmark (sync vs async DataReader)
Reader comparison (`ReadLargeDataReaderSync` vs `ReadLargeDataReaderAsync`) is available in `scr\JustyBase.NetezzaDriver.Benchmarks` (`AsyncReaderBench`) for multiple data-type scenarios.

Run:
```bash
dotnet run -c Release -f net10.0 --project .\scr\JustyBase.NetezzaDriver.Benchmarks -- --filter *AsyncReaderBench*
```
The benchmark reports execution time and memory allocations for sync and async variants.
Connection settings can be overridden via environment variables: `NZ_DEV_HOST`, `NZ_DEV_PORT`, `NZ_DEV_DB`, `NZ_DEV_USER`, `NZ_DEV_PASSWORD`.

Sample result (`net10.0`, BenchmarkDotNet):

| Scenario            | Sync Mean | Sync Allocated | Async Mean | Async Allocated | Time Ratio (Async/Sync) | Alloc Ratio (Async/Sync) |
|-------------------- |----------:|---------------:|-----------:|----------------:|------------------------:|-------------------------:|
| LargeMixed_500k     | 1.011 s   | 49.66 MB       | 1.006 s    | 52.27 MB        | 0.99x                   | 1.05x                    |
| NumericScalars_300k | 2.158 s   | 29.76 MB       | 2.164 s    | 31.13 MB        | 1.00x                   | 1.05x                    |
| TemporalNulls_300k  | 1.831 s   | 28.84 MB       | 1.867 s    | 30.12 MB        | 1.02x                   | 1.04x                    |
| Textual_250k        | 1.465 s   | 29.33 MB       | 1.514 s    | 29.45 MB        | 1.03x                   | 1.00x                    |

## License
[![License](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](https://opensource.org/licenses/Apache-2.0)

This project is licensed under the Apache License, Version 2.0 - see the [LICENSE](LICENSE) file for details.

```plaintext
Copyright: 2025-2026 Krzysztof Duśko
Copyright: 2019-2020 IBM, Inc

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
```

## Contact
For questions or support, please open an issue on GitHub.
