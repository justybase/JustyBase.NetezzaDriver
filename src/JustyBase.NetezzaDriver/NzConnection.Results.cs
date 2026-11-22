using JustyBase.NetezzaDriver.AbortQuery;
using JustyBase.NetezzaDriver.StringPool;
using JustyBase.NetezzaDriver.TypeConvertions;
using JustyBase.NetezzaDriver.Utility;
using Microsoft.Extensions.Logging;
using System.Buffers;
using System.Buffers.Binary;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;

// Row-description parsing and result-set decoding (DBOS and text paths).

namespace JustyBase.NetezzaDriver;

public sealed partial class NzConnection
{

    private void ResGetDbosColumnDescriptions(ReadOnlySpan<byte> data)
    {
        _protocolRowNumber = 0;
        _currentProtocolRowNumber = 0;
        int dataIdx = 0;
        _tupdesc.Version = IUnpack(data, dataIdx);
        _tupdesc.NullsAllowed = IUnpack(data, dataIdx + 4);
        _tupdesc.SizeWord = IUnpack(data, dataIdx + 8);
        _tupdesc.SizeWordSize = IUnpack(data, dataIdx + 12);
        _tupdesc.NumFixedFields = IUnpack(data, dataIdx + 16);
        _tupdesc.NumVaryingFields = IUnpack(data, dataIdx + 20);
        _tupdesc.FixedFieldsSize = IUnpack(data, dataIdx + 24);
        _tupdesc.MaxRecordSize = IUnpack(data, dataIdx + 28);
        _tupdesc.NumFields = IUnpack(data, dataIdx + 32);
        // NumFields comes straight off the wire, so validate it against what the
        // payload can hold (36-byte header + 36 bytes per field + 8-byte trailer)
        // before allocating the final arrays. A corrupt count can otherwise
        // request gigabytes.
        int maxPlausibleFields = (data.Length - 36) / 36;
        if (_tupdesc.NumFields < 0 || _tupdesc.NumFields > maxPlausibleFields)
        {
            throw new InvalidDataException(
                $"Invalid RowStandard field count {_tupdesc.NumFields} for a {data.Length}-byte descriptor.");
        }

        _tupdesc.Initialize(_tupdesc.NumFields);

        dataIdx += 36;
        for (int ix = 0; ix < _tupdesc.NumFields; ix++)
        {
            //https://github.com/IBM/nzpy/issues/61
            var ft = IUnpack(data, dataIdx);

            if (ft == NzTypeInt && _nzCommand?.NewPreparedStatement?.Description?[ix].TypeOID == 702)
            {
                _tupdesc.FieldTypeArr[ix] = NzTypeIntvsAbsTimeFIX;
            }
            else
            {
                _tupdesc.FieldTypeArr[ix] = ft;
            }

            _tupdesc.FieldSizeArr[ix] = IUnpack(data, dataIdx + 4);
            _tupdesc.FieldTrueSizeArr[ix] = IUnpack(data, dataIdx + 8);
            _tupdesc.FieldOffsetArr[ix] = IUnpack(data, dataIdx + 12);
            int physField = IUnpack(data, dataIdx + 16);
            _tupdesc.FieldPhysFieldArr[ix] = physField;
            // Precompute the null-bitmap address for the row loop. A corrupt
            // (negative) physical field number wraps to a large index and is
            // rejected by the payload bounds check when the bit is read; the
            // access stays memory-safe.
            DbosTupleDesc.ComputeNullTest(
                physField,
                out int nullByteIndex,
                out byte nullBitMask);
            _tupdesc.FieldNullByteIndexArr[ix] = nullByteIndex;
            _tupdesc.FieldNullBitMaskArr[ix] = nullBitMask;
            _tupdesc.FieldLogFieldArr[ix] = IUnpack(data, dataIdx + 20);
            _tupdesc.FieldNullAllowedArr[ix] = IUnpack(data, dataIdx + 24) != 0;
            _tupdesc.FieldFixedSizeArr[ix] = IUnpack(data, dataIdx + 28);
            _tupdesc.FieldSpringFieldArr[ix] = IUnpack(data, dataIdx + 32);
            dataIdx += 36;
        }

        _tupdesc.DateStyle = IUnpack(data, dataIdx);
        _tupdesc.EuroDates = IUnpack(data, dataIdx + 4);
        if (_logger?.IsEnabled(LogLevel.Debug) == true)
            _logger.LogDebug(
                "RowStandard descriptor: NumFields={NumFields} MaxRecordSize={MaxRecordSize} FixedFieldsSize={FixedFieldsSize}",
                _tupdesc.NumFields,
                _tupdesc.MaxRecordSize,
                _tupdesc.FixedFieldsSize);
    }


    private byte[] _tmp_buffer;

    /// <summary>
    /// Maximum size of <see cref="_tmp_buffer"/> that is retained for the life of
    /// the connection. Larger commands/messages use a transient pooled buffer
    /// that is released after use, so one unusually large query does not pin a
    /// multi-megabyte array on a pooled connection.
    /// </summary>
    internal const int TmpBufferRetainCap = 64 * 1024;
    private byte[]? _largeReadBuffer;

    internal int TmpBufferCapacity => _tmp_buffer.Length;

    private RowValue[]? _row;

    public bool UseStringPool { get; set; } = true;

    /// <summary>
    /// When enabled, <c>RowStandard</c> rows are parsed for structure only and
    /// individual columns are converted on first access instead of eagerly for
    /// every row. Off by default; the eager path is unchanged when disabled.
    /// </summary>
    public bool UseLazyColumnDecoding { get; set; }

    private ReadOnlyMemory<byte> _lazyRowMemory;
    private byte[]? _lazyOversizeBuffer;
    private bool _lazyRowActive;

    private void ReleaseLazyOversize()
    {
        var buffer = _lazyOversizeBuffer;
        if (buffer is not null)
        {
            _lazyOversizeBuffer = null;
            _readBuffer?.ReturnOversize(buffer);
        }
    }

    private void ParseLazyRow(NzCommand nzCommand, ReadOnlyMemory<byte> payload, int numFields)
    {
        if (_row is null || _row.Length < numFields)
        {
            _row = new RowValue[numFields];
        }

        PrepareVariableFieldOffsets(payload.Span);
        for (int i = 0; i < numFields; i++)
        {
            _row[i].ResetForLazyDecode();
        }

        _lazyRowMemory = payload;
        _lazyRowActive = true;
        nzCommand.AddLazyRow(_row);
    }

    /// <summary>
    /// Decodes a single column of the currently retained lazy row on demand.
    /// Called only from <see cref="NzCommand.GetValue(int)"/>.
    /// </summary>
    internal void EnsureFieldDecoded(int ordinal)
    {
        if (!_lazyRowActive)
            return;

        ref RowValue rowValue = ref _row![ordinal];
        if (rowValue.typeCode != RowValue.NotDecoded)
            return;

        var data = _lazyRowMemory.Span;
        if (ColumnIsNull(data, ordinal))
        {
            rowValue.typeCode = TypeCodeEx.Empty;
            return;
        }

        DecodeField(data, ordinal, ref rowValue);
    }

    /// <summary>
    /// Null test for the retained lazy row that avoids materialising the value.
    /// </summary>
    internal bool IsFieldNull(int ordinal)
    {
        if (!_lazyRowActive || _lazyRowMemory.Length == 0)
            return false;
        return ColumnIsNull(_lazyRowMemory.Span, ordinal);
    }

    private string GetStandardString(int curField, ReadOnlySpan<byte> spanData, Encoding encoding)
    {
        var sp = _nzCommand?.GetColumnStringPool(curField);
        if (UseStringPool && sp is not null)
        {
            return sp.GetString(spanData, encoding);
        }
        else
        {
            return encoding.GetString(spanData);
        }
    }

    private string GetFixedLenString(int curField, ReadOnlySpan<byte> fieldDataP, int fldlen, int cursize)
    {
        if (fldlen < 120)
        {
            Span<char> chars = stackalloc char[fldlen];
            return DecodeFixedLenString(curField, fieldDataP, fldlen, cursize, chars);
        }

        char[] rented = ArrayPool<char>.Shared.Rent(fldlen);
        try
        {
            return DecodeFixedLenString(curField, fieldDataP, fldlen, cursize, rented.AsSpan(0, fldlen));
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented, clearArray: true);
        }
    }

    private string DecodeFixedLenString(
        int curField, ReadOnlySpan<byte> fieldDataP, int fldlen, int cursize, scoped Span<char> chars)
    {
        NzConnectionHelpers.ClientEncoding.TryGetChars(fieldDataP.Slice(2, cursize), chars, out int charsRead);
        chars[charsRead..fldlen].Fill(' ');
        var spanData = chars[0..fldlen];
        var sp = _nzCommand?.GetColumnStringPool(curField);
        if (UseStringPool && sp is not null)
            return sp.GetString(spanData);
        return new string(spanData);
    }

    /// <summary>
    /// reading rows = standard. most common way
    /// main hot path
    /// </summary>
    /// <param name="nzCommand"></param>
    private void ResReadDbosTuple(NzCommand nzCommand)
    {
        int numFields = _tupdesc.NumFields;
        _currentProtocolRowNumber = ++_protocolRowNumber;
        int rowLength = ReadProtocolLength("rowStandard.rowLength");
        int payloadLength = ReadProtocolLength("rowStandard.dbosPayloadLength", allowZero: false);

        if (_logger?.IsEnabled(LogLevel.Debug) == true)
            _logger.LogDebug(
                "RowStandard payload: Row={Row} RowLength={RowLength} PayloadLength={PayloadLength} NumFields={NumFields} MaxRecordSize={MaxRecordSize}",
                _currentProtocolRowNumber,
                rowLength,
                payloadLength,
                numFields,
                _tupdesc.MaxRecordSize);

        if (UseLazyColumnDecoding)
        {
            ReleaseLazyOversize();
            if (payloadLength <= _readBuffer!.Capacity)
            {
                _readBuffer.Ensure(payloadLength);
                ParseLazyRow(nzCommand, _readBuffer.ReadMemory(payloadLength), numFields);
            }
            else
            {
                byte[] rented = _readBuffer.RentOversize(payloadLength);
                ParseLazyRow(nzCommand, rented.AsMemory(0, payloadLength), numFields);
                _lazyOversizeBuffer = rented;
            }
            return;
        }

        if (payloadLength <= _readBuffer!.Capacity)
        {
            // Decode in place: no per-row copy into _tmp_buffer.
            _readBuffer.Ensure(payloadLength);
            ParseDbosTupleData(nzCommand, _readBuffer.ReadSpan(payloadLength), numFields);
        }
        else
        {
            byte[] rented = _readBuffer.RentOversize(payloadLength);
            try
            {
                ParseDbosTupleData(nzCommand, rented.AsSpan(0, payloadLength), numFields);
            }
            finally
            {
                _readBuffer.ReturnOversize(rented);
            }
        }
    }

    private async ValueTask ResReadDbosTupleAsync(NzCommand nzCommand, CancellationToken cancellationToken = default)
    {
        int numFields = _tupdesc.NumFields;
        _currentProtocolRowNumber = ++_protocolRowNumber;
        int rowLength = await ReadProtocolLengthAsync(
            "rowStandard.rowLength",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        int payloadLength = await ReadProtocolLengthAsync(
            "rowStandard.dbosPayloadLength",
            allowZero: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (_logger?.IsEnabled(LogLevel.Debug) == true)
            _logger.LogDebug(
                "RowStandard payload: Row={Row} RowLength={RowLength} PayloadLength={PayloadLength} NumFields={NumFields} MaxRecordSize={MaxRecordSize}",
                _currentProtocolRowNumber,
                rowLength,
                payloadLength,
                numFields,
                _tupdesc.MaxRecordSize);

        if (UseLazyColumnDecoding)
        {
            ReleaseLazyOversize();
            if (payloadLength <= _readBuffer!.Capacity)
            {
                await _readBuffer.EnsureAsync(payloadLength, cancellationToken).ConfigureAwait(false);
                ParseLazyRow(nzCommand, _readBuffer.ReadMemory(payloadLength), numFields);
            }
            else
            {
                byte[] rented = await _readBuffer.RentOversizeAsync(payloadLength, cancellationToken).ConfigureAwait(false);
                ParseLazyRow(nzCommand, rented.AsMemory(0, payloadLength), numFields);
                _lazyOversizeBuffer = rented;
            }
            return;
        }

        if (payloadLength <= _readBuffer!.Capacity)
        {
            await _readBuffer.EnsureAsync(payloadLength, cancellationToken).ConfigureAwait(false);
            // Span locals are not allowed in async methods on the net8 target,
            // so decoding runs in this synchronous helper.
            ParseCurrentDbosTuple(nzCommand, payloadLength, numFields);
        }
        else
        {
            byte[] rented = await _readBuffer.RentOversizeAsync(payloadLength, cancellationToken).ConfigureAwait(false);
            try
            {
                ParseDbosTupleData(nzCommand, rented.AsSpan(0, payloadLength), numFields);
            }
            finally
            {
                _readBuffer.ReturnOversize(rented);
            }
        }
    }

    private void ParseCurrentDbosTuple(NzCommand nzCommand, int payloadLength, int numFields)
        => ParseDbosTupleData(nzCommand, _readBuffer!.ReadSpan(payloadLength), numFields);

    /// <summary>
    /// Fast discard for a <c>RowStandard</c> payload: validates framing exactly
    /// like <see cref="ResReadDbosTuple"/> but skips the bytes without calling
    /// <see cref="ParseDbosTupleData"/>. No strings, numerics, dates, or
    /// <c>RowValue</c> updates are performed.
    /// </summary>
    private void SkipRowStandardPayload()
    {
        _currentProtocolRowNumber = ++_protocolRowNumber;
        int rowLength = ReadProtocolLength("rowStandard.rowLength");
        int payloadLength = ReadProtocolLength("rowStandard.dbosPayloadLength", allowZero: false);

        // The previous lazy row (if any) is no longer needed once we move on.
        if (_lazyRowActive)
        {
            ReleaseLazyOversize();
            _lazyRowMemory = default;
            _lazyRowActive = false;
        }
        // A previous large DataRow payload must not be pinned while draining.
        ReleaseLargeReadBuffer();

        var discardBuffer = _readBuffer;
        if (discardBuffer is null)
            throw new NetezzaException("Connection is closed.");
        discardBuffer.Discard(payloadLength);
        DiscardedRows++;
    }

    private async ValueTask SkipRowStandardPayloadAsync(CancellationToken cancellationToken)
    {
        _currentProtocolRowNumber = ++_protocolRowNumber;
        int rowLength = await ReadProtocolLengthAsync(
            "rowStandard.rowLength",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        int payloadLength = await ReadProtocolLengthAsync(
            "rowStandard.dbosPayloadLength",
            allowZero: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (_lazyRowActive)
        {
            ReleaseLazyOversize();
            _lazyRowMemory = default;
            _lazyRowActive = false;
        }
        ReleaseLargeReadBuffer();

        var discardBuffer = _readBuffer;
        if (discardBuffer is null)
            throw new NetezzaException("Connection is closed.");
        await discardBuffer.DiscardAsync(payloadLength, cancellationToken).ConfigureAwait(false);
        DiscardedRows++;
    }

    private void SkipDataRowPayload()
    {
        int length = ReadProtocolLength("dataRowPayloadLength");
        ReleaseLargeReadBuffer();
        var discardBuffer = _readBuffer;
        if (discardBuffer is null)
            throw new NetezzaException("Connection is closed.");
        discardBuffer.Discard(length);
        DiscardedRows++;
    }

    private async ValueTask SkipDataRowPayloadAsync(CancellationToken cancellationToken)
    {
        int length = await ReadProtocolLengthAsync(
            "dataRowPayloadLength",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        ReleaseLargeReadBuffer();
        var discardBuffer = _readBuffer;
        if (discardBuffer is null)
            throw new NetezzaException("Connection is closed.");
        await discardBuffer.DiscardAsync(length, cancellationToken).ConfigureAwait(false);
        DiscardedRows++;
    }

    private void ParseDbosTupleData(NzCommand nzCommand, ReadOnlySpan<byte> data, int numFields)
    {

        if (_row is null || _row.Length < numFields)
        {
            _row = new RowValue[numFields];
        }

        PrepareVariableFieldOffsets(data);

        bool logDebug = _logger?.IsEnabled(LogLevel.Debug) == true;

        int fieldLf = 0;
        int curField = 0;

        while (fieldLf < numFields && curField < numFields)
        {
            ref RowValue rowValue = ref _row[fieldLf];
            rowValue.ResetForReuse(); // Drop references retained by the previous row before reusing this slot.

            //var standardImplementation = bitmap[tupdesc.FieldPhysField[fieldLf]] == 1;
            //Debug.Assert(standardImplementation == res);

            // a bitmap with value of 1 denotes null column
            if (ColumnIsNull(data, fieldLf))
            {
                rowValue.typeCode = TypeCodeEx.Empty;
                if (logDebug) _logger?.LogDebug("field={Field}, value= NULL", curField + 1);
                curField += 1;
                fieldLf += 1;
                continue;
            }

            // Locate the field payload only for non-null columns.
            ReadOnlySpan<byte> fieldDataP = CTableFieldAt(data, curField);

            // Fldlen is byte-length of backend-datatype
            // memsize is byte-length of ODBC-datatype or internal-datatype for (Numeric/Interval)
            int fldlen = CTableIFieldSize(curField);
            int fldtype = CTableIFieldType(curField);

            // Single dispatch: the previous if/else-if chain re-tested the type
            // several times per column. Types are mutually exclusive, so a
            // switch is equivalent and cheaper on the per-row hot path.
            switch (fldtype)
            {
                case NzTypeUnknown:
                case NzTypeVarChar:
                case NzTypeVarFixedChar:
                case NzTypeGeometry:
                case NzTypeJson:
                case NzTypeJsonb:
                case NzTypeJsonpath:
                    {
                        int cursize = BitConverter.ToUInt16(fieldDataP) - 2;
                        string value = GetStandardString(curField, fieldDataP.Slice(2, cursize), NzConnectionHelpers.CharVarcharEncoding);
                        rowValue.typeCode = TypeCodeEx.String;
                        rowValue.stringValue = value;
                        if (logDebug) _logger?.LogDebug("field={Field}, datatype={Datatype}, value={Value}", curField + 1, fldtype.ToString(), value);
                        break;
                    }

                case NzTypeVarBinary:
                    {
                        int cursize = BitConverter.ToUInt16(fieldDataP) - 2;
                        byte[] value = fieldDataP.Slice(2, cursize).ToArray();
                        rowValue.typeCode = TypeCodeEx.Object;
                        rowValue.objectValue = value;
                        if (logDebug) _logger?.LogDebug("field={Field}, datatype=VARBINARY, bytes={Value}", curField + 1, value.Length);
                        break;
                    }

                case NzTypeBinary:
                    {
                        byte[] value = fieldDataP.Slice(0, fldlen).ToArray();
                        rowValue.typeCode = TypeCodeEx.Object;
                        rowValue.objectValue = value;
                        if (logDebug) _logger?.LogDebug("field={Field}, datatype=BINARY, bytes={Value}", curField + 1, value.Length);
                        break;
                    }

                case NzTypeChar:
                    {
                        // CHAR is blank-padded in the binary row format. The
                        // shared wire corpus (and the Rust/Node/Python drivers)
                        // expose the logical value without the padding.
                        string value = GetStandardString(curField, fieldDataP.Slice(0, fldlen), NzConnectionHelpers.CharVarcharEncoding)
                            .TrimEnd(' ');
                        rowValue.typeCode = TypeCodeEx.String;
                        rowValue.stringValue = value;
                        if (logDebug) _logger?.LogDebug("field={Field}, datatype=CHAR, value={Value}", curField + 1, value);
                        break;
                    }

                case NzTypeNChar:
                case NzTypeNVarChar:
                    {
                        int cursize = BitConverter.ToUInt16(fieldDataP) - 2;
                        string value;
                        if (fldtype == NzTypeNVarChar || fldlen == cursize)
                        {
                            value = GetStandardString(curField, fieldDataP.Slice(2, cursize), NzConnectionHelpers.ClientEncoding);
                        }
                        else
                        {
                            value = GetFixedLenString(curField, fieldDataP, fldlen, cursize);
                        }
                        rowValue.typeCode = TypeCodeEx.String;
                        rowValue.stringValue = value;
                        if (logDebug) _logger?.LogDebug("field={Field}, datatype={Datatype}, value={Value}", curField + 1, fldtype.ToString(), value);
                        break;
                    }

                case NzTypeInt8:  // int64
                    {
                        long value = BitConverter.ToInt64(fieldDataP);
                        rowValue.typeCode = TypeCodeEx.Int64;
                        rowValue.int64Value = value;
                        if (logDebug) _logger?.LogDebug("field={Field}, datatype=NzTypeInt8, value={Value}", curField + 1, value);
                        break;
                    }

                case NzTypeIntvsAbsTimeFIX: //https://github.com/IBM/nzpy/issues/61 //TODO, SELECT CREATEDATE FROM SYSTEM.ADMIN._V_TABLE_STORAGE_STAT
                    {
                        DateTime value = DateTypes.TimestampRecvInt(fieldDataP);
                        rowValue.typeCode = TypeCodeEx.DateTime;
                        rowValue.dateTimeValue = value;
                        if (logDebug) _logger?.LogDebug("field={Field}, datatype=NzTypeInt4, value={Value}", curField + 1, value);
                        break;
                    }

                case NzTypeInt:  // int32
                    {
                        int value = BitConverter.ToInt32(fieldDataP);
                        rowValue.typeCode = TypeCodeEx.Int32;
                        rowValue.int32Value = value;
                        if (logDebug) _logger?.LogDebug("field={Field}, datatype=NzTypeInt4, value={Value}", curField + 1, value);
                        break;
                    }

                case NzTypeInt2:  // int16
                    {
                        short value = BitConverter.ToInt16(fieldDataP);
                        rowValue.typeCode = TypeCodeEx.Int16;
                        rowValue.int16Value = value;
                        if (logDebug) _logger?.LogDebug("field={Field}, datatype=NzTypeInt2, value={Value}", curField + 1, value);
                        break;
                    }

                case NzTypeInt1:
                    {
                        //sbyte value = (sbyte)fieldDataP[0];
                        Int16 value = (Int16)(sbyte)fieldDataP[0]; // int 16 to be in pair with ODBC
                        rowValue.typeCode = TypeCodeEx.Int16; //fix to byte ?
                        rowValue.int16Value = value;
                        if (logDebug) _logger?.LogDebug("field={Field}, datatype=NzTypeInt1, value={Value}", curField + 1, value);
                        break;
                    }

                case NzTypeDouble:
                    {
                        double value = BitConverter.ToDouble(fieldDataP);
                        rowValue.typeCode = TypeCodeEx.Double;
                        rowValue.doubleValue = value;
                        if (logDebug) _logger?.LogDebug("field={Field}, datatype=NzTypeDouble, value={Value}", curField + 1, value);
                        break;
                    }

                case NzTypeFloat:
                    {
                        float value = BitConverter.ToSingle(fieldDataP);
                        rowValue.typeCode = TypeCodeEx.Single;
                        rowValue.singleValue = value;
                        if (logDebug) _logger?.LogDebug("field={Field}, datatype=NzTypeFloat, value={Value}", curField + 1, value);
                        break;
                    }

                case NzTypeDate:
                    {
                        DateTime value = DateTypes.ToDateTimeFrom4Bytes(fieldDataP);
                        rowValue.typeCode = TypeCodeEx.DateTime;
                        rowValue.dateTimeValue = value;
                        if (logDebug) _logger?.LogDebug("field={Field}, datatype=DATE, value={Value}", curField + 1, value);
                        break;
                    }

                case NzTypeTime:
                    {
                        TimeSpan value = DateTypes.TimeRecvFloatX2(fieldDataP);
                        rowValue.typeCode = TypeCodeEx.TimeSpan;
                        rowValue.timeSpanValue = value;
                        if (logDebug) _logger?.LogDebug("field={Field}, datatype=TIME, value={Value}", curField + 1, value);
                        break;
                    }

                case NzTypeInterval:
                    {
                        string value = DateTypes.TimeRecvFloatX1(fieldDataP);
                        rowValue.typeCode = TypeCodeEx.String;
                        rowValue.stringValue = value;
                        if (logDebug) _logger?.LogDebug("field={Field}, datatype=INTERVAL, value={Value}", curField + 1, value);
                        break;
                    }

                case NzTypeTimeTz: // https://www.ibm.com/docs/en/netezza?topic=tdt-time-time-zone-timetz
                    {
                        TimeSpan timeSpanVal = DateTypes.TimeRecvFloatX2(fieldDataP);
                        int timetzZone = BitConverter.ToInt32(fieldDataP.Slice(fldlen - 4));
                        rowValue.typeCode = TypeCodeEx.String;
                        rowValue.stringValue = DateTypes.TimetzOutTimetzadt(timeSpanVal, timetzZone);
                        //rowValue.stringValue = value.ToString();
                        if (logDebug) _logger?.LogDebug("field={Field}, datatype=TIMETZ, value={Value}", curField + 1, rowValue.stringValue);
                        break;
                    }

                case NzTypeTimestamp:
                    {
                        DateTime value = DateTypes.ToDateTimeFrom8Bytes(fieldDataP);
                        rowValue.typeCode = TypeCodeEx.DateTime;
                        rowValue.dateTimeValue = value;
                        if (logDebug) _logger?.LogDebug("field={Field}, datatype=TIMESTAMP, value={Value}", curField + 1, value);
                        break;
                    }

                case NzTypeNumeric:
                    {
                        DecodeNumericField(fieldDataP, curField, ref rowValue, logDebug);
                        break;
                    }

                case NzTypeBool:
                    {
                        bool value = fieldDataP[0] == 0x01;
                        rowValue.typeCode = TypeCodeEx.Boolean;
                        rowValue.boolValue = value;
                        if (logDebug) _logger?.LogDebug("field={Field}, datatype=BOOL, value={Value}", curField + 1, value);
                        break;
                    }
            }

            curField += 1;
            fieldLf += 1;
        }

        nzCommand.AddRow(_row);
    }

    /// <summary>
    /// Decodes one NUMERIC column. Kept out of the row loops on purpose: the
    /// overflow fallback needs a try/catch, and an EH region inside the
    /// per-field loop prevents the JIT from keeping the loop state in
    /// registers. The rare exception paths stay in this helper.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void DecodeNumericField(ReadOnlySpan<byte> fieldDataP, int curField, ref RowValue rowValue, bool logDebug)
    {
        int prec = CTableIFieldPrecision(curField);
        int scale = CTableIFieldScale(curField);
        int count = CTableIFieldNumericDigit32Count(curField);

        // System.Decimal has at most 29 significant digits. A NUMERIC(38,19)
        // value fits in decimal's magnitude but not its precision: parsing it
        // would silently round (for example 9999999999999999999.9999999999999999999
        // became 10000000000000000000.000000000). Keep wide numerics exact as
        // text, matching the Rust/Node/Python drivers.
        if (prec > 28)
        {
            rowValue.typeCode = TypeCodeEx.String;
            rowValue.stringValue = Numeric.GetNumericString(fieldDataP, prec, scale, count);
            if (logDebug) _logger?.LogDebug("field={Field}, datatype=NUMERIC, value={Value}", curField + 1, rowValue.stringValue);
            return;
        }

        decimal value;
        try
        {
            value = Numeric.GetCsNumeric(fieldDataP, prec, scale, count);
        }
        catch (OverflowException)
        {
            // System.Decimal tops out at ~7.9e28. Wider NUMERIC values
            // (up to NUMERIC(38)) stay exact as text.
            rowValue.typeCode = TypeCodeEx.String;
            rowValue.stringValue = Numeric.GetNumericString(fieldDataP, prec, scale, count);
            if (logDebug) _logger?.LogDebug("field={Field}, datatype=NUMERIC, value={Value}", curField + 1, rowValue.stringValue);
            return;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException or InvalidCastException)
        {
            throw new InvalidCastException($"Failed to convert column {curField + 1} as NUMERIC.", ex);
        }

        rowValue.typeCode = TypeCodeEx.Decimal;
        rowValue.decimalValue = value;
        if (logDebug) _logger?.LogDebug("field={Field}, datatype=NUMERIC, value={Value}", curField + 1, value);
    }

    /// <summary>
    /// Decodes one column from a retained lazy row. The case bodies mirror the
    /// eager switch in <see cref="ParseDbosTupleData"/>; keep the two in sync.
    /// </summary>
    private void DecodeField(ReadOnlySpan<byte> data, int curField, ref RowValue rowValue)
    {
        ReadOnlySpan<byte> fieldDataP = CTableFieldAt(data, curField);
        int fldlen = CTableIFieldSize(curField);
        int fldtype = CTableIFieldType(curField);
        bool logDebug = _logger?.IsEnabled(LogLevel.Debug) == true;

        switch (fldtype)
        {
            case NzTypeUnknown:
            case NzTypeVarChar:
            case NzTypeVarFixedChar:
            case NzTypeGeometry:
            case NzTypeJson:
            case NzTypeJsonb:
            case NzTypeJsonpath:
                {
                    int cursize = BitConverter.ToUInt16(fieldDataP) - 2;
                    string value = GetStandardString(curField, fieldDataP.Slice(2, cursize), NzConnectionHelpers.CharVarcharEncoding);
                    rowValue.typeCode = TypeCodeEx.String;
                    rowValue.stringValue = value;
                    if (logDebug) _logger?.LogDebug("field={Field}, datatype={Datatype}, value={Value}", curField + 1, fldtype.ToString(), value);
                    break;
                }

            case NzTypeVarBinary:
                {
                    int cursize = BitConverter.ToUInt16(fieldDataP) - 2;
                    byte[] value = fieldDataP.Slice(2, cursize).ToArray();
                    rowValue.typeCode = TypeCodeEx.Object;
                    rowValue.objectValue = value;
                    if (logDebug) _logger?.LogDebug("field={Field}, datatype=VARBINARY, bytes={Value}", curField + 1, value.Length);
                    break;
                }

            case NzTypeBinary:
                {
                    byte[] value = fieldDataP.Slice(0, fldlen).ToArray();
                    rowValue.typeCode = TypeCodeEx.Object;
                    rowValue.objectValue = value;
                    if (logDebug) _logger?.LogDebug("field={Field}, datatype=BINARY, bytes={Value}", curField + 1, value.Length);
                    break;
                }

            case NzTypeChar:
                {
                    // See the synchronous decoder: CHAR padding is not part of
                    // the logical value.
                    string value = GetStandardString(curField, fieldDataP.Slice(0, fldlen), NzConnectionHelpers.CharVarcharEncoding)
                        .TrimEnd(' ');
                    rowValue.typeCode = TypeCodeEx.String;
                    rowValue.stringValue = value;
                    if (logDebug) _logger?.LogDebug("field={Field}, datatype=CHAR, value={Value}", curField + 1, value);
                    break;
                }

            case NzTypeNChar:
            case NzTypeNVarChar:
                {
                    int cursize = BitConverter.ToUInt16(fieldDataP) - 2;
                    string value;
                    if (fldtype == NzTypeNVarChar || fldlen == cursize)
                    {
                        value = GetStandardString(curField, fieldDataP.Slice(2, cursize), NzConnectionHelpers.ClientEncoding);
                    }
                    else
                    {
                        value = GetFixedLenString(curField, fieldDataP, fldlen, cursize);
                    }
                    rowValue.typeCode = TypeCodeEx.String;
                    rowValue.stringValue = value;
                    if (logDebug) _logger?.LogDebug("field={Field}, datatype={Datatype}, value={Value}", curField + 1, fldtype.ToString(), value);
                    break;
                }

            case NzTypeInt8:
                {
                    long value = BitConverter.ToInt64(fieldDataP);
                    rowValue.typeCode = TypeCodeEx.Int64;
                    rowValue.int64Value = value;
                    if (logDebug) _logger?.LogDebug("field={Field}, datatype=NzTypeInt8, value={Value}", curField + 1, value);
                    break;
                }

            case NzTypeIntvsAbsTimeFIX:
                {
                    DateTime value = DateTypes.TimestampRecvInt(fieldDataP);
                    rowValue.typeCode = TypeCodeEx.DateTime;
                    rowValue.dateTimeValue = value;
                    if (logDebug) _logger?.LogDebug("field={Field}, datatype=NzTypeInt4, value={Value}", curField + 1, value);
                    break;
                }

            case NzTypeInt:
                {
                    int value = BitConverter.ToInt32(fieldDataP);
                    rowValue.typeCode = TypeCodeEx.Int32;
                    rowValue.int32Value = value;
                    if (logDebug) _logger?.LogDebug("field={Field}, datatype=NzTypeInt4, value={Value}", curField + 1, value);
                    break;
                }

            case NzTypeInt2:
                {
                    short value = BitConverter.ToInt16(fieldDataP);
                    rowValue.typeCode = TypeCodeEx.Int16;
                    rowValue.int16Value = value;
                    if (logDebug) _logger?.LogDebug("field={Field}, datatype=NzTypeInt2, value={Value}", curField + 1, value);
                    break;
                }

            case NzTypeInt1:
                {
                    Int16 value = (Int16)(sbyte)fieldDataP[0];
                    rowValue.typeCode = TypeCodeEx.Int16;
                    rowValue.int16Value = value;
                    if (logDebug) _logger?.LogDebug("field={Field}, datatype=NzTypeInt1, value={Value}", curField + 1, value);
                    break;
                }

            case NzTypeDouble:
                {
                    double value = BitConverter.ToDouble(fieldDataP);
                    rowValue.typeCode = TypeCodeEx.Double;
                    rowValue.doubleValue = value;
                    if (logDebug) _logger?.LogDebug("field={Field}, datatype=NzTypeDouble, value={Value}", curField + 1, value);
                    break;
                }

            case NzTypeFloat:
                {
                    float value = BitConverter.ToSingle(fieldDataP);
                    rowValue.typeCode = TypeCodeEx.Single;
                    rowValue.singleValue = value;
                    if (logDebug) _logger?.LogDebug("field={Field}, datatype=NzTypeFloat, value={Value}", curField + 1, value);
                    break;
                }

            case NzTypeDate:
                {
                    DateTime value = DateTypes.ToDateTimeFrom4Bytes(fieldDataP);
                    rowValue.typeCode = TypeCodeEx.DateTime;
                    rowValue.dateTimeValue = value;
                    if (logDebug) _logger?.LogDebug("field={Field}, datatype=DATE, value={Value}", curField + 1, value);
                    break;
                }

            case NzTypeTime:
                {
                    TimeSpan value = DateTypes.TimeRecvFloatX2(fieldDataP);
                    rowValue.typeCode = TypeCodeEx.TimeSpan;
                    rowValue.timeSpanValue = value;
                    if (logDebug) _logger?.LogDebug("field={Field}, datatype=TIME, value={Value}", curField + 1, value);
                    break;
                }

            case NzTypeInterval:
                {
                    string value = DateTypes.TimeRecvFloatX1(fieldDataP);
                    rowValue.typeCode = TypeCodeEx.String;
                    rowValue.stringValue = value;
                    if (logDebug) _logger?.LogDebug("field={Field}, datatype=INTERVAL, value={Value}", curField + 1, value);
                    break;
                }

            case NzTypeTimeTz:
                {
                    TimeSpan timeSpanVal = DateTypes.TimeRecvFloatX2(fieldDataP);
                    int timetzZone = BitConverter.ToInt32(fieldDataP.Slice(fldlen - 4));
                    rowValue.typeCode = TypeCodeEx.String;
                    rowValue.stringValue = DateTypes.TimetzOutTimetzadt(timeSpanVal, timetzZone);
                    if (logDebug) _logger?.LogDebug("field={Field}, datatype=TIMETZ, value={Value}", curField + 1, rowValue.stringValue);
                    break;
                }

            case NzTypeTimestamp:
                {
                    DateTime value = DateTypes.ToDateTimeFrom8Bytes(fieldDataP);
                    rowValue.typeCode = TypeCodeEx.DateTime;
                    rowValue.dateTimeValue = value;
                    if (logDebug) _logger?.LogDebug("field={Field}, datatype=TIMESTAMP, value={Value}", curField + 1, value);
                    break;
                }

            case NzTypeNumeric:
                {
                    DecodeNumericField(fieldDataP, curField, ref rowValue, logDebug);
                    break;
                }

            case NzTypeBool:
                {
                    bool value = fieldDataP[0] == 0x01;
                    rowValue.typeCode = TypeCodeEx.Boolean;
                    rowValue.boolValue = value;
                    if (logDebug) _logger?.LogDebug("field={Field}, datatype=BOOL, value={Value}", curField + 1, value);
                    break;
                }
        }
    }


    private bool ColumnIsNull(ReadOnlySpan<byte> data, int fieldLf)
    {
        // When the descriptor says nulls are impossible, skip the bitmap read.
        var tupdesc = _tupdesc;
        if (tupdesc.NullsAllowed == 0)
            return false;

        // The byte offset and bit mask are precomputed when the descriptor is
        // parsed (see ResGetDbosColumnDescriptions): no per-field div/mod here.
        byte mask = tupdesc.FieldNullBitMaskArr[fieldLf];
        int byteIndex = tupdesc.FieldNullByteIndexArr[fieldLf];
        return (data[2 + byteIndex] & mask) != 0;
    }

    private int CTableIFieldPrecision(int coldex)
    {
        return ((_tupdesc.FieldSizeArr[coldex] >> 8) & 0x7F);
    }

    internal int CTableIFieldScale(int coldex)
    {
        return (_tupdesc.FieldSizeArr[coldex] & 0x00FF);
    }
    internal int CTableIFieldScaleAlternative(int coldex)
    {
        var typeModyfier = _nzCommand.NewPreparedStatement!.Description![coldex].TypeModifier;
        typeModyfier &= 0b111111;
        //return ((typeModyfier >> 3) - 2) * 8 + (typeModyfier & 0b000111);
        //return (typeModyfier & 0b111000) - 16 + (typeModyfier & 0b000111);
        return typeModyfier - 16;
    }

    internal int CTableIFieldPrecisionAlternative(int coldex)
    {
        var typeModyfier = _nzCommand.NewPreparedStatement!.Description![coldex].TypeModifier;
        typeModyfier = typeModyfier >> 16;
        return typeModyfier & 0b0000000000111111;
    }
    internal int CTableIFieldSizeAlternative(int coldex)
    {
        var ts = _nzCommand.NewPreparedStatement!.Description![coldex].TypeSize;
        if (ts != -1)
        {
            return ts;
        }
        if (IsExtendedRowDescriptionAvaiable())
        {
            return _tupdesc.FieldSizeArr[coldex];
        }
        return _nzCommand.NewPreparedStatement!.Description![coldex].TypeModifier - 16;
    }
    internal bool IsColumnNullable(int coldex)
    {
        if (!IsExtendedRowDescriptionAvaiable())
        {
            return true;
        }
        if (_tupdesc.NullsAllowed <= 0)
        {
            return false;
        }
        if (IsExtendedRowDescriptionAvaiable() && _tupdesc.NullsAllowed > 0)
        {
            return _tupdesc.FieldNullAllowedArr[coldex];
        }
        return true;
    }

    private int CTableIFieldNumericDigit32Count(int coldex)
    {
        int sizeTNumericDigit = 4;
        return _tupdesc.FieldTrueSizeArr[coldex] / sizeTNumericDigit;
    }
    internal bool IsExtendedRowDescriptionAvaiable() => _tupdesc is not null;


    private int CTableIFieldType(int curField)
    {
        return _tupdesc.FieldTypeArr[curField];
    }

    private int CTableIFieldSize(int curField)
    {
        return _tupdesc.FieldSizeArr[curField];
    }

    private int[] _variableFieldOffsets = Array.Empty<int>();
    private int _variableFieldOffsetCount;

    private ReadOnlySpan<byte> CTableFieldAt(ReadOnlySpan<byte> data, int curField)
    {
        if (_tupdesc.FieldFixedSizeArr[curField] != 0)
        {
            return CTableIFixedFieldPtr(data, _tupdesc.FieldOffsetArr[curField]);
        }

        int variableOrdinal = _tupdesc.FieldOffsetArr[curField];
        if ((uint)variableOrdinal >= (uint)_variableFieldOffsetCount)
        {
            throw new InvalidDataException($"Invalid varying-field offset {variableOrdinal} for column {curField + 1}.");
        }
        return data[_variableFieldOffsets[variableOrdinal]..];
    }

    internal static void FillVariableFieldOffsets(ReadOnlySpan<byte> data, int fixedOffset, Span<int> offsets)
    {
        if ((uint)fixedOffset > (uint)data.Length)
            throw new InvalidDataException("The fixed-field area extends beyond the row payload.");

        int position = fixedOffset;
        for (int i = 0; i < offsets.Length; i++)
        {
            if (position > data.Length - sizeof(short))
                throw new InvalidDataException("The varying-field length prefix is truncated.");

            int length = BitConverter.ToUInt16(data.Slice(position, sizeof(short)));
            if (length < sizeof(short))
                throw new InvalidDataException("The varying-field length is invalid.");

            int paddedLength = length + (length & 1);
            if (paddedLength > data.Length - position)
                throw new InvalidDataException("The varying-field payload extends beyond the row.");

            offsets[i] = position;
            position += paddedLength;
        }
    }

    private void PrepareVariableFieldOffsets(ReadOnlySpan<byte> data)
    {
        int count = _tupdesc.NumVaryingFields ?? 0;
        if (count < 0)
            throw new InvalidDataException("The varying-field count is invalid.");
        if (_variableFieldOffsets.Length < count)
            Array.Resize(ref _variableFieldOffsets, count);

        _variableFieldOffsetCount = count;
        if (count > 0)
            FillVariableFieldOffsets(data, _tupdesc.FixedFieldsSize, _variableFieldOffsets.AsSpan(0, count));
    }

    private static ReadOnlySpan<byte> CTableIFixedFieldPtr(ReadOnlySpan<byte> data, int offset)
    {
        return data[offset..];
    }

    //only for system tabeles  + selects without from ? -> "SELECT * FROM _V_TABLE"  or "SELECT 123"
    private void HandleDataRow(byte[] data, NzCommand nzCommand)
    {
        // bitmaplen denotes the number of bytes bitmap sent by backend.
        // For e.g.: for select statement with 9 columns, we would receive 2 bytes bitmap.
        int numberOfCol = nzCommand.NewPreparedStatement!.FieldCount;
        int bitmapLen = numberOfCol / 8;
        if ((numberOfCol % 8) > 0)
        {
            bitmapLen += 1;
        }

        int dataIdx = bitmapLen;
        if (_row is null || _row.Length < numberOfCol)
        {
            _row = new RowValue[numberOfCol];
        }

        for (int columnNumber = 0; columnNumber < numberOfCol; columnNumber++)
        {
            var byteToTest = (byte)data[columnNumber / 8];
            var positionInByteToTest = 7 - columnNumber % 8;
            var nullHelpValue = byteToTest & (1 << positionInByteToTest);
            ref RowValue rowValue = ref _row[columnNumber];
            rowValue.ResetForReuse(); // Drop references retained by the previous row before reusing this slot.
            if (nullHelpValue == 0)
            {
                rowValue.typeCode = TypeCodeEx.Empty;
            }
            else
            {
                var typeOid = nzCommand.NewPreparedStatement.Description![columnNumber].TypeOID;
                Sylvan? sp = UseStringPool ? nzCommand.GetColumnStringPool(columnNumber) : null;
                int vlen = IUnpack(data, dataIdx);
                dataIdx += 4;

                switch (typeOid)
                {
                    case 16: // boolean
                        rowValue.typeCode = TypeCodeEx.Boolean;
                        rowValue.boolValue = NzConnectionHelpers.BoolRecvTyped(data, dataIdx, vlen - 4);
                        break;
                    case 17: // bytea
                    case 2568: // varbinary(n)
                    case 2569: // varbinary
                        rowValue.typeCode = TypeCodeEx.Object;
                        rowValue.objectValue = NzConnectionHelpers.VarbinaryInTyped(data, dataIdx, vlen - 4);
                        break;
                    case 19: // name type
                        rowValue.typeCode = TypeCodeEx.String;
                        rowValue.stringValue = NzConnectionHelpers.TextRecv(data, dataIdx, vlen - 4, sp);
                        break;
                    case 20: // int8
                        rowValue.typeCode = TypeCodeEx.Int64;
                        rowValue.int64Value = NzConnectionHelpers.Int8RecvTyped(data, dataIdx, vlen - 4);
                        break;
                    case 21: // int2
                        rowValue.typeCode = TypeCodeEx.Int16;
                        rowValue.int16Value = NzConnectionHelpers.Int2RecvTyped(data, dataIdx, vlen - 4);
                        break;
                    case 23: // int4
                        rowValue.typeCode = TypeCodeEx.Int32;
                        rowValue.int32Value = NzConnectionHelpers.Int4RecvTyped(data, dataIdx, vlen - 4);
                        break;
                    case 25: // TEXT type
                        rowValue.typeCode = TypeCodeEx.String;
                        rowValue.stringValue = NzConnectionHelpers.TextRecv(data, dataIdx, vlen - 4, sp);
                        break;
                    case 26: // oid
                        rowValue.typeCode = TypeCodeEx.Int32;
                        rowValue.int32Value = NzConnectionHelpers.Int4RecvTyped(data, dataIdx, vlen - 4);
                        break;
                    case 28: // xid
                        rowValue.typeCode = TypeCodeEx.Int32;
                        rowValue.int32Value = NzConnectionHelpers.Int4RecvTyped(data, dataIdx, vlen - 4);
                        break;
                    case 700: // float4
                        rowValue.typeCode = TypeCodeEx.Single;
                        rowValue.singleValue = NzConnectionHelpers.Float4RecvTyped(data, dataIdx, vlen - 4);
                        break;
                    case 701: // float8
                        rowValue.typeCode = TypeCodeEx.Double;
                        rowValue.doubleValue = NzConnectionHelpers.Float8RecvTyped(data, dataIdx, vlen - 4);
                        break;
                    case 702: // SELECT CREATEDATE FROM _V_TABLE ORDER BY CREATEDATE DESC .. 
                        rowValue.typeCode = TypeCodeEx.DateTime; //with time
                        rowValue.dateTimeValue = DateTypes.TimestamptzRecvFloatTyped(data, dataIdx, vlen - 4);
                        break;
                    case 705: // unknown
                        rowValue.typeCode = TypeCodeEx.String;
                        rowValue.stringValue = NzConnectionHelpers.TextRecv(data, dataIdx, vlen - 4, sp);
                        break;
                    case 829: // MACADDR type
                        rowValue.typeCode = TypeCodeEx.String;
                        rowValue.stringValue = NzConnectionHelpers.TextRecv(data, dataIdx, vlen - 4, sp);
                        break;
                    case 1042: // CHAR type
                        rowValue.typeCode = TypeCodeEx.String;
                        rowValue.stringValue = NzConnectionHelpers.TextRecv(data, dataIdx, vlen - 4, sp);
                        break;
                    case 1043: // VARCHAR type
                        rowValue.typeCode = TypeCodeEx.String;
                        rowValue.stringValue = NzConnectionHelpers.TextRecv(data, dataIdx, vlen - 4, sp);
                        break;
                    case 1082: // date
                        rowValue.typeCode = TypeCodeEx.DateTime;//without time
                        rowValue.dateTimeValue = ConvertField(() => DateTypes.DateInTyped(data, dataIdx, vlen - 4), columnNumber, "DATE");
                        break;
                    case 1083: // time
                        rowValue.typeCode = TypeCodeEx.TimeSpan;
                        rowValue.timeSpanValue = DateTypes.TimeInTyped(data, dataIdx, vlen - 4);
                        break;
                    case 1114: // timestamp w/ tz
                        rowValue.typeCode = TypeCodeEx.DateTime;
                        rowValue.dateTimeValue = ConvertField(() => DateTypes.TimestampRecvFloatTyped(data, dataIdx, vlen - 4), columnNumber, "TIMESTAMP");
                        break;
                    case 1184:
                        rowValue.typeCode = TypeCodeEx.DateTime;
                        rowValue.dateTimeValue = ConvertField(() => DateTypes.TimestamptzRecvFloatTyped(data, dataIdx, vlen - 4), columnNumber, "TIMESTAMPTZ");
                        break;
                    case 1186:
                        rowValue.typeCode = TypeCodeEx.String;
                        rowValue.stringValue = NzConnectionHelpers.IntervalRecvInteger(data, dataIdx, vlen - 4);
                        break;
                    case 1700: // NUMERIC
                        {
                            var field = nzCommand.NewPreparedStatement.Description![columnNumber];
                            int precision = field.TypeModifier > 16
                                ? (field.TypeModifier - 16) >> 16
                                : 0;
                            // decimal has 29 significant digits at most. Parsing a
                            // wider NUMERIC rounds silently, so keep the server's
                            // exact text instead.
                            if (precision > 28)
                            {
                                rowValue.typeCode = TypeCodeEx.String;
                                rowValue.stringValue = Encoding.ASCII.GetString(data, dataIdx, vlen - 4);
                                break;
                            }

                            try
                            {
                                rowValue.typeCode = TypeCodeEx.Decimal;
                                rowValue.decimalValue = NzConnectionHelpers.NumericInTyped(data, dataIdx, vlen - 4);
                            }
                            catch (OverflowException)
                            {
                                rowValue.typeCode = TypeCodeEx.String;
                                rowValue.stringValue = Encoding.ASCII.GetString(data, dataIdx, vlen - 4);
                            }
                            break;
                        }
                    case 2275: // cstring
                        rowValue.typeCode = TypeCodeEx.String;
                        rowValue.stringValue = NzConnectionHelpers.TextRecv(data, dataIdx, vlen - 4, sp);
                        break;
                    case 2500: // SELECT 15::BYTEINT
                        rowValue.typeCode = TypeCodeEx.Int16;
                        rowValue.int16Value = NzConnectionHelpers.ByteRecvTyped(data, dataIdx, vlen - 4);
                        break;
                    case 2950: // uuid
                        rowValue.typeCode = TypeCodeEx.String;
                        rowValue.stringValue = NzConnectionHelpers.UuidRecvTyped(data, dataIdx, vlen - 4).ToString() ?? "no uuid";
                        break;
                    default:
                        rowValue.typeCode = TypeCodeEx.String;
                        rowValue.stringValue = NzConnectionHelpers.TextRecv(data, dataIdx, vlen - 4, sp);
                        break;
                }
                //TODO { 22, (FC_TEXT, VectorIn) },       // int2vector
                //TODO{ 114, (FC_TEXT, JsonIn) },        // json
                //TODO{ 1000, (FC_BINARY, ArrayRecv) },  // BOOL[]
                //TODO{ 1003, (FC_BINARY, ArrayRecv) },  // NAME[]
                //TODO{ 1005, (FC_BINARY, ArrayRecv) },  // INT2[]
                //TODO{ 1007, (FC_BINARY, ArrayRecv) },  // INT4[]
                //TODO{ 1009, (FC_BINARY, ArrayRecv) },  // TEXT[]
                //TODO{ 1014, (FC_BINARY, ArrayRecv) },  // CHAR[]
                //TODO{ 1015, (FC_BINARY, ArrayRecv) },  // VARCHAR[]
                //TODO{ 1016, (FC_BINARY, ArrayRecv) },  // INT8[]
                //TODO{ 1021, (FC_BINARY, ArrayRecv) },  // FLOAT4[]
                //TODO{ 1022, (FC_BINARY, ArrayRecv) },  // FLOAT8[]
                //TODO{ 1231, (FC_TEXT, ArrayIn) },      // NUMERIC[]
                //TODO{ 1263, (FC_BINARY, ArrayRecv) },  // cstring[]
                //TODO{{ 3802, (FC_TEXT, JsonIn) }        // jsonb
                dataIdx += vlen - 4;
            }
        }
        nzCommand.AddRow(_row);
    }

    private void HandleCommandComplete(byte[] data, int length, NzCommand nzCommand)
    {
        var values = Encoding.UTF8.GetString(data, 0, length - 1).Split(' ');
        var command = values[0];
        if (_commandsWithCount.Contains(command))
        {
            int rowCount = int.Parse(values[^1]);
            if (nzCommand._recordsAffected == -1)
            {
                nzCommand._recordsAffected = rowCount;
            }
            else
            {
                nzCommand._recordsAffected += rowCount;
            }
        }

        //if (command == "ALTER" || command == "CREATE")
        //{
        //    foreach (var scache in _caches.Values)
        //    {
        //        foreach (var pcache in scache.Values)
        //        {
        //            foreach (var ps in pcache["ps"].Values)
        //            {
        //                ClosePreparedStatement(ps["statement_name_bin"] as byte[]);

        //            }
        //            pcache["ps"].Clear();
        //        }
        //    }
        //}
    }
    private void HandleRowDescription(byte[] data, NzCommand nzCommand)
    {
        int count = HUnpack(data);
        int idx = 2;

        nzCommand.NewPreparedStatement!.Description = new RowDescriptionMessage(count);

        for (int i = 0; i < count; i++)
        {
            int nullByteIndex = Array.IndexOf(data, (byte)0x00, idx);
            byte[] nameBytes = data[idx..nullByteIndex];
            string name = Encoding.UTF8.GetString(nameBytes);
            idx += nameBytes.Length + 1;

            var (typeOid, typeSize, typeModifier, format) = IHICUnpack(data, idx);
            //var receiver = NzConnectionHelpers.GetPgTypeX(typeOid);

            var fieldNew = new FieldDescription
            {
                Name = name,
                TypeOID = (uint)typeOid,
                TypeSize = typeSize,
                TypeModifier = typeModifier,
                DataFormat = format,
                //CalculationFunc = receiver
            };
            if (UseStringPool && fieldNew.Type == typeof(string))
            {
                fieldNew.StringPool = new Sylvan();
            }

            nzCommand.NewPreparedStatement.Description[i] = fieldNew;
            idx += 11;
        }
    }

    //I = int = 32
    //H = short = 16
    //C = byte

    //PGUtil, ReadInt32
    private static int IUnpack(byte[] data, int offset = 0)
    {
        if (BitConverter.IsLittleEndian)
        {
            return (data[offset] << 24) | (data[offset + 1] << 16) |
                   (data[offset + 2] << 8) | data[offset + 3];
            // or BitConverter.ToInt32(data, offset) + 

        }
        else
        {
            return BitConverter.ToInt32(data, offset);
        }
    }

    private static short HUnpack(byte[] data, int offset = 0)
    {
        // Convert network byte order (big-endian) to host byte order
        if (BitConverter.IsLittleEndian)
        {
            return (short)((data[offset] << 8) | data[offset + 1]);
        }
        else
        {
            return BitConverter.ToInt16(data, offset);
        }
    }

    private static int IUnpack(ReadOnlySpan<byte> data, int offset = 0)
    {
        if (BitConverter.IsLittleEndian)
        {
            return (data[offset] << 24) | (data[offset + 1] << 16) |
                   (data[offset + 2] << 8) | data[offset + 3];
        }

        return BitConverter.ToInt32(data[offset..]);
    }

    private static short HUnpack(ReadOnlySpan<byte> data, int offset = 0)
    {
        if (BitConverter.IsLittleEndian)
        {
            return (short)((data[offset] << 8) | data[offset + 1]);
        }

        return BitConverter.ToInt16(data[offset..]);
    }

    //private (byte messageCode, int dataLen) CiUnpack(byte[] data)
    //{
    //    if (data == null || data.Length < 5)
    //        throw new ArgumentException("Invalid data length for unpacking");

    //    return (
    //        messageCode: data[0],
    //        dataLen: IUnpack(data, 1)
    //    );
    //}

    public static (int, short, int, byte) IHICUnpack(byte[] data, int index)
    {
        if (data.Length < 11)
        {
            throw new ArgumentException("Data array is too short.");
        }

        int i1 = IUnpack(data, index + 0);
        short s = HUnpack(data, index + 4);
        int i3 = IUnpack(data, index + 6);
        byte b = data[10];

        return (i1, s, i3, b);
    }

}
