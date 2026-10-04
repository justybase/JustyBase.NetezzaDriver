using System.Collections.Concurrent;

namespace JustyBase.NetezzaDriver;

public sealed class NzConnectionPool : IAsyncDisposable
{
    private readonly record struct IdleConnection(NzConnection Connection, DateTime ReturnedAtUtc);

    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromSeconds(30);
    private const int ValidationTimeoutSeconds = 5;

    private readonly string _host;
    private readonly string _database;
    private readonly string _user;
    private readonly string _password;
    private readonly int _port;
    private readonly int _minPoolSize;
    private readonly int _maxPoolSize;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeSpan _maxLifetime;
    private readonly TimeSpan _validationInterval;
    private readonly SemaphoreSlim _semaphore;
    // Guards only the multi-step maintenance/clear drains that dequeue and may
    // re-enqueue. Rent/return use ConcurrentQueue atomically without this lock.
    private readonly SemaphoreSlim _idleLock = new(1, 1);
    private readonly ConcurrentQueue<IdleConnection> _idle = new();
    private readonly ConcurrentDictionary<int, NzConnection> _active = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private int _totalConnections;
    private long _connectionValidationCount;
    private bool _disposed;
    private readonly Task _maintenanceTask;

    public NzConnectionPool(string host, string database, string user, string password,
        int port = 5480, int minPoolSize = 0, int maxPoolSize = 10,
        int connectionIdleTimeoutSeconds = 30, int connectionLifetimeSeconds = 0)
        : this(host, database, user, password, port, minPoolSize, maxPoolSize,
              connectionIdleTimeoutSeconds, connectionLifetimeSeconds,
              NzConnectionStringBuilder.DefaultConnectionValidationInterval)
    {
    }

    private NzConnectionPool(string host, string database, string user, string password,
        int port, int minPoolSize, int maxPoolSize, int connectionIdleTimeoutSeconds,
        int connectionLifetimeSeconds, int connectionValidationIntervalSeconds)
    {
        if (connectionValidationIntervalSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(connectionValidationIntervalSeconds));

        _host = host ?? throw new ArgumentNullException(nameof(host));
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _user = user ?? throw new ArgumentNullException(nameof(user));
        _password = password ?? throw new ArgumentNullException(nameof(password));
        _port = port;
        _minPoolSize = minPoolSize;
        _maxPoolSize = maxPoolSize > 0 ? maxPoolSize : 10;
        _idleTimeout = TimeSpan.FromSeconds(connectionIdleTimeoutSeconds > 0 ? connectionIdleTimeoutSeconds : 30);
        _maxLifetime = connectionLifetimeSeconds > 0 ? TimeSpan.FromSeconds(connectionLifetimeSeconds) : TimeSpan.MaxValue;
        _validationInterval = TimeSpan.FromSeconds(connectionValidationIntervalSeconds);
        _semaphore = new SemaphoreSlim(_maxPoolSize, _maxPoolSize);
        _maintenanceTask = Task.Run(() => RunMaintenanceLoopAsync(_disposeCts.Token));
    }

    public NzConnectionPool(NzConnectionStringBuilder builder)
        : this(builder.Host, builder.Database, builder.UserName, builder.Password,
              builder.Port, builder.MinPoolSize, builder.MaxPoolSize,
              builder.ConnectionIdleTimeout, builder.ConnectionLifetime,
              builder.ConnectionValidationInterval)
    {
    }

    public int ActiveCount => _active.Count;
    public int IdleCount => _idle.Count;
    public int MaxPoolSize => _maxPoolSize;
    internal long ConnectionValidationCount => Interlocked.Read(ref _connectionValidationCount);

    internal bool ShouldValidateIdleConnection(DateTime returnedAtUtc, DateTime nowUtc)
        => _validationInterval == TimeSpan.Zero || nowUtc - returnedAtUtc >= _validationInterval;

    internal bool ShouldValidateIdleConnection(NzConnection connection, DateTime returnedAtUtc, DateTime nowUtc)
        => connection.State != System.Data.ConnectionState.Open
            || IsConnectionExpired(connection)
            || ShouldValidateIdleConnection(returnedAtUtc, nowUtc);

    public async Task<PooledNzConnection> RentAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            while (true)
            {
                // Lock-free fast path: ConcurrentQueue.TryDequeue is thread-safe.
                // The previous _idleLock serialized all checkouts without adding
                // correctness (validation happens outside any lock anyway).
                if (!_idle.TryDequeue(out var idleEntry))
                    break;

                var candidate = idleEntry.Connection;
                bool needsValidation = ShouldValidateIdleConnection(candidate, idleEntry.ReturnedAtUtc, DateTime.UtcNow);
                bool isValid;
                try
                {
                    isValid = !needsValidation || await IsConnectionValidAsync(candidate, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    await DisposeConnectionAsync(candidate).ConfigureAwait(false);
                    Interlocked.Decrement(ref _totalConnections);
                    throw;
                }

                if (isValid)
                {
                    var pid = candidate.Pid;
                    _active.TryAdd(pid, candidate);
                    return new PooledNzConnection(candidate, this);
                }
                await DisposeConnectionAsync(candidate).ConfigureAwait(false);
                Interlocked.Decrement(ref _totalConnections);
            }

            var connection = await CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
            var connectionPid = connection.Pid;
            _active.TryAdd(connectionPid, connection);
            return new PooledNzConnection(connection, this);
        }
        catch
        {
            _semaphore.Release();
            throw;
        }
    }

    internal async Task ReturnAsync(NzConnection connection)
    {
        var pid = connection.Pid;
        if (_disposed || connection.State != System.Data.ConnectionState.Open || IsConnectionExpired(connection))
        {
            _active.TryRemove(pid, out _);
            await DisposeConnectionAsync(connection).ConfigureAwait(false);
            Interlocked.Decrement(ref _totalConnections);
            _semaphore.Release();
            return;
        }

        try
        {
            if (connection.InTransaction)
            {
                // Async path: must not block the pool with sync network I/O.
                await connection.RollbackAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            _active.TryRemove(pid, out _);
            await DisposeConnectionAsync(connection).ConfigureAwait(false);
            Interlocked.Decrement(ref _totalConnections);
            _semaphore.Release();
            return;
        }

        _active.TryRemove(pid, out _);
        // Don't let an unusually large last command/message pin memory on an
        // idle pooled connection.
        connection.ReleaseScratchBuffers();
        // Lock-free fast path: ConcurrentQueue.Enqueue is thread-safe.
        _idle.Enqueue(new IdleConnection(connection, DateTime.UtcNow));
        _semaphore.Release();
    }

    private async Task<NzConnection> CreateConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new NzConnection(_user, _password, _host, _database, _port);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _totalConnections);
        return connection;
    }

    private async Task<bool> IsConnectionValidAsync(NzConnection connection, CancellationToken cancellationToken)
    {
        if (connection.State != System.Data.ConnectionState.Open || IsConnectionExpired(connection))
            return false;

        Interlocked.Increment(ref _connectionValidationCount);
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT 1";
            cmd.CommandTimeout = ValidationTimeoutSeconds;
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private bool IsConnectionExpired(NzConnection connection)
    {
        if (_maxLifetime == TimeSpan.MaxValue)
            return false;
        return (DateTime.UtcNow - connection.CreatedAt) > _maxLifetime;
    }

    private bool IsIdleConnectionExpired(IdleConnection idleConnection, DateTime nowUtc)
    {
        if (_idleTimeout == TimeSpan.MaxValue)
            return false;

        return (nowUtc - idleConnection.ReturnedAtUtc) > _idleTimeout;
    }

    private async Task RunMaintenanceLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(MaintenanceInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await CleanupIdleAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task CleanupIdleAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
            return;

        var nowUtc = DateTime.UtcNow;
        var toRequeue = new List<IdleConnection>();
        var toDispose = new List<NzConnection>();
        int processed = 0;
        const int maxProcessPerCycle = 1000;

        await _idleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (processed < maxProcessPerCycle && _idle.TryDequeue(out var idleEntry))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var conn = idleEntry.Connection;
                if (conn.State != System.Data.ConnectionState.Open || IsConnectionExpired(conn) || IsIdleConnectionExpired(idleEntry, nowUtc))
                {
                    toDispose.Add(conn);
                }
                else
                {
                    toRequeue.Add(idleEntry);
                }
                processed++;
            }

            foreach (var entry in toRequeue)
            {
                _idle.Enqueue(entry);
            }
        }
        finally
        {
            _idleLock.Release();
        }

        foreach (var conn in toDispose)
        {
            try
            {
                await DisposeConnectionAsync(conn).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _totalConnections);
            }
        }

        var currentTotal = Volatile.Read(ref _totalConnections);
        while (currentTotal < _minPoolSize && currentTotal < _maxPoolSize)
        {
            try
            {
                var conn = await CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
                await _idleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    _idle.Enqueue(new IdleConnection(conn, DateTime.UtcNow));
                }
                finally
                {
                    _idleLock.Release();
                }
            }
            catch
            {
                break;
            }
            currentTotal = Volatile.Read(ref _totalConnections);
        }
    }

    private static async ValueTask DisposeConnectionAsync(NzConnection connection)
    {
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    public async Task ClearAsync()
    {
        var toDispose = new List<NzConnection>();
        await _idleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            while (_idle.TryDequeue(out var idleEntry))
            {
                toDispose.Add(idleEntry.Connection);
            }
        }
        finally
        {
            _idleLock.Release();
        }

        foreach (var connection in toDispose)
        {
            await DisposeConnectionAsync(connection).ConfigureAwait(false);
            Interlocked.Decrement(ref _totalConnections);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        _disposeCts.Cancel();
        await _maintenanceTask.ConfigureAwait(false);

        await ClearAsync().ConfigureAwait(false);

        foreach (var kvp in _active)
        {
            await DisposeConnectionAsync(kvp.Value).ConfigureAwait(false);
        }
        _active.Clear();

        _semaphore.Dispose();
        _idleLock.Dispose();
    }
}
