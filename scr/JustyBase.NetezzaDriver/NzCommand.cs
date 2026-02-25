using JustyBase.NetezzaDriver.StringPool;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace JustyBase.NetezzaDriver;

public sealed class NzCommand : DbCommand
{
    internal PreparedStatement? NewPreparedStatement { get; set; } = null;

    internal Sylvan? GetColumnStringPool(int colnum)
    {
        return NewPreparedStatement?.Description?[colnum].StringPool;
    }

    private RowValue[] _row = null!;
    public void AddRow(RowValue[] row)
    {
        _row = row;
    }
    public ref RowValue GetValue(int ordinal)
    {
        return ref _row[ordinal];
    }

    public NzCommand(NzConnection connection)
    {
        _connection = connection;
        connection.SetNzCommand(this);
    }
    public NzCommand(string sql, NzConnection connection)
    {
        _connection = connection;
        CommandText = sql;
    }

    private NzConnection _connection;

    internal int _recordsAffected = -1;

    internal string GetName(int fieldNum)
    {
        if (NewPreparedStatement is null)
        {
            return string.Empty;
        }
        return NewPreparedStatement.Description![fieldNum].Name;
    }
    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)]
    
    internal Type GetFieldType(int fieldNum)
    {
        if (NewPreparedStatement is null)
        {
            return typeof(object);
        }
        return NewPreparedStatement.Description![fieldNum].Type;
    }

    //internal uint GetFieldOid(int fieldNum)
    //{
    //    if (NewPreparedStatement is null)
    //    {
    //        return 0;
    //    }
    //    return NewPreparedStatement.Description![fieldNum].TypeOID;
    //}

    public int FieldCount => NewPreparedStatement?.FieldCount ?? -1;

    /// <summary>
    /// do not use this method, it is for internal use only
    /// </summary>
    /// <param name="operation"></param>
    /// <returns></returns>
    /// <exception cref="InterfaceException"></exception>
    private NzCommand Execute(string operation)
    {
        try
        {
            Clear();
            if (!_connection.InTransaction && !_connection.AutoCommit)
            {
                _connection.Execute(this, "begin");
                _connection.InTransaction = true;
            }
            _connection.Execute(this, operation);
            _connection.SetState(ConnectionState.Open);
        }
        catch (AttributeException ex)
        {
            if (_connection is null)
            {
                throw new InterfaceException("Command closed", ex);
            }
            else if (_connection.IsBaseStreamNull)
            {
                throw new InterfaceException("Connection closed in Command Execute", ex);
            }

            throw;
        }        
        return this;
    }

    private async Task<NzCommand> ExecuteAsync(string operation, CancellationToken cancellationToken = default)
    {
        try
        {
            Clear();
            if (!_connection.InTransaction && !_connection.AutoCommit)
            {
                await _connection.ExecuteAsync(this, "begin", cancellationToken).ConfigureAwait(false);
                _connection.InTransaction = true;
            }
            await _connection.ExecuteAsync(this, operation, cancellationToken).ConfigureAwait(false);
            _connection.SetState(ConnectionState.Open);
        }
        catch (AttributeException ex)
        {
            if (_connection is null)
            {
                throw new InterfaceException("Command closed", ex);
            }
            else if (_connection.IsBaseStreamNull)
            {
                throw new InterfaceException("Connection closed in Command Execute", ex);
            }

            throw;
        }
        return this;
    }

    private NzDataReader? _prevReader;
    private CommandType _commandType = CommandType.Text;
    private bool _designTimeVisible;
    private UpdateRowSource _updatedRowSource = UpdateRowSource.Both;
    private DbTransaction? _transaction;

    [AllowNull]
    public override string CommandText { get; set; } = string.Empty;
    public override int CommandTimeout
    {
        get => (int)_connection!.CommandTimeout.TotalSeconds;
        set
        {
            _connection!.CommandTimeout = TimeSpan.FromSeconds(value);
        }
    }
    public override CommandType CommandType
    {
        get => _commandType;
        set
        {
            if (value != CommandType.Text)
            {
                throw new NotSupportedException("Only CommandType.Text is supported.");
            }
            _commandType = value;
        }
    }
    protected override DbConnection? DbConnection 
    {
        get => _connection;
        set => _connection = (NzConnection)value!;
    }

    protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException("DbParameterCollection is not supported.");

    protected override DbTransaction? DbTransaction
    {
        get => _transaction;
        set => _transaction = value;
    }
    public override bool DesignTimeVisible
    {
        get => _designTimeVisible;
        set => _designTimeVisible = value;
    }
    public override UpdateRowSource UpdatedRowSource
    {
        get => _updatedRowSource;
        set => _updatedRowSource = value;
    }

    private void Clear()
    {
        _prevReader?.Close();
        _prevReader = null!;
        NewPreparedStatement = null;
        _recordsAffected = -1;
    }

    public override void Cancel()
    {
        _connection!.CancelQuery();
    }

    protected override DbParameter CreateDbParameter()
    {
        throw new NotSupportedException("DbParameter is not supported.");
    }
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        try
        {
            Clear();
            if (!_connection.InTransaction && !_connection.AutoCommit)
            {
                _connection.Execute(this, "begin");
                _connection.InTransaction = true;
            }
            _prevReader = _connection.ExecuteReader(this, CommandText);
            _connection.SetState(ConnectionState.Open);
            return _prevReader;
        }
        catch (Exception ex)
        {
            if (Connection is null)
            {
                throw new InterfaceException("Command closed", ex);
            }
            else if (_connection.IsBaseStreamNull)
            {
                throw new InterfaceException("Connection closed in Command Execute", ex);
            }
            throw;
        }
    }

    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            Clear();
            if (!_connection.InTransaction && !_connection.AutoCommit)
            {
                await _connection.ExecuteAsync(this, "begin", cancellationToken).ConfigureAwait(false);
                _connection.InTransaction = true;
            }
            _prevReader = await _connection.ExecuteReaderAsync(this, CommandText, cancellationToken).ConfigureAwait(false);
            _connection.SetState(ConnectionState.Open);
            return _prevReader;
        }
        catch (Exception ex)
        {
            if (Connection is null)
            {
                throw new InterfaceException("Command closed", ex);
            }
            else if (_connection.IsBaseStreamNull)
            {
                throw new InterfaceException("Connection closed in Command Execute", ex);
            }
            throw;
        }
    }

    public override int ExecuteNonQuery()
    {
        Execute(CommandText);
        return _recordsAffected;
    }

    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await ExecuteAsync(CommandText, cancellationToken).ConfigureAwait(false);
        return _recordsAffected;
    }

    public override object? ExecuteScalar()
    {
        using var rdr = ExecuteDbDataReader(CommandBehavior.SingleRow);
        if (rdr.Read())
        {
            return rdr.GetValue(0);
        }
        return null;
    }

    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var rdr = await ExecuteDbDataReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        if (await rdr.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return rdr.GetValue(0);
        }
        return null;
    }

    public override void Prepare()
    {
        // no-op: server-side prepared statements are not exposed via ADO.NET parameters
    }
}

