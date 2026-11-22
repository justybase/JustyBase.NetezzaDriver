using JustyBase.NetezzaDriver.TestSupport;
using System.Data;

namespace JustyBase.NetezzaDriver.Tests;

[Trait("Category", "Unit")]
public sealed class TransactionReplayTests
{
    [Fact]
    public async Task BeginCommitRollbackAndDispose_ManageConnectionState()
    {
        var options = new NzReplayOptions
        {
            ResponseOverride = (sql, _) => sql.Trim().ToLowerInvariant() switch
            {
                "begin" => BackendTestMessages.Success("BEGIN"),
                "commit" => BackendTestMessages.Success("COMMIT"),
                "rollback" => BackendTestMessages.Success("ROLLBACK"),
                _ => null,
            },
        };

        await using var server = NzReplayServer.Start(NzReplayFixture.LoadShipped("dimdate.nzreplay.gz"), options);
        using var connection = Open(server.Port);

        using (var transaction = connection.BeginTransaction())
        {
            Assert.Equal(IsolationLevel.ReadCommitted, transaction.IsolationLevel);
            Assert.True(connection.InTransaction);
            transaction.Commit();
            Assert.False(connection.InTransaction);
            Assert.True(connection.AutoCommit);
            Assert.Throws<InvalidOperationException>(transaction.Commit);
            Assert.Throws<InvalidOperationException>(transaction.Rollback);
        }

        using (var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted))
        {
            Assert.True(connection.InTransaction);
            transaction.Rollback();
            Assert.False(connection.InTransaction);
        }

        using (var transaction = connection.BeginTransaction())
        {
            Assert.True(connection.InTransaction);
            // Dispose without completing must roll back.
        }
        Assert.False(connection.InTransaction);
        Assert.Contains("rollback", server.Queries.Select(sql => sql.ToLowerInvariant()));

        Assert.Throws<NotSupportedException>(() => connection.BeginTransaction(IsolationLevel.Serializable));
        Assert.Throws<NotSupportedException>(() => connection.BeginTransaction(IsolationLevel.RepeatableRead));
        Assert.Throws<NotSupportedException>(() => connection.BeginTransaction(IsolationLevel.Snapshot));
        connection.Close();
        Assert.Throws<InvalidOperationException>(() => connection.BeginTransaction());
    }

    [Fact]
    public async Task BeginTransaction_RejectsNestedTransactionsAndKeepsTheOuterOne()
    {
        var options = new NzReplayOptions
        {
            ResponseOverride = (sql, _) => sql.Trim().ToLowerInvariant() switch
            {
                "begin" => BackendTestMessages.Success("BEGIN"),
                "rollback" => BackendTestMessages.Success("ROLLBACK"),
                _ => null,
            },
        };

        await using var server = NzReplayServer.Start(NzReplayFixture.LoadShipped("dimdate.nzreplay.gz"), options);
        using var connection = Open(server.Port);
        using var transaction = connection.BeginTransaction();

        var error = Assert.Throws<InvalidOperationException>(() => connection.BeginTransaction());
        Assert.Contains("nested", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(connection.InTransaction);
        transaction.Rollback();
        Assert.False(connection.InTransaction);
    }

    [Fact]
    public async Task BeginTransaction_UnspecifiedIsolation_BecomesReadCommitted()
    {
        var options = new NzReplayOptions
        {
            ResponseOverride = (sql, _) => sql.Trim().ToLowerInvariant() switch
            {
                "begin" => BackendTestMessages.Success("BEGIN"),
                "commit" => BackendTestMessages.Success("COMMIT"),
                _ => null,
            },
        };

        await using var server = NzReplayServer.Start(NzReplayFixture.LoadShipped("dimdate.nzreplay.gz"), options);
        using var connection = Open(server.Port);
        using var transaction = connection.BeginTransaction(IsolationLevel.Unspecified);

        Assert.Equal(IsolationLevel.ReadCommitted, transaction.IsolationLevel);
        transaction.Commit();
    }

    private static NzConnection Open(int port)
    {
        var connection = new NzConnection(
            "tx",
            "tx",
            "127.0.0.1",
            "JUST_DATA",
            port,
            SecurityLevelCode.OnlyUnsecuredSession)
        {
            CommandTimeout = Timeout.InfiniteTimeSpan,
            ConnectionTimeoutDuration = TimeSpan.FromSeconds(5),
        };
        connection.Open();
        return connection;
    }
}
