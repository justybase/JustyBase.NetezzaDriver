namespace JustyBase.NetezzaDriver;

public sealed class PooledNzConnection : IAsyncDisposable
{
    private NzConnectionPool? _pool;
    private bool _returned;

    internal PooledNzConnection(NzConnection connection, NzConnectionPool pool)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
    }

    public NzConnection Connection { get; }

    public async ValueTask DisposeAsync()
    {
        if (_returned)
            return;
        _returned = true;
        var pool = Interlocked.Exchange(ref _pool, null);
        if (pool is not null)
            await pool.ReturnAsync(Connection).ConfigureAwait(false);
    }
}
