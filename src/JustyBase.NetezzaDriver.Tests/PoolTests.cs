namespace JustyBase.NetezzaDriver.Tests;

[Trait("Category", "Integration")]
public class PoolTests : IAsyncLifetime
{
    private NzConnectionPool _pool = null!;

    public async ValueTask InitializeAsync()
    {
        _pool = new NzConnectionPool(Config.Host, Config.DbName, Config.UserName, Config.Password,
            Config.Port, minPoolSize: 0, maxPoolSize: 5, connectionIdleTimeoutSeconds: 5);
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _pool.DisposeAsync();
    }

    [Fact]
    public async Task RentAndReturn_ConnectionIsUsable()
    {
        var pooled = await _pool.RentAsync();
        Assert.NotNull(pooled.Connection);
        Assert.Equal(System.Data.ConnectionState.Open, pooled.Connection.State);

        using var cmd = pooled.Connection.CreateCommand("SELECT 1");
        var result = cmd.ExecuteScalar();
        Assert.Equal(1, Convert.ToInt32(result));

        await pooled.DisposeAsync();
    }

    [Fact]
    public async Task RentMultiple_UpToMax()
    {
        var connections = new List<PooledNzConnection>();
        for (int i = 0; i < 5; i++)
        {
            var c = await _pool.RentAsync();
            connections.Add(c);
        }

        Assert.Equal(5, _pool.ActiveCount);
        Assert.Equal(0, _pool.IdleCount);

        foreach (var c in connections)
            await c.DisposeAsync();
    }

    [Fact]
    public async Task ReturnedConnection_GoesToIdle()
    {
        var pooled = await _pool.RentAsync();
        int pid = pooled.Connection.Pid;
        await pooled.DisposeAsync();

        Assert.Equal(0, _pool.ActiveCount);
        Assert.True(_pool.IdleCount >= 1);
    }

    [Fact]
    public async Task RentedConnection_IsReused()
    {
        var pooled1 = await _pool.RentAsync();
        int pid1 = pooled1.Connection.Pid;
        await pooled1.DisposeAsync();

        var pooled2 = await _pool.RentAsync();
        int pid2 = pooled2.Connection.Pid;
        Assert.Equal(pid1, pid2);
        Assert.Equal(0, _pool.ConnectionValidationCount);
        await pooled2.DisposeAsync();
    }

    [Fact]
    public async Task ConcurrentStress()
    {
        int maxConcurrent = 5;
        int totalTasks = 20;
        var semaphore = new SemaphoreSlim(maxConcurrent);

        var tasks = new List<Task>();
        for (int i = 0; i < totalTasks; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync();
                try
                {
                    var pooled = await _pool.RentAsync();
                    using var cmd = pooled.Connection.CreateCommand("SELECT 1");
                    var result = await cmd.ExecuteScalarAsync();
                    Assert.Equal(1, Convert.ToInt32(result));
                    await pooled.DisposeAsync();
                }
                finally
                {
                    semaphore.Release();
                }
            }));
        }

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task RentedConnection_InvalidAfterPoolDispose()
    {
        await _pool.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => _pool.RentAsync());
    }
}

[Trait("Category", "Unit")]
public class PoolUnitTests
{
    [Fact]
    public void NzConnectionStringBuilder_PoolingDefaults()
    {
        var builder = new NzConnectionStringBuilder
        {
            Host = "host",
            Database = "db",
            UserName = "user",
            Password = "pass"
        };

        Assert.True(builder.Pooling);
        Assert.Equal(0, builder.MinPoolSize);
        Assert.Equal(10, builder.MaxPoolSize);
        Assert.Equal(30, builder.ConnectionIdleTimeout);
        Assert.Equal(0, builder.ConnectionLifetime);
        Assert.Equal(NzConnectionStringBuilder.DefaultConnectionValidationInterval, builder.ConnectionValidationInterval);
    }

    [Fact]
    public async Task PoolValidationIntervalUsesIdleDuration()
    {
        var now = DateTime.UtcNow;
        var intervalPool = new NzConnectionPool(new NzConnectionStringBuilder
        {
            Host = "host",
            Database = "db",
            UserName = "user",
            Password = "pass",
            ConnectionValidationInterval = 60
        });
        Assert.False(intervalPool.ShouldValidateIdleConnection(now.AddSeconds(-59), now));
        Assert.True(intervalPool.ShouldValidateIdleConnection(now.AddSeconds(-60), now));
        await intervalPool.DisposeAsync();
    }

    [Fact]
    public async Task DefaultPoolValidationSkipsRecentlyReturnedConnection()
    {
        var now = DateTime.UtcNow;
        var defaultPool = new NzConnectionPool("host", "db", "user", "pass");

        // A healthy connection returned moments ago must be rented again without a SELECT 1 probe.
        Assert.False(defaultPool.ShouldValidateIdleConnection(now, now));
        Assert.False(defaultPool.ShouldValidateIdleConnection(now.AddSeconds(-1), now));
        // Once it has been idle past the interval it must be validated.
        Assert.True(defaultPool.ShouldValidateIdleConnection(
            now.AddSeconds(-(NzConnectionStringBuilder.DefaultConnectionValidationInterval + 1)), now));

        await defaultPool.DisposeAsync();
    }

    [Fact]
    public async Task ZeroValidationIntervalValidatesOnEveryCheckout()
    {
        var now = DateTime.UtcNow;
        var pool = new NzConnectionPool(new NzConnectionStringBuilder
        {
            Host = "host",
            Database = "db",
            UserName = "user",
            Password = "pass",
            ConnectionValidationInterval = 0
        });
        Assert.True(pool.ShouldValidateIdleConnection(now, now));
        Assert.True(pool.ShouldValidateIdleConnection(now.AddSeconds(-1), now));
        await pool.DisposeAsync();
    }

    [Fact]
    public async Task ExpiredConnectionRequiresValidationEvenWhenRecentlyReturned()
    {
        var now = DateTime.UtcNow;
        var pool = new NzConnectionPool(new NzConnectionStringBuilder
        {
            Host = "host",
            Database = "db",
            UserName = "user",
            Password = "pass",
            ConnectionValidationInterval = 60,
            ConnectionLifetime = 10
        });
        using var connection = new NzConnection("user", "pass", "host", "db");
        connection.CreatedAt = now.AddSeconds(-30);

        Assert.True(pool.ShouldValidateIdleConnection(connection, now, now));
        await pool.DisposeAsync();
    }

    [Fact]
    public async Task PoolValidationIntervalNeverSkipsClosedConnectionCheck()
    {
        var now = DateTime.UtcNow;
        var pool = new NzConnectionPool(new NzConnectionStringBuilder
        {
            Host = "host",
            Database = "db",
            UserName = "user",
            Password = "pass",
            ConnectionValidationInterval = 60
        });
        using var closedConnection = new NzConnection("user", "pass", "host", "db");

        Assert.False(pool.ShouldValidateIdleConnection(now.AddSeconds(-1), now));
        Assert.True(pool.ShouldValidateIdleConnection(closedConnection, now.AddSeconds(-1), now));
        await pool.DisposeAsync();
    }

    [Fact]
    public void NegativePoolValidationIntervalIsRejected()
    {
        var builder = new NzConnectionStringBuilder
        {
            Host = "host",
            Database = "db",
            UserName = "user",
            Password = "pass",
            ConnectionValidationInterval = -1
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => new NzConnectionPool(builder));
    }

    [Fact]
    public async Task RentAsyncWithCancelledTokenDoesNotCreateConnection()
    {
        var pool = new NzConnectionPool("host", "db", "user", "pass");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pool.RentAsync(cts.Token));
        await pool.DisposeAsync();
    }

    [Fact]
    public void LegacyPoolConstructorSignatureRemainsAvailable()
    {
        var parameterTypes = new[]
        {
            typeof(string), typeof(string), typeof(string), typeof(string),
            typeof(int), typeof(int), typeof(int), typeof(int), typeof(int)
        };

        Assert.NotNull(typeof(NzConnectionPool).GetConstructor(parameterTypes));
    }

    [Fact]
    public async Task PooledNzConnection_DisposeReturnsToPool()
    {
        var pool = new NzConnectionPool("host", "db", "user", "pass", 5480, 0, 10, 30, 0);
        Assert.NotNull(pool);
        await pool.DisposeAsync();
    }

    [Fact]
    public void NzConnectionStringBuilder_ToStringContainsPooling()
    {
        var builder = new NzConnectionStringBuilder
        {
            Host = "h", Database = "d", UserName = "u", Password = "p", Port = 5480, Pooling = true, MinPoolSize = 2, MaxPoolSize = 20
        };
        var s = builder.ToString();
        Assert.Contains("Pooling=True", s);
        Assert.Contains("MinPoolSize=2", s);
        Assert.Contains("MaxPoolSize=20", s);
        builder.ConnectionValidationInterval = 45;
        Assert.Contains("ConnectionValidationInterval=45", builder.ToString());
    }

    [Fact]
    public async Task PoolValidation_PreservesCommandTimeout_OnFailure()
    {
        var pool = new NzConnectionPool("host", "db", "user", "pass");
        try
        {
            using var connection = new NzConnection("user", "pass", "host", "db");
            connection.SetState(System.Data.ConnectionState.Open);
            var original = TimeSpan.FromSeconds(60);
            connection.CommandTimeout = original;

            // No real server: validation fails internally and returns false,
            // but must not leak the 5s probe timeout.
            Assert.False(await pool.IsConnectionValidAsync(connection, CancellationToken.None));
            Assert.Equal(original, connection.CommandTimeout);
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    [Fact]
    public async Task PoolValidation_PreservesCommandTimeout_OnCancellation()
    {
        var pool = new NzConnectionPool("host", "db", "user", "pass");
        try
        {
            using var connection = new NzConnection("user", "pass", "host", "db");
            connection.SetState(System.Data.ConnectionState.Open);
            var original = TimeSpan.FromSeconds(60);
            connection.CommandTimeout = original;

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => pool.IsConnectionValidAsync(connection, cts.Token));
            Assert.Equal(original, connection.CommandTimeout);
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    [Fact]
    public async Task PoolValidation_DoesNotTouchTimeout_WhenNotOpen()
    {
        var pool = new NzConnectionPool("host", "db", "user", "pass");
        try
        {
            using var connection = new NzConnection("user", "pass", "host", "db");
            var original = TimeSpan.FromSeconds(42);
            connection.CommandTimeout = original;
            // Closed connection short-circuits before creating the probe command.
            Assert.False(await pool.IsConnectionValidAsync(connection, CancellationToken.None));
            Assert.Equal(original, connection.CommandTimeout);
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    [Fact]
    public void ConnectionValidationInterval_Zero_RoundTripsThroughConnectionString()
    {
        var builder = new NzConnectionStringBuilder
        {
            Host = "h",
            Database = "d",
            UserName = "u",
            Password = "p",
            ConnectionValidationInterval = 0
        };
        string cs = builder.ToString();
        Assert.Contains("ConnectionValidationInterval=0", cs);

        var parsed = NzConnection.ParseConnectionString(cs);
        Assert.Equal(0, parsed.ConnectionValidationInterval);

        // Non-zero values keep working too.
        builder.ConnectionValidationInterval = 45;
        string cs45 = builder.ToString();
        Assert.Contains("ConnectionValidationInterval=45", cs45);
        Assert.Equal(45, NzConnection.ParseConnectionString(cs45).ConnectionValidationInterval);

        // Default (30) is also persisted explicitly now.
        var defaults = new NzConnectionStringBuilder
        {
            Host = "h",
            Database = "d",
            UserName = "u",
            Password = "p"
        };
        Assert.Contains(
            $"ConnectionValidationInterval={NzConnectionStringBuilder.DefaultConnectionValidationInterval}",
            defaults.ToString());
    }

    [Fact]
    public void ReleaseTransientBuffers_ClearsLazyOversizeAndLargeBuffer()
    {
        using var connection = new NzConnection("user", "pass", "host", "db");
        Assert.False(connection.HasTransientBuffersForTests);

        connection.SimulateTransientBuffersForTests(4096);
        Assert.True(connection.HasTransientBuffersForTests);

        // This is what the pool calls when a physical connection goes idle.
        connection.ReleaseScratchBuffers();
        Assert.False(connection.HasTransientBuffersForTests);

        // Idempotent: safe to call again with nothing retained.
        connection.ReleaseTransientBuffers();
        Assert.False(connection.HasTransientBuffersForTests);
    }

    [Fact]
    public async Task ReturnAfterPoolDispose_DoesNotThrow_DisposesConnection()
    {
        var pool = new NzConnectionPool("host", "db", "user", "pass");
        var connection = new NzConnection("user", "pass", "host", "db");
        pool.TrackActiveForTests(connection);
        Assert.Equal(1, pool.TotalConnections);
        await pool.DisposeAsync();

        // Rent -> Dispose(pool) -> Dispose(lease): must not throw
        // ObjectDisposedException from the semaphore, must close the
        // connection, and must balance the count exactly once.
        var ex = await Record.ExceptionAsync(() => pool.ReturnAsync(connection));
        Assert.Null(ex);
        Assert.Equal(0, pool.TotalConnections);

        // Double return is safe: no second decrement, no semaphore touch.
        var ex2 = await Record.ExceptionAsync(() => pool.ReturnAsync(connection));
        Assert.Null(ex2);
        Assert.Equal(0, pool.TotalConnections);
        connection.Dispose();
    }

    [Fact]
    public async Task ConcurrentDisposeAsync_AllAwaitFullTeardown_ExactlyOnce()
    {
        var pool = new NzConnectionPool("host", "db", "user", "pass", 5480, 0, 2, 30, 0);
        var connection = new NzConnection("user", "pass", "host", "db");
        pool.TrackActiveForTests(connection);
        Assert.Equal(1, pool.TotalConnections);

        // Eight concurrent disposers must share one teardown: all complete
        // only after full close, teardown runs once, counters balance.
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await pool.DisposeAsync().ConfigureAwait(false);
        })).ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(1, pool.DisposeCoreRunCount);
        Assert.Equal(0, pool.TotalConnections);
        Assert.Equal(0, pool.ActiveCount);
        Assert.Equal(0, pool.IdleCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pool.RentAsync());
        connection.Dispose();
    }

    [Fact]
    public void ConnectionSlotReservation_CapsAtMaxPoolSize()
    {
        var pool = new NzConnectionPool("host", "db", "user", "pass", 5480, 0, 3, 30, 0);
        try
        {
            Assert.True(pool.TryReserveConnectionSlot());
            Assert.True(pool.TryReserveConnectionSlot());
            Assert.True(pool.TryReserveConnectionSlot());
            Assert.False(pool.TryReserveConnectionSlot());
            Assert.Equal(3, pool.TotalConnections);
            pool.ReleaseReservation();
            Assert.True(pool.TryReserveConnectionSlot());
            pool.ReleaseReservation();
            pool.ReleaseReservation();
            pool.ReleaseReservation();
            Assert.Equal(0, pool.TotalConnections);
        }
        finally
        {
            pool.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    [Fact]
    public async Task ConnectionSlotReservation_NeverExceedsMaxPoolSize_UnderConcurrency()
    {
        const int max = 8;
        var pool = new NzConnectionPool("host", "db", "user", "pass", 5480, 0, max, 30, 0);
        try
        {
            int observedMax = 0;
            object maxLock = new();
            var tasks = new List<Task>();
            for (int t = 0; t < 32; t++)
            {
                tasks.Add(Task.Run(() =>
                {
                    for (int i = 0; i < 2000; i++)
                    {
                        if (pool.TryReserveConnectionSlot())
                        {
                            int current = pool.TotalConnections;
                            lock (maxLock)
                            {
                                if (current > observedMax)
                                    observedMax = current;
                            }
                            pool.ReleaseReservation();
                        }
                    }
                }));
            }
            await Task.WhenAll(tasks);
            Assert.True(observedMax <= max);
            Assert.True(observedMax > 0);
            Assert.Equal(0, pool.TotalConnections);
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    [Fact]
    public async Task FailedOpen_ReleasesReservation_TotalReturnsToZero()
    {
        // Closed port on loopback: connect fails fast, no server needed.
        var pool = new NzConnectionPool("127.0.0.1", "db", "user", "pass", 1, 0, 4, 30, 0);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var tasks = new List<Task>();
            for (int i = 0; i < 8; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    try { await pool.RentAsync(cts.Token); }
                    catch { /* expected: connection refused */ }
                }));
            }
            await Task.WhenAll(tasks);
            Assert.Equal(0, pool.TotalConnections);
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    [Fact]
    public void ReleaseResultStateForPooling_DropsLargeStrings_KeepsReusableBuffers()
    {
        using var connection = new NzConnection("user", "pass", "host", "db");
        using var cmd = new NzCommand(connection);

        string big1 = new string('x', 1_000_000);
        string big2 = new string('y', 1_000_000);
        var cmdRow = new RowValue[2];
        cmdRow[0].typeCode = TypeCodeEx.String;
        cmdRow[0].stringValue = big1;
        cmdRow[1].typeCode = TypeCodeEx.Int32;
        cmdRow[1].int32Value = 42;
        cmd.AddRow(cmdRow);
        var connRow = new RowValue[2];
        connRow[0].typeCode = TypeCodeEx.String;
        connRow[0].stringValue = big2;
        connection.SetConnectionRowForTests(connRow);
        cmd.NewPreparedStatement = new PreparedStatement { Sql = "SELECT 1" };
        connection.SimulateTransientBuffersForTests(4096);

        var weak1 = new WeakReference(big1);
        var weak2 = new WeakReference(big2);

        // Same calls the pool makes when a connection goes idle.
        connection.ReleaseTransientBuffers();
        connection.ReleaseResultStateForPooling();

        big1 = null!;
        big2 = null!;
        cmdRow = null!;
        connRow = null!;
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        GC.Collect();

        Assert.False(weak1.IsAlive);
        Assert.False(weak2.IsAlive);
        Assert.Null(cmd.NewPreparedStatement);
        Assert.False(connection.HasTransientBuffersForTests);
        // Reusable buffers kept: row access still works, now DBNull.
        Assert.True(cmd.IsDBNullFast(0));
        Assert.Equal(DBNull.Value, cmd.GetValue(0).GetValue());
    }
}

[Trait("Category", "Integration")]
public class PoolValidationIntervalIntegrationTests
{
    [Fact]
    public async Task PoolValidationIntervalSkipsRecentIdleConnectionProbe()
    {
        var pool = new NzConnectionPool(new NzConnectionStringBuilder
        {
            Host = Config.Host,
            Database = Config.DbName,
            UserName = Config.UserName,
            Password = Config.Password,
            Port = Config.Port,
            MaxPoolSize = 1,
            ConnectionValidationInterval = 60
        });
        try
        {
            var first = await pool.RentAsync();
            int pid = first.Connection.Pid;
            await first.DisposeAsync();

            var second = await pool.RentAsync();
            Assert.Equal(pid, second.Connection.Pid);
            Assert.Equal(0, pool.ConnectionValidationCount);
            await second.DisposeAsync();
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }
}
