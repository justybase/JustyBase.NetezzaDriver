using JustyBase.NetezzaDriver.TestSupport;

namespace JustyBase.NetezzaDriver.Tests;

/// <summary>
/// Replays a byte-exact recorded server response through the real driver over
/// loopback, with no database. Validates that the fixture is usable and that the
/// client parses the recorded result set correctly.
/// </summary>
[Trait("Category", "Unit")]
public sealed class NzReplayTests
{
    [Fact]
    public async Task Replay_DimDate_ReadsExpectedRowsAndColumns()
    {
        var fixture = NzReplayFixture.LoadShipped("dimdate.nzreplay.gz");
        await using var server = StartServer(fixture);
        using var connection = OpenConnection(server.Port);

        using var command = connection.CreateCommand(fixture.Query);
        using var reader = command.ExecuteReader();

        Assert.Equal(fixture.ExpectedColumns, reader.FieldCount);

        int rows = 0;
        while (reader.Read())
        {
            rows++;
        }

        Assert.Equal(fixture.ExpectedRows, rows);
    }

    [Fact]
    public async Task Replay_RepeatedQueries_OnSingleConnection_StayInSync()
    {
        var fixture = NzReplayFixture.LoadShipped("dimdate.nzreplay.gz");
        await using var server = StartServer(fixture);
        using var connection = OpenConnection(server.Port);

        for (int iteration = 0; iteration < 3; iteration++)
        {
            using var command = connection.CreateCommand(fixture.Query);
            using var reader = command.ExecuteReader();

            int rows = 0;
            while (reader.Read())
            {
                rows++;
            }

            Assert.Equal(fixture.ExpectedRows, rows);
        }
    }

    [Fact]
    public async Task Replay_DimDate_LazyDecoding_MatchesEagerDecoding()
    {
        var fixture = NzReplayFixture.LoadShipped("dimdate.nzreplay.gz");
        await using var server = StartServer(fixture);

        using var eagerConnection = OpenConnection(server.Port);
        var eager = ReadAll(eagerConnection, fixture.Query, lazy: false);

        using var lazyConnection = OpenConnection(server.Port);
        var lazy = ReadAll(lazyConnection, fixture.Query, lazy: true);

        Assert.Equal(fixture.ExpectedRows, eager.Count);
        Assert.Equal(eager.Count, lazy.Count);
        for (int row = 0; row < eager.Count; row++)
        {
            Assert.Equal(eager[row].Length, lazy[row].Length);
            for (int col = 0; col < eager[row].Length; col++)
            {
                Assert.Equal(eager[row][col], lazy[row][col]);
            }
        }
    }

    private static List<object?[]> ReadAll(NzConnection connection, string query, bool lazy)
    {
        connection.UseLazyColumnDecoding = lazy;
        using var command = connection.CreateCommand(query);
        using var reader = command.ExecuteReader();

        int fields = reader.FieldCount;
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var values = new object?[fields];
            for (int i = 0; i < fields; i++)
            {
                values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            rows.Add(values);
        }
        return rows;
    }

    [Fact]
    public async Task LargeCommandText_DoesNotGrowRetainedTmpBuffer()
    {
        var fixture = NzReplayFixture.LoadShipped("dimdate.nzreplay.gz");
        await using var server = StartServer(fixture);
        using var connection = OpenConnection(server.Port);

        int initialCapacity = connection.TmpBufferCapacity;
        string largeQuery = "SELECT 1 /* " + new string('x', 256 * 1024) + " */";

        using (var command = connection.CreateCommand(largeQuery))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
            }
        }

        // The 256 KB command must not be pinned as the retained scratch buffer.
        Assert.True(connection.TmpBufferCapacity <= NzConnection.TmpBufferRetainCap);
        Assert.True(initialCapacity <= NzConnection.TmpBufferRetainCap);
    }

    [Fact]
    public async Task Replay_EarlyDispose_AfterOneRow_ConnectionReusable()
    {
        var fixture = NzReplayFixture.LoadShipped("dimdate.nzreplay.gz");
        await using var server = StartServer(fixture);
        using var connection = OpenConnection(server.Port);

        connection.DiscardedRows = 0;
        using (var command = connection.CreateCommand(fixture.Query))
        using (var reader = command.ExecuteReader())
        {
            Assert.True(reader.Read());
            // Dispose without consuming the rest: must drain via discard path.
        }

        Assert.True(connection.DiscardedRows > 0);

        // Connection must be immediately reusable with correct results.
        using (var command2 = connection.CreateCommand(fixture.Query))
        using (var reader2 = command2.ExecuteReader())
        {
            int rows = 0;
            while (reader2.Read())
                rows++;
            Assert.Equal(fixture.ExpectedRows, rows);
        }
    }

    [Fact]
    public async Task Replay_EarlyDispose_AfterTenRows_ConnectionReusable()
    {
        var fixture = NzReplayFixture.LoadShipped("dimdate.nzreplay.gz");
        await using var server = StartServer(fixture);
        using var connection = OpenConnection(server.Port);

        using (var command = connection.CreateCommand(fixture.Query))
        using (var reader = command.ExecuteReader())
        {
            for (int i = 0; i < 10; i++)
                Assert.True(reader.Read());
        }

        using (var command2 = connection.CreateCommand(fixture.Query))
        using (var reader2 = command2.ExecuteReader())
        {
            int rows = 0;
            while (reader2.Read())
                rows++;
            Assert.Equal(fixture.ExpectedRows, rows);
        }
    }

    [Fact]
    public async Task Replay_SingleRow_ReturnsFirstRow_AndConnectionReusable()
    {
        var fixture = NzReplayFixture.LoadShipped("dimdate.nzreplay.gz");
        await using var server = StartServer(fixture);
        using var connection = OpenConnection(server.Port);

        object? firstViaFullRead;
        using (var full = connection.CreateCommand(fixture.Query))
        using (var r = full.ExecuteReader())
        {
            Assert.True(r.Read());
            firstViaFullRead = r.GetValue(0);
        }

        using (var single = connection.CreateCommand(fixture.Query))
        using (var r = single.ExecuteReader(System.Data.CommandBehavior.SingleRow))
        {
            Assert.True(r.Read());
            Assert.Equal(firstViaFullRead, r.GetValue(0));
            // Second read must report no more rows without decoding the tail.
            Assert.False(r.Read());
        }

        // ExecuteScalar (which uses SingleRow internally) over the same fixture.
        using (var scalarCmd = connection.CreateCommand(fixture.Query))
        {
            var scalar = scalarCmd.ExecuteScalar();
            Assert.Equal(firstViaFullRead, scalar);
        }

        // Connection still in sync for the next query.
        using (var again = connection.CreateCommand(fixture.Query))
        using (var r = again.ExecuteReader())
        {
            int rows = 0;
            while (r.Read())
                rows++;
            Assert.Equal(fixture.ExpectedRows, rows);
        }
    }

    [Fact]
    public async Task Replay_EarlyDisposeAsync_AfterOneRow_ConnectionReusable()
    {
        var fixture = NzReplayFixture.LoadShipped("dimdate.nzreplay.gz");
        await using var server = StartServer(fixture);
        using var connection = OpenConnection(server.Port);

        await using (var command = connection.CreateCommand(fixture.Query))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
        }

        await using (var command2 = connection.CreateCommand(fixture.Query))
        await using (var reader2 = await command2.ExecuteReaderAsync())
        {
            int rows = 0;
            while (await reader2.ReadAsync())
                rows++;
            Assert.Equal(fixture.ExpectedRows, rows);
        }
    }

    [Fact]
    public async Task Maintenance_CancelledToken_LosesNothing()
    {
        var fixture = NzReplayFixture.LoadShipped("dimdate.nzreplay.gz");
        await using var server = StartServer(fixture);
        var pool = new NzConnectionPool(new NzConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Database = "JUST_DATA",
            UserName = "replay",
            Password = "replay",
            Port = server.Port,
            MaxPoolSize = 4,
        });
        try
        {
            // Three genuinely idle physical connections (held at once so the
            // pool cannot reuse a single one).
            var leases = new List<PooledNzConnection>();
            for (int i = 0; i < 3; i++)
                leases.Add(await pool.RentAsync());
            Assert.Equal(3, pool.TotalConnections);
            foreach (var lease in leases)
                await lease.DisposeAsync();
            Assert.Equal(3, pool.IdleCount);

            // Alternate cancelled and live maintenance passes: no pass may
            // lose a connection — every counted slot stays owned.
            for (int i = 0; i < 50; i++)
            {
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => pool.RunMaintenanceForTestsAsync(cancelled.Token));
                Assert.Equal(3, pool.TotalConnections);
                Assert.Equal(3, pool.IdleCount);

                await pool.RunMaintenanceForTestsAsync();
                Assert.Equal(pool.IdleCount + pool.ActiveCount, pool.TotalConnections);
                Assert.Equal(3, pool.TotalConnections);
                Assert.Equal(3, pool.IdleCount);
            }
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    [Fact]
    public async Task Rent_OpenCompletesAfterDispose_DoesNotRegister_NoLeak()
    {
        var fixture = NzReplayFixture.LoadShipped("dimdate.nzreplay.gz");
        await using var server = StartServer(fixture);
        var pool = new NzConnectionPool(new NzConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Database = "JUST_DATA",
            UserName = "replay",
            Password = "replay",
            Port = server.Port,
            MaxPoolSize = 4,
        });

        var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        NzConnection? created = null;
        pool.BeforeRegisterActiveForTests = async conn =>
        {
            created = conn;
            opened.SetResult(true);
            await release.Task.ConfigureAwait(false);
        };

        // Rent opens a connection, then stalls just before registering.
        var rent = pool.RentAsync();
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        Assert.NotNull(created);

        // Teardown completes while the rent is stalled (no active entry yet).
        await pool.DisposeAsync();
        Assert.Equal(0, pool.ActiveCount);

        release.SetResult(true);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => rent);

        // The late Open must not leave a registered connection or count slot.
        Assert.Equal(System.Data.ConnectionState.Closed, created.State);
        Assert.Equal(0, pool.TotalConnections);
        Assert.Equal(0, pool.ActiveCount);
        Assert.Equal(0, pool.IdleCount);
    }

    [Fact]
    public async Task MaintenanceRefill_CancelAfterOpen_DisposesAndReleases()
    {
        var fixture = NzReplayFixture.LoadShipped("dimdate.nzreplay.gz");
        await using var server = StartServer(fixture);
        var pool = new NzConnectionPool(new NzConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Database = "JUST_DATA",
            UserName = "replay",
            Password = "replay",
            Port = server.Port,
            MaxPoolSize = 4,
        });
        try
        {
            int before = pool.TotalConnections;
            var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            NzConnection? created = null;
            using var cts = new CancellationTokenSource();
            pool.BeforeParkIdleForTests = async conn =>
            {
                created = conn;
                opened.SetResult(true);
                // Block until the test cancels: the cancellation lands after
                // a successful Open but before the idle enqueue.
                await Task.Delay(Timeout.Infinite, cts.Token).ConfigureAwait(false);
            };

            var park = pool.TryCreateAndParkIdleAsync(cts.Token);
            Assert.True(await opened.Task.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false));
            Assert.NotNull(created);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => park);

            // Ownership rule: disposed connection, reservation released.
            Assert.Equal(System.Data.ConnectionState.Closed, created.State);
            Assert.Equal(before, pool.TotalConnections);
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    [Fact]
    public async Task PoolLifecycle_Stress_RentReturnMaintenanceCancelDispose()
    {
        var fixture = NzReplayFixture.LoadShipped("dimdate.nzreplay.gz");
        await using var server = StartServer(fixture);
        var pool = new NzConnectionPool(new NzConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Database = "JUST_DATA",
            UserName = "replay",
            Password = "replay",
            Port = server.Port,
            MaxPoolSize = 4,
            ConnectionValidationInterval = 0,
            ConnectionIdleTimeout = 1,
        });
        var stop = new ManualResetEventSlim(false);

        async Task Worker(int id)
        {
            var rnd = new Random(id * 7919 + 13);
            while (!stop.IsSet)
            {
                PooledNzConnection? lease;
                try
                {
                    using var opCts = new CancellationTokenSource();
                    if (rnd.Next(10) == 0)
                        opCts.Cancel();
                    lease = await pool.RentAsync(opCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                try
                {
                    using var cmd = lease.Connection.CreateCommand(fixture.Query);
                    using var reader = cmd.ExecuteReader();
                    int want = rnd.Next(1, 5);
                    int n = 0;
                    while (n < want && reader.Read())
                    {
                        _ = reader.GetValue(0);
                        n++;
                    }
                }
                finally
                {
                    // Must never throw (not even ODE): ReturnAsync is safe
                    // against a concurrently disposed pool by design.
                    await lease.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        async Task Maintainer()
        {
            var rnd = new Random(1234);
            while (!stop.IsSet)
            {
                try
                {
                    using var mcts = new CancellationTokenSource();
                    if (rnd.Next(3) == 0)
                        mcts.Cancel();
                    await pool.RunMaintenanceForTestsAsync(mcts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                await Task.Delay(5).ConfigureAwait(false);
            }
        }

        var workers = Enumerable.Range(0, 6).Select(i => Task.Run(() => Worker(i))).ToArray();
        var maints = Enumerable.Range(0, 2).Select(_ => Task.Run(Maintainer)).ToArray();

        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        // Tear down while workers/maintainers are still in flight.
        var disposers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            await pool.DisposeAsync().ConfigureAwait(false);
        })).ToArray();
        await Task.WhenAll(disposers).ConfigureAwait(false);
        stop.Set();
        await Task.WhenAll(workers).ConfigureAwait(false);
        await Task.WhenAll(maints).ConfigureAwait(false);

        Assert.Equal(0, pool.TotalConnections);
        Assert.Equal(0, pool.ActiveCount);
        Assert.Equal(0, pool.IdleCount);
        Assert.Equal(1, pool.DisposeCoreRunCount);
    }

    private static NzReplayServer StartServer(NzReplayFixture fixture) => NzReplayServer.Start(fixture);

    private static NzConnection OpenConnection(int port)
    {
        var connection = new NzConnection(
            "replay",
            "replay",
            "127.0.0.1",
            "JUST_DATA",
            port,
            SecurityLevelCode.OnlyUnsecuredSession)
        {
            CommandTimeout = Timeout.InfiniteTimeSpan,
        };
        connection.Open();
        return connection;
    }
}
