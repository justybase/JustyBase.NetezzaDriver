using System.Collections;
using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace JustyBase.NetezzaDriver;

public sealed class NzDataReader : DbDataReader
{
    private readonly NzCommand _nzCommand;
    private readonly NzConnection _nzConnection;

    private bool _opened = true;
    private bool _needsToBeNextResultCalled = false;
    private bool _hasRows = false;
    private bool _disposed = false;

    private DataTable? _schemaTable;

    public NzDataReader(NzCommand nzCommand) : this(nzCommand, initializeReader: true)
    {
    }

    private NzDataReader(NzCommand nzCommand, bool initializeReader)
    {
        _nzCommand = nzCommand ?? throw new ArgumentNullException(nameof(nzCommand));
        _nzConnection = nzCommand.Connection as NzConnection ?? throw new ArgumentNullException(nameof(nzCommand.Connection));

        if (initializeReader)
        {
            InitializeReader();
        }
    }

    internal static async Task<NzDataReader> CreateAsync(NzCommand nzCommand, CancellationToken cancellationToken = default)
    {
        var reader = new NzDataReader(nzCommand, initializeReader: false);
        await reader.InitializeReaderAsync(cancellationToken).ConfigureAwait(false);
        return reader;
    }

    private void InitializeReader()
    {
        while (_opened = _nzConnection.DoNextStep(_nzCommand))
        {
            if (_nzConnection.NewRowDescriptionReceived())
            {
                CheckIfRowsAreAvailable();
                break;
            }
        }
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override bool Read()
    {
        // Fast path for common case
        if(_disposed || !_opened || _needsToBeNextResultCalled)
        {
            return false;
        }

        while (_opened = _nzConnection.DoNextStep(_nzCommand))
        {
            if (_nzConnection.NewRowReceived())
            {
                return true;
            }
            if (_nzConnection.NewRowDescriptionReceived())
            {
                CheckIfRowsAreAvailable();
                _needsToBeNextResultCalled = true;
                return false;
            }
        }
        return false;//final stop!
    }

    public override Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed || !_opened || _needsToBeNextResultCalled)
        {
            return Task.FromResult(false);
        }

        var readTask = ReadAsyncCore(cancellationToken);
        if (readTask.IsCompletedSuccessfully)
        {
            return Task.FromResult(readTask.Result);
        }

        return readTask.AsTask();
    }

    private async ValueTask<bool> ReadAsyncCore(CancellationToken cancellationToken)
    {
        while (_opened = await _nzConnection.DoNextStepAsync(_nzCommand, cancellationToken).ConfigureAwait(false))
        {
            if (_nzConnection.NewRowReceived())
            {
                return true;
            }
            if (_nzConnection.NewRowDescriptionReceived())
            {
                await CheckIfRowsAreAvailableAsync(cancellationToken).ConfigureAwait(false);
                _needsToBeNextResultCalled = true;
                return false;
            }
        }
        return false;
    }

    public override void Close()
    {
        if (!_disposed && _opened)
        {
            base.Close();
            try
            {
                while (_opened = _nzConnection.DoNextStep(_nzCommand))
                {
                }
            }
            catch (NetezzaException)
            {
                _opened = false;
            }
        }
    }

    private async Task InitializeReaderAsync(CancellationToken cancellationToken = default)
    {
        while (_opened = await _nzConnection.DoNextStepAsync(_nzCommand, cancellationToken).ConfigureAwait(false))
        {
            if (_nzConnection.NewRowDescriptionReceived())
            {
                await CheckIfRowsAreAvailableAsync(cancellationToken).ConfigureAwait(false);
                break;
            }
        }
    }

    private async Task CloseAsyncCore(CancellationToken cancellationToken)
    {
        if (!_disposed && _opened)
        {
            base.Close();
            try
            {
                while (_opened = await _nzConnection.DoNextStepAsync(_nzCommand, cancellationToken).ConfigureAwait(false))
                {
                }
            }
            catch (NetezzaException)
            {
                _opened = false;
            }
        }
    }

    internal Task CloseAsync(CancellationToken cancellationToken)
        => CloseAsyncCore(cancellationToken);

    public override Task CloseAsync()
        => CloseAsyncCore(CancellationToken.None);

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            Close();
            _disposed = true;
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            await CloseAsyncCore(CancellationToken.None).ConfigureAwait(false);
            _disposed = true;
        }
        await base.DisposeAsync().ConfigureAwait(false);
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
        if (_disposed) ThrowObjectDisposedException();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowObjectDisposedException() =>
        throw new ObjectDisposedException(nameof(NzDataReader));


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ValidateOrdinal(int ordinal)
    {
        if ((uint)ordinal >= (uint)FieldCount)
        {
            ThrowOrdinalOutOfRange(ordinal);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowOrdinalOutOfRange(int ordinal) =>
    throw new IndexOutOfRangeException($"Ordinal {ordinal} is out of range. Valid range is 0 to {FieldCount - 1}");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ValidateCanRead()
    {
        if (_disposed || !_opened) ThrowCannotRead();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowCannotRead() =>
     throw new InvalidOperationException("Reader is closed or disposed");


    public override object this[int ordinal]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ValidateOrdinal(ordinal);
            return GetValue(ordinal);
        }
    }

    public override object this[string name]
    {
        get
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("Column name cannot be null or empty", nameof(name));

            var idx = _nzCommand?.NewPreparedStatement?.Description?.FieldIndex(name);
            if (idx is int index)
            {
                return GetValue(index);
            }
            throw new IndexOutOfRangeException($"Column '{name}' not found");
        }
    }


    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowColumnNotFound(string name) =>
    throw new IndexOutOfRangeException($"Column '{name}' not found");



    public override int Depth => 0; // Netezza doesn't support nested results

    public override int FieldCount => _nzCommand.FieldCount;

    public override bool HasRows => _hasRows;

    public override bool IsClosed => !_opened || _disposed;

    public override int RecordsAffected => _nzCommand._recordsAffected;


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override bool GetBoolean(int ordinal)
    {
        ValidateOrdinal(ordinal);
        ref readonly var rw = ref _nzCommand.GetValue(ordinal);
        if (rw.typeCode == TypeCodeEx.DBNull || rw.typeCode == TypeCodeEx.Empty)
            throw new InvalidCastException($"Cannot cast DBNull to bool for column {ordinal}");
        if (rw.typeCode != TypeCodeEx.Boolean)
            throw new InvalidCastException($"Cannot cast column {ordinal} of type {rw.typeCode} to bool");
        return rw.boolValue;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override byte GetByte(int ordinal)
    {
        ValidateOrdinal(ordinal);
        ref readonly var rw = ref _nzCommand.GetValue(ordinal);
        if (rw.typeCode == TypeCodeEx.DBNull || rw.typeCode == TypeCodeEx.Empty)
            throw new InvalidCastException($"Cannot cast DBNull to by for column {ordinal}");
        if (rw.typeCode != TypeCodeEx.Byte)
            throw new InvalidCastException($"Cannot cast column {ordinal} of type {rw.typeCode} to byte");
        return rw.byteValue;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override char GetChar(int ordinal)
    {
        ValidateOrdinal(ordinal);
        ref readonly var rw = ref _nzCommand.GetValue(ordinal);
        if (rw.typeCode == TypeCodeEx.DBNull || rw.typeCode == TypeCodeEx.Empty)
            throw new InvalidCastException($"Cannot cast DBNull to char for column {ordinal}");
        if (rw.typeCode != TypeCodeEx.Char)
            throw new InvalidCastException($"Cannot cast column {ordinal} of type {rw.typeCode} to char");
        return rw.charValue;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override DateTime GetDateTime(int ordinal)
    {
        ValidateOrdinal(ordinal);
        ref readonly var rw = ref _nzCommand.GetValue(ordinal);
        if (rw.typeCode == TypeCodeEx.DBNull || rw.typeCode == TypeCodeEx.Empty)
            throw new InvalidCastException($"Cannot cast DBNull to DateTime for column {ordinal}");
        if (rw.typeCode != TypeCodeEx.DateTime)
            throw new InvalidCastException($"Cannot cast column {ordinal} of type {rw.typeCode} to datetime");
        return rw.dateTimeValue;
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override decimal GetDecimal(int ordinal)
    {
        ValidateOrdinal(ordinal);
        ref readonly var rw = ref _nzCommand.GetValue(ordinal);
        if (rw.typeCode == TypeCodeEx.DBNull || rw.typeCode == TypeCodeEx.Empty)
            throw new InvalidCastException($"Cannot cast DBNull to decimal for column {ordinal}");
        if (rw.typeCode != TypeCodeEx.Decimal)
            throw new InvalidCastException($"Cannot cast column {ordinal} of type {rw.typeCode} to decimal");
        return rw.decimalValue;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override double GetDouble(int ordinal)
    {
        ValidateOrdinal(ordinal);
        ref readonly var rw = ref _nzCommand.GetValue(ordinal);
        if (rw.typeCode == TypeCodeEx.DBNull || rw.typeCode == TypeCodeEx.Empty)
            throw new InvalidCastException($"Cannot cast DBNull to double for column {ordinal}");
        if (rw.typeCode != TypeCodeEx.Double)
            throw new InvalidCastException($"Cannot cast column {ordinal} of type {rw.typeCode} to double");
        return rw.doubleValue;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override float GetFloat(int ordinal)
    {
        ValidateOrdinal(ordinal);
        ref readonly var rw = ref _nzCommand.GetValue(ordinal);
        if (rw.typeCode == TypeCodeEx.DBNull || rw.typeCode == TypeCodeEx.Empty)
            throw new InvalidCastException($"Cannot cast DBNull to float for column {ordinal}");
        if (rw.typeCode != TypeCodeEx.Single)
            throw new InvalidCastException($"Cannot cast column {ordinal} of type {rw.typeCode} to float");
        return rw.singleValue;
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override short GetInt16(int ordinal)
    {
        ValidateOrdinal(ordinal);
        ref readonly var rw = ref _nzCommand.GetValue(ordinal);
        if (rw.typeCode == TypeCodeEx.DBNull || rw.typeCode == TypeCodeEx.Empty)
            throw new InvalidCastException($"Cannot cast DBNull to Int16 for column {ordinal}");
        if (rw.typeCode != TypeCodeEx.Int16)
            throw new InvalidCastException($"Cannot cast column {ordinal} of type {rw.typeCode} to Int16");
        return rw.int16Value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetInt32(int ordinal)
    {
        ValidateOrdinal(ordinal);
        ref readonly var rw = ref _nzCommand.GetValue(ordinal);
        if (rw.typeCode == TypeCodeEx.DBNull || rw.typeCode == TypeCodeEx.Empty)
            throw new InvalidCastException($"Cannot cast DBNull to Int32 for column {ordinal}");
        if (rw.typeCode != TypeCodeEx.Int32)
            throw new InvalidCastException($"Cannot cast column {ordinal} of type {rw.typeCode} to Int32");
        return rw.int32Value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override long GetInt64(int ordinal)
    {
        ValidateOrdinal(ordinal);
        ref readonly var rw = ref _nzCommand.GetValue(ordinal);
        if (rw.typeCode == TypeCodeEx.DBNull || rw.typeCode == TypeCodeEx.Empty)
            throw new InvalidCastException($"Cannot cast DBNull to Int64 for column {ordinal}");
        if (rw.typeCode != TypeCodeEx.Int64)
            throw new InvalidCastException($"Cannot cast column {ordinal} of type {rw.typeCode} to Int64");
        return rw.int64Value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override string GetString(int ordinal)
    {
        ValidateOrdinal(ordinal);
        ref readonly var rw = ref _nzCommand.GetValue(ordinal);
        if (rw.typeCode == TypeCodeEx.DBNull || rw.typeCode == TypeCodeEx.Empty)
            throw new InvalidCastException($"Cannot cast DBNull to string for column {ordinal}");
        if(rw.typeCode != TypeCodeEx.String)
            throw new InvalidCastException($"Cannot cast column {ordinal} of type {rw.typeCode} to string");
        return rw.stringValue ?? string.Empty;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override object GetValue(int ordinal)
    {
        ValidateOrdinal(ordinal);
        return _nzCommand.GetValue(ordinal).GetValue();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override bool IsDBNull(int ordinal)
    {
        ValidateOrdinal(ordinal);
        ref readonly var rw = ref _nzCommand.GetValue(ordinal);
        return rw.typeCode == TypeCodeEx.DBNull || rw.typeCode == TypeCodeEx.Empty;
    }

    public override int GetValues(object[] values)
    {
        if (values == null) throw new ArgumentNullException(nameof(values));

        ValidateCanRead();

        int copyCount = Math.Min(FieldCount, values.Length);
        ref object valuesRef = ref MemoryMarshal.GetArrayDataReference(values);

        for (int i = 0; i < copyCount; i++)
        {
            Unsafe.Add(ref valuesRef, i) = GetValue(i);
        }

        return copyCount;
    }

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        ValidateCanRead();
        ValidateOrdinal(ordinal);

        if (dataOffset < 0) throw new ArgumentOutOfRangeException(nameof(dataOffset));
        if (buffer != null && bufferOffset < 0) throw new ArgumentOutOfRangeException(nameof(bufferOffset));
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));

        var value = GetValue(ordinal);
        if (value is DBNull)
        {
            return 0;
        }

        byte[] source = value switch
        {
            byte[] data => data,
            string text => NzConnectionHelpers.ClientEncoding.GetBytes(text),
            _ => throw new InvalidCastException($"Cannot cast column {ordinal} value of type '{value.GetType().Name}' to bytes")
        };

        if (buffer is null)
        {
            return source.Length;
        }

        if (bufferOffset > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferOffset));
        }

        if (length > buffer.Length - bufferOffset)
        {
            throw new ArgumentException("The sum of bufferOffset and length is larger than the buffer size.");
        }

        if (dataOffset >= source.Length)
        {
            return 0;
        }

        int available = source.Length - (int)dataOffset;
        int toCopy = Math.Min(length, available);
        source.AsSpan((int)dataOffset, toCopy).CopyTo(buffer.AsSpan(bufferOffset, toCopy));
        return toCopy;
    }

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        ValidateCanRead();
        ValidateOrdinal(ordinal);

        if (dataOffset < 0) throw new ArgumentOutOfRangeException(nameof(dataOffset));
        if (buffer != null && bufferOffset < 0) throw new ArgumentOutOfRangeException(nameof(bufferOffset));
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));

        var value = GetValue(ordinal);
        if (value is DBNull)
        {
            return 0;
        }

        string source = value switch
        {
            string text => text,
            char[] chars => new string(chars),
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
        };

        if (buffer is null)
        {
            return source.Length;
        }

        if (bufferOffset > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferOffset));
        }

        if (length > buffer.Length - bufferOffset)
        {
            throw new ArgumentException("The sum of bufferOffset and length is larger than the buffer size.");
        }

        if (dataOffset >= source.Length)
        {
            return 0;
        }

        int available = source.Length - (int)dataOffset;
        int toCopy = Math.Min(length, available);
        source.AsSpan((int)dataOffset, toCopy).CopyTo(buffer.AsSpan(bufferOffset, toCopy));
        return toCopy;
    }

    public override IEnumerator GetEnumerator()
    {
        // Return a simple enumerator that yields current row values
        for (int i = 0; i < FieldCount; i++)
        {
            yield return GetValue(i);
        }
    }

    public override string GetDataTypeName(int ordinal)
    {
        ValidateOrdinal(ordinal);
        return GetFieldType(ordinal).Name;
    }



    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)]
    public override Type GetFieldType(int ordinal)
    {
        ValidateOrdinal(ordinal);
        return _nzCommand.GetFieldType(ordinal);
    }

    public override Guid GetGuid(int ordinal)
    {
        ValidateOrdinal(ordinal);
        var value = GetValue(ordinal);
        return value is Guid guid ? guid : throw new InvalidCastException($"Cannot cast column {ordinal} to Guid");
    }


    public override string GetName(int ordinal)
    {
        ValidateOrdinal(ordinal);
        return _nzCommand.GetName(ordinal);
    }

    public override int GetOrdinal(string name)
    {
        ValidateCanRead();
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Column name cannot be null or empty", nameof(name));

        var index = _nzCommand?.NewPreparedStatement?.Description?.FieldIndex(name);
        if (index.HasValue)
        {
            return index.Value;
        }
        throw new IndexOutOfRangeException($"Column '{name}' not found");
    }

    // Extension methods for better performance (if RowValue supports them)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override T GetFieldValue<T>(int ordinal)
    {
        ValidateOrdinal(ordinal);
        return (T)GetValue(ordinal);
    }

    public override bool NextResult()
    {
        ThrowIfDisposed();
        _needsToBeNextResultCalled = false;
        return _opened;
    }

    public override Task<bool> NextResultAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        _needsToBeNextResultCalled = false;
        return Task.FromResult(_opened);
    }

    public bool ShouldContinue => _opened && !_disposed;


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CheckIfRowsAreAvailable()
    {
        _nzConnection.ReadNextResponseByte();
        _hasRows = !_nzConnection.IsCommandComplete();

        if (_hasRows && _nzConnection.NewRowDescriptionStandardReceived())
        {
            _opened = _nzConnection.DoNextStep(_nzCommand);
        }
    }

    private async Task CheckIfRowsAreAvailableAsync(CancellationToken cancellationToken = default)
    {
        await _nzConnection.ReadNextResponseByteAsync(cancellationToken).ConfigureAwait(false);
        _hasRows = !_nzConnection.IsCommandComplete();

        if (_hasRows && _nzConnection.NewRowDescriptionStandardReceived())
        {
            _opened = await _nzConnection.DoNextStepAsync(_nzCommand, cancellationToken).ConfigureAwait(false);
        }
    }

    public override DataTable? GetSchemaTable()
    {
        ValidateCanRead();

        // Cache schema table for performance
        if (_schemaTable != null)
        {
            return _schemaTable;
        }

        _schemaTable = new DataTable("Schema Table");
        _schemaTable.Columns.Add("ColumnName", typeof(string));
        _schemaTable.Columns.Add("ColumnOrdinal", typeof(int));
        _schemaTable.Columns.Add("ColumnSize", typeof(Int16));
        _schemaTable.Columns.Add("NumericPrecision", typeof(int));
        _schemaTable.Columns.Add("NumericScale", typeof(int));
#pragma warning disable IL2111 // Method with parameters or return value with `DynamicallyAccessedMembersAttribute` is accessed via reflection. Trimmer can't guarantee availability of the requirements of the method.
        _schemaTable.Columns.Add("DataType", typeof(Type));
#pragma warning restore IL2111 // Method with parameters or return value with `DynamicallyAccessedMembersAttribute` is accessed via reflection. Trimmer can't guarantee availability of the requirements of the method.
        _schemaTable.Columns.Add("ProviderType", typeof(int));
        _schemaTable.Columns.Add("AllowDBNull", typeof(bool));

        _schemaTable.Columns.Add(new DataColumn("IsAutoIncrement", typeof(bool)) { DefaultValue = false });
        _schemaTable.Columns.Add(new DataColumn("IsLong", typeof(bool)) { DefaultValue = false});
        _schemaTable.Columns.Add(new DataColumn("IsReadOnly", typeof(bool)) { DefaultValue = false });


        for (int ordinal = 0; ordinal < FieldCount; ordinal++)
        {
            var currentType = GetFieldType(ordinal);
            var fieldSize = _nzConnection.CTableIFieldSizeAlternative(ordinal);
            var allowDbNull = _nzConnection.IsColumnNullable(ordinal);

            switch (currentType)
            {
                case Type t when t == typeof(decimal):
                    _schemaTable.Rows.Add([
                        _nzCommand.NewPreparedStatement!.Description![ordinal].Name
                    , ordinal + 1
                    , fieldSize
                    , _nzConnection.CTableIFieldPrecisionAlternative(ordinal)
                    , _nzConnection.CTableIFieldScaleAlternative(ordinal),
                        currentType, _nzCommand.NewPreparedStatement!.Description![ordinal].TypeOID, allowDbNull]);
                    break;
                case Type t when t == typeof(string):
                    _schemaTable.Rows.Add([_nzCommand.NewPreparedStatement!.Description![ordinal].Name
                        , ordinal + 1
                        , fieldSize
                        , -1
                        , -1
                        , currentType, _nzCommand.NewPreparedStatement!.Description![ordinal].TypeOID, allowDbNull]);
                    break;
                case Type t when t == typeof(DateTime):
                    _schemaTable.Rows.Add([_nzCommand.NewPreparedStatement!.Description![ordinal].Name
                        , ordinal + 1
                        , fieldSize
                        , -1
                        , -1
                        , currentType, _nzCommand.NewPreparedStatement!.Description![ordinal].TypeOID, allowDbNull]);
                    break;
                case Type t when t == typeof(TimeSpan):
                    _schemaTable.Rows.Add([_nzCommand.NewPreparedStatement!.Description![ordinal].Name
                        , ordinal + 1
                        , fieldSize
                        , -1
                        , -1
                        , currentType, _nzCommand.NewPreparedStatement!.Description![ordinal].TypeOID, allowDbNull]);
                    break;
                default:
                    _schemaTable.Rows.Add([_nzCommand.NewPreparedStatement!.Description![ordinal].Name
                        , ordinal + 1
                        , fieldSize
                        , -1
                        , -1
                        , currentType, _nzCommand.NewPreparedStatement!.Description![ordinal].TypeOID, allowDbNull]);
                    break;
            };
        }

        return _schemaTable;
    }

    public ReadOnlyCollection<DbColumn> GetColumnSchema()
    {
        ThrowIfDisposed();
        var columns = new List<DbColumn>(FieldCount);
        for (int i = 0; i < FieldCount; i++)
        {
            var fd = _nzCommand.NewPreparedStatement!.Description![i];
            var declaredTypeName = BuildDeclaredTypeName(i, fd);
            var col = new NzDbColumn(
                columnName: fd.Name,
                columnOrdinal: i,
                providerType: (int)fd.TypeOID,
                typeModifier: fd.TypeModifier,
                dataType: fd.Type,
                dataTypeName: fd.Type.Name,
                columnSize: GetColumnSize(i, fd),
                numericPrecision: GetNumericPrecision(i, fd),
                numericScale: GetNumericScale(i, fd),
                allowDBNull: _nzConnection.IsColumnNullable(i),
                baseColumnName: fd.Name,
                baseServerName: _nzConnection.DataSource,
                declaredTypeName: declaredTypeName
            );
            columns.Add(col);
        }
        return columns.AsReadOnly();
    }

    public int GetProviderType(int ordinal)
    {
        ValidateOrdinal(ordinal);
        var fd = _nzCommand.NewPreparedStatement!.Description![ordinal];
        return (int)fd.TypeOID;
    }

    public string GetDeclaredTypeName(int ordinal)
    {
        ValidateOrdinal(ordinal);
        var fd = _nzCommand.NewPreparedStatement!.Description![ordinal];
        return BuildDeclaredTypeName(ordinal, fd);
    }

    private static int GetColumnSize(int ordinal, FieldDescription fd)
    {
        if (fd.TypeSize != -1)
            return fd.TypeSize;
        if (fd.TypeModifier > 16)
            return fd.TypeModifier - 16;
        return 0;
    }

    private static int? GetNumericPrecision(int ordinal, FieldDescription fd)
    {
        if (fd.TypeOID == 1700 && fd.TypeModifier > 16)
        {
            var normalized = fd.TypeModifier - 16;
            return normalized >> 16;
        }
        return null;
    }

    private static int? GetNumericScale(int ordinal, FieldDescription fd)
    {
        if (fd.TypeOID == 1700 && fd.TypeModifier > 16)
        {
            var normalized = fd.TypeModifier - 16;
            return normalized & 0xFFFF;
        }
        return null;
    }

    private static string BuildDeclaredTypeName(int ordinal, FieldDescription fd)
    {
        var typeName = fd.TypeOID switch
        {
            16 => "BOOLEAN",
            20 => "BIGINT",
            21 => "SMALLINT",
            23 => "INTEGER",
            25 => "TEXT",
            700 => "REAL",
            701 => "DOUBLE",
            702 => "ABSTIME",
            1042 => "CHAR",
            1043 => "VARCHAR",
            1082 => "DATE",
            1083 => "TIME",
            1114 => "TIMESTAMP",
            1184 => "TIMESTAMPTZ",
            1186 => "INTERVAL",
            1700 => "NUMERIC",
            2500 => "BYTEINT",
            2522 => "NCHAR",
            2530 => "NVARCHAR",
            _ => fd.Type.Name.ToUpperInvariant()
        };

        if (fd.TypeOID == 1700 && fd.TypeModifier > 16)
        {
            var normalized = fd.TypeModifier - 16;
            var prec = normalized >> 16;
            var scale = normalized & 0xFFFF;
            if (prec > 0)
                return $"NUMERIC({prec},{scale})";
        }

        if (fd.TypeOID is 1042 or 1043 or 2522 or 2530 && fd.TypeModifier > 16)
        {
            var len = fd.TypeModifier - 16;
            return $"{typeName}({len})";
        }

        return typeName;
    }
}


//CREATE TABLE TEST_NOT_NULL
//(
//ID INT NOT NULL
//)
//DISTRIBUTE ON RANDOM;

//INSERT INTO TEST_NOT_NULL
//SELECT 15
