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
    private readonly object _disposeLock = new();
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
    internal int TotalConnections => Volatile.Read(ref _totalConnections);

    /// <summary>
    /// Test hook mirroring the accounting of a successful rent: tracks the
    /// connection as active and reserves its physical slot.
    /// </summary>
    internal void TrackActiveForTests(NzConnection connection)
    {
        if (TryReserveConnectionSlot())
            _active.TryAdd(connection.Pid, connection);
    }

    internal bool ShouldValidateIdleConnection(DateTime returnedAtUtc, DateTime nowUtc)
        => _validationInterval == TimeSpan.Zero || nowUtc - returnedAtUtc >= _validationInterval;

    internal bool ShouldValidateIdleConnection(NzConnection connection, DateTime returnedAtUtc, DateTime nowUtc)
        => connection.State != System.Data.ConnectionState.Open
            || IsConnectionExpired(connection)
            || ShouldValidateIdleConnection(returnedAtUtc, nowUtc);

    public async Task<PooledNzConnection> RentAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        bool permitAcquired = false;
        try
        {
            await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            permitAcquired = true;
            if (_disposed)
                throw new ObjectDisposedException(GetType().FullName);

            while (true)
            {
                // Lock-free fast path: ConcurrentQueue.TryDequeue is thread-safe.
                // The previous _idleLock serialized all checkouts without adding
                // correctness (validation happens outside any lock anyway).
                while (_idle.TryDequeue(out var idleEntry))
                {
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

                if (_disposed)
                    throw new ObjectDisposedException(GetType().FullName);

                // The queue looked empty. Maintenance may be holding idle
                // entries off-queue for inspection; creation itself is guarded
                // by an atomic reservation so _totalConnections can never
                // exceed MaxPoolSize. If no slot is free, wait briefly and
                // retry instead of overshooting.
                if (!TryReserveConnectionSlot())
                {
                    await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                NzConnection connection;
                try
                {
                    connection = await CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    ReleaseReservation();
                    throw;
                }
                var connectionPid = connection.Pid;
                _active.TryAdd(connectionPid, connection);
                return new PooledNzConnection(connection, this);
            }
        }
        catch
        {
            // The semaphore may already be disposed if the pool died while
            // this rent was in flight; never mask the original exception.
            if (permitAcquired)
                ReleaseSemaphoreSafe();
            throw;
        }
    }

    /// <summary>
    /// Atomically reserves one physical-connection slot. Returns false when
    /// the pool already owns <see cref="_maxPoolSize"/> connections, so
    /// concurrent creators (rent + maintenance refill) can never overshoot.
    /// Every successful reservation must be paired with either a live
    /// connection or <see cref="ReleaseReservation"/>.
    /// </summary>
    internal bool TryReserveConnectionSlot()
    {
        while (true)
        {
            int current = Volatile.Read(ref _totalConnections);
            if (current >= _maxPoolSize)
                return false;
            if (Interlocked.CompareExchange(ref _totalConnections, current + 1, current) == current)
                return true;
        }
    }

    internal void ReleaseReservation()
        => Interlocked.Decrement(ref _totalConnections);

    private void ReleaseSemaphoreSafe()
    {
        try
        {
            _semaphore.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal async Task ReturnAsync(NzConnection connection)
    {
        var pid = connection.Pid;
        bool removed = _active.TryRemove(pid, out _);
        if (_disposed)
        {
            // Pool is dead: close the connection, balance the count only for
            // a slot we actually owned, and never touch the semaphore (it is
            // disposed). Safe against double return: the second call finds
            // nothing to remove and only re-closes idempotently.
            await DisposeConnectionAsync(connection).ConfigureAwait(false);
            if (removed)
                Interlocked.Decrement(ref _totalConnections);
            return;
        }
        if (connection.State != System.Data.ConnectionState.Open || IsConnectionExpired(connection))
        {
            await DisposeConnectionAsync(connection).ConfigureAwait(false);
            if (removed)
                Interlocked.Decrement(ref _totalConnections);
            ReleaseSemaphoreSafe();
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
            await DisposeConnectionAsync(connection).ConfigureAwait(false);
            if (removed)
                Interlocked.Decrement(ref _totalConnections);
            ReleaseSemaphoreSafe();
            return;
        }

        // The enqueue + permit release below is serialized against pool
        // teardown via _idleLock (DisposeAsync clears under the same lock and
        // only disposes the semaphore/lock afterwards), so a return racing
        // DisposeAsync either re-checks _disposed inside and closes the
        // connection directly, or enqueues while the pool is still alive.
        bool lockAcquired = false;
        try
        {
            try
            {
                await _idleLock.WaitAsync().ConfigureAwait(false);
                lockAcquired = true;
            }
            catch (ObjectDisposedException)
            {
                await DisposeConnectionAsync(connection).ConfigureAwait(false);
                if (removed)
                    Interlocked.Decrement(ref _totalConnections);
                return;
            }

            if (_disposed)
            {
                await DisposeConnectionAsync(connection).ConfigureAwait(false);
                if (removed)
                    Interlocked.Decrement(ref _totalConnections);
                return;
            }

            // Don't let the last result set pin memory on an idle pooled
            // connection: drop result references, then transient buffers.
            connection.ReleaseTransientBuffers();
            connection.ReleaseResultStateForPooling();
            _idle.Enqueue(new IdleConnection(connection, DateTime.UtcNow));
            _semaphore.Release();
        }
        finally
        {
            if (lockAcquired)
                ReleaseIdleLockSafe();
        }
    }

    private void ReleaseIdleLockSafe()
    {
        try
        {
            _idleLock.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Opens a new physical connection. The caller must hold a reservation
    /// from <see cref="TryReserveConnectionSlot"/> and release it if open fails.
    /// </summary>
    private async Task<NzConnection> CreateConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new NzConnection(_user, _password, _host, _database, _port);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    internal async Task<bool> IsConnectionValidAsync(NzConnection connection, CancellationToken cancellationToken)
    {
        if (connection.State != System.Data.ConnectionState.Open || IsConnectionExpired(connection))
            return false;

        Interlocked.Increment(ref _connectionValidationCount);
        // NzCommand.CommandTimeout currently forwards to the shared
        // NzConnection.CommandTimeout, so save/restore around the probe.
        // Otherwise validation would permanently change the application's
        // timeout on the physical connection.
        var originalTimeout = connection.CommandTimeout;
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
        finally
        {
            connection.CommandTimeout = originalTimeout;
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

        while (Volatile.Read(ref _totalConnections) < _minPoolSize)
        {
            // Atomic reservation: concurrent rents can never push the total
            // past MaxPoolSize, and a failed open returns the reservation.
            if (!TryReserveConnectionSlot())
                break;
            NzConnection conn;
            try
            {
                conn = await CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                ReleaseReservation();
                break;
            }
            try
            {
                await _idleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (_disposed)
                    {
                        await DisposeConnectionAsync(conn).ConfigureAwait(false);
                        ReleaseReservation();
                    }
                    else
                    {
                        _idle.Enqueue(new IdleConnection(conn, DateTime.UtcNow));
                    }
                }
                finally
                {
                    ReleaseIdleLockSafe();
                }
            }
            catch (ObjectDisposedException)
            {
                await DisposeConnectionAsync(conn).ConfigureAwait(false);
                ReleaseReservation();
                break;
            }
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
        lock (_disposeLock)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        _disposeCts.Cancel();
        await _maintenanceTask.ConfigureAwait(false);

        await ClearAsync().ConfigureAwait(false);

        // Reap rented connections. Removal is exclusive per key, so a
        // concurrent ReturnAsync can never double-decrement the same slot:
        // whoever removes the entry owns its count.
        foreach (var kvp in _active)
        {
            if (_active.TryRemove(kvp.Key, out var conn))
            {
                await DisposeConnectionAsync(conn).ConfigureAwait(false);
                Interlocked.Decrement(ref _totalConnections);
            }
        }

        // Disposed after ClearAsync: in-flight ReturnAsync calls either hold
        // _idleLock (and re-check _disposed before touching the queue) or
        // observe the disposed lock/semaphore and close their connection
        // directly instead of throwing.
        _semaphore.Dispose();
        _idleLock.Dispose();
    }
}
