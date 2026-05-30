using System.Collections.Concurrent;
using System.Diagnostics;

namespace JustyBase.NetezzaDriver;

public sealed class NzConnectionPool : IAsyncDisposable
{
    private readonly string _host;
    private readonly string _database;
    private readonly string _user;
    private readonly string _password;
    private readonly int _port;
    private readonly int _minPoolSize;
    private readonly int _maxPoolSize;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeSpan _maxLifetime;
    private readonly SemaphoreSlim _semaphore;
    private readonly ConcurrentQueue<NzConnection> _idle = new();
    private readonly ConcurrentDictionary<int, NzConnection> _active = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private int _totalConnections;
    private bool _disposed;
    private Timer? _maintenanceTimer;

    public NzConnectionPool(string host, string database, string user, string password,
        int port = 5480, int minPoolSize = 0, int maxPoolSize = 10,
        int connectionIdleTimeoutSeconds = 30, int connectionLifetimeSeconds = 0)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _user = user ?? throw new ArgumentNullException(nameof(user));
        _password = password ?? throw new ArgumentNullException(nameof(password));
        _port = port;
        _minPoolSize = minPoolSize;
        _maxPoolSize = maxPoolSize > 0 ? maxPoolSize : 10;
        _idleTimeout = TimeSpan.FromSeconds(connectionIdleTimeoutSeconds > 0 ? connectionIdleTimeoutSeconds : 30);
        _maxLifetime = connectionLifetimeSeconds > 0 ? TimeSpan.FromSeconds(connectionLifetimeSeconds) : TimeSpan.MaxValue;
        _semaphore = new SemaphoreSlim(_maxPoolSize, _maxPoolSize);
        _maintenanceTimer = new Timer(_ => CleanupIdle(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public NzConnectionPool(NzConnectionStringBuilder builder)
        : this(builder.Host, builder.Database, builder.UserName, builder.Password,
              builder.Port, builder.MinPoolSize, builder.MaxPoolSize,
              builder.ConnectionIdleTimeout, builder.ConnectionLifetime)
    {
    }

    public int ActiveCount => _active.Count;
    public int IdleCount => _idle.Count;
    public int MaxPoolSize => _maxPoolSize;

    public async Task<PooledNzConnection> RentAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            while (_idle.TryDequeue(out var candidate))
            {
                if (IsConnectionValid(candidate))
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
        if (_disposed || connection.State != System.Data.ConnectionState.Open || IsConnectionExpired(connection))
        {
            await DisposeConnectionAsync(connection).ConfigureAwait(false);
            Interlocked.Decrement(ref _totalConnections);
            _semaphore.Release();
            return;
        }

        try
        {
            if (connection.InTransaction)
            {
                connection.Rollback();
            }
        }
        catch
        {
            await DisposeConnectionAsync(connection).ConfigureAwait(false);
            Interlocked.Decrement(ref _totalConnections);
            _semaphore.Release();
            return;
        }

        var pid = connection.Pid;
        _active.TryRemove(pid, out _);
        _idle.Enqueue(connection);
        _semaphore.Release();
    }

    private async Task<NzConnection> CreateConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new NzConnection(_user, _password, _host, _database, _port);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _totalConnections);
        return connection;
    }

    private bool IsConnectionValid(NzConnection connection)
    {
        if (connection.State != System.Data.ConnectionState.Open)
            return false;
        if (IsConnectionExpired(connection))
            return false;

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT 1";
            cmd.CommandTimeout = 0;
            using var reader = cmd.ExecuteReader();
            return reader.Read();
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

    private void CleanupIdle()
    {
        if (_disposed)
            return;

        int cleaned = 0;
        while (_idle.TryDequeue(out var conn))
        {
            if (conn.State != System.Data.ConnectionState.Open || IsConnectionExpired(conn))
            {
                DisposeConnectionAsync(conn).GetAwaiter().GetResult();
                Interlocked.Decrement(ref _totalConnections);
                cleaned++;
            }
            else
            {
                _idle.Enqueue(conn);
                break;
            }
        }

        while (_totalConnections < _minPoolSize && _totalConnections < _maxPoolSize)
        {
            try
            {
                var conn = CreateConnectionAsync(CancellationToken.None).GetAwaiter().GetResult();
                _idle.Enqueue(conn);
            }
            catch
            {
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
        while (_idle.TryDequeue(out var conn))
        {
            await DisposeConnectionAsync(conn).ConfigureAwait(false);
            Interlocked.Decrement(ref _totalConnections);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        _maintenanceTimer?.Dispose();
        _maintenanceTimer = null;
        _disposeCts.Cancel();

        await ClearAsync().ConfigureAwait(false);

        foreach (var kvp in _active)
        {
            await DisposeConnectionAsync(kvp.Value).ConfigureAwait(false);
        }
        _active.Clear();

        _semaphore.Dispose();
    }
}
