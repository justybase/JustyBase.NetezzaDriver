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

namespace JustyBase.NetezzaDriver;

public sealed partial class NzConnection : DbConnection
{
    private string? _error;
    private NetezzaException? _backendException;

    private int _commandNumber = -1;

    public bool InTransaction { get; set; } = false;//TODO
    public bool AutoCommit { get; set; } = true;

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
    private string _user;
    private string _password;
    private string _host;
    private int _port;
    private bool _bulkInsertEnabled = true;

    public DateTime CreatedAt { get; internal set; } = DateTime.UtcNow;

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

    /// <summary>
    /// Creates an unconfigured connection. Intended for
    /// <see cref="DbProviderFactory.CreateConnection"/>; assign
    /// <see cref="ConnectionString"/> before opening.
    /// </summary>
    public NzConnection()
    {
        _database = string.Empty;
        _user = string.Empty;
        _password = string.Empty;
        _host = string.Empty;
        _port = 5480;
        _tmp_buffer = ArrayPool<byte>.Shared.Rent(4096);
    }

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
        _bulkInsertEnabled = parameters.BulkInsert;
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
        _bulkInsertEnabled = parameters.BulkInsert;
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
    internal static (string User, string Password, string Host, string? Database, int? Port, int? Timeout, bool Pooling, int MinPoolSize, int MaxPoolSize, int ConnectionIdleTimeout, int ConnectionLifetime, int ConnectionValidationInterval, bool BulkInsert) ParseConnectionString(string connectionString)
    {
        NzConnectionStringBuilder builder;
        try
        {
            builder = new NzConnectionStringBuilder(connectionString);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            throw new NetezzaException($"Invalid connection string: {ex.Message}", ex);
        }

        if (!builder.ContainsKey("User") && !builder.ContainsKey("Username"))
            throw new NetezzaException("Username is required in connection string");

        if (!builder.ContainsKey("Password") && !builder.ContainsKey("Pwd"))
            throw new NetezzaException("Password is required in connection string");

        if (!builder.ContainsKey("Host") && !builder.ContainsKey("Server"))
            throw new NetezzaException("Host is required in connection string");

        string? database = builder.TryGetValue("Database", out var rawDatabase)
            ? Convert.ToString(rawDatabase, CultureInfo.InvariantCulture)
            : null;

        int? port = null;
        if (builder.TryGetValue("Port", out var rawPort)
            && int.TryParse(
                Convert.ToString(rawPort, CultureInfo.InvariantCulture),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsedPort))
        {
            port = parsedPort;
        }

        int? timeout = null;
        if (builder.TryGetValue("Timeout", out var rawTimeout)
            && int.TryParse(
                Convert.ToString(rawTimeout, CultureInfo.InvariantCulture),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsedTimeout))
        {
            timeout = parsedTimeout;
        }
        bool pooling = !builder.TryGetValue("Pooling", out _) || builder.Pooling;
        int minPoolSize = builder.TryGetValue("MinPoolSize", out _) ? builder.MinPoolSize : 0;
        int maxPoolSize = builder.TryGetValue("MaxPoolSize", out _) ? builder.MaxPoolSize : 10;
        int connectionIdleTimeout = builder.TryGetValue("ConnectionIdleTimeout", out _) ? builder.ConnectionIdleTimeout : 30;
        int connectionLifetime = builder.TryGetValue("ConnectionLifetime", out _) ? builder.ConnectionLifetime : 0;
        int connectionValidationInterval = builder.TryGetValue("ConnectionValidationInterval", out _)
            ? builder.ConnectionValidationInterval
            : NzConnectionStringBuilder.DefaultConnectionValidationInterval;
        bool bulkInsert = !builder.TryGetValue("BulkInsert", out _) || builder.BulkInsert;

        return (
            builder.UserName,
            builder.Password,
            builder.Host,
            database,
            port,
            timeout,
            pooling,
            minPoolSize,
            maxPoolSize,
            connectionIdleTimeout,
            connectionLifetime,
            connectionValidationInterval,
            bulkInsert);
    }


    public TimeSpan ConnectionTimeoutDuration { get; set; } = TimeSpan.FromSeconds(15);

    public override int ConnectionTimeout => (int)ConnectionTimeoutDuration.TotalSeconds;


    private const int _bufferSize = 65536; // 64 KB

    /// <summary>
    /// Size of the application-level read buffer created on open. The 8-64 KB
    /// sweep did not establish a robust throughput difference on the available
    /// replay fixtures, while the smaller sizes increase use of the oversize
    /// payload path for large rows; the default therefore stays at 64 KB.
    /// Internal so the trade-off can be measured without a public tuning knob.
    /// </summary>
    internal int ReadBufferSize { get; set; } = _bufferSize;

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
    private static readonly Lock _cancelLock = new();
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
            // The cancel acknowledgement is best effort: never let a silent
            // backend block the caller for the whole connection timeout.
            socketX.ReceiveTimeout = ProtocolSyncTimeoutMs;
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
    // Separate from _disposed because Close() is reusable. Set before tearing
    // down protocol resources and cleared only after a new read buffer exists.
    private int _protocolReadUnavailable = 1;
    private bool _protocolFaulted;
    private long _protocolRowNumber;
    private long _currentProtocolRowNumber;
    /// <summary>
    /// Number of row payloads skipped via the fast discard path (no decode).
    /// Used by benchmarks to quantify early-dispose savings.
    /// </summary>
    internal long DiscardedRows;
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
        Interlocked.Exchange(ref _protocolReadUnavailable, 1);
        ReleaseLazyOversize();
        ReleaseScratchBuffers();
        _lazyRowActive = false;
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
        Interlocked.Exchange(ref _protocolReadUnavailable, 1);
        ReleaseLazyOversize();
        ReleaseScratchBuffers();
        _lazyRowActive = false;
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

    /// <summary>
    /// Creates a bulk-copy helper for inserting rows into a Netezza table.
    /// </summary>
    public NzBulkCopy CreateBulkCopy() => new(this);

    internal bool ExecuteBulkCopy(NzCommand command, string sql, BulkInsertData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (_pendingBulkData is not null)
            throw new InvalidOperationException("A bulk transfer is already active on this connection.");

        _pendingBulkData = data;
        try
        {
            return Execute(command, sql);
        }
        finally
        {
            _pendingBulkData = null;
        }
    }

    internal async Task<bool> ExecuteBulkCopyAsync(
        NzCommand command,
        string sql,
        BulkInsertData data,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (_pendingBulkData is not null)
            throw new InvalidOperationException("A bulk transfer is already active on this connection.");

        _pendingBulkData = data;
        try
        {
            return await ExecuteAsync(command, sql, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pendingBulkData = null;
        }
    }

    public override void Open()
    {
        Open(ClientTypeId.SqlDotnet);
    }
    public void Open(ClientTypeId clientVersionId = ClientTypeId.SqlDotnet, bool useBufferedStream = false, bool setSocketBufferSizes = false)
    {
        if (_state != ConnectionState.Closed)
        {
            throw new InvalidOperationException("The connection is already open.");
        }

        try
        {
            OpenCore(clientVersionId, useBufferedStream, setSocketBufferSizes);
        }
        catch
        {
            try { Close(); } catch { /* preserve the original failure */ }
            throw;
        }
    }

    private void OpenCore(ClientTypeId clientVersionId, bool useBufferedStream, bool setSocketBufferSizes)
    {
        Volatile.Write(ref _protocolReadUnavailable, 1);
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
            _readBuffer = new NzReadBuffer(_stream, ReadBufferSize);
            Volatile.Write(ref _protocolReadUnavailable, 0);
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
        if (_state != ConnectionState.Closed)
        {
            throw new InvalidOperationException("The connection is already open.");
        }

        try
        {
            await OpenCoreAsync(clientVersionId, cancellationToken, useBufferedStream, setSocketBufferSizes)
                .ConfigureAwait(false);
        }
        catch
        {
            try { await CloseAsync().ConfigureAwait(false); } catch { /* preserve the original failure */ }
            throw;
        }
    }

    private async Task OpenCoreAsync(ClientTypeId clientVersionId, CancellationToken cancellationToken, bool useBufferedStream, bool setSocketBufferSizes)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Volatile.Write(ref _protocolReadUnavailable, 1);
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
            _readBuffer = new NzReadBuffer(_stream, ReadBufferSize);
            Volatile.Write(ref _protocolReadUnavailable, 0);
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
