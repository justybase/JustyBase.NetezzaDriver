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
    // Serializes queue teardown (clear/dispose) against the return path's
    // enqueue section. Rent dequeues lock-free; maintenance drains under it.
    private readonly SemaphoreSlim _idleLock = new(1, 1);
    private readonly ConcurrentQueue<IdleConnection> _idle = new();
    // Keyed by reference: backend Pids are not guaranteed unique (replay/fake
    // connections all report -1), and each physical connection must own its
    // count slot independently for exact total accounting.
    private readonly ConcurrentDictionary<NzConnection, byte> _active = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private int _totalConnections;
    private long _connectionValidationCount;
    private bool _disposed;
    private readonly object _disposeLock = new();
    private Task? _disposeTask;
    internal int DisposeCoreRunCount;
    // Tracks ReturnAsync bodies in flight so teardown can wait for started
    // returns instead of finishing while a return still owns a connection.
    // Guarded by _disposeLock, which also owns the _disposed transition, so a
    // return and pool teardown can never interleave in a way that lets a
    // started return escape the barrier.
    private int _inFlightReturns;
    private TaskCompletionSource? _returnsDrained;
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
    internal bool IsDisposed => _disposed;
    internal long ConnectionValidationCount => Interlocked.Read(ref _connectionValidationCount);
    internal int TotalConnections => Volatile.Read(ref _totalConnections);

    /// <summary>
    /// Test hook mirroring the accounting of a successful rent: tracks the
    /// connection as active and reserves its physical slot.
    /// </summary>
    internal void TrackActiveForTests(NzConnection connection)
    {
        if (TryReserveConnectionSlot())
            TryRegisterActive(connection);
    }

    /// <summary>
    /// Registers a physical connection as active under the dispose lock.
    /// Returns false once the pool is disposed, so a rent whose Open finished
    /// concurrently with teardown can never add a fresh entry after
    /// <see cref="DisposeCoreAsync"/> already reaped <c>_active</c>. Callers
    /// must dispose + release the slot when this returns false.
    /// </summary>
    private bool TryRegisterActive(NzConnection connection)
    {
        lock (_disposeLock)
        {
            if (_disposed)
                return false;
            return _active.TryAdd(connection, 0);
        }
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

        // The wait must also observe pool teardown (a proven .NET gotcha:
        // SemaphoreSlim.Dispose() never completes a parked WaitAsync waiter).
        // A linked CTS is only needed when the caller supplies its own token;
        // the common CancellationToken.None case reuses the dispose token
        // directly and allocates nothing.
        CancellationTokenSource? linkedCts = null;
        CancellationToken rentToken;
        if (!cancellationToken.CanBeCanceled)
        {
            rentToken = _disposeCts.Token;
        }
        else
        {
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
            rentToken = linkedCts.Token;
        }

        bool permitAcquired = false;
        try
        {
            await _semaphore.WaitAsync(rentToken).ConfigureAwait(false);
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
                        isValid = !needsValidation || await IsConnectionValidAsync(candidate, rentToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (rentToken.IsCancellationRequested)
                    {
                        await DisposeConnectionAsync(candidate).ConfigureAwait(false);
                        Interlocked.Decrement(ref _totalConnections);
                        throw;
                    }

                    if (isValid)
                    {
                        // Registration is serialized with teardown: a rent that
                        // loses the race must not add an entry after DisposeAsync
                        // has finished reaping _active.
                        if (!TryRegisterActive(candidate))
                        {
                            await DisposeConnectionAsync(candidate).ConfigureAwait(false);
                            Interlocked.Decrement(ref _totalConnections);
                            throw new ObjectDisposedException(GetType().FullName);
                        }
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
                    await Task.Delay(10, rentToken).ConfigureAwait(false);
                    continue;
                }

                NzConnection connection;
                try
                {
                    connection = await CreateConnectionAsync(rentToken).ConfigureAwait(false);
                }
                catch
                {
                    ReleaseReservation();
                    throw;
                }

                if (BeforeRegisterActiveForTests is not null)
                    await BeforeRegisterActiveForTests(connection).ConfigureAwait(false);

                // Open succeeded but the pool may have been disposed meanwhile.
                if (!TryRegisterActive(connection))
                {
                    await DisposeConnectionAsync(connection).ConfigureAwait(false);
                    ReleaseReservation();
                    throw new ObjectDisposedException(GetType().FullName);
                }
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
        finally
        {
            linkedCts?.Dispose();
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
        bool tracked = TryBeginReturn();
        try
        {
            await ReturnAsyncCore(connection, tracked).ConfigureAwait(false);
        }
        finally
        {
            if (tracked)
                EndReturn();
        }
    }

    /// <summary>
    /// Registers a return as in-flight under the dispose lock. Returns false
    /// once teardown has begun — such returns take the direct-close path and
    /// are never counted, so the barrier captured by teardown is exact.
    /// A fresh completion source is created on each idle→busy transition, so
    /// the barrier is reusable across many rent/return cycles.
    /// </summary>
    private bool TryBeginReturn()
    {
        lock (_disposeLock)
        {
            if (_disposed)
                return false;
            if (_inFlightReturns++ == 0)
                _returnsDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return true;
        }
    }

    private void EndReturn()
    {
        TaskCompletionSource? drained = null;
        lock (_disposeLock)
        {
            if (--_inFlightReturns == 0)
            {
                drained = _returnsDrained;
                _returnsDrained = null;
            }
        }
        drained?.TrySetResult();
    }

    /// <summary>
    /// Test hook invoked once a return has removed its active entry but
    /// before cleanup. Null in production.
    /// </summary>
    internal Func<NzConnection, Task>? BeforeReturnCleanupForTests;

    private async Task ReturnAsyncCore(NzConnection connection, bool tracked)
    {
        bool removed = _active.TryRemove(connection, out _);
        if (!tracked)
        {
            // Started after teardown claimed the pool: close directly and
            // never touch the idle queue or synchronization primitives.
            await DisposeConnectionAsync(connection).ConfigureAwait(false);
            if (removed)
                Interlocked.Decrement(ref _totalConnections);
            return;
        }

        if (BeforeReturnCleanupForTests is not null)
            await BeforeReturnCleanupForTests(connection).ConfigureAwait(false);
        if (_disposed)
        {
            // Pool is dead: close the connection, balance the count only for
            // a slot we actually owned, and never touch the semaphore (its
            // permits die with the pool). Safe against double return: the
            // second call finds nothing to remove and only re-closes
            // idempotently.
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
                // Cancelled by teardown so a long rollback cannot stall
                // DisposeAsync waiting for in-flight returns.
                await connection.RollbackAsync(_disposeCts.Token).ConfigureAwait(false);
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
        // teardown via _idleLock (DisposeAsync clears under the same lock),
        // so a return racing DisposeAsync either re-checks _disposed inside
        // and closes the connection directly, or enqueues while the pool is
        // still alive.
        bool lockAcquired = false;
        try
        {
            try
            {
                // Same parked-waiter gotcha as the semaphore: the dispose
                // token wakes the wait with OCE instead of hanging it.
                // Either signal means the pool is dying: close directly.
                await _idleLock.WaitAsync(_disposeCts.Token).ConfigureAwait(false);
                lockAcquired = true;
            }
            catch (OperationCanceledException) when (_disposed)
            {
                await DisposeConnectionAsync(connection).ConfigureAwait(false);
                if (removed)
                    Interlocked.Decrement(ref _totalConnections);
                return;
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

        // Linked with pool teardown: a parked lock wait must wake on dispose
        // instead of being orphaned by it (SemaphoreSlim.Dispose never
        // completes parked waiters). Cancellation may only prevent *starting*
        // the drain; once entries are dequeued they are settled synchronously
        // with no further cancellation points.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        var ct = linkedCts.Token;

        var nowUtc = DateTime.UtcNow;
        var toRequeue = new List<IdleConnection>();
        var toDispose = new List<NzConnection>();
        int processed = 0;
        const int maxProcessPerCycle = 1000;

        await _idleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (processed < maxProcessPerCycle && _idle.TryDequeue(out var idleEntry))
            {
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

        while (!_disposed && Volatile.Read(ref _totalConnections) < _minPoolSize)
        {
            bool parked;
            try
            {
                parked = await TryCreateAndParkIdleAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                break;
            }
            if (!parked)
                break;
        }
    }

    /// <summary>
    /// Test hook invoked with the freshly opened connection before it is
    /// parked in the idle queue. Null in production.
    /// </summary>
    internal Func<NzConnection, Task>? BeforeParkIdleForTests;

    /// <summary>
    /// Test hook invoked after a rent's Open succeeded and before active
    /// registration. Null in production.
    /// </summary>
    internal Func<NzConnection, Task>? BeforeRegisterActiveForTests;

    /// <summary>
    /// Reserves a slot, opens one physical connection and parks it in the
    /// idle queue. Ownership rule: once the reservation succeeds there are
    /// exactly two outcomes — parked (the reservation becomes the live
    /// connection) or disposed + released. Any exception after a successful
    /// open (cancelled lock wait, disposed pool/lock, probe failure) takes
    /// the second path via try/finally, so it cannot be forgotten.
    /// Returns false when no slot was free. Cancellation is rethrown after
    /// cleanup.
    /// </summary>
    internal async Task<bool> TryCreateAndParkIdleAsync(CancellationToken cancellationToken)
    {
        // Atomic reservation: concurrent rents can never push the total
        // past MaxPoolSize, and a failed open returns the reservation.
        if (!TryReserveConnectionSlot())
            return false;
        NzConnection conn;
        try
        {
            conn = await CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ReleaseReservation();
            throw;
        }
        bool parked = false;
        // Same parked-waiter gotcha as elsewhere: link teardown so a lock
        // wait here wakes on dispose instead of being orphaned by it.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        var ct = linkedCts.Token;
        try
        {
            if (BeforeParkIdleForTests is not null)
                await BeforeParkIdleForTests(conn).ConfigureAwait(false);
            await _idleLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!_disposed)
                {
                    _idle.Enqueue(new IdleConnection(conn, DateTime.UtcNow));
                    parked = true;
                }
            }
            finally
            {
                ReleaseIdleLockSafe();
            }
        }
        finally
        {
            if (!parked)
            {
                await DisposeConnectionAsync(conn).ConfigureAwait(false);
                ReleaseReservation();
            }
        }
        return parked;
    }

    /// <summary>
    /// Test hook driving one maintenance pass directly.
    /// </summary>
    internal Task RunMaintenanceForTestsAsync(CancellationToken cancellationToken = default)
        => CleanupIdleAsync(cancellationToken);

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

    /// <summary>
    /// Drops all idle connections. Observes pool teardown: after dispose it
    /// throws instead of parking a lock wait that teardown could orphan.
    /// </summary>
    public Task ClearAsync()
        => ClearAsyncCore(_disposeCts.Token);

    internal async Task ClearAsyncCore(CancellationToken cancellationToken)
    {
        var toDispose = new List<NzConnection>();
        await _idleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Idempotent shared teardown: the first caller creates exactly one
    /// dispose task, every concurrent caller awaits the same task, so no
    /// caller observes completion before the pool is fully closed. No
    /// double-dispose, no double-decrement, no deadlocks; a concurrent
    /// <see cref="ReturnAsync"/> stays safe via its disposed checks.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        Task task;
        lock (_disposeLock)
        {
            if (_disposed)
            {
                task = _disposeTask ?? Task.CompletedTask;
            }
            else
            {
                _disposed = true;
                // Capture the barrier for exactly the returns already
                // in-flight. Setting _disposed under this same lock means no
                // new tracked return can start after this point, closing the
                // 0→Return race; a fresh TCS per cycle closes the one-shot
                // TCS reuse bug.
                var returnsToAwait = _returnsDrained?.Task;
                task = DisposeCoreAsync(returnsToAwait);
                _disposeTask = task;
            }
        }
        return new ValueTask(task);
    }

    private async Task DisposeCoreAsync(Task? returnsToAwait)
    {
        DisposeCoreRunCount++;

        // Order: mark disposed (caller did), cancel pending work, wait for
        // already-started returns, then clear/reap/dispose primitives.
        _disposeCts.Cancel();
        try
        {
            await _maintenanceTask.ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // A maintenance tick that lost the race with teardown; the queue
            // below is drained deterministically anyway.
        }

        // A ReturnAsync may have removed its entry from _active and still be
        // rolling back / cleaning up. Wait for the barrier captured at
        // dispose time (exact set of returns in-flight then) so no return
        // outlives teardown holding a connection and a count slot.
        if (returnsToAwait is not null)
            await returnsToAwait.ConfigureAwait(false);

        // Tokenless core: every other pool wait is teardown-linked and brief
        // lock holders always release, so this wait cannot be orphaned; the
        // public ClearAsync observes teardown instead.
        await ClearAsyncCore(CancellationToken.None).ConfigureAwait(false);

        // Reap rented connections. Removal is exclusive per key, so a
        // concurrent ReturnAsync can never double-decrement the same slot:
        // whoever removes the entry owns its count.
        foreach (var kvp in _active)
        {
            if (_active.TryRemove(kvp.Key, out _))
            {
                await DisposeConnectionAsync(kvp.Key).ConfigureAwait(false);
                Interlocked.Decrement(ref _totalConnections);
            }
        }

        // NOTE: the two semaphores are deliberately NOT disposed. Proven .NET
        // behavior: SemaphoreSlim.Dispose() unlinks parked WaitAsync waiters
        // without completing them, hanging those tasks forever. Every pool
        // wait is teardown-linked (rent semaphore, idle lock, maintenance) or
        // provably brief, so after cancel no waiter can be left parked — while
        // disposing could orphan one. The instances hold no native resources
        // (AvailableWaitHandle is never used), so this leaks nothing.
    }
}
