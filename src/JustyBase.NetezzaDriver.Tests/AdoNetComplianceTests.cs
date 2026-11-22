using JustyBase.NetezzaDriver.TestSupport;
using System.Data;
using System.Data.Common;

namespace JustyBase.NetezzaDriver.Tests;

[Trait("Category", "Unit")]
public sealed class AdoNetComplianceTests
{
    private static NzConnection CreateReplayConnection(int port)
        => new(
            "replay",
            "replay",
            "127.0.0.1",
            "JUST_DATA",
            port,
            SecurityLevelCode.OnlyUnsecuredSession)
        {
            CommandTimeout = Timeout.InfiniteTimeSpan,
        };

    private static async Task<(NzReplayServer Server, NzConnection Connection)> OpenReplayAsync(
        string fixture = "dimdate.nzreplay.gz")
    {
        var server = NzReplayServer.Start(NzReplayFixture.LoadShipped(fixture));
        var connection = CreateReplayConnection(server.Port);
        try
        {
            await connection.OpenAsync();
            return (server, connection);
        }
        catch
        {
            await connection.DisposeAsync();
            await server.DisposeAsync();
            throw;
        }
    }

    [Fact]
    public void Builder_DerivesFromDbConnectionStringBuilder_AndSupportsStandardMembers()
    {
        DbConnectionStringBuilder builder = new NzConnectionStringBuilder
        {
            Host = "h",
            Database = "d",
            UserName = "u",
            Password = "p",
        };

        Assert.Equal("h", builder["Host"]);

        builder["Host"] = "h2";
        Assert.Equal("h2", ((NzConnectionStringBuilder)builder).Host);

        Assert.True(builder.TryGetValue("Host", out var value));
        Assert.Equal("h2", value);

        Assert.True(builder.ContainsKey("Password"));
        builder.Remove("Password");
        Assert.False(builder.ContainsKey("Password"));
        Assert.Equal(string.Empty, ((NzConnectionStringBuilder)builder).Password);
    }

    [Fact]
    public void Builder_RoundTripsPasswordWithSemicolons()
    {
        var builder = new NzConnectionStringBuilder
        {
            Host = "h",
            Database = "d",
            UserName = "u",
            Password = "p;ass=word",
            Port = 5490,
        };

        var parsed = new NzConnectionStringBuilder(builder.ConnectionString);

        Assert.Equal("h", parsed.Host);
        Assert.Equal("d", parsed.Database);
        Assert.Equal("u", parsed.UserName);
        Assert.Equal("p;ass=word", parsed.Password);
        Assert.Equal(5490, parsed.Port);
    }

    [Fact]
    public void Builder_AcceptsLegacyCurlyBraceQuoting()
    {
        var parsed = new NzConnectionStringBuilder(
            "Host=h;Database=d;User=u;Password={p;ass};Port=5481");

        Assert.Equal("p;ass", parsed.Password);
        Assert.Equal(5481, parsed.Port);
    }

    [Fact]
    public void Builder_SafeConnectionStringRedactsPassword()
    {
        var builder = new NzConnectionStringBuilder
        {
            Host = "h",
            Database = "d",
            UserName = "u",
            Password = "secret",
        };

        Assert.Contains("Password=********", builder.SafeConnectionString);
        Assert.DoesNotContain("secret", builder.SafeConnectionString);
        Assert.Equal(builder.SafeConnectionString, builder.ToSafeString());
    }

    [Fact]
    public void Factory_CreatesAdoNetObjects()
    {
        var factory = NzFactory.Instance;

        Assert.IsType<NzConnection>(factory.CreateConnection());
        Assert.IsType<NzCommand>(factory.CreateCommand());
        Assert.IsType<NzParameter>(factory.CreateParameter());
        Assert.IsType<NzConnectionStringBuilder>(factory.CreateConnectionStringBuilder());
        Assert.False(factory.CanCreateDataSourceEnumerator);
    }

    [Fact]
    public void FactoryCreatedCommand_BindsToConnection()
    {
        using var connection = new NzConnection("u", "p", "h", "d");
        using var command = (NzCommand)NzFactory.Instance.CreateCommand()!;

        command.Connection = connection;
        command.CommandText = "SELECT 1";

        Assert.Same(connection, command.Connection);
        Assert.Equal(60, command.CommandTimeout);
    }

    [Fact]
    public void CommandTimeout_IsPerCommand_AndDoesNotMutateConnection()
    {
        using var connection = new NzConnection("u", "p", "h", "d");
        connection.CommandTimeout = TimeSpan.FromSeconds(30);

        using var first = connection.CreateCommand();
        using var second = connection.CreateCommand();

        first.CommandTimeout = 5;

        Assert.Equal(5, first.CommandTimeout);
        Assert.Equal(30, second.CommandTimeout);
        Assert.Equal(30, (int)connection.CommandTimeout.TotalSeconds);
        Assert.Throws<ArgumentOutOfRangeException>(() => first.CommandTimeout = -1);
    }

    [Fact]
    public void CommandTimeout_WithoutOverride_FollowsConnectionDefault()
    {
        using var connection = new NzConnection("u", "p", "h", "d");
        connection.CommandTimeout = TimeSpan.FromSeconds(12);

        using var command = connection.CreateCommand();
        Assert.Equal(12, command.CommandTimeout);

        connection.CommandTimeout = TimeSpan.FromSeconds(7);
        Assert.Equal(7, command.CommandTimeout);
    }

    [Fact]
    public void ConnectionString_Setter_AppliesSettingsWhenClosed()
    {
        using var connection = new NzConnection();

        connection.ConnectionString =
            "Host=h;Database=d;User=u;Password=p;Port=5491;Timeout=42;BulkInsert=False";

        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Equal("h", connection.DataSource);
        Assert.Equal("d", connection.Database);
        Assert.Equal(42, connection.ConnectionTimeout);
        Assert.False(connection.BulkInsertEnabled);
        Assert.Contains("Port=5491", connection.ConnectionString);
        Assert.Contains("Password=p", connection.ConnectionString);
    }

    [Fact]
    public void ConnectionString_Setter_ThrowsWhenOpen()
    {
        using var connection = new NzConnection("u", "p", "h", "d");
        connection.SetState(ConnectionState.Open);

        Assert.Throws<InvalidOperationException>(() =>
            connection.ConnectionString = "Host=x;Database=y;User=z;Password=q");
    }

    [Fact]
    public void ConnectionString_RoundTripsSemicolonPassword()
    {
        using var connection = new NzConnection();
        connection.ConnectionString = "Host=h;Database=d;User=u;Password={p;a;ss};Port=5480";

        var text = connection.ConnectionString;
        var parsed = new NzConnectionStringBuilder(text);

        Assert.Equal("p;a;ss", parsed.Password);
        Assert.Equal("h", parsed.Host);
    }

    [Fact]
    public async Task Replay_GetColumnSchemaThroughBaseType_ReturnsColumns()
    {
        var (server, connection) = await OpenReplayAsync();
        await using (server)
        await using (connection)
        {
            await using var command = connection.CreateCommand(
                "SELECT * FROM JUST_DATA.ADMIN.DIMDATE");
            await using var reader = await command.ExecuteReaderAsync();

            DbDataReader baseReader = reader;
            var columns = baseReader.GetColumnSchema();

            Assert.Equal(19, columns.Count);
            Assert.True(baseReader.CanGetColumnSchema());
            Assert.Equal(0, columns[0].ColumnOrdinal);
            Assert.Equal(18, columns[18].ColumnOrdinal);
            Assert.All(columns, column => Assert.False(string.IsNullOrEmpty(column.ColumnName)));

            var asyncColumns = await baseReader.GetColumnSchemaAsync();
            Assert.Equal(columns.Count, asyncColumns.Count);
        }
    }

    [Fact]
    public async Task Replay_GetSchemaTable_UsesZeroBasedOrdinalsAndIntSizes()
    {
        var (server, connection) = await OpenReplayAsync();
        await using (server)
        await using (connection)
        {
            await using var command = connection.CreateCommand(
                "SELECT * FROM JUST_DATA.ADMIN.DIMDATE");
            await using var reader = await command.ExecuteReaderAsync();

            var schema = reader.GetSchemaTable();

            Assert.NotNull(schema);
            Assert.Equal(19, schema!.Rows.Count);
            Assert.Equal(typeof(int), schema.Columns["ColumnSize"]!.DataType);
            Assert.Equal(typeof(int), schema.Columns["ColumnOrdinal"]!.DataType);

            for (int index = 0; index < schema.Rows.Count; index++)
            {
                Assert.Equal(index, (int)schema.Rows[index]["ColumnOrdinal"]);
                Assert.False(string.IsNullOrEmpty((string)schema.Rows[index]["ColumnName"]));
            }
        }
    }

    [Fact]
    public async Task Replay_GetEnumerator_IteratesRowsAsDataRecords()
    {
        var (server, connection) = await OpenReplayAsync();
        await using (server)
        await using (connection)
        {
            await using var command = connection.CreateCommand(
                "SELECT * FROM JUST_DATA.ADMIN.DIMDATE");
            await using var reader = await command.ExecuteReaderAsync();

            int rows = 0;
            foreach (IDataRecord record in reader)
            {
                Assert.Equal(reader.FieldCount, record.FieldCount);
                rows++;
                if (rows == 3)
                {
                    break;
                }
            }

            Assert.Equal(3, rows);
        }
    }
}
