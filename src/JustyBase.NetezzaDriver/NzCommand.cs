using JustyBase.NetezzaDriver.StringPool;
using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace JustyBase.NetezzaDriver;

public sealed class NzCommand : DbCommand
{
    internal PreparedStatement? NewPreparedStatement { get; set; } = null;

    internal Sylvan? GetColumnStringPool(int colnum)
    {
        return NewPreparedStatement?.Description?[colnum].StringPool;
    }

    private RowValue[] _row = null!;
    private bool _lazyRow;
    private readonly List<string> _notices = [];
    private readonly ReadOnlyCollection<string> _noticesView;

    /// <summary>
    /// Notices returned during the most recent execution of this command.
    /// </summary>
    public IReadOnlyList<string> Notices => _noticesView;

    internal void AddNotice(string notice) => _notices.Add(notice);

    public void AddRow(RowValue[] row)
    {
        _row = row;
        _lazyRow = false;
    }

    internal void AddLazyRow(RowValue[] row)
    {
        _row = row;
        _lazyRow = true;
    }

    internal bool IsLazyRow => _lazyRow;

    public ref RowValue GetValue(int ordinal)
    {
        if (_lazyRow)
        {
            _connection.EnsureFieldDecoded(ordinal);
        }
        return ref _row[ordinal];
    }

    /// <summary>
    /// Null test that, under lazy decoding, consults the row null bitmap without
    /// materialising the column value.
    /// </summary>
    internal bool IsDBNullFast(int ordinal)
    {
        if (_lazyRow && _row[ordinal].typeCode == RowValue.NotDecoded)
        {
            return _connection.IsFieldNull(ordinal);
        }
        var typeCode = _row[ordinal].typeCode;
        return typeCode == TypeCodeEx.DBNull || typeCode == TypeCodeEx.Empty;
    }

    public NzCommand(NzConnection connection)
    {
        _noticesView = _notices.AsReadOnly();
        _connection = connection;
        connection.SetNzCommand(this);
    }
    public NzCommand(string sql, NzConnection connection)
    {
        _noticesView = _notices.AsReadOnly();
        _connection = connection;
        CommandText = sql;
    }

    private NzConnection _connection;
    private readonly NzParameterCollection _parameters = [];
    private string? _cachedParamSql;
    private NzParameterHelper.SqlTemplatePlan? _cachedParamPlan;

    /// <summary>
    /// Behavior requested by the last ExecuteReader call. Used to honor
    /// <see cref="CommandBehavior.SingleRow"/> without changing the public API.
    /// </summary>
    internal CommandBehavior RequestedBehavior { get; private set; } = CommandBehavior.Default;

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
    private string ResolveCommandText(string operation)
    {
        if (_parameters is not null && _parameters.Count > 0)
        {
            // Cache the parsed template per CommandText value: avoids rescanning
            // quotes/comments/dollar-quotes on every Execute with the same text.
            // The plan depends only on SQL (offsets), not parameter values.
            var cachedPlan = _cachedParamPlan;
            var cachedSql = _cachedParamSql;
            if (cachedPlan is null || cachedSql is null || !string.Equals(cachedSql, operation, StringComparison.Ordinal))
            {
                cachedPlan = NzParameterHelper.ParseTemplate(operation);
                _cachedParamSql = operation;
                _cachedParamPlan = cachedPlan;
            }
            return NzParameterHelper.RenderWithPlan(operation, cachedPlan, _parameters);
        }

        // No parameters: still need to detect stray placeholders, but avoid
        // parsing when the text is unchanged and known to be placeholder-free.
        // Fast path: if same SQL as cached and cached plan is empty, return directly.
        if (_cachedParamSql is not null && string.Equals(_cachedParamSql, operation, StringComparison.Ordinal)
            && _cachedParamPlan is not null && _cachedParamPlan.Placeholders.Length == 0)
            return operation;

        var plan0 = NzParameterHelper.ParseTemplate(operation);
        if (plan0.Placeholders.Length > 0)
        {
            var first = plan0.Placeholders[0];
            string name = first.IsNamed ? operation.Substring(first.Start, first.Length) : "?";
            throw new InvalidOperationException($"Missing value for SQL parameter '{name}'.");
        }
        _cachedParamSql = operation;
        _cachedParamPlan = plan0;
        return operation;
    }

    private NzCommand Execute(string operation)
    {
        try
        {
            Clear();
            var resolvedSql = ResolveCommandText(operation);
            if (!_connection.InTransaction && !_connection.AutoCommit)
            {
                _connection.Execute(this, "begin");
                _connection.InTransaction = true;
            }
            _connection.Execute(this, resolvedSql);
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
            var resolvedSql = ResolveCommandText(operation);
            if (!_connection.InTransaction && !_connection.AutoCommit)
            {
                await _connection.ExecuteAsync(this, "begin", cancellationToken).ConfigureAwait(false);
                _connection.InTransaction = true;
            }
            await _connection.ExecuteAsync(this, resolvedSql, cancellationToken).ConfigureAwait(false);
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

    protected override DbParameterCollection DbParameterCollection => _parameters;
    public new NzParameterCollection Parameters => _parameters;

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
        _notices.Clear();
    }

    public override void Cancel()
    {
        _connection!.CancelQuery();
    }

    protected override DbParameter CreateDbParameter()
    {
        return new NzParameter();
    }
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        try
        {
            RequestedBehavior = behavior;
            Clear();
            var resolvedSql = ResolveCommandText(CommandText);
            if (!_connection.InTransaction && !_connection.AutoCommit)
            {
                _connection.Execute(this, "begin");
                _connection.InTransaction = true;
            }
            _prevReader = _connection.ExecuteReader(this, resolvedSql);
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
            RequestedBehavior = behavior;
            Clear();
            var resolvedSql = ResolveCommandText(CommandText);
            if (!_connection.InTransaction && !_connection.AutoCommit)
            {
                await _connection.ExecuteAsync(this, "begin", cancellationToken).ConfigureAwait(false);
                _connection.InTransaction = true;
            }
            _prevReader = await _connection.ExecuteReaderAsync(this, resolvedSql, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Executes the command and maps each row as it is read, without buffering
    /// the full result set in memory.
    /// </summary>
    public async IAsyncEnumerable<T> ExecuteRowsAsync<T>(
        Func<DbDataReader, T> map,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(map);

        await using var reader = await ExecuteDbDataReaderAsync(
            CommandBehavior.Default,
            cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return map(reader);
        }
    }

    public override void Prepare()
    {
        // no-op: server-side prepared statements are not exposed via ADO.NET parameters
    }
}
