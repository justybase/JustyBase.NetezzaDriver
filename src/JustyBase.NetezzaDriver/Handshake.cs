using JustyBase.NetezzaDriver.AbortQuery;
using JustyBase.NetezzaDriver.Utility;
using Microsoft.Extensions.Logging;
using System.Buffers;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace JustyBase.NetezzaDriver;

internal sealed class Handshake
{

    // CP Version
    //const short CP_VERSION_1 = 1;
    const short CP_VERSION_2 = 2;
    const short CP_VERSION_3 = 3;
    const short CP_VERSION_4 = 4;
    const short CP_VERSION_5 = 5;
    const short CP_VERSION_6 = 6;

    //  Handshake version
    //const short HSV2_INVALID_OPCODE = 0;
    const short HSV2_CLIENT_BEGIN = 1;
    const short HSV2_DB = 2;
    const short HSV2_USER = 3;
    const short HSV2_OPTIONS = 4;
    //const short HSV2_TTY = 5;
    const short HSV2_REMOTE_PID = 6;
    //const short HSV2_PRIOR_PID = 7;
    const short HSV2_CLIENT_TYPE = 8;
    const short HSV2_PROTOCOL = 9;
    //const short HSV2_HOSTCASE = 10;
    const short HSV2_SSL_NEGOTIATE = 11;
    const short HSV2_SSL_CONNECT = 12;
    const short HSV2_APPNAME = 13;
    const short HSV2_CLIENT_OS = 14;
    const short HSV2_CLIENT_HOST_NAME = 15;
    const short HSV2_CLIENT_OS_USER = 16;
    const short HSV2_64BIT_VARLENA_ENABLED = 17;
    const short HSV2_CLIENT_DONE = 1000;

    //  PG PROTOCOL
    const short PG_PROTOCOL_3 = 3;
    const short PG_PROTOCOL_4 = 4;
    const short PG_PROTOCOL_5 = 5;

    //  Authentication types
    const int AUTH_REQ_OK = 0;
    //const int AUTH_REQ_KRB4 = 1;
    const int AUTH_REQ_KRB5 = 2;
    const int AUTH_REQ_PASSWORD = 3;
    //const int AUTH_REQ_CRYPT = 4;
    const int AUTH_REQ_MD5 = 5;
    const int AUTH_REQ_SHA256 = 6;

    //  Client type
    //const short NPS_CLIENT = 0;
    const short IPS_CLIENT = 1;

    // Defensive bounds: a broken or hostile backend must not be able to keep
    // the handshake in an unbounded loop.
    private const int MaxHandshakeNegotiationAttempts = 8;
    private const int MaxConnectionCompleteMessages = 64;

    private readonly Socket _usocket;
    private Stream _stream; // NetworkStream or sslStream
    private readonly string _host = "";
    private readonly string? _sslCerFilePath;
    private readonly bool _rejectUnauthorized;
    private readonly bool _verifyHostname;
    private readonly ILogger? _logger = null!;
    private readonly string _guardiumClientOS;
    private readonly string _guardiumClientOSUser;
    private readonly string _guardiumClientHostName;
    private readonly string _guardiumAppName;
    private int _hsVersion = -1;
    private short _protocol1 = -1;
    private short _protocol2 = -1;
    private readonly byte[] _singleByteBuffer = new byte[1];
    private readonly byte[] _int16ScratchBuffer = new byte[2];
    private readonly byte[] _int32ScratchBuffer = new byte[4];
    private readonly byte[] _skipScratchBuffer = new byte[32];
    //private Dictionary<string, string>? _sslParams;

    public ClientTypeId NPSCLIENT_TYPE_PYTHON { get; init; } = ClientTypeId.SqlPython;
    public Handshake(
        Socket socket,
        Stream stream,
        string host,
        string? sslCerFilePath,
        ILoggerFactory? loggerFactory,
        bool rejectUnauthorized = true,
        bool verifyHostname = false)
    {
        _usocket = socket;
        _stream = stream;
        _host = host;
        _sslCerFilePath = sslCerFilePath;
        _rejectUnauthorized = rejectUnauthorized;
        _verifyHostname = verifyHostname;
        _logger = loggerFactory?.CreateLogger<Handshake>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _guardiumClientOS = "Windows";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            _guardiumClientOS = "Linux";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            _guardiumClientOS = "macOS";
        }
        else
        {
            _guardiumClientOS = "Unknown";
        }
        _guardiumClientOSUser = Environment.UserName;
        _guardiumClientHostName = System.Net.Dns.GetHostName();
        _guardiumAppName = Path.GetFileName(Environment.GetCommandLineArgs()[0]);
    }

    public Stream? Startup(string database, SecurityLevelCode securityLevel, string user, string password, object? pgOptions)
    {
        // Negotiate the handshake version (connection protocol)
        if (!ConnHandshakeNegotiate(_hsVersion, _protocol2))
        {
            _logger?.LogInformation("Handshake negotiation unsuccessful");
            return null;
        }

        _logger?.LogDebug("Sending handshake information to server");
        if (!ConnSendHandshakeInfo(database, securityLevel, _hsVersion, user, pgOptions))
        {
            _logger?.LogWarning("Error in ConnSendHandshakeInfo");
            return null;
        }

        if (!ConnAuthenticate(password))
        {
            _logger?.LogWarning("Error in ConnAuthenticate");
            return null;
        }

        if (!ConnConnectionComplete())
        {
            _logger?.LogWarning("Error in ConnConnectionComplete");
            return null;
        }

        return _stream;
    }

    public async Task<Stream?> StartupAsync(string database, SecurityLevelCode securityLevel, string user, string password, object? pgOptions, CancellationToken cancellationToken = default)
    {
        if (!await ConnHandshakeNegotiateAsync(cancellationToken).ConfigureAwait(false))
        {
            _logger?.LogInformation("Handshake negotiation unsuccessful");
            return null;
        }

        _logger?.LogDebug("Sending handshake information to server");
        if (!await ConnSendHandshakeInfoAsync(database, securityLevel, _hsVersion, user, pgOptions, cancellationToken).ConfigureAwait(false))
        {
            _logger?.LogWarning("Error in ConnSendHandshakeInfo");
            return null;
        }

        if (!await ConnAuthenticateAsync(password, cancellationToken).ConfigureAwait(false))
        {
            _logger?.LogWarning("Error in ConnAuthenticate");
            return null;
        }

        if (!await ConnConnectionCompleteAsync(cancellationToken).ConfigureAwait(false))
        {
            _logger?.LogWarning("Error in ConnConnectionComplete");
            return null;
        }

        return _stream;
    }

    private async Task<int> ReadByteAsync(CancellationToken cancellationToken = default)
    {
        int read = await _stream.ReadAsync(_singleByteBuffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        return read == 0 ? -1 : _singleByteBuffer[0];
    }

    private ValueTask<int> ReadInt32Async(CancellationToken cancellationToken = default)
        => PGUtil.ReadInt32Async(_stream, _int32ScratchBuffer, cancellationToken);

    private ValueTask WriteInt32Async(int value, CancellationToken cancellationToken = default)
        => PGUtil.WriteInt32Async(_stream, value, _int32ScratchBuffer, cancellationToken);

    private ValueTask WriteInt16Async(short value, CancellationToken cancellationToken = default)
        => PGUtil.WriteInt16Async(_stream, value, _int16ScratchBuffer, cancellationToken);

    private ValueTask Skip4BytesAsync(int num = 4, CancellationToken cancellationToken = default)
        => num <= _skipScratchBuffer.Length
            ? PGUtil.Skip4BytesAsync(_stream, _skipScratchBuffer, num, cancellationToken)
            : new ValueTask(PGUtil.Skip4BytesAsync(_stream, num, cancellationToken));

    private bool ConnAuthenticate(string password)
    {
        var beresp = _stream.ReadByte();
        _logger?.LogDebug("Got response: {Response}", beresp);

        if (beresp != (byte)BackendMessageCode.AuthenticationRequest)
        {
            _logger?.LogWarning("Authentication error");
            return false;
        }

        _logger?.LogDebug("auth got 'R' - request for password");
        int areq = PGUtil.ReadInt32(_stream);
        _logger?.LogDebug("areq = {Areq}", areq);

        switch (areq)
        {
            case AUTH_REQ_OK:
                _logger?.LogInformation("success");
                return true;

            case AUTH_REQ_PASSWORD:
                {
                    _logger?.LogInformation("Plain password requested");
                    byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
                    PGUtil.WriteInt32(_stream, passwordBytes.Length + 1 + 4);
                    _stream.Write(passwordBytes);
                    _stream.WriteByte((byte)0);
                    _stream.Flush();
                    return true;
                }

            case AUTH_REQ_MD5:
                {
                    _logger?.LogInformation("Password type is MD5");
                    byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
                    byte[] salt = new byte[2];
                    _stream.ReadExactly(salt, 0, 2);
                    var md5encoded = MD5.HashData(salt.Concat(passwordBytes).ToArray());
                    var md5pwd = Convert.ToBase64String(md5encoded);
                    var pwd = md5pwd.TrimEnd('=').ToArray();
                    _logger?.LogDebug("MD5 password response generated");

                    var pwdBytes = Encoding.UTF8.GetBytes(pwd);
                    // Int32 - Message length including self.
                    // String - The password.  Password may be encrypted.
                    PGUtil.WriteInt32(_stream, pwdBytes.Length + 1 + 4);
                    _stream.Write(pwdBytes);
                    _stream.WriteByte((byte)0);
                    _stream.Flush();
                    return true;
                }

            case AUTH_REQ_SHA256:
                {
                    _logger?.LogInformation("Password type is SHA-256");
                    byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
                    byte[] salt = new byte[2];
                    _stream.ReadExactly(salt, 0, 2);
                    var sha256encoded = SHA256.HashData(salt.Concat(passwordBytes).ToArray());
                    var sha256pwd = Convert.ToBase64String(sha256encoded);
                    var pwd = sha256pwd.TrimEnd('=').ToArray();
                    _logger?.LogDebug("SHA256 password response generated");

                    var pwdBytes = Encoding.UTF8.GetBytes(pwd);
                    // Int32 - Message length including self.
                    // String - The password.  Password may be encrypted.
                    PGUtil.WriteInt32(_stream, pwdBytes.Length + 1 + 4);
                    _stream.Write(pwdBytes);
                    _stream.WriteByte((byte)0);
                    _stream.Flush();
                    return true;
                }

            case AUTH_REQ_KRB5:
                throw new NetezzaException(
                    "Kerberos authentication was requested by the server, but this driver does not support it.");

            default:
                throw new NetezzaException(
                    $"Unsupported authentication method requested by the server (code {areq}).");
        }
    }

    private async Task<bool> ConnAuthenticateAsync(string password, CancellationToken cancellationToken = default)
    {
        var beresp = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
        _logger?.LogDebug("Got response: {Response}", beresp);

        if (beresp != (byte)BackendMessageCode.AuthenticationRequest)
        {
            _logger?.LogWarning("Authentication error");
            return false;
        }

        _logger?.LogDebug("auth got 'R' - request for password");
        int areq = await ReadInt32Async(cancellationToken).ConfigureAwait(false);
        _logger?.LogDebug("areq = {Areq}", areq);

        switch (areq)
        {
            case AUTH_REQ_OK:
                _logger?.LogInformation("success");
                return true;

            case AUTH_REQ_PASSWORD:
                {
                    _logger?.LogInformation("Plain password requested");
                    byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
                    await WriteInt32Async(passwordBytes.Length + 1 + 4, cancellationToken).ConfigureAwait(false);
                    await _stream.WriteAsync(passwordBytes.AsMemory(), cancellationToken).ConfigureAwait(false);
                    await _stream.WriteAsync(new byte[] { 0 }, cancellationToken).ConfigureAwait(false);
                    await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    return true;
                }

            case AUTH_REQ_MD5:
                {
                    _logger?.LogInformation("Password type is MD5");
                    byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
                    byte[] salt = new byte[2];
                    await _stream.ReadExactlyAsync(salt.AsMemory(0, 2), cancellationToken).ConfigureAwait(false);
                    var md5encoded = MD5.HashData(salt.Concat(passwordBytes).ToArray());
                    var md5pwd = Convert.ToBase64String(md5encoded);
                    var pwd = md5pwd.TrimEnd('=').ToArray();
                    _logger?.LogDebug("MD5 password response generated");

                    var pwdBytes = Encoding.UTF8.GetBytes(pwd);
                    await WriteInt32Async(pwdBytes.Length + 1 + 4, cancellationToken).ConfigureAwait(false);
                    await _stream.WriteAsync(pwdBytes.AsMemory(), cancellationToken).ConfigureAwait(false);
                    await _stream.WriteAsync(new byte[] { 0 }, cancellationToken).ConfigureAwait(false);
                    await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    return true;
                }

            case AUTH_REQ_SHA256:
                {
                    _logger?.LogInformation("Password type is SHA-256");
                    byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
                    byte[] salt = new byte[2];
                    await _stream.ReadExactlyAsync(salt.AsMemory(0, 2), cancellationToken).ConfigureAwait(false);
                    var sha256encoded = SHA256.HashData(salt.Concat(passwordBytes).ToArray());
                    var sha256pwd = Convert.ToBase64String(sha256encoded);
                    var pwd = sha256pwd.TrimEnd('=').ToArray();
                    _logger?.LogDebug("SHA256 password response generated");

                    var pwdBytes = Encoding.UTF8.GetBytes(pwd);
                    await WriteInt32Async(pwdBytes.Length + 1 + 4, cancellationToken).ConfigureAwait(false);
                    await _stream.WriteAsync(pwdBytes.AsMemory(), cancellationToken).ConfigureAwait(false);
                    await _stream.WriteAsync(new byte[] { 0 }, cancellationToken).ConfigureAwait(false);
                    await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    return true;
                }

            case AUTH_REQ_KRB5:
                throw new NetezzaException(
                    "Kerberos authentication was requested by the server, but this driver does not support it.");

            default:
                throw new NetezzaException(
                    $"Unsupported authentication method requested by the server (code {areq}).");
        }
    }



    private bool ConnHandshakeNegotiate(int? hsVersion, int? protocol2)
    {
        int version = CP_VERSION_6;
        _logger?.LogDebug("Latest-handshake version (conn-protocol) = {Version}", version);

        for (int attempt = 1; attempt <= MaxHandshakeNegotiationAttempts; attempt++)
        {
            _logger?.LogDebug("sending version: {Version}", version);

            PGUtil.WriteInt32(_stream, 2 + 2 + 4);
            PGUtil.WriteInt16(_stream, (short)HSV2_CLIENT_BEGIN);//2
            PGUtil.WriteInt16(_stream, (short)version);//2
            _stream.Flush();

            _logger?.LogInformation("sent handshake negotiation block successfully");

            var beresp = _stream.ReadByte();

            _logger?.LogDebug("Got response: {Response}", beresp);

            if (beresp == (byte)'N')
            {
                _hsVersion = version;
                _protocol2 = 0;
                return true;
            }
            else if (beresp == (byte)'M')
            {
                var newVersion = _stream.ReadByte();
                var negotiated = newVersion switch
                {
                    (byte)'2' => CP_VERSION_2,
                    (byte)'3' => CP_VERSION_3,
                    (byte)'4' => CP_VERSION_4,
                    (byte)'5' => CP_VERSION_5,
                    _ => -1,
                };

                if (negotiated < CP_VERSION_2 || negotiated >= version)
                {
                    _logger?.LogWarning(
                        "Server requested an invalid handshake version {Version}; current version is {Current}",
                        newVersion,
                        version);
                    return false;
                }

                version = negotiated;
            }
            else if (beresp == (byte)'E')
            {
                _logger?.LogWarning("Bad attribute value error");
                return false;
            }
            else
            {
                _logger?.LogWarning("Bad protocol error");
                return false;
            }
        }

        _logger?.LogWarning("Handshake negotiation did not converge after {Attempts} attempts", MaxHandshakeNegotiationAttempts);
        return false;
    }

    private async Task<bool> ConnHandshakeNegotiateAsync(CancellationToken cancellationToken = default)
    {
        int version = CP_VERSION_6;
        _logger?.LogDebug("Latest-handshake version (conn-protocol) = {Version}", version);

        for (int attempt = 1; attempt <= MaxHandshakeNegotiationAttempts; attempt++)
        {
            _logger?.LogDebug("sending version: {Version}", version);

            await WriteInt32Async(2 + 2 + 4, cancellationToken).ConfigureAwait(false);
            await WriteInt16Async(HSV2_CLIENT_BEGIN, cancellationToken).ConfigureAwait(false);
            await WriteInt16Async((short)version, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);

            _logger?.LogInformation("sent handshake negotiation block successfully");

            var beresp = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
            _logger?.LogDebug("Got response: {Response}", beresp);

            if (beresp == (byte)'N')
            {
                _hsVersion = version;
                _protocol2 = 0;
                return true;
            }
            else if (beresp == (byte)'M')
            {
                var newVersion = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
                var negotiated = newVersion switch
                {
                    (byte)'2' => CP_VERSION_2,
                    (byte)'3' => CP_VERSION_3,
                    (byte)'4' => CP_VERSION_4,
                    (byte)'5' => CP_VERSION_5,
                    _ => -1,
                };

                if (negotiated < CP_VERSION_2 || negotiated >= version)
                {
                    _logger?.LogWarning(
                        "Server requested an invalid handshake version {Version}; current version is {Current}",
                        newVersion,
                        version);
                    return false;
                }

                version = negotiated;
            }
            else if (beresp == (byte)'E')
            {
                _logger?.LogWarning("Bad attribute value error");
                return false;
            }
            else
            {
                _logger?.LogWarning("Bad protocol error");
                return false;
            }
        }

        _logger?.LogWarning("Handshake negotiation did not converge after {Attempts} attempts", MaxHandshakeNegotiationAttempts);
        return false;
    }



    private bool ConnSendHandshakeInfo(string database, SecurityLevelCode securityLevel, int? hsVersion, string user, object? pgOptions)
    {
        // We need database information at the backend in order to
        // select security restrictions. So always send the database first
        if (!ConnSendDatabase(database))
        {
            return false;
        }

        // If the backend supports security features and if the driver
        // requires secured session, negotiate security requirements now
        if (!ConnSecureSession(securityLevel))
        {
            return false;
        }

        if (!ConnSetNextDataProtocol(_protocol1, _protocol2))
        {
            return false;
        }

        if (hsVersion == CP_VERSION_6 || hsVersion == CP_VERSION_4)
        {
            return ConnSendHandshakeVersion4(hsVersion, user, pgOptions);
        }
        else if (hsVersion == CP_VERSION_5 || hsVersion == CP_VERSION_3 || hsVersion == CP_VERSION_2)
        {
            return ConnSendHandshakeVersion2(hsVersion, user, pgOptions);
        }

        return true;
    }

    private async Task<bool> ConnSendHandshakeInfoAsync(string database, SecurityLevelCode securityLevel, int? hsVersion, string user, object? pgOptions, CancellationToken cancellationToken = default)
    {
        if (!await ConnSendDatabaseAsync(database, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        if (!await ConnSecureSessionAsync(securityLevel, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        if (!ConnSetNextDataProtocol(_protocol1, _protocol2))
        {
            return false;
        }

        if (hsVersion == CP_VERSION_6 || hsVersion == CP_VERSION_4)
        {
            return await ConnSendHandshakeVersion4Async(hsVersion, user, pgOptions, cancellationToken).ConfigureAwait(false);
        }

        if (hsVersion == CP_VERSION_5 || hsVersion == CP_VERSION_3 || hsVersion == CP_VERSION_2)
        {
            return await ConnSendHandshakeVersion2Async(hsVersion, user, pgOptions, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }


    private bool ConnSendDatabase(string database)
    {
        if (database != null)
        {
            byte[]? db = Encoding.UTF8.GetBytes(database);
            _logger?.LogInformation("Database name: {Database}", Encoding.UTF8.GetString(db));
            PGUtil.WriteInt32(_stream, (int)(2 + db.Length + 1 + 4));
            PGUtil.WriteInt16(_stream, (short)HSV2_DB);//2
            _stream.Write(db, 0, db.Length); //db.Length
            _stream.WriteByte((byte)0); //1
            _stream.Flush();
        }

        var beresp = _stream.ReadByte();
        _logger?.LogInformation("Backend response: {beresp}", beresp);

        if (beresp == (byte)'N')
        {
            return true;
        }
        else if (beresp == (byte)BackendMessageCode.ErrorResponse)
        {
            _logger?.LogWarning("ERROR_AUTHOR_BAD");
            return false;
        }
        else
        {
            _logger?.LogWarning("Unknown response");
            return false;
        }
    }

    private async Task<bool> ConnSendDatabaseAsync(string database, CancellationToken cancellationToken = default)
    {
        if (database != null)
        {
            byte[] db = Encoding.UTF8.GetBytes(database);
            _logger?.LogInformation("Database name: {Database}", Encoding.UTF8.GetString(db));
            await WriteInt32Async(2 + db.Length + 1 + 4, cancellationToken).ConfigureAwait(false);
            await WriteInt16Async(HSV2_DB, cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync(db.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync(new byte[] { 0 }, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        var beresp = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
        _logger?.LogInformation("Backend response: {beresp}", beresp);

        if (beresp == (byte)'N')
        {
            return true;
        }
        else if (beresp == (byte)BackendMessageCode.ErrorResponse)
        {
            _logger?.LogWarning("ERROR_AUTHOR_BAD");
            return false;
        }
        else
        {
            _logger?.LogWarning("Unknown response");
            return false;
        }
    }

    private bool ConnSetNextDataProtocol(short? protocol1, short? protocol2)
    {
        if (_protocol2 == 0)
        {
            _protocol2 = PG_PROTOCOL_5;
        }
        else if (protocol2 == 5)
        {
            _protocol2 = PG_PROTOCOL_4;
        }
        else if (protocol2 == 4)
        {
            _protocol2 = PG_PROTOCOL_3;
        }
        else
        {
            return false;
        }

        _protocol1 = PG_PROTOCOL_3;
        _logger?.LogDebug("Connection protocol set to: {Protocol1} {Protocol2}", _protocol1, _protocol2);
        return true;
    }

    // ... other code ...

    private bool ConnSecureSession(SecurityLevelCode securityLevel)
    {
        short information = HSV2_SSL_NEGOTIATE;
        int currSecLevel = (int)securityLevel;
        bool tlsEstablished = false;

        while (information != 0)
        {
            short opcode = information;
            if (information == HSV2_SSL_NEGOTIATE)
            {
                _logger?.LogDebug("Security Level requested = {SecurityLevel}", securityLevel);
            }

            PGUtil.WriteInt32(_stream, 2 + 4 + 4);
            PGUtil.WriteInt16(_stream, (short)opcode); //2
            PGUtil.WriteInt32(_stream, currSecLevel);//4
            _stream.Flush();

            var beresp = _stream.ReadByte();
            _logger?.LogDebug("Got response = {Response}", beresp);

            if (beresp == (byte)'S')
            {
                // The backend sends 'S' only in 3 cases
                // Client requests strict SSL and backend supports it.
                // Client requests preferred SSL and backend supports it.
                // Client requests preferred non-SSL, but backend supports only secured sessions.
                if (securityLevel == SecurityLevelCode.OnlyUnsecuredSession)
                {
                    throw new NetezzaException(
                        "The server requires a TLS session, but SecurityLevelCode.OnlyUnsecuredSession was requested.");
                }

                PGUtil.WriteInt32(_stream, 2 + 4);
                PGUtil.WriteInt16(_stream, (short)HSV2_SSL_CONNECT); // 12

                var sslStream = new SslStream(_stream, false);
                sslStream.AuthenticateAsClient(CreateSslClientOptions());
                _stream = sslStream;
                _stream.Flush();
                tlsEstablished = true;
                beresp = _stream.ReadByte();
            }

            if (beresp == (byte)'N')
            {
                if (!tlsEstablished && securityLevel == SecurityLevelCode.OnlySecuredSession)
                {
                    throw new NetezzaException(
                        "The server refused a TLS session while SecurityLevelCode.OnlySecuredSession was requested.");
                }

                if (!tlsEstablished)
                {
                    _logger?.LogDebug("Attempting unsecured session");
                }

                information = 0;
                return true;
            }
            else if (beresp == (byte)'E')
            {
                throw new NetezzaException("The server rejected the TLS security negotiation.");
            }
            else
            {
                throw new NetezzaException($"Unexpected TLS security negotiation response 0x{beresp:X2}.");
            }
        }

        return true;
    }

    private async Task<bool> ConnSecureSessionAsync(SecurityLevelCode securityLevel, CancellationToken cancellationToken = default)
    {
        short information = HSV2_SSL_NEGOTIATE;
        int currSecLevel = (int)securityLevel;
        bool tlsEstablished = false;

        while (information != 0)
        {
            short opcode = information;
            if (information == HSV2_SSL_NEGOTIATE)
            {
                _logger?.LogDebug("Security Level requested = {SecurityLevel}", securityLevel);
            }

            await WriteInt32Async(2 + 4 + 4, cancellationToken).ConfigureAwait(false);
            await WriteInt16Async(opcode, cancellationToken).ConfigureAwait(false);
            await WriteInt32Async(currSecLevel, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);

            var beresp = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
            _logger?.LogDebug("Got response = {Response}", beresp);

            if (beresp == (byte)'S')
            {
                if (securityLevel == SecurityLevelCode.OnlyUnsecuredSession)
                {
                    throw new NetezzaException(
                        "The server requires a TLS session, but SecurityLevelCode.OnlyUnsecuredSession was requested.");
                }

                await WriteInt32Async(2 + 4, cancellationToken).ConfigureAwait(false);
                await WriteInt16Async(HSV2_SSL_CONNECT, cancellationToken).ConfigureAwait(false);

                var sslStream = new SslStream(_stream, false);
                await sslStream.AuthenticateAsClientAsync(CreateSslClientOptions(), cancellationToken).ConfigureAwait(false);
                _stream = sslStream;
                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                tlsEstablished = true;
                beresp = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
            }

            if (beresp == (byte)'N')
            {
                if (!tlsEstablished && securityLevel == SecurityLevelCode.OnlySecuredSession)
                {
                    throw new NetezzaException(
                        "The server refused a TLS session while SecurityLevelCode.OnlySecuredSession was requested.");
                }

                if (!tlsEstablished)
                {
                    _logger?.LogDebug("Attempting unsecured session");
                }

                information = 0;
                return true;
            }
            else if (beresp == (byte)'E')
            {
                throw new NetezzaException("The server rejected the TLS security negotiation.");
            }
            else
            {
                throw new NetezzaException($"Unexpected TLS security negotiation response 0x{beresp:X2}.");
            }
        }

        return true;
    }

    private SslClientAuthenticationOptions CreateSslClientOptions()
    {
        return new SslClientAuthenticationOptions
        {
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            AllowRenegotiation = false,
            TargetHost = _host.TrimStart('[').TrimEnd(']'),
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            RemoteCertificateValidationCallback = UserCertificateValidationCallback,
        };
    }

    private bool UserCertificateValidationCallback(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        if (!_rejectUnauthorized)
        {
            _logger?.LogWarning(
                "TLS certificate validation is disabled; accepting policy errors {SslPolicyErrors}.",
                sslPolicyErrors);
            return true;
        }

        if (certificate is null
            || (sslPolicyErrors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0)
        {
            _logger?.LogError("TLS peer did not provide a usable server certificate.");
            return false;
        }

        if (_verifyHostname
            && (sslPolicyErrors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
        {
            _logger?.LogError(
                "TLS server certificate does not match host '{Host}'.",
                _host);
            return false;
        }

        X509Certificate2Collection caCertificates = GetCaCertificates();
        if (caCertificates.Count > 0)
        {
            return ValidateWithCustomTrust(certificate, caCertificates);
        }

        if ((sslPolicyErrors & SslPolicyErrors.RemoteCertificateChainErrors) != 0)
        {
            _logger?.LogError(
                "TLS certificate chain validation failed with policy errors: {SslPolicyErrors}.",
                sslPolicyErrors);
            if (chain is not null)
            {
                foreach (X509ChainStatus item in chain.ChainStatus)
                {
                    _logger?.LogError("ValidateServerCertificate {Message}", item.StatusInformation);
                }
            }
            return false;
        }

        return true;
    }

    private bool ValidateWithCustomTrust(
        X509Certificate certificate,
        X509Certificate2Collection caCertificates)
    {
        using var serverCertificate = certificate as X509Certificate2
            ?? new X509Certificate2(certificate);
        using var customChain = new X509Chain();
        customChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        customChain.ChainPolicy.CustomTrustStore.AddRange(caCertificates);
        customChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        customChain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        // A custom chain bypasses SslStream's own application-policy check;
        // require the server-authentication EKU explicitly.
        customChain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));

        bool valid = customChain.Build(serverCertificate);
        if (!valid)
        {
            _logger?.LogError("TLS certificate is not trusted by the configured CA bundle.");
            foreach (X509ChainStatus item in customChain.ChainStatus)
            {
                _logger?.LogError("ValidateServerCertificate {Message}", item.StatusInformation);
            }
        }

        return valid;
    }

    private readonly object _caCertificateSync = new();
    private X509Certificate2Collection? _caCertificates;

    private X509Certificate2Collection GetCaCertificates()
    {
        if (string.IsNullOrWhiteSpace(_sslCerFilePath))
        {
            return [];
        }

        lock (_caCertificateSync)
        {
            if (_caCertificates is not null)
            {
                return _caCertificates;
            }

            var certificates = new X509Certificate2Collection();
            try
            {
                certificates.ImportFromPemFile(_sslCerFilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
            {
                throw new NetezzaException(
                    $"Unable to load the TLS CA certificate '{_sslCerFilePath}': {ex.Message}",
                    ex);
            }

            if (certificates.Count == 0)
            {
                throw new NetezzaException(
                    $"The TLS CA certificate file '{_sslCerFilePath}' does not contain a PEM certificate.");
            }

            _caCertificates = certificates;
            return _caCertificates;
        }
    }



    private bool ConnSendHandshakeVersion2(int? hsVersion, string user, object? pgOptions)
    {
        byte[] userBytes = Encoding.UTF8.GetBytes(user);
        short information = HSV2_USER;
        PGUtil.WriteInt32(_stream, (int)(2 + userBytes.Length + 1 + 4));
        PGUtil.WriteInt16(_stream, information);///2
        _stream.Write(userBytes); // userBytes.Length 
        _stream.WriteByte((byte)0); // 1
        _stream.Flush();
        information = HSV2_PROTOCOL;

        while (information != 0)
        {
            var beresp = _stream.ReadByte(); // pool
            _logger?.LogInformation("Backend response: {Response}", beresp);

            if (beresp == (byte)'N')
            {
                switch (information)
                {
                    case HSV2_PROTOCOL:
                        PGUtil.WriteInt32(_stream, 2 + 2 + 2 + 4);
                        PGUtil.WriteInt16(_stream, information);//2  9
                        PGUtil.WriteInt16(_stream, _protocol1);//2   3
                        PGUtil.WriteInt16(_stream, _protocol2);//2   5 
                        _stream.Flush();
                        information = HSV2_REMOTE_PID;
                        continue;
                    case HSV2_REMOTE_PID:
                        PGUtil.WriteInt32(_stream, 2 + 4 + 4);
                        PGUtil.WriteInt16(_stream, (information));//2 
                        PGUtil.WriteInt32(_stream, Environment.ProcessId);//4
                        _stream.Flush();
                        information = pgOptions is null ? HSV2_CLIENT_TYPE : HSV2_OPTIONS;
                        continue;
                    case HSV2_OPTIONS:
                        if (pgOptions != null)
                        {
                            // TODO !!
                            //var tmpBytes = Encoding.UTF8.GetBytes(pgOptions);
                            //PGUtil.WriteInt32_I(_sock, (4 + tmpBytes.Length + 1 + 4));
                            //PGUtil.WriteInt16_H(_sock, information);//2
                            //_sock.Write(tmpBytes); // tmpBytes.Length
                            //_sock.WriteByte((byte)0); // 1
                            //_sock.Flush();
                        }
                        information = HSV2_CLIENT_TYPE;
                        continue;
                    case HSV2_CLIENT_TYPE:
                        PGUtil.WriteInt32(_stream, 2 + 2 + 4);
                        PGUtil.WriteInt16(_stream, information);//2
                        PGUtil.WriteInt16(_stream, (short)NPSCLIENT_TYPE_PYTHON);
                        _stream.Flush();
                        if (hsVersion == CP_VERSION_5 || hsVersion == CP_VERSION_6)
                        {
                            information = HSV2_64BIT_VARLENA_ENABLED;
                        }
                        else
                        {
                            information = HSV2_CLIENT_DONE;
                        }
                        continue;
                    case HSV2_64BIT_VARLENA_ENABLED:
                        PGUtil.WriteInt32(_stream, 2 + 2 + 4);
                        PGUtil.WriteInt16(_stream, information);//2
                        PGUtil.WriteInt16(_stream, IPS_CLIENT);//2
                        _stream.Flush();
                        information = HSV2_CLIENT_DONE;
                        continue;
                    case HSV2_CLIENT_DONE:
                        PGUtil.WriteInt32(_stream, 2 + 4);
                        PGUtil.WriteInt16(_stream, information);//2
                        _stream.Flush();
                        return true;
                }
            }
            else if (beresp == (byte)BackendMessageCode.ErrorResponse)
            {
                _logger?.LogWarning("ERROR_CONN_FAIL");
                return false;
            }
        }
        return true;
    }

    private async Task<bool> ConnSendHandshakeVersion2Async(int? hsVersion, string user, object? pgOptions, CancellationToken cancellationToken = default)
    {
        byte[] userBytes = Encoding.UTF8.GetBytes(user);
        short information = HSV2_USER;
        await WriteInt32Async(2 + userBytes.Length + 1 + 4, cancellationToken).ConfigureAwait(false);
        await WriteInt16Async(information, cancellationToken).ConfigureAwait(false);
        await _stream.WriteAsync(userBytes.AsMemory(), cancellationToken).ConfigureAwait(false);
        await _stream.WriteAsync(new byte[] { 0 }, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        information = HSV2_PROTOCOL;

        while (information != 0)
        {
            var beresp = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
            _logger?.LogInformation("Backend response: {Response}", beresp);

            if (beresp == (byte)'N')
            {
                switch (information)
                {
                    case HSV2_PROTOCOL:
                        await WriteInt32Async(2 + 2 + 2 + 4, cancellationToken).ConfigureAwait(false);
                        await WriteInt16Async(information, cancellationToken).ConfigureAwait(false);
                        await WriteInt16Async(_protocol1, cancellationToken).ConfigureAwait(false);
                        await WriteInt16Async(_protocol2, cancellationToken).ConfigureAwait(false);
                        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                        information = HSV2_REMOTE_PID;
                        continue;
                    case HSV2_REMOTE_PID:
                        await WriteInt32Async(2 + 4 + 4, cancellationToken).ConfigureAwait(false);
                        await WriteInt16Async(information, cancellationToken).ConfigureAwait(false);
                        await WriteInt32Async(Environment.ProcessId, cancellationToken).ConfigureAwait(false);
                        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                        information = pgOptions is null ? HSV2_CLIENT_TYPE : HSV2_OPTIONS;
                        continue;
                    case HSV2_OPTIONS:
                        information = HSV2_CLIENT_TYPE;
                        continue;
                    case HSV2_CLIENT_TYPE:
                        await WriteInt32Async(2 + 2 + 4, cancellationToken).ConfigureAwait(false);
                        await WriteInt16Async(information, cancellationToken).ConfigureAwait(false);
                        await WriteInt16Async((short)NPSCLIENT_TYPE_PYTHON, cancellationToken).ConfigureAwait(false);
                        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                        if (hsVersion == CP_VERSION_5 || hsVersion == CP_VERSION_6)
                        {
                            information = HSV2_64BIT_VARLENA_ENABLED;
                        }
                        else
                        {
                            information = HSV2_CLIENT_DONE;
                        }
                        continue;
                    case HSV2_64BIT_VARLENA_ENABLED:
                        await WriteInt32Async(2 + 2 + 4, cancellationToken).ConfigureAwait(false);
                        await WriteInt16Async(information, cancellationToken).ConfigureAwait(false);
                        await WriteInt16Async(IPS_CLIENT, cancellationToken).ConfigureAwait(false);
                        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                        information = HSV2_CLIENT_DONE;
                        continue;
                    case HSV2_CLIENT_DONE:
                        await WriteInt32Async(2 + 4, cancellationToken).ConfigureAwait(false);
                        await WriteInt16Async(information, cancellationToken).ConfigureAwait(false);
                        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                        return true;
                }
            }
            else if (beresp == (byte)BackendMessageCode.ErrorResponse)
            {
                _logger?.LogWarning("ERROR_CONN_FAIL");
                return false;
            }
        }
        return true;
    }

    public bool ConnSendHandshakeVersion4(int? hsVersion, string user, object? pgOptions)
    {
        byte[] userBytes = Encoding.UTF8.GetBytes(user);
        short information = HSV2_USER;
        PGUtil.WriteInt32(_stream, (int)(2 + userBytes.Length + 1 + 4));
        PGUtil.WriteInt16(_stream, information);///2
        _stream.Write(userBytes); // userBytes.Length 
        _stream.WriteByte((byte)0); // 1
        _stream.Flush();
        information = HSV2_APPNAME; // !! vs v2 

        while (information != 0)
        {
            var beresp = _stream.ReadByte();
            _logger?.LogInformation("Backend response: {Response}", beresp);

            if (beresp == (byte)'N')
            {
                switch (information)
                {
                    case HSV2_APPNAME:
                        var _guardiumAppNameBytes = Encoding.UTF8.GetBytes(_guardiumAppName);
                        PGUtil.WriteInt32(_stream, (int)(2 + _guardiumAppNameBytes.Length + 1 + 4));
                        PGUtil.WriteInt16(_stream, information);//2
                        _stream.Write(_guardiumAppNameBytes);
                        _stream.WriteByte((byte)0);
                        _stream.Flush();

                        _logger?.LogDebug("Appname: {_guardiumAppName}", _guardiumAppName);
                        information = HSV2_CLIENT_OS;
                        continue;

                    case HSV2_CLIENT_OS:
                        var _guardiumClientOSBytes = Encoding.UTF8.GetBytes(_guardiumClientOS);
                        PGUtil.WriteInt32(_stream, (int)(2 + _guardiumClientOSBytes.Length + 1 + 4));
                        PGUtil.WriteInt16(_stream, information);//2
                        _stream.Write(_guardiumClientOSBytes);
                        _stream.WriteByte((byte)0);
                        _stream.Flush();
                        _logger?.LogDebug("Client OS: {guardium_clientOS}", _guardiumClientOS);
                        information = HSV2_CLIENT_HOST_NAME;
                        continue;

                    case HSV2_CLIENT_HOST_NAME:
                        var _guardiumClientHostNameBytes = Encoding.UTF8.GetBytes(_guardiumClientHostName);
                        PGUtil.WriteInt32(_stream, (int)(2 + _guardiumClientHostNameBytes.Length + 1 + 4));
                        PGUtil.WriteInt16(_stream, information);//2
                        _stream.Write(_guardiumClientHostNameBytes);
                        _stream.WriteByte((byte)0);
                        _stream.Flush();
                        _logger?.LogDebug("Client Host Name: {guardium_clientHostName}", _guardiumClientHostName);
                        information = HSV2_CLIENT_OS_USER;
                        continue;

                    case HSV2_CLIENT_OS_USER:
                        var _guardiumClientOSUserBytes = Encoding.UTF8.GetBytes(_guardiumClientOSUser);
                        PGUtil.WriteInt32(_stream, (int)(2 + _guardiumClientOSUserBytes.Length + 1 + 4));
                        PGUtil.WriteInt16(_stream, information);//2
                        _stream.Write(_guardiumClientOSUserBytes);
                        _stream.WriteByte((byte)0);
                        _stream.Flush();
                        _logger?.LogDebug("Client OS User: {guardium_clientOSUser}", _guardiumClientOSUser);
                        information = HSV2_PROTOCOL;
                        continue;

                    case HSV2_PROTOCOL:
                        PGUtil.WriteInt32(_stream, 2 + 2 + 2 + 4);
                        PGUtil.WriteInt16(_stream, information);//2
                        PGUtil.WriteInt16(_stream, _protocol1);//2
                        PGUtil.WriteInt16(_stream, _protocol2);//2
                        _stream.Flush();
                        information = HSV2_REMOTE_PID;
                        continue;
                    case HSV2_REMOTE_PID:
                        PGUtil.WriteInt32(_stream, 2 + 4 + 4);
                        PGUtil.WriteInt16(_stream, information);//2
                        PGUtil.WriteInt32(_stream, Environment.ProcessId);//4
                        _stream.Flush();
                        information = pgOptions is null ? HSV2_CLIENT_TYPE : HSV2_OPTIONS;
                        continue;
                    case HSV2_OPTIONS:
                        if (pgOptions != null)
                        {
                            // TODO !!
                            //var tmpBytes = Encoding.UTF8.GetBytes(pgOptions);
                            //PGUtil.WriteInt32_I(_sock, (4 + tmpBytes.Length + 1 + 4));
                            //PGUtil.WriteInt16_H(_sock, information);//2
                            //_sock.Write(tmpBytes); // tmpBytes.Length
                            //_sock.WriteByte((byte)0); // 1
                            //_sock.Flush();
                        }
                        information = HSV2_CLIENT_TYPE;
                        continue;
                    case HSV2_CLIENT_TYPE:
                        PGUtil.WriteInt32(_stream, 2 + 2 + 4);
                        PGUtil.WriteInt16(_stream, information);//2
                        PGUtil.WriteInt16(_stream, (short)NPSCLIENT_TYPE_PYTHON);//2
                        _stream.Flush();
                        if (hsVersion == CP_VERSION_5 || hsVersion == CP_VERSION_6)
                        {
                            information = HSV2_64BIT_VARLENA_ENABLED;
                        }
                        else
                        {
                            information = HSV2_CLIENT_DONE;
                        }
                        continue;
                    case HSV2_64BIT_VARLENA_ENABLED:
                        PGUtil.WriteInt32(_stream, 2 + 2 + 4);
                        PGUtil.WriteInt16(_stream, information);//2
                        PGUtil.WriteInt16(_stream, IPS_CLIENT);//2
                        _stream.Flush();
                        information = HSV2_CLIENT_DONE;
                        continue;

                    case HSV2_CLIENT_DONE:
                        PGUtil.WriteInt32(_stream, (int)(2 + 4));
                        PGUtil.WriteInt16(_stream, information);//2
                        _stream.Flush();
                        information = 0;
                        return true;
                }
            }
            else if (beresp == (byte)BackendMessageCode.ErrorResponse)
            {
                _logger?.LogWarning("ERROR_CONN_FAIL");
                return false;
            }
        }
        return false;
    }

    private async Task<bool> ConnSendHandshakeVersion4Async(int? hsVersion, string user, object? pgOptions, CancellationToken cancellationToken = default)
    {
        byte[] userBytes = Encoding.UTF8.GetBytes(user);
        short information = HSV2_USER;
        await WriteInt32Async(2 + userBytes.Length + 1 + 4, cancellationToken).ConfigureAwait(false);
        await WriteInt16Async(information, cancellationToken).ConfigureAwait(false);
        await _stream.WriteAsync(userBytes.AsMemory(), cancellationToken).ConfigureAwait(false);
        await _stream.WriteAsync(new byte[] { 0 }, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        information = HSV2_APPNAME;

        while (information != 0)
        {
            var beresp = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
            _logger?.LogInformation("Backend response: {Response}", beresp);

            if (beresp == (byte)'N')
            {
                switch (information)
                {
                    case HSV2_APPNAME:
                        var guardiumAppNameBytes = Encoding.UTF8.GetBytes(_guardiumAppName);
                        await WriteInt32Async(2 + guardiumAppNameBytes.Length + 1 + 4, cancellationToken).ConfigureAwait(false);
                        await WriteInt16Async(information, cancellationToken).ConfigureAwait(false);
                        await _stream.WriteAsync(guardiumAppNameBytes.AsMemory(), cancellationToken).ConfigureAwait(false);
                        await _stream.WriteAsync(new byte[] { 0 }, cancellationToken).ConfigureAwait(false);
                        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                        _logger?.LogDebug("Appname: {_guardiumAppName}", _guardiumAppName);
                        information = HSV2_CLIENT_OS;
                        continue;
                    case HSV2_CLIENT_OS:
                        var guardiumClientOSBytes = Encoding.UTF8.GetBytes(_guardiumClientOS);
                        await WriteInt32Async(2 + guardiumClientOSBytes.Length + 1 + 4, cancellationToken).ConfigureAwait(false);
                        await WriteInt16Async(information, cancellationToken).ConfigureAwait(false);
                        await _stream.WriteAsync(guardiumClientOSBytes.AsMemory(), cancellationToken).ConfigureAwait(false);
                        await _stream.WriteAsync(new byte[] { 0 }, cancellationToken).ConfigureAwait(false);
                        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                        _logger?.LogDebug("Client OS: {guardium_clientOS}", _guardiumClientOS);
                        information = HSV2_CLIENT_HOST_NAME;
                        continue;
                    case HSV2_CLIENT_HOST_NAME:
                        var guardiumClientHostNameBytes = Encoding.UTF8.GetBytes(_guardiumClientHostName);
                        await WriteInt32Async(2 + guardiumClientHostNameBytes.Length + 1 + 4, cancellationToken).ConfigureAwait(false);
                        await WriteInt16Async(information, cancellationToken).ConfigureAwait(false);
                        await _stream.WriteAsync(guardiumClientHostNameBytes.AsMemory(), cancellationToken).ConfigureAwait(false);
                        await _stream.WriteAsync(new byte[] { 0 }, cancellationToken).ConfigureAwait(false);
                        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                        _logger?.LogDebug("Client Host Name: {guardium_clientHostName}", _guardiumClientHostName);
                        information = HSV2_CLIENT_OS_USER;
                        continue;
                    case HSV2_CLIENT_OS_USER:
                        var guardiumClientOSUserBytes = Encoding.UTF8.GetBytes(_guardiumClientOSUser);
                        await WriteInt32Async(2 + guardiumClientOSUserBytes.Length + 1 + 4, cancellationToken).ConfigureAwait(false);
                        await WriteInt16Async(information, cancellationToken).ConfigureAwait(false);
                        await _stream.WriteAsync(guardiumClientOSUserBytes.AsMemory(), cancellationToken).ConfigureAwait(false);
                        await _stream.WriteAsync(new byte[] { 0 }, cancellationToken).ConfigureAwait(false);
                        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                        _logger?.LogDebug("Client OS User: {guardium_clientOSUser}", _guardiumClientOSUser);
                        information = HSV2_PROTOCOL;
                        continue;
                    case HSV2_PROTOCOL:
                        await WriteInt32Async(2 + 2 + 2 + 4, cancellationToken).ConfigureAwait(false);
                        await WriteInt16Async(information, cancellationToken).ConfigureAwait(false);
                        await WriteInt16Async(_protocol1, cancellationToken).ConfigureAwait(false);
                        await WriteInt16Async(_protocol2, cancellationToken).ConfigureAwait(false);
                        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                        information = HSV2_REMOTE_PID;
                        continue;
                    case HSV2_REMOTE_PID:
                        await WriteInt32Async(2 + 4 + 4, cancellationToken).ConfigureAwait(false);
                        await WriteInt16Async(information, cancellationToken).ConfigureAwait(false);
                        await WriteInt32Async(Environment.ProcessId, cancellationToken).ConfigureAwait(false);
                        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                        information = pgOptions is null ? HSV2_CLIENT_TYPE : HSV2_OPTIONS;
                        continue;
                    case HSV2_OPTIONS:
                        information = HSV2_CLIENT_TYPE;
                        continue;
                    case HSV2_CLIENT_TYPE:
                        await WriteInt32Async(2 + 2 + 4, cancellationToken).ConfigureAwait(false);
                        await WriteInt16Async(information, cancellationToken).ConfigureAwait(false);
                        await WriteInt16Async((short)NPSCLIENT_TYPE_PYTHON, cancellationToken).ConfigureAwait(false);
                        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                        if (hsVersion == CP_VERSION_5 || hsVersion == CP_VERSION_6)
                        {
                            information = HSV2_64BIT_VARLENA_ENABLED;
                        }
                        else
                        {
                            information = HSV2_CLIENT_DONE;
                        }
                        continue;
                    case HSV2_64BIT_VARLENA_ENABLED:
                        await WriteInt32Async(2 + 2 + 4, cancellationToken).ConfigureAwait(false);
                        await WriteInt16Async(information, cancellationToken).ConfigureAwait(false);
                        await WriteInt16Async(IPS_CLIENT, cancellationToken).ConfigureAwait(false);
                        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                        information = HSV2_CLIENT_DONE;
                        continue;

                    case HSV2_CLIENT_DONE:
                        await WriteInt32Async(2 + 4, cancellationToken).ConfigureAwait(false);
                        await WriteInt16Async(information, cancellationToken).ConfigureAwait(false);
                        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                        information = 0;
                        return true;
                }
            }
            else if (beresp == (byte)BackendMessageCode.ErrorResponse)
            {
                _logger?.LogWarning("ERROR_CONN_FAIL");
                return false;
            }
        }
        return false;
    }

    public BackendKeyDataMessage BackendKeyData { get; set; } = null!;
    private bool ConnConnectionComplete()
    {
        for (int messageCount = 1; messageCount <= MaxConnectionCompleteMessages; messageCount++)
        {
            var response = _stream.ReadByte();
            _logger?.LogInformation("backend response: {Response}", response);

            if (response < 0)
            {
                throw new NetezzaException("The server closed the connection during the handshake.");
            }

            if (!IsKnownConnectionCompleteMessage(response))
            {
                throw new NetezzaException(
                    $"Unexpected backend message 0x{response:X2} during the handshake.");
            }

            if (response != (byte)BackendMessageCode.AuthenticationRequest && response != (byte)BackendMessageCode.ErrorResponse)
            {
                PGUtil.Skip4Bytes(_stream);
                // do not use just ignore
                //int length = PGUtil.ReadInt32(_stream);
                PGUtil.Skip4Bytes(_stream);
            }

            if (response == (byte)BackendMessageCode.AuthenticationRequest)
            {
                int areq = PGUtil.ReadInt32(_stream);
                _logger?.LogInformation("backend response: {Areq}", areq);
            }

            if (response == (byte)BackendMessageCode.NoticeResponse)
            {
                int length = ProtocolLengthValidator.Validate(
                    PGUtil.ReadInt32(_stream),
                    "handshake.noticePayloadLength");

                byte[] bytes = new byte[length];
                _stream.ReadExactly(bytes, 0, length);
                string notices = Encoding.UTF8.GetString(bytes);
                _logger?.LogDebug("Response received from backend: {Notices}", notices);
            }

            if (response == (byte)BackendMessageCode.BackendKeyData)
            {
                BackendKeyData = new BackendKeyDataMessage(_stream);
                _logger?.LogDebug("Backend response PID: {Pid}", BackendKeyData.BackendProcessId);
                _logger?.LogDebug("Backend response KEY received");
            }

            if (response == (byte)BackendMessageCode.ReadyForQuery)
            {
                _logger?.LogInformation("Authentication Successful");
                return true;
            }

            if (response == (byte)BackendMessageCode.ErrorResponse)
            {
                byte[] bytes = ArrayPool<byte>.Shared.Rent(1024);
                int readed = _stream.Read(bytes, 0, 1024);
                string error = Encoding.UTF8.GetString(bytes, 0, readed);
                ArrayPool<byte>.Shared.Return(bytes);
                _logger?.LogWarning("Error occurred, server response: {Error}", error);
                throw new NetezzaException(error);
                //return false;
            }
        }

        throw new NetezzaException(
            $"The handshake did not reach ReadyForQuery after {MaxConnectionCompleteMessages} messages.");
    }

    private async Task<bool> ConnConnectionCompleteAsync(CancellationToken cancellationToken = default)
    {
        for (int messageCount = 1; messageCount <= MaxConnectionCompleteMessages; messageCount++)
        {
            var response = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
            _logger?.LogInformation("backend response: {Response}", response);

            if (response < 0)
            {
                throw new NetezzaException("The server closed the connection during the handshake.");
            }

            if (!IsKnownConnectionCompleteMessage(response))
            {
                throw new NetezzaException(
                    $"Unexpected backend message 0x{response:X2} during the handshake.");
            }

            if (response != (byte)BackendMessageCode.AuthenticationRequest && response != (byte)BackendMessageCode.ErrorResponse)
            {
                await Skip4BytesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                await Skip4BytesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            if (response == (byte)BackendMessageCode.AuthenticationRequest)
            {
                int areq = await ReadInt32Async(cancellationToken).ConfigureAwait(false);
                _logger?.LogInformation("backend response: {Areq}", areq);
            }

            if (response == (byte)BackendMessageCode.NoticeResponse)
            {
                int length = ProtocolLengthValidator.Validate(
                    await ReadInt32Async(cancellationToken).ConfigureAwait(false),
                    "handshake.noticePayloadLength");

                byte[] bytes = new byte[length];
                await _stream.ReadExactlyAsync(bytes.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                string notices = Encoding.UTF8.GetString(bytes);
                _logger?.LogDebug("Response received from backend: {Notices}", notices);
            }

            if (response == (byte)BackendMessageCode.BackendKeyData)
            {
                BackendKeyData = await BackendKeyDataMessage.CreateAsync(_stream, cancellationToken).ConfigureAwait(false);
                _logger?.LogDebug("Backend response PID: {Pid}", BackendKeyData.BackendProcessId);
                _logger?.LogDebug("Backend response KEY received");
            }

            if (response == (byte)BackendMessageCode.ReadyForQuery)
            {
                _logger?.LogInformation("Authentication Successful");
                return true;
            }

            if (response == (byte)BackendMessageCode.ErrorResponse)
            {
                byte[] bytes = ArrayPool<byte>.Shared.Rent(1024);
                try
                {
                    int readed = await _stream.ReadAsync(bytes.AsMemory(0, 1024), cancellationToken).ConfigureAwait(false);
                    string error = Encoding.UTF8.GetString(bytes, 0, readed);
                    _logger?.LogWarning("Error occurred, server response: {Error}", error);
                    throw new NetezzaException(error);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(bytes);
                }
            }
        }

        throw new NetezzaException(
            $"The handshake did not reach ReadyForQuery after {MaxConnectionCompleteMessages} messages.");
    }

    private static bool IsKnownConnectionCompleteMessage(int response)
        => response == (byte)BackendMessageCode.AuthenticationRequest
            || response == (byte)BackendMessageCode.ErrorResponse
            || response == (byte)BackendMessageCode.NoticeResponse
            || response == (byte)BackendMessageCode.BackendKeyData
            || response == (byte)BackendMessageCode.ReadyForQuery
            || response == (byte)BackendMessageCode.ParameterStatus;

}

