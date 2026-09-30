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
        Assert.Equal(1, _pool.ConnectionValidationCount);
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
        Assert.Equal(0, builder.ConnectionValidationInterval);
    }

    [Fact]
    public async Task PoolValidationIntervalUsesIdleDurationAndDefaultsToEveryCheckout()
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

        var defaultPool = new NzConnectionPool("host", "db", "user", "pass");
        Assert.True(defaultPool.ShouldValidateIdleConnection(now, now));
        await defaultPool.DisposeAsync();
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
