using Xunit;

namespace JustyBase.NetezzaDriver.Tests;

/// <summary>
/// Collection definition to ensure all tests run sequentially
/// </summary>
[CollectionDefinition("Sequential")]
public class SequentialTestCollection : ICollectionFixture<SequentialTestFixture>
{
}

/// <summary>
/// Fixture for sequential test collection. Creates the shared object used by
/// tests that need a real table to read from, and removes it when the
/// collection is torn down.
/// </summary>
public class SequentialTestFixture : IDisposable
{
    private const string OneRowTable = "ONE_ROW_TABLE";

    public SequentialTestFixture()
    {
        using var connection = CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"DROP TABLE {OneRowTable} IF EXISTS;" +
            $"CREATE TABLE {OneRowTable} AS SELECT 1 AS ID DISTRIBUTE ON RANDOM;";
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        try
        {
            using var connection = CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"DROP TABLE {OneRowTable} IF EXISTS";
            command.ExecuteNonQuery();
        }
        catch (Exception)
        {
            // Best-effort teardown; the next fixture run recreates the table.
        }
    }

    private static NzConnection CreateConnection() =>
        new(Config.UserName, Config.Password, Config.Host, Config.DbName, Config.Port);
}
