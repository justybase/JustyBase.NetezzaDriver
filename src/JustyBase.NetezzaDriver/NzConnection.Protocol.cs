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

// Command execution, timeouts and protocol synchronization.
// Kept partial so NzConnection stays navigable; all members share one instance.

namespace JustyBase.NetezzaDriver;

public sealed partial class NzConnection
{

    private void PreExecution(NzCommand nzCommand, string query)
    {
        try
        {
            ThrowIfProtocolFaulted();
            ThrowIfProtocolReadUnavailable();
            ReleaseLazyOversize();
            _lazyRowActive = false;
            _error = null;
            _backendException = null;
            nzCommand._recordsAffected = -1;
            nzCommand.NewPreparedStatement = new PreparedStatement();
            nzCommand.NewPreparedStatement.Sql = query;
            //if (State == ConnectionState.Executing)
            if (State != ConnectionState.Connecting)
            {
                // Netezza terminates a query with ReadyForQuery followed by an
                // optional run of NUL padding. Consume only padding that is
                // already buffered: some captures/appliances end at the frame
                // header, so a blocking four-byte skip would wait for the next
                // response before the next query is even sent.
                EnsureProtocolSynced(query);
            }
            byte[] writeBuffer = RentQueryBuffer(query is null ? 16 : 10 + 4 * query.Length, out bool pooledWriteBuffer);
            try
            {
                writeBuffer[0] = (byte)'P';
                if (_commandNumber != -1)
                {
                    _commandNumber += 1;
                    Core.IPack(_commandNumber, writeBuffer.AsSpan(1));
                }
                else
                {
                    writeBuffer[1] = 0xFF;//NEW
                    writeBuffer[2] = 0xFF;//NEW
                    writeBuffer[3] = 0xFF;//NEW
                    writeBuffer[4] = 0xFF;//NEW
                }

                if (_commandNumber > 100000)
                {
                    _commandNumber = 1;
                }

                int written = 5;
                if (query != null)
                {
                    written += Encoding.UTF8.GetBytes(query, writeBuffer.AsSpan(written));//NEW
                    writeBuffer[written] = 0;
                    written += 1;
                }
                _stream.Write(writeBuffer, 0, written);
                _stream.Flush();
                if (_logger?.IsEnabled(LogLevel.Debug) == true)
                    _logger.LogDebug("Buffer sent to nps: {Buffer}", NzConnectionHelpers.ClientEncoding.GetString(writeBuffer, 0, written));
            }
            finally
            {
                ReleaseQueryBuffer(writeBuffer, pooledWriteBuffer);
            }
            _state = ConnectionState.Executing;
        }
        catch (Exception ex) when (IsProtocolTeardownException(ex))
        {
            throw CreateConnectionClosedException(ex);
        }
    }

    private async Task PreExecutionAsync(NzCommand nzCommand, string query, CancellationToken cancellationToken = default)
    {
        try
        {
            ThrowIfProtocolFaulted();
            ThrowIfProtocolReadUnavailable();
            ReleaseLazyOversize();
            _lazyRowActive = false;
            _error = null;
            _backendException = null;
            nzCommand._recordsAffected = -1;
            nzCommand.NewPreparedStatement = new PreparedStatement();
            nzCommand.NewPreparedStatement.Sql = query;
            if (State != ConnectionState.Connecting)
            {
                // See PreExecution: ReadyForQuery may or may not be followed by
                // NUL padding. Drain only what is already available.
                await EnsureProtocolSyncedAsync(query, cancellationToken).ConfigureAwait(false);
            }
            byte[] writeBuffer = RentQueryBuffer(query is null ? 16 : 10 + 4 * query.Length, out bool pooledWriteBuffer);
            try
            {
                writeBuffer[0] = (byte)'P';
                if (_commandNumber != -1)
                {
                    _commandNumber += 1;
                    Core.IPack(_commandNumber, writeBuffer.AsSpan(1));
                }
                else
                {
                    writeBuffer[1] = 0xFF;
                    writeBuffer[2] = 0xFF;
                    writeBuffer[3] = 0xFF;
                    writeBuffer[4] = 0xFF;
                }

                if (_commandNumber > 100000)
                {
                    _commandNumber = 1;
                }

                int written = 5;
                if (query != null)
                {
                    written += Encoding.UTF8.GetBytes(query, writeBuffer.AsSpan(written));
                    writeBuffer[written] = 0;
                    written += 1;
                }
                await _stream.WriteAsync(writeBuffer.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (_logger?.IsEnabled(LogLevel.Debug) == true)
                    _logger.LogDebug("Buffer sent to nps: {Buffer}", NzConnectionHelpers.ClientEncoding.GetString(writeBuffer, 0, written));
            }
            finally
            {
                ReleaseQueryBuffer(writeBuffer, pooledWriteBuffer);
            }
            _state = ConnectionState.Executing;
        }
        catch (Exception ex) when (IsProtocolTeardownException(ex))
        {
            throw CreateConnectionClosedException(ex);
        }
    }

    private byte[] Read(int length, byte[]? buffer = null)
    {
        ValidateProtocolLength(length, "bufferRead");
        if (buffer is not null && buffer.Length < length)
        {
            _protocolFaulted = true;
            throw new NetezzaException(
                $"Backend protocol read requested {length} bytes but the supplied buffer has {buffer.Length} bytes. " +
                "The connection is no longer safe to reuse; reconnect is required.");
        }

        byte[] buf = buffer ?? new byte[length];
        _readBuffer!.ReadExactly(buf.AsSpan(0, length));
        return buf;
    }

    private async ValueTask<byte[]> ReadAsync(int length, byte[]? buffer = null, CancellationToken cancellationToken = default)
    {
        ValidateProtocolLength(length, "bufferRead");
        if (buffer is not null && buffer.Length < length)
        {
            _protocolFaulted = true;
            throw new NetezzaException(
                $"Backend protocol read requested {length} bytes but the supplied buffer has {buffer.Length} bytes. " +
                "The connection is no longer safe to reuse; reconnect is required.");
        }

        byte[] buf = buffer ?? new byte[length];
        await _readBuffer!.ReadExactlyAsync(buf.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
        return buf;
    }
    private void WriteSpan(Span<byte> buf)
    {
        _stream.Write(buf);
    }

    private ValueTask WriteSpanAsync(ReadOnlyMemory<byte> buf, CancellationToken cancellationToken = default)
    {
        return _stream.WriteAsync(buf, cancellationToken);
    }

    private void Flush()
    {
        _stream.Flush();
    }

    private Task FlushAsync(CancellationToken cancellationToken = default)
    {
        return _stream.FlushAsync(cancellationToken);
    }

    private async ValueTask SkipBytesAsync(int count, CancellationToken cancellationToken = default)
    {
        while (count > 0)
        {
            int chunkSize = Math.Min(count, _readBuffer!.Capacity);
            await _readBuffer.EnsureAsync(chunkSize, cancellationToken).ConfigureAwait(false);
            _readBuffer.Skip(chunkSize);
            count -= chunkSize;
        }
    }

    public delegate void NzNoticeEventHandler(object sender, NzNoticeEventArgs e);
    public event NzNoticeEventHandler? NoticeReceived;

    private void OnNoticeReceived(string notice, NzCommand nzCommand)
    {
        if (notice.StartsWith("NOTICE:"))
        {
            notice = notice["NOTICE:".Length..];
        }
        notice = notice.Trim().TrimEnd('\x00');
        nzCommand.AddNotice(notice);
        NoticeReceived?.Invoke(this, new NzNoticeEventArgs(notice));
    }

    private NetezzaException CreateCurrentException()
    {
        return _backendException ?? new NetezzaException(_error ?? "Netezza backend returned an unspecified error.");
    }

    private TimeSpan _defaultCommandTimeout = TimeSpan.FromSeconds(60);
    public TimeSpan DefaultCommandTimeout
    {
        get => _defaultCommandTimeout;
        set
        {
            _defaultCommandTimeout = value;
            CommandTimeout = value;
        }
    }
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>
    /// Gets whether external-table transfers are enabled for <see cref="NzBulkCopy"/>.
    /// When false, bulk-copy operations use row-by-row INSERT statements.
    /// </summary>
    public bool BulkInsertEnabled => _bulkInsertEnabled;

    private NzMetadata? _metadata;
    public NzMetadata Meta => _metadata ??= new NzMetadata(this);

    [AllowNull]
    public override string ConnectionString
    {
        get => new NzConnectionStringBuilder()
        {
            Host = _host,
            Database = _database,
            UserName = _user,
            Password = _password,
            Port = _port,
            Timeout = (int)ConnectionTimeoutDuration.TotalSeconds,
            LoggerFactory = _loggerFactory,
            BulkInsert = _bulkInsertEnabled
        }.ConnectionString;
        set
        {
            if (State != ConnectionState.Closed)
            {
                throw new InvalidOperationException(
                    "The ConnectionString property cannot be set while the connection is open.");
            }

            ApplyConnectionString(value ?? string.Empty);
        }
    }

    private void ApplyConnectionString(string connectionString)
    {
        var parameters = ParseConnectionString(connectionString);
        _database = parameters.Database ?? string.Empty;
        _user = parameters.User;
        _password = parameters.Password;
        _host = parameters.Host;
        _port = parameters.Port ?? 5480;
        _bulkInsertEnabled = parameters.BulkInsert;
        ConnectionTimeoutDuration = parameters.Timeout.HasValue
            ? TimeSpan.FromSeconds(parameters.Timeout.Value)
            : TimeSpan.FromSeconds(15);
    }

    public string SafeConnectionString => new NzConnectionStringBuilder()
    {
        Host = _host,
        Database = _database,
        UserName = _user,
        Password = _password,
        Port = _port,
        Timeout = (int)ConnectionTimeoutDuration.TotalSeconds,
        LoggerFactory = _loggerFactory,
        BulkInsert = _bulkInsertEnabled
    }.SafeConnectionString;

    public override string Database => _database;

    public override string DataSource => _host;

    private string _serverVersion = "";
    public override string ServerVersion => _serverVersion;

    private ConnectionState _state = ConnectionState.Closed;
    public override ConnectionState State => _state;

    internal void SetState(ConnectionState state)
    {
        _state = state;
    }

    private DbosTupleDesc _tupdesc = null!;

    private static T ConvertField<T>(Func<T> converter, int fieldOrdinal, string dataTypeName)
    {
        try
        {
            return converter();
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentOutOfRangeException or ArgumentException or InvalidCastException)
        {
            throw new InvalidCastException($"Failed to convert column {fieldOrdinal + 1} as {dataTypeName}.", ex);
        }
    }

    private void HandleTimeout(TimeSpan timeout)
    {
        double microsToWait = timeout.TotalMicroseconds;
        bool pollresult = true;
        while (microsToWait > 0.5)
        {
            int toWait;
            if (microsToWait < int.MaxValue)
            {
                toWait = (int)microsToWait;
                microsToWait = 0;
            }
            else
            {
                toWait = int.MaxValue;
                microsToWait -= toWait;
            }
            pollresult = _socket.Poll(toWait, SelectMode.SelectRead);
            if (!pollresult)
            {
                break;
            }
        }
        if (!pollresult)
        {
            CancelQuery();
            _error = "Command timeout";
        }
    }

    /// <summary>
    /// Starts a wall-clock timer for a synchronous command. The old
    /// <see cref="HandleTimeout"/> only polled once before reading and treated
    /// the always-present backend preamble as progress, so a long query could
    /// block for the whole socket receive timeout. The timer cancels the query
    /// at the command deadline while the normal read loop drains the response.
    /// </summary>
    private Timer? StartCommandTimeoutTimer(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
        {
            return null;
        }

        var dueMilliseconds = (long)Math.Ceiling(timeout.TotalMilliseconds);
        dueMilliseconds = Math.Clamp(dueMilliseconds, 1, int.MaxValue);
        return new Timer(
            _ =>
            {
                try
                {
                    CancelQuery();
                }
                catch (Exception error)
                {
                    _logger?.LogWarning(error, "Cancel request failed after a command timeout.");
                }
                finally
                {
                    Volatile.Write(ref _error, "Command timeout");
                    _backendException = null;
                }
            },
            null,
            (int)dueMilliseconds,
            Timeout.Infinite);
    }

    private CancellationTokenSource? _cachedTimeoutCts;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryGetCommandTimeoutToken(
        TimeSpan timeout,
        CancellationToken callerToken,
        out CancellationToken effectiveToken,
        out CancellationTokenSource? linkedToDispose,
        out CancellationTokenSource? timeoutSource)
    {
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
        {
            effectiveToken = callerToken;
            linkedToDispose = null;
            timeoutSource = null;
            return false;
        }

        // Common path: caller token cannot be canceled (default). Reuse a
        // per-connection CTS via TryReset to avoid allocating a linked CTS
        // + Timer per command. Sequential use only; concurrent executes on
        // the same connection are already unsupported (single stream).
        if (!callerToken.CanBeCanceled)
        {
            var cached = _cachedTimeoutCts;
            if (cached is null)
            {
                cached = new CancellationTokenSource();
                _cachedTimeoutCts = cached;
            }
            else if (!cached.TryReset())
            {
                // Outstanding registrations (should not happen after prior
                // operations completed); replace to stay safe.
                cached.Dispose();
                cached = new CancellationTokenSource();
                _cachedTimeoutCts = cached;
            }
            cached.CancelAfter(timeout);
            effectiveToken = cached.Token;
            linkedToDispose = null;
            timeoutSource = cached;
            return true;
        }

        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        linkedCts.CancelAfter(timeout);
        effectiveToken = linkedCts.Token;
        linkedToDispose = linkedCts;
        timeoutSource = linkedCts;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsCommandTimeout(CancellationTokenSource? timeoutSource, CancellationToken callerToken)
        => timeoutSource is not null && timeoutSource.IsCancellationRequested && !callerToken.IsCancellationRequested;


    public bool Execute(NzCommand nzCommand, string query)
    {
        PreExecution(nzCommand, query);
        _nextRelatedFileStream = null!;

        using var timeoutTimer = StartCommandTimeoutTimer(nzCommand.EffectiveCommandTimeout);
        while (DoNextStep(nzCommand)) ;
        timeoutTimer?.Change(Timeout.Infinite, Timeout.Infinite);

        var response = true;

        if (_error != null)
        {
            throw CreateCurrentException();
        }

        return response;
    }

    public async Task<bool> ExecuteAsync(NzCommand nzCommand, string query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TryGetCommandTimeoutToken(nzCommand.EffectiveCommandTimeout, cancellationToken, out var effectiveCancellationToken, out var linkedCts, out var timeoutSource);
        using (linkedCts)
        {
            try
            {
                await PreExecutionAsync(nzCommand, query, effectiveCancellationToken).ConfigureAwait(false);
                _nextRelatedFileStream = null!;

                while (await DoNextStepAsync(nzCommand, effectiveCancellationToken).ConfigureAwait(false)) ;
                var response = true;

                if (_error != null)
                {
                    throw CreateCurrentException();
                }

                return response;
            }
            catch (OperationCanceledException) when (IsCommandTimeout(timeoutSource, cancellationToken))
            {
                await HandleCommandTimeoutAsync(nzCommand).ConfigureAwait(false);
                throw new NetezzaException("Command timeout");
            }
        }
    }

    /// <summary>
    /// Cancels a timed-out command and drains the response the backend sends
    /// for the cancel request (usually ErrorResponse + ReadyForQuery). Without
    /// the drain, the next command mistakes those frames for its own response.
    /// </summary>
    private async Task HandleCommandTimeoutAsync(NzCommand nzCommand)
    {
        try
        {
            CancelQuery();
        }
        catch (Exception cancelError)
        {
            _logger?.LogWarning(cancelError, "Cancel request failed after a command timeout.");
            _protocolFaulted = true;
            _error = "Command timeout";
            _backendException = null;
            return;
        }

        if (!await DrainCancelledResponseAsync(nzCommand).ConfigureAwait(false))
        {
            _protocolFaulted = true;
        }
        _error = "Command timeout";
        _backendException = null;
    }

    /// <summary>
    /// Consumes the response left behind by <see cref="CancelQuery"/> up to
    /// ReadyForQuery. Rows are discarded without decoding. Returns false when
    /// the stream could not be resynchronized (the connection must reconnect).
    /// </summary>
    private async Task<bool> DrainCancelledResponseAsync(NzCommand nzCommand)
    {
        using var drainSource = new CancellationTokenSource(ProtocolSyncTimeoutMs);
        try
        {
            while (true)
            {
                if (_shouldReadByte)
                {
                    await ReadNextResponseByteAsync(drainSource.Token).ConfigureAwait(false);
                }

                var more = await IntepretReturnedByteAsync(
                    nzCommand,
                    drainSource.Token,
                    discardRows: true).ConfigureAwait(false);
                if (!more)
                {
                    return true;
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning("Timed out while draining the cancelled response; reconnect is required.");
            return false;
        }
        catch (Exception error) when (IsProtocolTeardownException(error))
        {
            _logger?.LogWarning(error, "Connection closed while draining the cancelled response.");
            return false;
        }
        catch (Exception error)
        {
            _logger?.LogWarning(error, "Malformed response while draining the cancelled command.");
            return false;
        }
    }

    public NzDataReader ExecuteReader(NzCommand nzCommand, string query)
    {
        PreExecution(nzCommand, query);
        HandleTimeout(nzCommand.EffectiveCommandTimeout);
        _nextRelatedFileStream = null!;
        var rdr = new NzDataReader(nzCommand);
        if (_error != null)
        {
            throw CreateCurrentException();
        }
        return rdr;
    }

    public async Task<NzDataReader> ExecuteReaderAsync(NzCommand nzCommand, string query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TryGetCommandTimeoutToken(nzCommand.EffectiveCommandTimeout, cancellationToken, out var effectiveCancellationToken, out var linkedCts, out var timeoutSource);
        using (linkedCts)
        {
            try
            {
                await PreExecutionAsync(nzCommand, query, effectiveCancellationToken).ConfigureAwait(false);
                _nextRelatedFileStream = null!;
                var rdr = await NzDataReader.CreateAsync(nzCommand, effectiveCancellationToken).ConfigureAwait(false);
                if (_error != null)
                {
                    throw CreateCurrentException();
                }

                return rdr;
            }
            catch (OperationCanceledException) when (IsCommandTimeout(timeoutSource, cancellationToken))
            {
                await HandleCommandTimeoutAsync(nzCommand).ConfigureAwait(false);
                throw new NetezzaException("Command timeout");
            }
        }
    }

    private int _lastResponse = -1;

    private FileStream _nextRelatedFileStream = null!;
    private BulkInsertData? _pendingBulkData;

    internal bool NewRowReceived()
    {
        return _lastResponse == (byte)BackendMessageCode.RowStandard || _lastResponse == (byte)BackendMessageCode.DataRow;
    }
    internal bool NewRowDescriptionReceived()
    {
        return _lastResponse == (byte)BackendMessageCode.RowDescription;
    }
    internal bool NewRowDescriptionStandardReceived()
    {
        return _lastResponse == (byte)BackendMessageCode.RowDescriptionStandard;
    }

    internal bool IsCommandComplete()
    {
        return _lastResponse == (byte)BackendMessageCode.CommandComplete;
    }

    internal bool DoNextStep(NzCommand nzCommand)
    {
        try
        {
            ThrowIfProtocolFaulted();
            ThrowIfProtocolReadUnavailable();
            if (_shouldReadByte)
            {
                ReadNextResponseByte();
            }
            var res = IntepretReturnedByte(nzCommand);
            if (_error != null)
            {
                while (res && _shouldReadByte)
                {
                    ReadNextResponseByte();
                    res = IntepretReturnedByte(nzCommand);
                }
                throw CreateCurrentException();
            }
            return res;
        }
        catch (Exception ex) when (IsProtocolTeardownException(ex))
        {
            throw CreateConnectionClosedException(ex);
        }
    }

    internal async ValueTask<bool> DoNextStepAsync(NzCommand nzCommand, CancellationToken cancellationToken = default)
    {
        try
        {
            ThrowIfProtocolFaulted();
            ThrowIfProtocolReadUnavailable();
            if (_shouldReadByte)
            {
                await ReadNextResponseByteAsync(cancellationToken).ConfigureAwait(false);
            }

            var res = await IntepretReturnedByteAsync(nzCommand, cancellationToken).ConfigureAwait(false);
            if (_error != null)
            {
                while (res && _shouldReadByte)
                {
                    await ReadNextResponseByteAsync(cancellationToken).ConfigureAwait(false);
                    res = await IntepretReturnedByteAsync(nzCommand, cancellationToken).ConfigureAwait(false);
                }
                throw CreateCurrentException();
            }
            return res;
        }
        catch (Exception ex) when (IsProtocolTeardownException(ex))
        {
            throw CreateConnectionClosedException(ex);
        }
    }

    /// <summary>
    /// Drain path for rows the caller will never consume (early close,
    /// <c>SingleRow</c> tail). Framing, length validation, and non-row state
    /// machines are identical to <see cref="DoNextStep"/>; only
    /// <c>RowStandard</c>/<c>DataRow</c> payloads are skipped without decode.
    /// </summary>
    internal bool DoNextStepDiscardingRows(NzCommand nzCommand)
    {
        try
        {
            ThrowIfProtocolFaulted();
            ThrowIfProtocolReadUnavailable();
            if (_shouldReadByte)
            {
                ReadNextResponseByte();
            }
            var res = IntepretReturnedByte(nzCommand, discardRows: true);
            if (_error != null)
            {
                while (res && _shouldReadByte)
                {
                    ReadNextResponseByte();
                    res = IntepretReturnedByte(nzCommand, discardRows: true);
                }
                throw CreateCurrentException();
            }
            return res;
        }
        catch (Exception ex) when (IsProtocolTeardownException(ex))
        {
            throw CreateConnectionClosedException(ex);
        }
    }

    internal async ValueTask<bool> DoNextStepDiscardingRowsAsync(NzCommand nzCommand, CancellationToken cancellationToken = default)
    {
        try
        {
            ThrowIfProtocolFaulted();
            ThrowIfProtocolReadUnavailable();
            if (_shouldReadByte)
            {
                await ReadNextResponseByteAsync(cancellationToken).ConfigureAwait(false);
            }

            var res = await IntepretReturnedByteAsync(nzCommand, cancellationToken, discardRows: true).ConfigureAwait(false);
            if (_error != null)
            {
                while (res && _shouldReadByte)
                {
                    await ReadNextResponseByteAsync(cancellationToken).ConfigureAwait(false);
                    res = await IntepretReturnedByteAsync(nzCommand, cancellationToken, discardRows: true).ConfigureAwait(false);
                }
                throw CreateCurrentException();
            }
            return res;
        }
        catch (Exception ex) when (IsProtocolTeardownException(ex))
        {
            throw CreateConnectionClosedException(ex);
        }
    }

    internal void ReadNextResponseByte()
    {
        try
        {
            ThrowIfProtocolReadUnavailable();
            _lastResponse = _readBuffer!.ReadByteOrEof();
            _shouldReadByte = false;
        }
        catch (Exception ex) when (IsProtocolTeardownException(ex))
        {
            throw CreateConnectionClosedException(ex);
        }
    }

    private async ValueTask<int> ReadByteAsync(CancellationToken cancellationToken = default)
    {
        await _readBuffer!.EnsureAsync(1, cancellationToken).ConfigureAwait(false);
        return _readBuffer.ReadByte();
    }

    private async ValueTask<int> ReadInt32Async(CancellationToken cancellationToken = default)
    {
        await _readBuffer!.EnsureAsync(4, cancellationToken).ConfigureAwait(false);
        return _readBuffer.ReadInt32BigEndian();
    }

    private async ValueTask<short> ReadInt16Async(CancellationToken cancellationToken = default)
    {
        await _readBuffer!.EnsureAsync(2, cancellationToken).ConfigureAwait(false);
        return _readBuffer.ReadInt16BigEndian();
    }

    private long ProtocolReadOffset => _readBuffer?.ConsumedBytes ?? -1;

    private string ProtocolResponseType => (uint)_lastResponse <= byte.MaxValue
        ? $"{(char)_lastResponse} (0x{_lastResponse:X2})"
        : $"0x{_lastResponse:X}";

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ValidateProtocolLength(
        int length,
        string field,
        bool allowZero = true,
        long? offset = null)
    {
        // Fast path: numeric checks only. No string interpolation, no context,
        // no exception object on success.
        if ((uint)length <= (uint)ProtocolLengthValidator.MaxPayloadLength)
        {
            if (allowZero || length != 0)
            {
                if (length >= 0)
                    return length;
            }
        }

        return ValidateProtocolLengthSlow(length, field, allowZero, offset);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int ValidateProtocolLengthSlow(
        int length,
        string field,
        bool allowZero,
        long? offset)
    {
        var context =
            $"response={ProtocolResponseType}, row={_currentProtocolRowNumber}, offset={offset ?? ProtocolReadOffset}";
        try
        {
            return ProtocolLengthValidator.Validate(length, field, allowZero, context);
        }
        catch (NetezzaException)
        {
            _protocolFaulted = true;
            throw;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ValidateProtocolLengthAfterOverhead(
        int frameLength,
        int overhead,
        string frameField,
        string payloadField,
        bool payloadAllowZero = true)
    {
        // Fast path: pure arithmetic + range checks, no strings/logging.
        if ((uint)overhead <= (uint)frameLength)
        {
            int payloadLength = frameLength - overhead;
            if ((uint)payloadLength <= (uint)ProtocolLengthValidator.MaxPayloadLength)
            {
                if (payloadAllowZero || payloadLength != 0)
                {
                    if (frameLength > 0 && payloadLength >= 0)
                        return ValidateProtocolLengthAfterOverheadFastLog(
                            frameLength, overhead, frameField, payloadField,
                            payloadLength);
                }
            }
        }

        return ValidateProtocolLengthAfterOverheadSlow(
            frameLength, overhead, frameField, payloadField, payloadAllowZero);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ValidateProtocolLengthAfterOverheadFastLog(
        int frameLength,
        int overhead,
        string frameField,
        string payloadField,
        int payloadLength)
    {
        // Debug logging only when enabled; otherwise zero additional work.
        if (_logger?.IsEnabled(LogLevel.Debug) == true)
            LogDerivedLengthSlow(frameField, payloadField, frameLength, overhead, payloadLength);
        return payloadLength;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void LogDerivedLengthSlow(
        string frameField,
        string payloadField,
        int frameLength,
        int overhead,
        int payloadLength)
    {
        _logger?.LogDebug(
            "Backend protocol derived length: ResponseType={ResponseType} FrameField={FrameField} " +
            "PayloadField={PayloadField} FrameLength={FrameLength} Overhead={Overhead} " +
            "PayloadLength={PayloadLength} Offset={Offset} Row={Row}",
            ProtocolResponseType,
            frameField,
            payloadField,
            frameLength,
            overhead,
            payloadLength,
            ProtocolReadOffset,
            _currentProtocolRowNumber);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int ValidateProtocolLengthAfterOverheadSlow(
        int frameLength,
        int overhead,
        string frameField,
        string payloadField,
        bool payloadAllowZero)
    {
        var context =
            $"response={ProtocolResponseType}, row={_currentProtocolRowNumber}, offset={ProtocolReadOffset}";
        try
        {
            int payloadLength = ProtocolLengthValidator.ValidateAfterOverhead(
                frameLength,
                overhead,
                frameField,
                payloadField,
                payloadAllowZero,
                context);

            if (_logger?.IsEnabled(LogLevel.Debug) == true)
                LogDerivedLengthSlow(frameField, payloadField, frameLength, overhead, payloadLength);

            return payloadLength;
        }
        catch (NetezzaException)
        {
            _protocolFaulted = true;
            throw;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LogProtocolLength(
        string field,
        string rawHex,
        int value,
        long offset)
    {
        if (_logger?.IsEnabled(LogLevel.Debug) != true)
            return;
        LogProtocolLengthSlow(field, rawHex, value, offset);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void LogProtocolLengthSlow(
        string field,
        string rawHex,
        int value,
        long offset)
    {
        _logger?.LogDebug(
            "Backend protocol length: ResponseType={ResponseType} ResponseCode=0x{ResponseCode:X2} Field={Field} Value={Value} Raw={RawBytes} Offset={Offset} Row={Row}",
            ProtocolResponseType,
            _lastResponse,
            field,
            value,
            rawHex,
            offset,
            _currentProtocolRowNumber);
    }

    private int ReadProtocolInt32(string field)
    {
        long offset = ProtocolReadOffset;
        var value = _readBuffer!.ReadInt32BigEndian(out uint raw);
        if (_logger?.IsEnabled(LogLevel.Debug) == true)
            LogProtocolLength(field, raw.ToString("X8"), value, offset);
        return value;
    }

    private int ReadProtocolLength(string field, bool allowZero = true)
    {
        var value = ReadProtocolInt32(field);
        return ValidateProtocolLength(value, field, allowZero);
    }

    private short ReadProtocolInt16Length(string field, bool allowZero = true)
    {
        long offset = ProtocolReadOffset;
        var value = _readBuffer!.ReadInt16BigEndian(out ushort raw);
        if (_logger?.IsEnabled(LogLevel.Debug) == true)
            LogProtocolLength(field, raw.ToString("X4"), value, offset);
        return checked((short)ValidateProtocolLength(value, field, allowZero));
    }

    private async ValueTask<int> ReadProtocolInt32Async(
        string field,
        CancellationToken cancellationToken = default)
    {
        long offset = ProtocolReadOffset;
        await _readBuffer!.EnsureAsync(sizeof(int), cancellationToken).ConfigureAwait(false);
        var value = _readBuffer.ReadInt32BigEndian(out uint raw);
        if (_logger?.IsEnabled(LogLevel.Debug) == true)
            LogProtocolLength(field, raw.ToString("X8"), value, offset);
        return value;
    }

    private async ValueTask<int> ReadProtocolLengthAsync(
        string field,
        bool allowZero = true,
        CancellationToken cancellationToken = default)
    {
        var value = await ReadProtocolInt32Async(field, cancellationToken).ConfigureAwait(false);
        return ValidateProtocolLength(value, field, allowZero);
    }

    private async ValueTask<short> ReadProtocolInt16LengthAsync(
        string field,
        bool allowZero = true,
        CancellationToken cancellationToken = default)
    {
        long offset = ProtocolReadOffset;
        await _readBuffer!.EnsureAsync(sizeof(short), cancellationToken).ConfigureAwait(false);
        var value = _readBuffer.ReadInt16BigEndian(out ushort raw);
        if (_logger?.IsEnabled(LogLevel.Debug) == true)
            LogProtocolLength(field, raw.ToString("X4"), value, offset);
        return checked((short)ValidateProtocolLength(value, field, allowZero));
    }

    private void ThrowIfProtocolFaulted()
    {
        if (_protocolFaulted)
        {
            throw new NetezzaException(
                "The connection encountered an invalid backend protocol length and cannot be reused; reconnect is required.");
        }
    }

    /// <summary>
    /// Reads are unavailable while closed or while teardown is releasing the
    /// protocol resources. This state is separate from <see cref="_disposed"/>
    /// because a connection can be opened again after <see cref="Close"/>.
    /// </summary>
    private bool IsProtocolReadUnavailable
        => Volatile.Read(ref _protocolReadUnavailable) != 0 || _disposed;

    private bool IsProtocolTeardownException(Exception exception)
        => IsProtocolReadUnavailable
            && exception is not NetezzaException
            && exception is not OperationCanceledException;

    private static NetezzaException CreateConnectionClosedException(Exception? innerException = null)
        => innerException is null
            ? new NetezzaException("Connection is closed.")
            : new NetezzaException("Connection was closed during a protocol operation.", innerException);

    private void ThrowIfProtocolReadUnavailable()
    {
        if (IsProtocolReadUnavailable)
        {
            throw CreateConnectionClosedException();
        }
    }

    internal async ValueTask ReadNextResponseByteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            ThrowIfProtocolReadUnavailable();
            _lastResponse = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
            _shouldReadByte = false;
        }
        catch (Exception ex) when (IsProtocolTeardownException(ex))
        {
            throw CreateConnectionClosedException(ex);
        }
    }
    private bool _shouldReadByte = true;

}
