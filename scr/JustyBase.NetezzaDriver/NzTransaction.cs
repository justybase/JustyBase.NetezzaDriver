using System.Data;
using System.Data.Common;

namespace JustyBase.NetezzaDriver;

public sealed class NzTransaction : DbTransaction
{
    private readonly NzConnection _connection;
    private readonly IsolationLevel _isolationLevel;
    private bool _completed;
    private bool _disposed;

    internal NzTransaction(NzConnection connection, IsolationLevel isolationLevel)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _isolationLevel = isolationLevel;
    }

    public override IsolationLevel IsolationLevel => _isolationLevel;

    protected override DbConnection DbConnection => _connection;

    public override void Commit()
    {
        ThrowIfDisposedOrCompleted();
        _connection.Commit();
        _connection.AutoCommit = true;
        _completed = true;
    }

    public override void Rollback()
    {
        ThrowIfDisposedOrCompleted();
        _connection.Rollback();
        _connection.AutoCommit = true;
        _completed = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed || !disposing)
        {
            base.Dispose(disposing);
            return;
        }

        if (!_completed && _connection.State == ConnectionState.Open && _connection.InTransaction)
        {
            _connection.Rollback();
        }

        _connection.AutoCommit = true;
        _disposed = true;
        base.Dispose(disposing);
    }

    private void ThrowIfDisposedOrCompleted()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(NzTransaction));
        }

        if (_completed)
        {
            throw new InvalidOperationException("The transaction has already been completed.");
        }
    }
}
