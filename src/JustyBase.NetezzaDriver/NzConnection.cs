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
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;

namespace JustyBase.NetezzaDriver;

public sealed class NzConnection : DbConnection
{
    private string? _error;
    private NetezzaException? _backendException;
 
    private int _commandNumber = -1;

    public bool InTransaction { get; set; } = false;//TODO
    public bool AutoCommit { get;set; } = true;

    /// <summary>
    /// The physical connection socket to the backend.
    /// </summary>
    Socket _socket = default!;

    /// <summary>
    /// The physical connection stream to the backend, without anything on top.
    /// </summary>
    //NetworkStream _baseStream = default!;
    public bool IsBaseStreamNull => _stream == null;//??

    /// <summary>
    /// The physical connection stream to the backend, layered with an SSL/TTLS stream if in secure mode.
    /// this should be used mailny for reading and writing?
    /// </summary>
    Stream _stream = default!;

    /// <summary>
    /// Application-level read buffer layered over <see cref="_stream"/>.
    /// Serves protocol primitives without a stream call each and lets row
    /// payloads be decoded in place (no per-row copy).
    /// </summary>
    private NzReadBuffer? _readBuffer;

    private readonly List<string> _commandsWithCount = ["INSERT", "DELETE", "UPDATE"];
    private readonly ILogger? _logger = null!;
    private readonly bool _tcpKeepAlive = true;

    private BackendKeyDataMessage _backendKeyData = null!;

    public int Pid => _backendKeyData?.BackendProcessId ?? -1;

    private string _database;
    private readonly string _user;
    private readonly string _password;
    private readonly string _host;
    private readonly int _port;

    public DateTime CreatedAt { get; private set; } = DateTime.UtcNow;

    public const int NzTypeRecAddr = 1;
    public const int NzTypeDouble = 2;
    public const int NzTypeInt = 3;
    public const int NzTypeFloat = 4;
    public const int NzTypeMoney = 5;
    public const int NzTypeDate = 6;
    public const int NzTypeNumeric = 7;
    public const int NzTypeTime = 8;
    public const int NzTypeTimestamp = 9;
    public const int NzTypeInterval = 10;
    public const int NzTypeTimeTz = 11;
    public const int NzTypeBool = 12;
    public const int NzTypeInt1 = 13;
    public const int NzTypeBinary = 14;
    public const int NzTypeChar = 15;
    public const int NzTypeVarChar = 16;
    public const int NzDEPR_Text = 17; // OBSOLETE 3.0: BLAST Era Large 'text' Object
    public const int NzTypeUnknown = 18; // corresponds to PG UNKNOWNOID data type - an untyped string literal
    public const int NzTypeInt2 = 19;
    public const int NzTypeInt8 = 20;
    public const int NzTypeVarFixedChar = 21;
    public const int NzTypeGeometry = 22;
    public const int NzTypeVarBinary = 23;
    public const int NzDEPR_Blob = 24; // OBSOLETE 3.0: BLAST Era Large 'binary' Object
    public const int NzTypeNChar = 25;
    public const int NzTypeNVarChar = 26;
    public const int NzDEPR_NText = 27; // OBSOLETE 3.0: BLAST Era Large 'nchar text' Object
                                        // skip 28
                                        // skip 29
    public const int NzTypeJson = 30;
    public const int NzTypeJsonb = 31;
    public const int NzTypeJsonpath = 32;
    public const int NzTypeLastEntry = 33;
    public const int NzTypeIntvsAbsTimeFIX = 39;//https://github.com/IBM/nzpy/issues/61

    public NzConnection(string user, string password, string host, string database,
        int port = 5480, SecurityLevelCode securityLevel = SecurityLevelCode.PreferredUnsecured, string? sslCerFilePath = null, ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<NzConnection>();
        _securityLevel = securityLevel;
        _sslCerFilePath = sslCerFilePath;
        _database = database;
        _user = user;
        _password = password;
        _host = host;
        _port = port;
        _tmp_buffer = ArrayPool<byte>.Shared.Rent(4096);
    }

    public NzConnection(string connectionString, SecurityLevelCode securityLevel = SecurityLevelCode.PreferredUnsecured, string? sslCerFilePath = null, ILoggerFactory? loggerFactory = null)
    {
        var parameters = ParseConnectionString(connectionString);

        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<NzConnection>();
        _securityLevel = securityLevel;
        _sslCerFilePath = sslCerFilePath;
        _database = parameters.Database ?? "";
        _user = parameters.User;
        _password = parameters.Password;
        _host = parameters.Host;
        _port = parameters.Port ?? 5480;
        if (parameters.Timeout.HasValue)
        {
            ConnectionTimeoutDuration = TimeSpan.FromSeconds(parameters.Timeout.Value);
        }
        _tmp_buffer = ArrayPool<byte>.Shared.Rent(4096);
    }

    public NzConnection(string connectionString, string database, int commandTimeoutSec, SecurityLevelCode securityLevel = SecurityLevelCode.PreferredUnsecured, string? sslCerFilePath = null, ILoggerFactory? loggerFactory = null)
    {
        var parameters = ParseConnectionString(connectionString);

        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<NzConnection>();
        _securityLevel = securityLevel;
        _sslCerFilePath = sslCerFilePath;
        _database = database;
        _user = parameters.User;
        _password = parameters.Password;
        _host = parameters.Host;
        _port = parameters.Port ?? 5480;
        if (parameters.Timeout.HasValue)
        {
            ConnectionTimeoutDuration = TimeSpan.FromSeconds(parameters.Timeout.Value);
        }
        _tmp_buffer = ArrayPool<byte>.Shared.Rent(4096);
        this.CommandTimeout = TimeSpan.FromSeconds(commandTimeoutSec);
    }


    /// <summary>
    /// Parses a connection string into its component parts, supporting passwords that may contain semicolons when enclosed in curly braces.
    /// </summary>
    /// <param name="connectionString">Connection string in format "User=value;Password=value" or "User=value;Password={value;with;semicolons}"</param>
    /// <returns>A tuple containing connection parameters</returns>
    /// <exception cref="NetezzaException">Thrown when mandatory parameters are missing or format is invalid</exception>
    private static (string User, string Password, string Host, string? Database, int? Port, int? Timeout, bool Pooling, int MinPoolSize, int MaxPoolSize, int ConnectionIdleTimeout, int ConnectionLifetime) ParseConnectionString(string connectionString)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int position = 0;

        while (position < connectionString.Length)
        {
            // Skip whitespace
            while (position < connectionString.Length && char.IsWhiteSpace(connectionString[position]))
                position++;

            // Find key
            int keyStart = position;
            while (position < connectionString.Length && connectionString[position] != '=')
                position++;

            if (position >= connectionString.Length)
                break;

            string key = connectionString[keyStart..position].Trim();
            position++; // Skip '='

            // Handle value
            string value;
            if (position < connectionString.Length && connectionString[position] == '{')
            {
                // Handle curly brace enclosed value (for passwords with semicolons)
                int braceStart = position + 1;
                position = connectionString.IndexOf('}', braceStart);

                if (position == -1)
                    throw new NetezzaException("Unterminated curly brace in connection string");

                value = connectionString[braceStart..position];
                position++; // Skip closing brace

                // Skip to next parameter
                while (position < connectionString.Length && connectionString[position] != ';')
                    position++;
            }
            else
            {
                // Handle regular value
                int valueStart = position;
                while (position < connectionString.Length && connectionString[position] != ';')
                    position++;

                value = connectionString[valueStart..position].Trim();
            }

            if (key.Length > 0)
            {
                parameters[key] = value;
            }

            position++; // Skip semicolon
        }

        // Extract mandatory parameters
        if (!parameters.TryGetValue("User", out var user) && !parameters.TryGetValue("Username", out user))
        {
            throw new NetezzaException("Username is required in connection string");
        }

        if (!parameters.TryGetValue("Password", out var password) && !parameters.TryGetValue("Pwd", out password))
        {
            throw new NetezzaException("Password is required in connection string");
        }

        if (!parameters.TryGetValue("Host", out var host) && !parameters.TryGetValue("Server", out host))
        {
            throw new NetezzaException("Host is required in connection string");
        }

        // Extract optional parameters
        parameters.TryGetValue("Database", out var database);

        int? port = null;
        if (parameters.TryGetValue("Port", out var portStr) && int.TryParse(portStr, out var parsedPort))
        {
            port = parsedPort;
        }

        int? timeout = null;
        if (parameters.TryGetValue("Timeout", out var timeoutStr) && int.TryParse(timeoutStr, out var parsedTimeout))
        {
            timeout = parsedTimeout;
        }

        bool pooling = !parameters.TryGetValue("Pooling", out var poolingStr) || bool.Parse(poolingStr);
        int minPoolSize = 0;
        if (parameters.TryGetValue("MinPoolSize", out var minPoolStr))
            int.TryParse(minPoolStr, out minPoolSize);
        int maxPoolSize = 10;
        if (parameters.TryGetValue("MaxPoolSize", out var maxPoolStr))
            int.TryParse(maxPoolStr, out maxPoolSize);
        int connectionIdleTimeout = 30;
        if (parameters.TryGetValue("ConnectionIdleTimeout", out var idleStr))
            int.TryParse(idleStr, out connectionIdleTimeout);
        int connectionLifetime = 0;
        if (parameters.TryGetValue("ConnectionLifetime", out var lifetimeStr))
            int.TryParse(lifetimeStr, out connectionLifetime);

        return (user, password, host, database, port, timeout, pooling, minPoolSize, maxPoolSize, connectionIdleTimeout, connectionLifetime);
    }


    public TimeSpan ConnectionTimeoutDuration { get; init; } =  TimeSpan.FromSeconds(15);

    public override int ConnectionTimeout => (int)ConnectionTimeoutDuration.TotalSeconds;


    private const int _bufferSize = 65536; // 64 KB
    private Stream Initialize(string host, int port, bool useBufferedStream = true, bool setSocketBufferSizes = false)
    {
        try
        {
            if (host is not null)
            {
                _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                if (setSocketBufferSizes)
                {
                    _socket.ReceiveBufferSize = 65536;  // 64 KB
                    _socket.SendBufferSize = 65536;     // 64 KB
                }
                //if (noDelay)
                //    _socket.NoDelay = true;
            }
            else
            {
                throw new NetezzaException("one of host or unix_sock must be provided");
            }

            if (host is not null)
            {
                var beginConnect = _socket.BeginConnect(host, port, null, null);
                if (!beginConnect.AsyncWaitHandle.WaitOne(ConnectionTimeoutDuration, true))
                {
                    _socket.Close();
                    throw new NetezzaException("Connection timeout");
                }
                _socket.EndConnect(beginConnect);
                //_socket.Connect(host, port);
            }

            _socket.ReceiveTimeout = (int)ConnectionTimeoutDuration.TotalMilliseconds;

            var baseStream = new NetworkStream(_socket, ownsSocket: true);
            

            if (_tcpKeepAlive)
            {
                _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            }
            if (useBufferedStream)
            {
                return new BufferedStream(baseStream, _bufferSize);
            }
            else
            {
                return baseStream;
            }
        }
        catch (Exception ex)
        {
            _socket.Close();
            throw new InterfaceException("communication error", ex);
        }
    }

    private async Task<Stream> InitializeAsync(string host, int port, bool useBufferedStream = true, bool setSocketBufferSizes = false, CancellationToken cancellationToken = default)
    {
        try
        {
            if (host is not null)
            {
                _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                if (setSocketBufferSizes)
                {
                    _socket.ReceiveBufferSize = 65536;
                    _socket.SendBufferSize = 65536;
                }
            }
            else
            {
                throw new NetezzaException("one of host or unix_sock must be provided");
            }

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(ConnectionTimeoutDuration);
            try
            {
                await _socket.ConnectAsync(host, port, connectCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new NetezzaException("Connection timeout");
            }

            _socket.ReceiveTimeout = (int)ConnectionTimeoutDuration.TotalMilliseconds;

            var baseStream = new NetworkStream(_socket, ownsSocket: true);

            if (_tcpKeepAlive)
            {
                _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            }

            return useBufferedStream
                ? new BufferedStream(baseStream, _bufferSize)
                : baseStream;
        }
        catch (OperationCanceledException)
        {
            _socket.Close();
            throw;
        }
        catch (NetezzaException)
        {
            _socket.Close();
            throw;
        }
        catch (Exception ex)
        {
            _socket.Close();
            throw new InterfaceException("communication error", ex);
        }
    }

#if NET9_0_OR_GREATER
    private static readonly Lock _cancelLock = new ();
#else
    private static readonly object _cancelLock = new ();
#endif
    //TODO SSL CASE..
    public void CancelQuery()
    {
        lock (_cancelLock)
        {
            int pid = _backendKeyData.BackendProcessId;
            int secretKey = _backendKeyData.BackendSecretKey;
            var socketX = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            //socketX.Connect(_host, _port);
            var beginConnect = socketX.BeginConnect(_host, _port, null, null);
            if (!beginConnect.AsyncWaitHandle.WaitOne(ConnectionTimeoutDuration, true))
            {
                socketX.Close();
                throw new NetezzaException("Connection timeout");
            }
            socketX.EndConnect(beginConnect);

            var baseStream = new NetworkStream(socketX, ownsSocket: true);
            var stream2 = new BufferedStream(baseStream);

            Canceling.WriteCancelRequest(stream2, pid, secretKey);
            stream2.Flush();
            var count = stream2.ReadByte();
            stream2.Dispose();
            //Error = "Query canceled";
            //Status = Core.CONN_CANCELLED;
        }
    }

    //public void Terminate()
    //{
    //    const int len = sizeof(byte) +  // Message code
    //            sizeof(int);    // Length
    //    PGUtil.WriteInt32_I(_stream, len);
    //    _stream.Write([FrontendMessageCode.Terminate]);
    //    PGUtil.WriteInt32_I(_stream, len - 1);
    //    _stream.Flush();
    //}

    /// <summary>
    /// Sends initial connection queries to setup database session
    /// </summary>
    private bool ConnSendQuery(string dateStyle = "ISO")
    {
        if (!Execute(_nzCommand, "set nz_encoding to 'utf8'"))
            return false;

        // Set the Datestyle to the format the driver expects
        string query = dateStyle switch
        {
            "MDY" => "set DateStyle to 'US'",
            "DMY" => "set DateStyle to 'EUROPEAN'",
            _ => "set DateStyle to 'ISO'"
        };

        if (!Execute(_nzCommand, query))
            return false;

        var systemArch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
        string procArch = systemArch == System.Runtime.InteropServices.Architecture.X64 ? "AMD64" : "other";

        string clientInfo = $@"select version(), 
        'Netezza Python Client Version {NzConnectionHelpers.NZPY_CLIENT_VERSION}', 
        '{procArch}',
        'OS Platform: {Environment.OSVersion}',
        'OS Username: {Environment.UserName}'";

        _nzCommand.CommandText = clientInfo;
        using var rdr = _nzCommand.ExecuteReader();

        while (rdr.Read())
        {
            var row = new object[5];
            rdr.GetValues(row);
            _serverVersion = (row[0] as string) ?? "no version info";
            _logger?.LogDebug("Version info: {row0}, {row1}, {row2}, {row3}, {row4}",
                row[0], row[1], row[2], row[3], row[4]);
        }


        if (!Execute(_nzCommand, $"SET CLIENT_VERSION = '{NzConnectionHelpers.NZPY_CLIENT_VERSION}'"))
            return false;


        _nzCommand.CommandText = @"select ascii(' ') as space, encoding as ccsid from _v_database where objid = current_db";
        using var rdr2 = _nzCommand.ExecuteReader();
        while (rdr2.Read())
        {
            var row = new object[2];
            rdr2.GetValues(row);
            _logger?.LogDebug("Space: {row0}, CCSID: {row1}", row[0], row[1]);
        }

        _nzCommand.CommandText = @"select feature from _v_odbc_feature where spec_level = '3.5'";
        using var rdr3 = _nzCommand.ExecuteReader();
        while (rdr3.Read())
        {
            var row = new object[1];
            rdr3.GetValues(row);
            _logger?.LogDebug("Feature: {row0}", row[0]);
        }
        _nzCommand.CommandText = "select identifier_case, current_catalog, current_user";

        using var rdr4 = _nzCommand.ExecuteReader();
        while (rdr4.Read())
        {
            var row = new object[3];
            rdr4.GetValues(row);
            _logger?.LogDebug("Case: {row0}, Catalog: {row1}, User: {row2}", row[0], row[1], row[2]);
        }

        return true;
    }

    private async Task<bool> ConnSendQueryAsync(string dateStyle = "ISO", CancellationToken cancellationToken = default)
    {
        if (!await ExecuteAsync(_nzCommand, "set nz_encoding to 'utf8'", cancellationToken).ConfigureAwait(false))
            return false;

        string query = dateStyle switch
        {
            "MDY" => "set DateStyle to 'US'",
            "DMY" => "set DateStyle to 'EUROPEAN'",
            _ => "set DateStyle to 'ISO'"
        };

        if (!await ExecuteAsync(_nzCommand, query, cancellationToken).ConfigureAwait(false))
            return false;

        var systemArch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
        string procArch = systemArch == System.Runtime.InteropServices.Architecture.X64 ? "AMD64" : "other";

        string clientInfo = $@"select version(), 
        'Netezza Python Client Version {NzConnectionHelpers.NZPY_CLIENT_VERSION}', 
        '{procArch}',
        'OS Platform: {Environment.OSVersion}',
        'OS Username: {Environment.UserName}'";

        _nzCommand.CommandText = clientInfo;
        await using var rdr = await _nzCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await rdr.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = new object[5];
            rdr.GetValues(row);
            _serverVersion = (row[0] as string) ?? "no version info";
            _logger?.LogDebug("Version info: {row0}, {row1}, {row2}, {row3}, {row4}",
                row[0], row[1], row[2], row[3], row[4]);
        }

        if (!await ExecuteAsync(_nzCommand, $"SET CLIENT_VERSION = '{NzConnectionHelpers.NZPY_CLIENT_VERSION}'", cancellationToken).ConfigureAwait(false))
            return false;

        _nzCommand.CommandText = @"select ascii(' ') as space, encoding as ccsid from _v_database where objid = current_db";
        await using var rdr2 = await _nzCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await rdr2.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = new object[2];
            rdr2.GetValues(row);
            _logger?.LogDebug("Space: {row0}, CCSID: {row1}", row[0], row[1]);
        }

        _nzCommand.CommandText = @"select feature from _v_odbc_feature where spec_level = '3.5'";
        await using var rdr3 = await _nzCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await rdr3.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = new object[1];
            rdr3.GetValues(row);
            _logger?.LogDebug("Feature: {row0}", row[0]);
        }

        _nzCommand.CommandText = "select identifier_case, current_catalog, current_user";
        await using var rdr4 = await _nzCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await rdr4.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = new object[3];
            rdr4.GetValues(row);
            _logger?.LogDebug("Case: {row0}, Catalog: {row1}, User: {row2}", row[0], row[1], row[2]);
        }

        return true;
    }

    private readonly Dictionary<string, string>? _pgOptions = null;

    private readonly SecurityLevelCode _securityLevel = SecurityLevelCode.PreferredUnsecured;
    private readonly string? _sslCerFilePath;
    private readonly ILoggerFactory? _loggerFactory;

    private bool _disposed = false;
    private bool _protocolFaulted;
    private long _protocolRowNumber;
    private long _currentProtocolRowNumber;
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
            return;
        if (disposing)
            Close();
        _disposed = true;
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        await CloseAsync().ConfigureAwait(false);
        _disposed = true;
    }

    public void Commit()
    {
        if (this.InTransaction)
        {
            Execute(this._nzCommand, "commit");
            InTransaction = false;
            SetState(ConnectionState.Open);
        }
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        if (this.InTransaction)
        {
            await ExecuteAsync(this._nzCommand, "commit", cancellationToken).ConfigureAwait(false);
            InTransaction = false;
            SetState(ConnectionState.Open);
        }
    }

    public void Rollback()
    {
        if (this.InTransaction)
        {
            Execute(this._nzCommand, "rollback");
            InTransaction = false;
            SetState(ConnectionState.Open);
        }
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        if (this.InTransaction)
        {
            await ExecuteAsync(this._nzCommand, "rollback", cancellationToken).ConfigureAwait(false);
            InTransaction = false;
            SetState(ConnectionState.Open);
        }
    }

    private const int ProtocolSyncTimeoutMs = 2000;

    private byte? _protocolSyncPushback;

    private static NetezzaException ProtocolSyncError(string? context, string detail)
    {
        var preview = string.IsNullOrEmpty(context)
            ? ""
            : context.Replace('\r', ' ').Replace('\n', ' ');
        if (preview.Length > 80)
        {
            preview = preview[..80];
        }
        return new NetezzaException(
            $"Connection protocol out of sync before executing \"{preview}\": {detail}. Reconnect required.");
    }

    /// <summary>
    /// Test helper: send a simple query packet without consuming the backend response
    /// (simulates abandoned SELECT CURRENT_SID after cancel/timeout).
    /// Writes via the socket to avoid BufferedStream's "can't write while read buffer
    /// is non-empty" restriction when RFQ padding is still buffered.
    /// </summary>
    internal void InjectUnreadQuery(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(_socket);

        var queryBytes = Encoding.UTF8.GetBytes(sql);
        var packet = new byte[1 + 4 + queryBytes.Length + 1];
        packet[0] = (byte)'P';
        if (_commandNumber != -1)
        {
            _commandNumber += 1;
            if (_commandNumber > 100000)
            {
                _commandNumber = 1;
            }
            Core.IPack(_commandNumber, packet.AsSpan(1));
        }
        else
        {
            packet[1] = 0xFF;
            packet[2] = 0xFF;
            packet[3] = 0xFF;
            packet[4] = 0xFF;
        }

        queryBytes.CopyTo(packet.AsSpan(5));
        packet[5 + queryBytes.Length] = 0;
        _socket.Send(packet);
    }

    private bool TryReadByteNow(out byte value)
    {
        value = 0;
        if (_readBuffer is null || _socket is null)
        {
            return false;
        }

        // Prefer bytes already buffered by the read buffer: no socket probing.
        if (_readBuffer.BytesBuffered > 0)
        {
            value = _readBuffer.ReadByte();
            return true;
        }

        // Non-blocking probe: BufferedStream/SslStream return immediately when they
        // already have buffered bytes; otherwise NetworkStream hits a non-blocking
        // socket and fails at once (no ~1ms ReceiveTimeout on the healthy empty path).
        bool wasBlocking = _socket.Blocking;
        try
        {
            _socket.Blocking = false;
            int read = _stream.ReadByte();
            if (read < 0)
            {
                return false;
            }
            value = (byte)read;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            _socket.Blocking = wasBlocking;
        }
    }

    private void ReadExactWithDeadline(Span<byte> buffer, long deadlineTickCount64, string? context, string detail)
    {
        // Bytes already in the application buffer are available immediately.
        int offset = _readBuffer!.ReadBuffered(buffer);
        while (offset < buffer.Length)
        {
            if (Environment.TickCount64 > deadlineTickCount64)
            {
                throw ProtocolSyncError(context, detail);
            }

            var oldTimeout = _socket.ReceiveTimeout;
            try
            {
                var remainingMs = (int)Math.Clamp(deadlineTickCount64 - Environment.TickCount64, 1, ProtocolSyncTimeoutMs);
                _socket.ReceiveTimeout = remainingMs;
                int read = _stream.Read(buffer.Slice(offset));
                if (read <= 0)
                {
                    throw ProtocolSyncError(context, detail);
                }
                offset += read;
            }
            catch (IOException ex)
            {
                throw ProtocolSyncError(context, $"{detail} ({ex.Message})");
            }
            catch (SocketException ex)
            {
                throw ProtocolSyncError(context, $"{detail} ({ex.Message})");
            }
            finally
            {
                _socket.ReceiveTimeout = oldTimeout;
            }
        }
    }

    private void SkipBytesExact(int count, long deadlineTickCount64, string? context, string detail)
    {
        while (count > 0)
        {
            int chunk = Math.Min(count, _tmp_buffer.Length);
            ReadExactWithDeadline(_tmp_buffer.AsSpan(0, chunk), deadlineTickCount64, context, detail);
            count -= chunk;
        }
    }

    /// <summary>
    /// Non-blocking: discard leading 0x00 padding already buffered. If a non-null
    /// byte is found, stash it in <see cref="_protocolSyncPushback"/>.
    /// Returns true when orphaned (non-null) data is present.
    /// </summary>
    private bool TryDetectOrphanedData()
    {
        _protocolSyncPushback = null;
        while (TryReadByteNow(out byte b))
        {
            if (b != 0)
            {
                _protocolSyncPushback = b;
                return true;
            }
        }
        return false;
    }

    private bool TryReadProtocolByte(long deadlineTickCount64, out byte value)
    {
        if (_protocolSyncPushback is byte pushed)
        {
            _protocolSyncPushback = null;
            value = pushed;
            return true;
        }

        // Serve from the read buffer before touching the socket deadline logic.
        if (_readBuffer is not null && _readBuffer.BytesBuffered > 0)
        {
            value = _readBuffer.ReadByte();
            return true;
        }

        if (Environment.TickCount64 > deadlineTickCount64)
        {
            value = 0;
            return false;
        }

        var oldTimeout = _socket.ReceiveTimeout;
        try
        {
            var remainingMs = (int)Math.Clamp(deadlineTickCount64 - Environment.TickCount64, 1, ProtocolSyncTimeoutMs);
            _socket.ReceiveTimeout = remainingMs;
            int read = _stream.ReadByte();
            if (read < 0)
            {
                value = 0;
                return false;
            }
            value = (byte)read;
            return true;
        }
        catch (IOException)
        {
            value = 0;
            return false;
        }
        catch (SocketException)
        {
            value = 0;
            return false;
        }
        finally
        {
            _socket.ReceiveTimeout = oldTimeout;
        }
    }

    /// <summary>
    /// If a previous command left an unread backend response on the wire
    /// (e.g. abandoned SELECT CURRENT_SID), consume it up to ReadyForQuery before
    /// the next query so leftover RowDescription/DataRow are not mis-attributed.
    /// Called after the normal 4-byte null padding skip in PreExecution.
    /// </summary>
    private void EnsureProtocolSynced(string? context = null)
    {
        if (_stream is null || _socket is null || _readBuffer is null)
        {
            return;
        }

        // Fast path: nothing buffered beyond the padding already skipped.
        if (!TryDetectOrphanedData())
        {
            return;
        }

        _logger?.LogDebug("Orphaned backend data before command, draining. Context: {Context}", context);

        var deadline = Environment.TickCount64 + ProtocolSyncTimeoutMs;
        Span<byte> headerScratch = stackalloc byte[8];

        while (true)
        {
            if (!TryReadProtocolByte(deadline, out byte type))
            {
                throw ProtocolSyncError(context, "orphaned response incomplete (no ReadyForQuery)");
            }

            while (type == 0)
            {
                if (!TryReadProtocolByte(deadline, out type))
                {
                    throw ProtocolSyncError(context, "truncated orphaned null padding");
                }
            }

            // Netezza messages: type + 4-byte header, then type-specific payload.
            ReadExactWithDeadline(headerScratch[..4], deadline, context, $"truncated orphaned header for type 0x{type:x2}");

            if (type == (byte)BackendMessageCode.ReadyForQuery || type == (byte)'L')
            {
                // Discard trailing null padding already buffered after RFQ (non-blocking).
                // Do not wait — in-flight nulls for this orphan are usually already present
                // after the inject-test delay; waiting would risk eating the next response.
                while (TryReadByteNow(out byte b))
                {
                    if (b != 0)
                    {
                        _protocolSyncPushback = b;
                        break;
                    }
                }
                if (_protocolSyncPushback is byte leftover && leftover != 0)
                {
                    continue;
                }
                _protocolSyncPushback = null;
                return;
            }

            // '0' and 'A' have only the shared 4-byte header (same as IntepretReturnedByte).
            if (type == (byte)'0' || type == (byte)'A')
            {
                continue;
            }

            // Binary row: after the 4-byte header skip, payload is 8 + rowLength (same as ResReadDbosTuple).
            if (type == (byte)BackendMessageCode.RowStandard)
            {
                ReadExactWithDeadline(headerScratch, deadline, context, "truncated orphaned RowStandard header");
                int rowLength = BinaryPrimitives.ReadInt32BigEndian(headerScratch[4..8]);
                if (rowLength < 0 || rowLength > ProtocolLengthValidator.MaxPayloadLength)
                {
                    throw ProtocolSyncError(context, $"invalid orphaned RowStandard rowLength={rowLength}");
                }
                SkipBytesExact(rowLength, deadline, context, "truncated orphaned RowStandard payload");
                continue;
            }

            // EmptyQueryResponse ('I') carries a length-prefixed body after the header
            // (same layout as NoticeResponse in IntepretReturnedByte).
            if (type is (byte)BackendMessageCode.CommandComplete
                or (byte)BackendMessageCode.ErrorResponse
                or (byte)BackendMessageCode.NoticeResponse
                or (byte)BackendMessageCode.EmptyQueryResponse
                or (byte)BackendMessageCode.RowDescription
                or (byte)BackendMessageCode.DataRow
                or (byte)BackendMessageCode.RowDescriptionStandard
                or (byte)'P')
            {
                ReadExactWithDeadline(headerScratch[..4], deadline, context, $"truncated orphaned length for type 0x{type:x2}");
                int len = BinaryPrimitives.ReadInt32BigEndian(headerScratch[..4]);
                if (len < 0 || len > ProtocolLengthValidator.MaxPayloadLength)
                {
                    throw ProtocolSyncError(context, $"invalid orphaned length={len} for type 0x{type:x2}");
                }
                if (len > 0)
                {
                    SkipBytesExact(len, deadline, context, $"truncated orphaned payload for type 0x{type:x2}");
                }
                continue;
            }

            // Unknown length-prefixed backend message
            ReadExactWithDeadline(headerScratch[..4], deadline, context, $"truncated orphaned length for unknown type 0x{type:x2}");
            int unknownLen = BinaryPrimitives.ReadInt32BigEndian(headerScratch[..4]);
            if (unknownLen < 0 || unknownLen > ProtocolLengthValidator.MaxPayloadLength)
            {
                throw ProtocolSyncError(context, $"invalid orphaned length={unknownLen} for unknown type 0x{type:x2}");
            }
            if (unknownLen > 0)
            {
                SkipBytesExact(unknownLen, deadline, context, $"truncated orphaned payload for unknown type 0x{type:x2}");
            }
        }
    }

    private async Task EnsureProtocolSyncedAsync(string? context = null, CancellationToken cancellationToken = default)
    {
        // Orphaned payloads are already buffered after cancel/timeout waits in practice;
        // use the same drain against the shared stream.
        cancellationToken.ThrowIfCancellationRequested();
        EnsureProtocolSynced(context);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void PreExecution(NzCommand nzCommand, string query)
    {
        ThrowIfProtocolFaulted();
        _error = null;
        _backendException = null;
        nzCommand._recordsAffected = -1;
        nzCommand.NewPreparedStatement = new PreparedStatement();
        nzCommand.NewPreparedStatement.Sql = query;
        //if (State == ConnectionState.Executing)
        if (State != ConnectionState.Connecting)
        {
            _readBuffer!.Skip(4);
            EnsureProtocolSynced(query);
        }
        if (query is not null)
        {
            RegenerateBuffer(10 + 4 * query.Length);
        }
        _tmp_buffer[0] = (byte)'P';
        if (_commandNumber != -1)
        {
            _commandNumber += 1;
            Core.IPack(_commandNumber, _tmp_buffer.AsSpan(1));
        }
        else
        {
            _tmp_buffer[1] = 0xFF;//NEW
            _tmp_buffer[2] = 0xFF;//NEW
            _tmp_buffer[3] = 0xFF;//NEW
            _tmp_buffer[4] = 0xFF;//NEW
        }

        if (_commandNumber > 100000)
        {
            _commandNumber = 1;
        }

        int written = 5;
        if (query != null)
        {
            written += Encoding.UTF8.GetBytes(query, _tmp_buffer.AsSpan(written));//NEW
            _tmp_buffer[written] = 0;
            written += 1;
        }
        _stream.Write(_tmp_buffer,0,written);
        _stream.Flush();
        if (_logger?.IsEnabled(LogLevel.Debug) == true)
            _logger.LogDebug("Buffer sent to nps: {Buffer}", NzConnectionHelpers.ClientEncoding.GetString(_tmp_buffer,0,written));
        _state = ConnectionState.Executing;
    }

    private async Task PreExecutionAsync(NzCommand nzCommand, string query, CancellationToken cancellationToken = default)
    {
        ThrowIfProtocolFaulted();
        _error = null;
        _backendException = null;
        nzCommand._recordsAffected = -1;
        nzCommand.NewPreparedStatement = new PreparedStatement();
        nzCommand.NewPreparedStatement.Sql = query;
        if (State != ConnectionState.Connecting)
        {
            await SkipBytesAsync(4, cancellationToken).ConfigureAwait(false);
            await EnsureProtocolSyncedAsync(query, cancellationToken).ConfigureAwait(false);
        }
        if (query is not null)
        {
            RegenerateBuffer(10 + 4 * query.Length);
        }
        _tmp_buffer[0] = (byte)'P';
        if (_commandNumber != -1)
        {
            _commandNumber += 1;
            Core.IPack(_commandNumber, _tmp_buffer.AsSpan(1));
        }
        else
        {
            _tmp_buffer[1] = 0xFF;
            _tmp_buffer[2] = 0xFF;
            _tmp_buffer[3] = 0xFF;
            _tmp_buffer[4] = 0xFF;
        }

        if (_commandNumber > 100000)
        {
            _commandNumber = 1;
        }

        int written = 5;
        if (query != null)
        {
            written += Encoding.UTF8.GetBytes(query, _tmp_buffer.AsSpan(written));
            _tmp_buffer[written] = 0;
            written += 1;
        }
        await _stream.WriteAsync(_tmp_buffer.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (_logger?.IsEnabled(LogLevel.Debug) == true)
            _logger.LogDebug("Buffer sent to nps: {Buffer}", NzConnectionHelpers.ClientEncoding.GetString(_tmp_buffer, 0, written));
        _state = ConnectionState.Executing;
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

    private NzMetadata? _metadata;
    public NzMetadata Meta => _metadata ??= new NzMetadata(this);

    [AllowNull]
    public override string ConnectionString {
        get => new NzConnectionStringBuilder()
        {
            Host = _host,
            Database = _database,
            UserName = _user,
            Password = _password,
            Port= _port,
            Timeout= (int)ConnectionTimeoutDuration.TotalSeconds,
            LoggerFactory= _loggerFactory
        }.ConnectionString;
        set => throw new NotSupportedException("Setting ConnectionString is not supported. Create a new NzConnection instance.");
    }

    public string SafeConnectionString => new NzConnectionStringBuilder()
    {
        Host = _host,
        Database = _database,
        UserName = _user,
        Password = _password,
        Port = _port,
        Timeout = (int)ConnectionTimeoutDuration.TotalSeconds,
        LoggerFactory = _loggerFactory
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

    private void HandleTimeout()
    {
        double microsToWait = CommandTimeout.TotalMicroseconds;
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

    private CancellationTokenSource? _cachedTimeoutCts;

    private CancellationTokenSource? CreateCommandTimeoutTokenSource(CancellationToken cancellationToken)
    {
        if (CommandTimeout <= TimeSpan.Zero || CommandTimeout == Timeout.InfiniteTimeSpan)
        {
            return null;
        }

        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(CommandTimeout);
        return linkedCts;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryGetCommandTimeoutToken(
        CancellationToken callerToken,
        out CancellationToken effectiveToken,
        out CancellationTokenSource? linkedToDispose,
        out CancellationTokenSource? timeoutSource)
    {
        if (CommandTimeout <= TimeSpan.Zero || CommandTimeout == Timeout.InfiniteTimeSpan)
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
            cached.CancelAfter(CommandTimeout);
            effectiveToken = cached.Token;
            linkedToDispose = null;
            timeoutSource = cached;
            return true;
        }

        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        linkedCts.CancelAfter(CommandTimeout);
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
        HandleTimeout();
        _nextRelatedFileStream = null!;

        while (DoNextStep(nzCommand)) ;
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
        TryGetCommandTimeoutToken(cancellationToken, out var effectiveCancellationToken, out var linkedCts, out var timeoutSource);
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
                CancelQuery();
                _error = "Command timeout";
                _backendException = null;
                throw new NetezzaException(_error);
            }
        }
    }

    public NzDataReader ExecuteReader(NzCommand nzCommand, string query)
    {
        PreExecution(nzCommand, query);
        HandleTimeout();
        _nextRelatedFileStream = null!;
        var rdr =  new NzDataReader(nzCommand);
        if (_error != null)
        {
            throw CreateCurrentException();
        }
        return rdr;
    }

    public async Task<NzDataReader> ExecuteReaderAsync(NzCommand nzCommand, string query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TryGetCommandTimeoutToken(cancellationToken, out var effectiveCancellationToken, out var linkedCts, out var timeoutSource);
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
                CancelQuery();
                _error = "Command timeout";
                _backendException = null;
                throw new NetezzaException(_error);
            }
        }
    }

    private int _lastResponse = -1;

    private FileStream _nextRelatedFileStream = null!;

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
        ThrowIfProtocolFaulted();
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

    internal async ValueTask<bool> DoNextStepAsync(NzCommand nzCommand, CancellationToken cancellationToken = default)
    {
        ThrowIfProtocolFaulted();
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

    internal void ReadNextResponseByte()
    {
        _lastResponse = _readBuffer!.ReadByteOrEof();
        _shouldReadByte = false;
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

    internal async ValueTask ReadNextResponseByteAsync(CancellationToken cancellationToken = default)
    {
        _lastResponse = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
        _shouldReadByte = false;
    }
    private bool _shouldReadByte = true;

    private void InitializeUnloadFileStream(string fileName)
    {
        try
        {
            _nextRelatedFileStream = new FileStream(fileName, FileMode.OpenOrCreate, FileAccess.Write);
            _logger?.LogDebug("Successfully opened file: {Filename}", fileName);
            byte[] buf = [0, 0, 0, 0];
            WriteSpan(buf);
            Flush();
        }
        catch (IOException ex)
        {
            _logger?.LogWarning(ex, "Error while opening file");
            throw new NetezzaException("Error while opening unload file", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger?.LogWarning(ex, "Error while opening file");
            throw new NetezzaException("Error while opening unload file", ex);
        }
        catch (ArgumentException ex)
        {
            _logger?.LogWarning(ex, "Error while opening file");
            throw new NetezzaException("Error while opening unload file", ex);
        }
        catch (NotSupportedException ex)
        {
            _logger?.LogWarning(ex, "Error while opening file");
            throw new NetezzaException("Error while opening unload file", ex);
        }
    }

    private async Task InitializeUnloadFileStreamAsync(string fileName, CancellationToken cancellationToken)
    {
        try
        {
            _nextRelatedFileStream = new FileStream(fileName, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 4096, useAsync: true);
            _logger?.LogDebug("Successfully opened file: {Filename}", fileName);
            byte[] buf = [0, 0, 0, 0];
            await WriteSpanAsync(buf, cancellationToken).ConfigureAwait(false);
            await FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            _logger?.LogWarning(ex, "Error while opening file");
            throw new NetezzaException("Error while opening unload file", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger?.LogWarning(ex, "Error while opening file");
            throw new NetezzaException("Error while opening unload file", ex);
        }
        catch (ArgumentException ex)
        {
            _logger?.LogWarning(ex, "Error while opening file");
            throw new NetezzaException("Error while opening unload file", ex);
        }
        catch (NotSupportedException ex)
        {
            _logger?.LogWarning(ex, "Error while opening file");
            throw new NetezzaException("Error while opening unload file", ex);
        }
    }

    private void HandleExternalTableProtocolMessage()
    {
        switch (_lastResponse)
        {
            case (byte)'u':
                _readBuffer!.Skip(10);
                _readBuffer.Skip(16);
                int length = ReadProtocolLength("externalTable.fileNameLength");
                var fileNameBytes = Read(length);
                string fileName = Encoding.UTF8.GetString(fileNameBytes, 0, length);
                InitializeUnloadFileStream(fileName);
                return;

            case (byte)'U':
                if (_nextRelatedFileStream is null)
                {
                    throw new NetezzaException("Unload file stream is not initialized.");
                }
                ReceiveAndWriteDataToExternal(_nextRelatedFileStream);
                return;

            case (byte)'l':
                XferTable();
                return;

            case (byte)'x':
                _readBuffer!.Skip(4);
                _logger?.LogWarning("Error operation cancel");
                return;
        }
    }

    private async Task HandleExternalTableProtocolMessageAsync(CancellationToken cancellationToken)
    {
        switch (_lastResponse)
        {
            case (byte)'u':
                await SkipBytesAsync(10, cancellationToken).ConfigureAwait(false);
                await SkipBytesAsync(16, cancellationToken).ConfigureAwait(false);
                int length = await ReadProtocolLengthAsync(
                    "externalTable.fileNameLength",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                var fileNameBytes = await ReadAsync(length, cancellationToken: cancellationToken).ConfigureAwait(false);
                string fileName = Encoding.UTF8.GetString(fileNameBytes, 0, length);
                await InitializeUnloadFileStreamAsync(fileName, cancellationToken).ConfigureAwait(false);
                return;

            case (byte)'U':
                if (_nextRelatedFileStream is null)
                {
                    throw new NetezzaException("Unload file stream is not initialized.");
                }
                await ReceiveAndWriteDataToExternalAsync(_nextRelatedFileStream, cancellationToken).ConfigureAwait(false);
                return;

            case (byte)'l':
                await XferTableAsync(cancellationToken).ConfigureAwait(false);
                return;

            case (byte)'x':
                await SkipBytesAsync(4, cancellationToken).ConfigureAwait(false);
                _logger?.LogWarning("Error operation cancel");
                return;
        }
    }

    private bool IntepretReturnedByte(NzCommand nzCommand)
    {
        _shouldReadByte = true;
        if (_logger?.IsEnabled(LogLevel.Debug) == true)
            _logger.LogDebug(
                "Backend response: ResponseType={ResponseType} ResponseCode=0x{ResponseCode:X2} Row={Row}",
                ProtocolResponseType,
                _lastResponse,
                _currentProtocolRowNumber);
        ReadProtocolInt32("frameHeaderValue");

        if (_lastResponse == (byte)BackendMessageCode.CommandComplete)
        {
            // portal query command, no tuples returned
            int length = ReadProtocolLength("commandCompletePayloadLength");
            RegenerateBuffer(length);
            var data = Read(length, _tmp_buffer);
            HandleCommandComplete(data, length,  nzCommand);
            //returnet data informs about command type (SELECT/SET VARIABLE/...)
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
                _logger.LogDebug("Response received from backend: {Data}", Encoding.UTF8.GetString(data, 0, length));
        }
        else if (_lastResponse == (byte)BackendMessageCode.ReadyForQuery)
        {
            return false;
        }
        else if (_lastResponse == (byte)'L')
        {
            return false;
        }
        else if (_lastResponse == (byte)'0')
        {
            //return true;
        }
        else if (_lastResponse == (byte)'A')
        {
            //return true;
        }
        else if (_lastResponse == (byte)'P')//80
        {
            int length = ReadProtocolLength("preparedPayloadLength");
            RegenerateBuffer(length);
            var data = Read(length, _tmp_buffer);
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
                _logger.LogDebug("Response received from backend: {Data}", Encoding.UTF8.GetString(data, 0, length));
            //doContinue = true;
        }
        else if (_lastResponse == (byte)BackendMessageCode.ErrorResponse)
        {
            int length = ReadProtocolLength("errorPayloadLength");
            RegenerateBuffer(length);
            var data = Read(length, _tmp_buffer);
            _backendException = new NetezzaException(BackendDiagnosticResponseParser.Parse(data.AsSpan(0, length)));
            _error = _backendException.Message;
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
                _logger.LogDebug("Response received from backend: {_error}", _error);
            //doContinue = true;
        }
        //this STARTS (after 'P') single rowset
        else if (_lastResponse == (byte)BackendMessageCode.RowDescription)
        {
            int length = ReadProtocolLength("rowDescriptionPayloadLength");
            nzCommand.NewPreparedStatement ??= new PreparedStatement();
            RegenerateBuffer(length);
            var data = Read(length, _tmp_buffer);
            HandleRowDescription(data, nzCommand);
            // We've got row_desc that allows us to identify what we're going to get back from this statement.
            //nzCommand.NewPreparedStatement.input_funcs = nzCommand.NewPreparedStatement!.Description!.GetFuncArray;
        }
        else if (_lastResponse == (byte)BackendMessageCode.DataRow)//read rows in schema/system queries - hot path
        {
            int length = ReadProtocolLength("dataRowPayloadLength");
            RegenerateBuffer(length);
            var data = Read(length, _tmp_buffer);
            HandleDataRow(data, nzCommand); 
        }
        else if (_lastResponse == (byte)BackendMessageCode.RowDescriptionStandard)// metadata for standard query, occurs after BackendMessageCode.RowDescription
        {
            int length = ReadProtocolLength("rowDescriptionStandardPayloadLength");
            _tupdesc = new DbosTupleDesc();
            RegenerateBuffer(length);
            var data = Read(length, _tmp_buffer);
            ResGetDbosColumnDescriptions(data.AsSpan(0, length));
            //doContinue = true;
        }
        else if (_lastResponse == (byte)BackendMessageCode.RowStandard)//!!!!!!, main hot path - read rows
        {
            ResReadDbosTuple(nzCommand);
            //Thread.Sleep(50);
            //doContinue = true;
        }
        else if (_lastResponse is (byte)'u' or (byte)'U' or (byte)'l' or (byte)'x')
        {
            HandleExternalTableProtocolMessage();
        }
        else if (_lastResponse == (byte)'e')
        {
            int length = ReadProtocolLength("fileTransfer.logDirectoryLength", allowZero: false);
            int logDirectoryPayloadLength = ValidateProtocolLengthAfterOverhead(
                length,
                1,
                "fileTransfer.logDirectoryLength",
                "fileTransfer.logDirectoryPayloadLength");
            string logDir = Encoding.UTF8.GetString(Read(logDirectoryPayloadLength));

            _readBuffer!.ReadByte();
            // ignore one byte as it is null character at the end of the string
            var filenameBuf = new List<byte> { _readBuffer.ReadByte() };
            while (true)
            {
                var charByte = _readBuffer.ReadByte();
                if (charByte == 0x00)
                {
                    break;
                }
                filenameBuf.Add(charByte);
            }

            string filename = Encoding.UTF8.GetString(filenameBuf.ToArray());
            int logType = _readBuffer.ReadInt32BigEndian();
            if (!GetFileFromBE(logDir, filename, logType))
            {
                _logger?.LogDebug("Error in writing file received from BE");
            }
            //doContinue = true;
        }
        else if (_lastResponse == (byte)BackendMessageCode.NoticeResponse)
        {
            int length = ReadProtocolLength("noticePayloadLength");
            RegenerateBuffer(length);
            var data = Read(length, _tmp_buffer);
            string notice = BackendDiagnosticResponseParser.Parse(data.AsSpan(0, length)).Message;
            OnNoticeReceived(notice, nzCommand);
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
                _logger.LogDebug("Response received from backend: {Notice}", notice);
        }
        else if (_lastResponse == (byte)'I')
        {
            int length = ReadProtocolLength("emptyQueryPayloadLength");
            RegenerateBuffer(length);
            var data = Read(length, _tmp_buffer);
            string notice = Encoding.UTF8.GetString(data[0..length]);
            OnNoticeReceived(notice, nzCommand);
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
                _logger.LogDebug("Response received from backend: {Notice}", notice);
            nzCommand.AddRow([]);
        }

        return true;
    }

    private async ValueTask<bool> IntepretReturnedByteAsync(NzCommand nzCommand, CancellationToken cancellationToken = default)
    {
        _shouldReadByte = true;
        if (_logger?.IsEnabled(LogLevel.Debug) == true)
            _logger.LogDebug(
                "Backend response: ResponseType={ResponseType} ResponseCode=0x{ResponseCode:X2} Row={Row}",
                ProtocolResponseType,
                _lastResponse,
                _currentProtocolRowNumber);
        await ReadProtocolInt32Async("frameHeaderValue", cancellationToken)
            .ConfigureAwait(false);

        if (_lastResponse == (byte)BackendMessageCode.CommandComplete)
        {
            int length = await ReadProtocolLengthAsync(
                "commandCompletePayloadLength",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            RegenerateBuffer(length);
            var data = await ReadAsync(length, _tmp_buffer, cancellationToken).ConfigureAwait(false);
            HandleCommandComplete(data, length, nzCommand);
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
                _logger.LogDebug("Response received from backend: {Data}", Encoding.UTF8.GetString(data, 0, length));
        }
        else if (_lastResponse == (byte)BackendMessageCode.ReadyForQuery)
        {
            return false;
        }
        else if (_lastResponse == (byte)'L')
        {
            return false;
        }
        else if (_lastResponse == (byte)'0')
        {
        }
        else if (_lastResponse == (byte)'A')
        {
        }
        else if (_lastResponse == (byte)'P')
        {
            int length = await ReadProtocolLengthAsync(
                "preparedPayloadLength",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            RegenerateBuffer(length);
            var data = await ReadAsync(length, _tmp_buffer, cancellationToken).ConfigureAwait(false);
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
                _logger.LogDebug("Response received from backend: {Data}", Encoding.UTF8.GetString(data, 0, length));
        }
        else if (_lastResponse == (byte)BackendMessageCode.ErrorResponse)
        {
            int length = await ReadProtocolLengthAsync(
                "errorPayloadLength",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            RegenerateBuffer(length);
            var data = await ReadAsync(length, _tmp_buffer, cancellationToken).ConfigureAwait(false);
            _backendException = new NetezzaException(BackendDiagnosticResponseParser.Parse(data.AsSpan(0, length)));
            _error = _backendException.Message;
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
                _logger.LogDebug("Response received from backend: {_error}", _error);
        }
        else if (_lastResponse == (byte)BackendMessageCode.RowDescription)
        {
            int length = await ReadProtocolLengthAsync(
                "rowDescriptionPayloadLength",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            nzCommand.NewPreparedStatement ??= new PreparedStatement();
            RegenerateBuffer(length);
            var data = await ReadAsync(length, _tmp_buffer, cancellationToken).ConfigureAwait(false);
            HandleRowDescription(data, nzCommand);
        }
        else if (_lastResponse == (byte)BackendMessageCode.DataRow)
        {
            int length = await ReadProtocolLengthAsync(
                "dataRowPayloadLength",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            RegenerateBuffer(length);
            var data = await ReadAsync(length, _tmp_buffer, cancellationToken).ConfigureAwait(false);
            HandleDataRow(data, nzCommand);
        }
        else if (_lastResponse == (byte)BackendMessageCode.RowDescriptionStandard)
        {
            int length = await ReadProtocolLengthAsync(
                "rowDescriptionStandardPayloadLength",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            _tupdesc = new DbosTupleDesc();
            RegenerateBuffer(length);
            var data = await ReadAsync(length, _tmp_buffer, cancellationToken).ConfigureAwait(false);
            ResGetDbosColumnDescriptions(data.AsSpan(0, length));
        }
        else if (_lastResponse == (byte)BackendMessageCode.RowStandard)
        {
            await ResReadDbosTupleAsync(nzCommand, cancellationToken).ConfigureAwait(false);
        }
        else if (_lastResponse is (byte)'u' or (byte)'U' or (byte)'l' or (byte)'x')
        {
            await HandleExternalTableProtocolMessageAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (_lastResponse == (byte)'e')
        {
            int length = await ReadProtocolLengthAsync(
                "fileTransfer.logDirectoryLength",
                allowZero: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            int logDirectoryPayloadLength = ValidateProtocolLengthAfterOverhead(
                length,
                1,
                "fileTransfer.logDirectoryLength",
                "fileTransfer.logDirectoryPayloadLength");
            var logDirBytes = await ReadAsync(
                logDirectoryPayloadLength,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            string logDir = Encoding.UTF8.GetString(logDirBytes);

            _ = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
            var filenameBuf = new List<byte> { (byte)await ReadByteAsync(cancellationToken).ConfigureAwait(false) };
            while (true)
            {
                var charByte = (byte)await ReadByteAsync(cancellationToken).ConfigureAwait(false);
                if (charByte == 0x00)
                {
                    break;
                }
                filenameBuf.Add(charByte);
            }

            string filename = Encoding.UTF8.GetString(filenameBuf.ToArray());
            int logType = await ReadInt32Async(cancellationToken).ConfigureAwait(false);
            if (!await GetFileFromBEAsync(logDir, filename, logType, cancellationToken).ConfigureAwait(false))
            {
                _logger?.LogDebug("Error in writing file received from BE");
            }
        }
        else if (_lastResponse == (byte)BackendMessageCode.NoticeResponse)
        {
            int length = await ReadProtocolLengthAsync(
                "noticePayloadLength",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            RegenerateBuffer(length);
            var data = await ReadAsync(length, _tmp_buffer, cancellationToken).ConfigureAwait(false);
            string notice = BackendDiagnosticResponseParser.Parse(data.AsSpan(0, length)).Message;
            OnNoticeReceived(notice, nzCommand);
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
                _logger.LogDebug("Response received from backend: {Notice}", notice);
        }
        else if (_lastResponse == (byte)'I')
        {
            int length = await ReadProtocolLengthAsync(
                "emptyQueryPayloadLength",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            RegenerateBuffer(length);
            var data = await ReadAsync(length, _tmp_buffer, cancellationToken).ConfigureAwait(false);
            string notice = Encoding.UTF8.GetString(data, 0, length);
            OnNoticeReceived(notice, nzCommand);
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
                _logger.LogDebug("Response received from backend: {Notice}", notice);
            nzCommand.AddRow([]);
        }

        return true;
    }


    private void XferTable()
    {
        ReadProtocolInt32("externalTable.frameHeaderValue");
        int clientVersion = 1;

        byte charByte = _readBuffer!.ReadByte();

        var filenameBuf = new List<byte> { charByte };
        while (true)
        {
            charByte = _readBuffer.ReadByte();
            if (charByte == 0x00)
            {
                break;
            }
            filenameBuf.Add(charByte);
        }

        string filename = NzConnectionHelpers.ClientEncoding.GetString(filenameBuf.ToArray());

        int hostVersion = _readBuffer.ReadInt32BigEndian();
        PGUtil.WriteInt32(_stream, clientVersion);

        Flush();

        int format = _readBuffer.ReadInt32BigEndian();
        int blockSize = ReadProtocolLength("externalTable.blockSize", allowZero: false);
        _logger?.LogInformation("Format={Format} Block size={BlockSize} Host version={HostVersion}", format, blockSize, hostVersion);

        int effectiveBlockSize = Math.Max(blockSize, 1);
        try
        {
            using var filehandle = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.Read, Math.Max(effectiveBlockSize, 4096), useAsync: false);

            if (_logger ?.IsEnabled(LogLevel.Information) == true)
            {
                _logger?.LogInformation("Successfully opened External file to read: {Filename}", filename);
            }           

            byte[] bytesBuffer = ArrayPool<byte>.Shared.Rent(effectiveBlockSize);
            try
            {
                while (true)
                {
                    int bytesReaded = filehandle.Read(bytesBuffer, 0, effectiveBlockSize);
                    if (bytesReaded == 0)
                    {
                        break;
                    }

                    Span<byte> dataBytes = bytesBuffer.AsSpan();
                    dataBytes = dataBytes[..bytesReaded];

                    if (blockSize < dataBytes.Length)
                    {
                        int diff = dataBytes.Length - blockSize;

                        PGUtil.WriteInt32(_stream, Core.EXTAB_SOCK_DATA);
                        PGUtil.WriteInt32(_stream, blockSize);
                        WriteSpan(dataBytes[..blockSize]);
                        Flush();

                        PGUtil.WriteInt32(_stream, Core.EXTAB_SOCK_DATA);
                        PGUtil.WriteInt32(_stream, diff);
                        WriteSpan(dataBytes[blockSize..]);
                        Flush();
                    }
                    else
                    {
                        PGUtil.WriteInt32(_stream, Core.EXTAB_SOCK_DATA);
                        PGUtil.WriteInt32(_stream, dataBytes.Length);
                        WriteSpan(dataBytes);
                        Flush();
                    }
                    if (_logger?.IsEnabled(LogLevel.Debug) == true)
                    {
                        _logger?.LogDebug("No. of bytes sent to BE: {BytesSent}", dataBytes.Length);
                    }                
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(bytesBuffer);
            }

            PGUtil.WriteInt32(_stream, Core.EXTAB_SOCK_DONE);
            Flush();
            _logger?.LogInformation("sent EXTAB_SOCK_DONE to reader");
        }
        catch (IOException ex)
        {
            _logger?.LogWarning(ex, "Error opening file");
            throw new NetezzaException("Error opening file", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger?.LogWarning(ex, "Error opening file");
            throw new NetezzaException("Error opening file", ex);
        }
        catch (ArgumentException ex)
        {
            _logger?.LogWarning(ex, "Error opening file");
            throw new NetezzaException("Error opening file", ex);
        }
        catch (NotSupportedException ex)
        {
            _logger?.LogWarning(ex, "Error opening file");
            throw new NetezzaException("Error opening file", ex);
        }
    }

    private async Task XferTableAsync(CancellationToken cancellationToken = default)
    {
        await ReadProtocolInt32Async(
            "externalTable.frameHeaderValue",
            cancellationToken).ConfigureAwait(false);
        int clientVersion = 1;

        byte charByte = (byte)await ReadByteAsync(cancellationToken).ConfigureAwait(false);
        var filenameBuf = new List<byte> { charByte };
        while (true)
        {
            charByte = (byte)await ReadByteAsync(cancellationToken).ConfigureAwait(false);
            if (charByte == 0x00)
            {
                break;
            }
            filenameBuf.Add(charByte);
        }

        string filename = NzConnectionHelpers.ClientEncoding.GetString(filenameBuf.ToArray());
        int hostVersion = await ReadInt32Async(cancellationToken).ConfigureAwait(false);
        await PGUtil.WriteInt32Async(_stream, clientVersion, cancellationToken).ConfigureAwait(false);
        await FlushAsync(cancellationToken).ConfigureAwait(false);

        int format = await ReadInt32Async(cancellationToken).ConfigureAwait(false);
        int blockSize = await ReadProtocolLengthAsync(
            "externalTable.blockSize",
            allowZero: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        _logger?.LogInformation("Format={Format} Block size={BlockSize} Host version={HostVersion}", format, blockSize, hostVersion);

        int effectiveBlockSize = Math.Max(blockSize, 1);
        try
        {
            await using var filehandle = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.Read, Math.Max(effectiveBlockSize, 4096), useAsync: true);

            if (_logger?.IsEnabled(LogLevel.Information) == true)
            {
                _logger?.LogInformation("Successfully opened External file to read: {Filename}", filename);
            }

            byte[] bytesBuffer = ArrayPool<byte>.Shared.Rent(effectiveBlockSize);
            try
            {
                while (true)
                {
                    int bytesReaded = await filehandle.ReadAsync(bytesBuffer.AsMemory(0, effectiveBlockSize), cancellationToken).ConfigureAwait(false);
                    if (bytesReaded == 0)
                    {
                        break;
                    }

                    ReadOnlyMemory<byte> dataBytes = bytesBuffer.AsMemory(0, bytesReaded);
                    await PGUtil.WriteInt32Async(_stream, Core.EXTAB_SOCK_DATA, cancellationToken).ConfigureAwait(false);
                    await PGUtil.WriteInt32Async(_stream, dataBytes.Length, cancellationToken).ConfigureAwait(false);
                    await WriteSpanAsync(dataBytes, cancellationToken).ConfigureAwait(false);
                    await FlushAsync(cancellationToken).ConfigureAwait(false);

                    if (_logger?.IsEnabled(LogLevel.Debug) == true)
                    {
                        _logger?.LogDebug("No. of bytes sent to BE: {BytesSent}", dataBytes.Length);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(bytesBuffer);
            }

            await PGUtil.WriteInt32Async(_stream, Core.EXTAB_SOCK_DONE, cancellationToken).ConfigureAwait(false);
            await FlushAsync(cancellationToken).ConfigureAwait(false);
            _logger?.LogInformation("sent EXTAB_SOCK_DONE to reader");
        }
        catch (IOException ex)
        {
            _logger?.LogWarning(ex, "Error opening file");
            throw new NetezzaException("Error opening file", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger?.LogWarning(ex, "Error opening file");
            throw new NetezzaException("Error opening file", ex);
        }
        catch (ArgumentException ex)
        {
            _logger?.LogWarning(ex, "Error opening file");
            throw new NetezzaException("Error opening file", ex);
        }
        catch (NotSupportedException ex)
        {
            _logger?.LogWarning(ex, "Error opening file");
            throw new NetezzaException("Error opening file", ex);
        }
    }

    private bool GetFileFromBE(string logDir, string filename, int logType)
    {
        bool status = true;

        // If no explicit -logDir mentioned (defaulted by backend to /tmp)
        string fullpath = Path.Combine(logDir, filename);

        FileStream? fh = null;
        if (logType == 1)
        {
            fullpath += ".nzlog";
            fh = new FileStream(fullpath, FileMode.Create, FileAccess.Write);
        }
        else if (logType == 2)
        {
            fullpath += ".nzbad";
            fh = new FileStream(fullpath, FileMode.Create, FileAccess.Write);
        }
        else if (logType == 3)
        {
            fullpath += ".nzstats";
            fh = new FileStream(fullpath, FileMode.Create, FileAccess.Write);
        }
        if (fh is null)
        {
            throw new NullReferenceException(nameof(fh));
        }

        using (StreamWriter writer = new StreamWriter(fh, Encoding.UTF8))
        {
            while (true)
            {
                int numBytes = ReadProtocolLength("fileTransfer.chunkLength");

                if (numBytes == 0)  // zeros means EOF, no more data
                {
                    break;
                }
                RegenerateBuffer(numBytes);
                var data = Read(numBytes, _tmp_buffer);
                if (status)
                {
                    int maxCharCount = NzConnectionHelpers.ClientEncoding.GetMaxCharCount(numBytes);
                    char[] tmpChars = ArrayPool<char>.Shared.Rent(maxCharCount);
                    try
                    {
                        int charsWritten = NzConnectionHelpers.ClientEncoding.GetChars(data, 0, numBytes, tmpChars, 0);
                        writer.Write(tmpChars,0, charsWritten);
                        writer.Flush();
                        _logger?.LogInformation("Successfully written data into file: {FullPath}", fullpath);
                    }
                    catch (IOException ex)
                    {
                        _logger?.LogWarning(ex, "Error in writing data to file");
                        status = false;
                    }
                    catch (ArgumentException ex)
                    {
                        _logger?.LogWarning(ex, "Error in writing data to file");
                        status = false;
                    }
                    finally
                    {
                        ArrayPool<char>.Shared.Return(tmpChars);
                    }
                }
            }
        }

        return status;
    }

    private async Task<bool> GetFileFromBEAsync(string logDir, string filename, int logType, CancellationToken cancellationToken = default)
    {
        bool status = true;
        string fullpath = Path.Combine(logDir, filename);

        FileStream? fh = null;
        if (logType == 1)
        {
            fullpath += ".nzlog";
            fh = new FileStream(fullpath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        }
        else if (logType == 2)
        {
            fullpath += ".nzbad";
            fh = new FileStream(fullpath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        }
        else if (logType == 3)
        {
            fullpath += ".nzstats";
            fh = new FileStream(fullpath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        }
        if (fh is null)
        {
            throw new NullReferenceException(nameof(fh));
        }

        await using (fh.ConfigureAwait(false))
        {
            using StreamWriter writer = new StreamWriter(fh, Encoding.UTF8);
            while (true)
            {
                int numBytes = await ReadProtocolLengthAsync(
                    "fileTransfer.chunkLength",
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (numBytes == 0)
                {
                    break;
                }
                RegenerateBuffer(numBytes);
                var data = await ReadAsync(numBytes, _tmp_buffer, cancellationToken).ConfigureAwait(false);
                if (status)
                {
                    try
                    {
                        int maxCharCount = NzConnectionHelpers.ClientEncoding.GetMaxCharCount(numBytes);
                        char[] tmpChars = ArrayPool<char>.Shared.Rent(maxCharCount);
                        try
                        {
                            int charsWritten = NzConnectionHelpers.ClientEncoding.GetChars(data, 0, numBytes, tmpChars, 0);
                            await writer.WriteAsync(tmpChars, 0, charsWritten).ConfigureAwait(false);
                            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                            _logger?.LogInformation("Successfully written data into file: {FullPath}", fullpath);
                        }
                        finally
                        {
                            ArrayPool<char>.Shared.Return(tmpChars);
                        }
                    }
                    catch (IOException ex)
                    {
                        _logger?.LogWarning(ex, "Error in writing data to file");
                        status = false;
                    }
                    catch (ArgumentException ex)
                    {
                        _logger?.LogWarning(ex, "Error in writing data to file");
                        status = false;
                    }
                }
            }
        }

        return status;
    }


    private void ReceiveAndWriteDataToExternal(FileStream fh)
    {
        if (fh is null)
        {
            throw new NetezzaException("Unload file stream is not initialized.");
        }

        ReadProtocolInt32("externalTable.receiveFrameHeaderValue");

        while (true)
        {
            // Get EXTAB_SOCK Status
            int status;
            try
            {
                status = _readBuffer!.ReadInt32BigEndian();
            }
            catch (IOException ex)
            {
                _logger?.LogWarning(ex, "Error while retrieving status, closing unload file");
                fh.Close();
                throw new NetezzaException("Error while retrieving unload status", ex);
            }

            if (status == Core.EXTAB_SOCK_DATA)
            {
                // get number of bytes in block
                int numBytes = ReadProtocolLength("externalTable.chunkLength");
                byte[] bytes = ArrayPool<byte>.Shared.Rent(numBytes);
                try
                {
                    bytes = Read(numBytes, bytes);
                    fh.Write(bytes, 0, numBytes);
                    fh.Flush();
                    _logger?.LogInformation("Successfully written data into file");
                }
                catch (IOException ex)
                {
                    _logger?.LogWarning(ex, "Error in writing data to file");
                    throw new NetezzaException("Error in writing data to unload file", ex);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(bytes);
                }
                continue;
            }

            if (status == Core.EXTAB_SOCK_DONE)
            {
                fh.Close();
                _logger?.LogInformation("unload - done receiving data");
                break;
            }

            if (status == Core.EXTAB_SOCK_ERROR)
            {
                //int len = HUnpack(_read(2));
                short len = ReadProtocolInt16Length("externalTable.errorMessageLength");

                string errorMsg = NzConnectionHelpers.ClientEncoding.GetString(Read(len));
                //len = HUnpack(_read(2));
                len = ReadProtocolInt16Length("externalTable.errorObjectLength");
                string errorObject = NzConnectionHelpers.ClientEncoding.GetString(Read(len));

                _logger?.LogWarning("unload - ErrorMsg: {ErrorMsg}", errorMsg);
                _logger?.LogWarning("unload - ErrorObj: {ErrorObject}", errorObject);

                fh.Close();
                _logger?.LogDebug("unload - done receiving data");
                throw new NetezzaException($"Unload error: {errorMsg}. Object: {errorObject}");
            }
            else
            {
                fh.Close();
                throw new NetezzaException($"Unknown unload status code: {status}");
            }
        }
    }

    private async Task ReceiveAndWriteDataToExternalAsync(FileStream fh, CancellationToken cancellationToken = default)
    {
        if (fh is null)
        {
            throw new NetezzaException("Unload file stream is not initialized.");
        }

        await ReadProtocolInt32Async(
            "externalTable.receiveFrameHeaderValue",
            cancellationToken).ConfigureAwait(false);

        while (true)
        {
            int status;
            try
            {
                status = await ReadInt32Async(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                _logger?.LogWarning(ex, "Error while retrieving status, closing unload file");
                await fh.DisposeAsync().ConfigureAwait(false);
                throw new NetezzaException("Error while retrieving unload status", ex);
            }

            if (status == Core.EXTAB_SOCK_DATA)
            {
                int numBytes = await ReadProtocolLengthAsync(
                    "externalTable.chunkLength",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                try
                {
                    byte[] bytes = ArrayPool<byte>.Shared.Rent(numBytes);
                    try
                    {
                        await _readBuffer!.ReadExactlyAsync(bytes.AsMemory(0, numBytes), cancellationToken).ConfigureAwait(false);
                        await fh.WriteAsync(bytes.AsMemory(0, numBytes), cancellationToken).ConfigureAwait(false);
                        await fh.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(bytes);
                    }
                    _logger?.LogInformation("Successfully written data into file");
                }
                catch (IOException ex)
                {
                    _logger?.LogWarning(ex, "Error in writing data to file");
                    throw new NetezzaException("Error in writing data to unload file", ex);
                }
                continue;
            }

            if (status == Core.EXTAB_SOCK_DONE)
            {
                await fh.DisposeAsync().ConfigureAwait(false);
                _logger?.LogInformation("unload - done receiving data");
                break;
            }

            if (status == Core.EXTAB_SOCK_ERROR)
            {
                short len = await ReadProtocolInt16LengthAsync(
                    "externalTable.errorMessageLength",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                string errorMsg = NzConnectionHelpers.ClientEncoding.GetString(await ReadAsync(len, cancellationToken: cancellationToken).ConfigureAwait(false));
                len = await ReadProtocolInt16LengthAsync(
                    "externalTable.errorObjectLength",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                string errorObject = NzConnectionHelpers.ClientEncoding.GetString(await ReadAsync(len, cancellationToken: cancellationToken).ConfigureAwait(false));

                _logger?.LogWarning("unload - ErrorMsg: {ErrorMsg}", errorMsg);
                _logger?.LogWarning("unload - ErrorObj: {ErrorObject}", errorObject);

                await fh.DisposeAsync().ConfigureAwait(false);
                _logger?.LogDebug("unload - done receiving data");
                throw new NetezzaException($"Unload error: {errorMsg}. Object: {errorObject}");
            }
            else
            {
                await fh.DisposeAsync().ConfigureAwait(false);
                throw new NetezzaException($"Unknown unload status code: {status}");
            }
        }
    }

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
        // NumFields comes straight off the wire, so never pre-size from it
        // unbounded: a corrupt value could request gigabytes before the field
        // loop fails on a short payload. Cap by what the payload can hold
        // (36-byte header + 36 bytes per field + 8-byte trailer); the loop
        // below still performs the real validation.
        int maxPlausibleFields = (data.Length - 36) / 36;
        _tupdesc.EnsureCapacity(Math.Min(_tupdesc.NumFields, maxPlausibleFields));

        dataIdx += 36;
        for (int ix = 0; ix < _tupdesc.NumFields; ix++)
        {
            //https://github.com/IBM/nzpy/issues/61
            var ft = IUnpack(data, dataIdx);

            if (ft == NzTypeInt && _nzCommand?.NewPreparedStatement?.Description?[ix].TypeOID == 702)
            {
                _tupdesc.FieldType.Add(NzTypeIntvsAbsTimeFIX);
            }
            else
            {
                _tupdesc.FieldType.Add(ft);
            }

            _tupdesc.FieldSize.Add(IUnpack(data, dataIdx + 4));
            _tupdesc.FieldTrueSize.Add(IUnpack(data, dataIdx + 8));
            _tupdesc.FieldOffset.Add(IUnpack(data, dataIdx + 12));
            _tupdesc.FieldPhysField.Add(IUnpack(data, dataIdx + 16));
            _tupdesc.FieldLogField.Add(IUnpack(data, dataIdx + 20));
            _tupdesc.FieldNullAllowed.Add(IUnpack(data, dataIdx + 24) != 0);
            _tupdesc.FieldFixedSize.Add(IUnpack(data, dataIdx + 28));
            _tupdesc.FieldSpringField.Add(IUnpack(data, dataIdx + 32));
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

        _tupdesc.Freeze();
    }


    private byte[] _tmp_buffer;
    private RowValue[]? _row;

    public bool UseStringPool { get; set; } = true;

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
            //CTableFieldAt can span be used here ? - to reduce alocation
            ReadOnlySpan<byte> fieldDataP = CTableFieldAt(data, curField);

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
                case NzTypeVarBinary:
                case NzTypeJson:
                case NzTypeJsonb:
                case NzTypeJsonpath:
                {
                    int cursize = BitConverter.ToInt16(fieldDataP) - 2;
                    string value = GetStandardString(curField, fieldDataP.Slice(2, cursize), NzConnectionHelpers.CharVarcharEncoding);
                    rowValue.typeCode = TypeCodeEx.String;
                    rowValue.stringValue = value;
                    if (logDebug) _logger?.LogDebug("field={Field}, datatype={Datatype}, value={Value}", curField + 1, fldtype.ToString(), value);
                    break;
                }

                case NzTypeChar:
                {
                    string value = GetStandardString(curField, fieldDataP.Slice(0, fldlen), NzConnectionHelpers.CharVarcharEncoding);
                    rowValue.typeCode = TypeCodeEx.String;
                    rowValue.stringValue = value;
                    if (logDebug) _logger?.LogDebug("field={Field}, datatype=CHAR, value={Value}", curField + 1, value);
                    break;
                }

                case NzTypeNChar:
                case NzTypeNVarChar:
                {
                    int cursize = BitConverter.ToInt16(fieldDataP) - 2;
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
                    int prec = CTableIFieldPrecision(curField);
                    int scale = CTableIFieldScale(curField);
                    int count = CTableIFieldNumericDigit32Count(curField);
                    decimal value;
                    try
                    {
                        value = Numeric.GetCsNumeric(fieldDataP, prec, scale, count);
                    }
                    catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentOutOfRangeException or InvalidCastException)
                    {
                        throw new InvalidCastException($"Failed to convert column {curField + 1} as NUMERIC.", ex);
                    }
                    rowValue.typeCode = TypeCodeEx.Decimal;
                    rowValue.decimalValue = value;
                    if (logDebug) _logger?.LogDebug("field={Field}, datatype=NUMERIC, value={Value}", curField + 1, value);
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


    private void RegenerateBuffer(int length)
    {
        ValidateProtocolLength(length, "bufferAllocation");
        if (_tmp_buffer.Length < length)
        {
            if (_tmp_buffer.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(_tmp_buffer);
            }
            _tmp_buffer = ArrayPool<byte>.Shared.Rent(length);
        }
    }

    private bool ColumnIsNull(ReadOnlySpan<byte> data, int fieldLf)
    {
        var decodedColumnNumber = _tupdesc.FieldPhysFieldArr[fieldLf];
        byte numberToTest = data[2 + decodedColumnNumber / 8];
        var numberOfBitToCheck = decodedColumnNumber % 8;
        var columnIsNull = (numberToTest & (1 << numberOfBitToCheck)) != 0;
        return _tupdesc.NullsAllowed != 0 && columnIsNull;
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
            return _tupdesc.FieldSize[coldex];
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
            return _tupdesc.FieldNullAllowed[coldex];
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

            int length = BitConverter.ToInt16(data.Slice(position, sizeof(short)));
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
                        rowValue.typeCode = TypeCodeEx.String; 
                        rowValue.stringValue = NzConnectionHelpers.ByteaRecv(data, dataIdx, vlen - 4);
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
                        rowValue.typeCode = TypeCodeEx.Decimal;
                        rowValue.decimalValue = NzConnectionHelpers.NumericInTyped(data, dataIdx, vlen - 4);
                        break;
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

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        if (State != ConnectionState.Open)
        {
            throw new InvalidOperationException("Connection must be open to begin a transaction.");
        }

        if (isolationLevel != IsolationLevel.Unspecified && isolationLevel != IsolationLevel.ReadCommitted)
        {
            throw new NotSupportedException("Only IsolationLevel.ReadCommitted is supported.");
        }

        AutoCommit = false;
        if (!InTransaction)
        {
            _nzCommand ??= (NzCommand)CreateCommand();
            Execute(this._nzCommand, "begin");
            InTransaction = true;
        }
        SetState(ConnectionState.Open);

        return new NzTransaction(this, IsolationLevel.ReadCommitted);
    }

    public override void ChangeDatabase(string databaseName)
    {
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            throw new ArgumentException("Database name cannot be null or empty.", nameof(databaseName));
        }

        var catalogName = GetValidCatalogIdentifier(databaseName);

        if (State != ConnectionState.Open)
        {
            throw new InvalidOperationException("Connection must be open to change database.");
        }

        if (InTransaction)
        {
            throw new InvalidOperationException("Cannot change database while a transaction is active.");
        }

        if (string.Equals(databaseName, _database, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _nzCommand ??= (NzCommand)CreateCommand();
        Execute(_nzCommand, $"SET CATALOG {catalogName}");
        _database = databaseName;
        SetState(ConnectionState.Open);
    }

    private static string GetValidCatalogIdentifier(string databaseName)
    {
        var catalogName = databaseName.Trim();

        if (!IsIdentifierStart(catalogName[0]))
        {
            throw new ArgumentException("Database name must be a valid unquoted identifier.", nameof(databaseName));
        }

        for (var i = 1; i < catalogName.Length; i++)
        {
            if (!IsIdentifierPart(catalogName[i]))
            {
                throw new ArgumentException("Database name must be a valid unquoted identifier.", nameof(databaseName));
            }
        }

        return catalogName;
    }

    private static bool IsIdentifierStart(char c)
    {
        return c == '_' || char.IsAsciiLetter(c);
    }

    private static bool IsIdentifierPart(char c)
    {
        return c == '_' || c == '$' || char.IsAsciiLetterOrDigit(c);
    }

    public override void Close()
    {
        _readBuffer?.Dispose();
        _readBuffer = null;
        _stream?.Dispose();
        _stream = null!;

        _socket?.Dispose();
        _socket = null!;
        _nzCommand = null!;
        _state = ConnectionState.Closed;
        if (_tmp_buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_tmp_buffer);
            _tmp_buffer = [];
        }
        _cachedTimeoutCts?.Dispose();
        _cachedTimeoutCts = null;
    }

    public override async Task CloseAsync()
    {
        _readBuffer?.Dispose();
        _readBuffer = null;
        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null!;
        }

        _socket?.Dispose();
        _socket = null!;
        _nzCommand = null!;
        _state = ConnectionState.Closed;

        if (_tmp_buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_tmp_buffer);
            _tmp_buffer = [];
        }
        _cachedTimeoutCts?.Dispose();
        _cachedTimeoutCts = null;
    }

    private NzCommand _nzCommand = null!;
    public void SetNzCommand(NzCommand nzCommand)
    {
        _nzCommand = nzCommand;
    }

    protected override DbCommand CreateDbCommand()
    {
        _nzCommand = new NzCommand(this);
        return _nzCommand;
    }

    public DbCommand CreateDbCommand(string sql)
    {
        _nzCommand = new NzCommand(this);
        _nzCommand.CommandText = sql;
        return _nzCommand;
    }

    public NzCommand CreateCommand(string sql)
    {
        return (NzCommand)CreateDbCommand(sql);
    }

    public override void Open()
    {
        Open(ClientTypeId.SqlDotnet);
    }
    public void Open(ClientTypeId clientVersionId = ClientTypeId.SqlDotnet, bool useBufferedStream = false, bool setSocketBufferSizes = false)
    {
        if (_tmp_buffer.Length == 0)
        {
            _tmp_buffer = ArrayPool<byte>.Shared.Rent(4096);
        }
        _state = ConnectionState.Connecting;
        _stream = Initialize(_host, _port, useBufferedStream, setSocketBufferSizes);
        Handshake handShake = new(_socket, _stream, _host, _sslCerFilePath, _loggerFactory)
        {
            NPSCLIENT_TYPE_PYTHON = clientVersionId
        };
        Stream? response = handShake.Startup(_database, _securityLevel, _user, _password, _pgOptions);
        _backendKeyData = handShake.BackendKeyData;

        if (response is not null)
        {
            _stream = response;
            _readBuffer?.Dispose();
            _readBuffer = new NzReadBuffer(_stream);
            _protocolFaulted = false;
            _protocolRowNumber = 0;
            _currentProtocolRowNumber = 0;
        }
        else
        {
            throw new NetezzaException("Error in handshake");
        }

        _nzCommand = (NzCommand)CreateCommand();
        if (!ConnSendQuery())
        {
            _logger?.LogWarning("Error sending initial setup queries");
        }
        _commandNumber = 0;

        InTransaction = false;
        _state = ConnectionState.Open;
        CreatedAt = DateTime.UtcNow;
    }

    public override Task OpenAsync(CancellationToken cancellationToken)
    {
        return OpenAsync(ClientTypeId.SqlDotnet, cancellationToken);
    }

    public async Task OpenAsync(ClientTypeId clientVersionId = ClientTypeId.SqlDotnet, CancellationToken cancellationToken = default, bool useBufferedStream = false, bool setSocketBufferSizes = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_tmp_buffer.Length == 0)
        {
            _tmp_buffer = ArrayPool<byte>.Shared.Rent(4096);
        }
        _state = ConnectionState.Connecting;
        _stream = await InitializeAsync(_host, _port, useBufferedStream, setSocketBufferSizes, cancellationToken).ConfigureAwait(false);
        Handshake handShake = new(_socket, _stream, _host, _sslCerFilePath, _loggerFactory)
        {
            NPSCLIENT_TYPE_PYTHON = clientVersionId
        };
        Stream? response = await handShake.StartupAsync(_database, _securityLevel, _user, _password, _pgOptions, cancellationToken).ConfigureAwait(false);
        _backendKeyData = handShake.BackendKeyData;

        if (response is not null)
        {
            _stream = response;
            _readBuffer?.Dispose();
            _readBuffer = new NzReadBuffer(_stream);
            _protocolFaulted = false;
            _protocolRowNumber = 0;
            _currentProtocolRowNumber = 0;
        }
        else
        {
            throw new NetezzaException("Error in handshake");
        }

        _nzCommand = (NzCommand)CreateCommand();
        if (!await ConnSendQueryAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            _logger?.LogWarning("Error sending initial setup queries");
        }
        _commandNumber = 0;

        InTransaction = false;
        _state = ConnectionState.Open;
        CreatedAt = DateTime.UtcNow;
    }

}

public sealed class NetezzaException : DbException
{
    private static readonly IReadOnlyDictionary<char, string> EmptyDiagnostics =
        new System.Collections.ObjectModel.ReadOnlyDictionary<char, string>(new Dictionary<char, string>());

    public NetezzaException() : this(string.Empty) { }

    public NetezzaException(string msg) : base(msg)
    {
        RawResponse = msg;
        Diagnostics = EmptyDiagnostics;
    }

    public NetezzaException(string msg, Exception exception) : base(msg, exception)
    {
        RawResponse = msg;
        Diagnostics = EmptyDiagnostics;
    }

    public NetezzaException(Exception exception) : this(string.Empty, exception) { }

    internal NetezzaException(BackendDiagnosticResponse response) : base(response.Message)
    {
        RawResponse = response.RawResponse;
        Severity = response.Severity;
        SqlState = response.SqlState;
        Detail = response.Detail;
        Hint = response.Hint;
        Diagnostics = response.Diagnostics;
    }

    /// <summary>The severity reported by Netezza, when included in the response.</summary>
    public string? Severity { get; }

    /// <summary>The five-character SQLSTATE reported by Netezza, when included.</summary>
    public override string? SqlState { get; }

    /// <summary>Additional server detail, when included in the response.</summary>
    public string? Detail { get; }

    /// <summary>A server-provided hint, when included in the response.</summary>
    public string? Hint { get; }

    /// <summary>The complete decoded backend response, including its diagnostic fields.</summary>
    public string? RawResponse { get; }

    /// <summary>All diagnostic fields supplied by the backend, keyed by protocol field code.</summary>
    public IReadOnlyDictionary<char, string> Diagnostics { get; }
}
public sealed class InterfaceException : DbException
{
    public InterfaceException(): base() { }
    public InterfaceException( string msg) : base(msg) { }
    public InterfaceException(string msg, Exception exception) : base(msg, exception) { }
}


public sealed class AttributeException : DbException { }
//public class NotSupportedException : DbException { }
//public class ConnectionClosedException : DbException { }
//public class DatabaseException : DbException { }
//public class OperationalException : DbException { }
//public class IntegrityException : DbException { }
//public class InternalException : DbException { }
