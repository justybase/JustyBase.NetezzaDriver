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

    private readonly Socket _usocket;
    private Stream _stream; // NetworkStream or sslStream
    private readonly string _host = "";
    private readonly string? _sslCerFilePath;
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
    public Handshake(Socket socket, Stream stream, string host,string? sslCerFilePath,  ILoggerFactory? loggerFactory)
    {
        _usocket = socket;
        _stream = stream;
        _host = host;
        _sslCerFilePath = sslCerFilePath;
        _logger = loggerFactory?.CreateLogger<Handshake>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _guardiumClientOS = "Windows";
        }
        else if(RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
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
        byte[] passwordBytes = Encoding.UTF8.GetBytes(password);

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

        if (areq == AUTH_REQ_OK)
        {
            _logger?.LogInformation("success");
            return true;
        }

        if (areq == AUTH_REQ_PASSWORD)
        {
            _logger?.LogInformation("Plain password requested");
            PGUtil.WriteInt32(_stream, (int)(passwordBytes.Length + 1 + 4));
            _stream.Write(passwordBytes);
            _stream.WriteByte((byte)0);
            _stream.Flush();
        }

        if (areq == AUTH_REQ_MD5)
        {
            _logger?.LogInformation("Password type is MD5");
            byte[] salt = new byte[2];
            _stream.ReadExactly(salt, 0, 2);
            _logger?.LogDebug("Salt = {Salt}", Encoding.UTF8.GetString(salt));
            if (passwordBytes == null)
            {
                throw new InterfaceException("server requesting MD5 password authentication, but no password was provided");
            }
            var md5encoded = MD5.HashData(salt.Concat(passwordBytes).ToArray());
            var md5pwd = Convert.ToBase64String(md5encoded);
            var pwd = md5pwd.TrimEnd('=').ToArray();
            _logger?.LogDebug("md5 encrypted password is = {Pwd}", new string(pwd));

            var pwdBytes = Encoding.UTF8.GetBytes(pwd);
            // Int32 - Message length including self.
            // String - The password.  Password may be encrypted.
            PGUtil.WriteInt32(_stream, (int)(pwdBytes.Length + 1 + 4));
            _stream.Write(pwdBytes);
            _stream.WriteByte((byte)0);
            _stream.Flush();
        }

        if (areq == AUTH_REQ_SHA256)
        {
            _logger?.LogInformation("Password type is SSH");
            byte[] salt = new byte[2];
            _stream.ReadExactly(salt, 0, 2);
            _logger?.LogDebug("Salt = {Salt}", Encoding.UTF8.GetString(salt));
            if (passwordBytes == null)
            {
                throw new InterfaceException("server requesting MD5 password authentication, but no password was provided");
            }
            var sha256encoded = SHA256.HashData(salt.Concat(passwordBytes).ToArray());
            var sha256pwd = Convert.ToBase64String(sha256encoded);
            var pwd = sha256pwd.TrimEnd('=').ToArray();
            _logger?.LogDebug("sha256 encrypted password is = {Pwd}", new string(pwd));

            var pwdBytes = Encoding.UTF8.GetBytes(pwd);
            // Int32 - Message length including
            // String - The password.  Password may be encrypted.
            PGUtil.WriteInt32(_stream, (int)(pwdBytes.Length + 1 + 4));
            _stream.Write(pwdBytes);
            _stream.WriteByte((byte)0);
        }

        if (areq == AUTH_REQ_KRB5)
        {
            _logger?.LogInformation("krb encryption requested from backend");
        }

        return true;
    }

    private async Task<bool> ConnAuthenticateAsync(string password, CancellationToken cancellationToken = default)
    {
        byte[] passwordBytes = Encoding.UTF8.GetBytes(password);

        var beresp = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
        _logger?.LogDebug("Got response: {Response}", beresp);

        if (beresp != (byte)BackendMessageCode.AuthenticationRequest)
        {
            _logger?.LogWarning("Authentication error");
            return false;
        }

        _logger?.LogDebug("auth got 'R' - request for password");
        int areq = await ReadInt32Async( cancellationToken).ConfigureAwait(false);
        _logger?.LogDebug("areq = {Areq}", areq);

        if (areq == AUTH_REQ_OK)
        {
            _logger?.LogInformation("success");
            return true;
        }

        if (areq == AUTH_REQ_PASSWORD)
        {
            _logger?.LogInformation("Plain password requested");
            await WriteInt32Async( passwordBytes.Length + 1 + 4, cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync(passwordBytes.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync(new byte[] { 0 }, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        if (areq == AUTH_REQ_MD5)
        {
            _logger?.LogInformation("Password type is MD5");
            byte[] salt = new byte[2];
            await _stream.ReadExactlyAsync(salt.AsMemory(0, 2), cancellationToken).ConfigureAwait(false);
            _logger?.LogDebug("Salt = {Salt}", Encoding.UTF8.GetString(salt));
            var md5encoded = MD5.HashData(salt.Concat(passwordBytes).ToArray());
            var md5pwd = Convert.ToBase64String(md5encoded);
            var pwd = md5pwd.TrimEnd('=').ToArray();
            _logger?.LogDebug("md5 encrypted password is = {Pwd}", new string(pwd));

            var pwdBytes = Encoding.UTF8.GetBytes(pwd);
            await WriteInt32Async( pwdBytes.Length + 1 + 4, cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync(pwdBytes.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync(new byte[] { 0 }, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        if (areq == AUTH_REQ_SHA256)
        {
            _logger?.LogInformation("Password type is SSH");
            byte[] salt = new byte[2];
            await _stream.ReadExactlyAsync(salt.AsMemory(0, 2), cancellationToken).ConfigureAwait(false);
            _logger?.LogDebug("Salt = {Salt}", Encoding.UTF8.GetString(salt));
            var sha256encoded = SHA256.HashData(salt.Concat(passwordBytes).ToArray());
            var sha256pwd = Convert.ToBase64String(sha256encoded);
            var pwd = sha256pwd.TrimEnd('=').ToArray();
            _logger?.LogDebug("sha256 encrypted password is = {Pwd}", new string(pwd));

            var pwdBytes = Encoding.UTF8.GetBytes(pwd);
            await WriteInt32Async( pwdBytes.Length + 1 + 4, cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync(pwdBytes.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync(new byte[] { 0 }, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        if (areq == AUTH_REQ_KRB5)
        {
            _logger?.LogInformation("krb encryption requested from backend");
        }

        return true;
    }



    private bool ConnHandshakeNegotiate(int? hsVersion,int? protocol2)
    {
        int version = CP_VERSION_6;
        _logger?.LogDebug("Latest-handshake version (conn-protocol) = {Version}", version);

        while (true)
        {
            if (version == CP_VERSION_6) version = CP_VERSION_6;
            if (version == CP_VERSION_5) version = CP_VERSION_5;
            if (version == CP_VERSION_4) version = CP_VERSION_4;
            if (version == CP_VERSION_3) version = CP_VERSION_3;
            if (version == CP_VERSION_2) version = CP_VERSION_2;

            _logger?.LogDebug("sending version: {Version}", version);

            PGUtil.WriteInt32(_stream, 2+2 + 4);
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
                if (newVersion == (byte)'2') version = CP_VERSION_2;
                if (newVersion == (byte)'3') version = CP_VERSION_3;
                if (newVersion == (byte)'4') version = CP_VERSION_4;
                if (newVersion == (byte)'5') version = CP_VERSION_5;
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
    }

    private async Task<bool> ConnHandshakeNegotiateAsync(CancellationToken cancellationToken = default)
    {
        int version = CP_VERSION_6;
        _logger?.LogDebug("Latest-handshake version (conn-protocol) = {Version}", version);

        while (true)
        {
            if (version == CP_VERSION_6) version = CP_VERSION_6;
            if (version == CP_VERSION_5) version = CP_VERSION_5;
            if (version == CP_VERSION_4) version = CP_VERSION_4;
            if (version == CP_VERSION_3) version = CP_VERSION_3;
            if (version == CP_VERSION_2) version = CP_VERSION_2;

            _logger?.LogDebug("sending version: {Version}", version);

            await WriteInt32Async( 2 + 2 + 4, cancellationToken).ConfigureAwait(false);
            await WriteInt16Async( HSV2_CLIENT_BEGIN, cancellationToken).ConfigureAwait(false);
            await WriteInt16Async( (short)version, cancellationToken).ConfigureAwait(false);
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
                if (newVersion == (byte)'2') version = CP_VERSION_2;
                if (newVersion == (byte)'3') version = CP_VERSION_3;
                if (newVersion == (byte)'4') version = CP_VERSION_4;
                if (newVersion == (byte)'5') version = CP_VERSION_5;
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
    }



    private bool ConnSendHandshakeInfo(string database, SecurityLevelCode securityLevel,int? hsVersion,string user,object? pgOptions)
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
            await WriteInt32Async( 2 + db.Length + 1 + 4, cancellationToken).ConfigureAwait(false);
            await WriteInt16Async( HSV2_DB, cancellationToken).ConfigureAwait(false);
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

        while (information != 0)
        {
            short opcode = information;
            if (information == HSV2_SSL_NEGOTIATE)
            {
                _logger?.LogDebug("Security Level requested = {SecurityLevel}", securityLevel);
            }

            PGUtil.WriteInt32(_stream, 2 + 4 + 4);
            PGUtil.WriteInt16(_stream, (short)opcode); //2
            PGUtil.WriteInt32(_stream, (int)currSecLevel);//4
            _stream.Flush();

            if (information == HSV2_SSL_CONNECT)
            {
                try
                {
                    var sslStream = new SslStream(new NetworkStream(_usocket));
                    sslStream.AuthenticateAsClient(_usocket!.RemoteEndPoint!.ToString()!);

                    _stream = sslStream;
                    _logger?.LogInformation("Secured Connect Success");
                }
                catch (AuthenticationException)
                {
                    _logger?.LogWarning("Problem establishing secured session");
                    return false;
                }
            }

            if (information != 0)
            {
                var beresp = _stream.ReadByte();
                _logger?.LogDebug("Got response = {Response}", beresp);

                if (beresp == (byte)'S')
                {
                    // The backend sends 'S' only in 3 cases
                    // Client requests strict SSL and backend supports it.
                    // Client requests preferred SSL and backend supports it.
                    // Client requests preferred non-SSL, but backend supports only secured sessions.


                    PGUtil.WriteInt32(_stream, 2 + 4);
                    PGUtil.WriteInt16(_stream, (short)HSV2_SSL_CONNECT); // 12


                    var sslStream = new SslStream(_stream, false);
                    if (_sslCerFilePath is null)
                    {
                        throw new NetezzaException("_sslCerFilePath hould not be empty");
                    }

                    var certFromPem = X509Certificate2.CreateFromPem(System.IO.File.ReadAllText(_sslCerFilePath));
                    sslStream.AuthenticateAsClient(new SslClientAuthenticationOptions()
                    {
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                        AllowRenegotiation = false,
                        TargetHost = _host,
                        ClientCertificates = [(X509Certificate)certFromPem],
                        EnabledSslProtocols = SslProtocols.Tls12,
                        RemoteCertificateValidationCallback = UserCertificateValidationCallback,
                        LocalCertificateSelectionCallback = UserCertificateSelectionCallback
                    }); 
                    _stream = sslStream;
                    _stream.Flush();
                    beresp = _stream.ReadByte();
                }
                if (beresp == (byte)'N')
                {
                    if (information == HSV2_SSL_NEGOTIATE)
                    {
                        _logger?.LogDebug("Attempting unsecured session");
                    }
                    information = 0;
                    return true;
                }
                else if (beresp == (byte)'E')
                {
                    _logger?.LogWarning("Error: connection failed");
                    return false;
                }
            }
        }

        return true;
    }

    private async Task<bool> ConnSecureSessionAsync(SecurityLevelCode securityLevel, CancellationToken cancellationToken = default)
    {
        short information = HSV2_SSL_NEGOTIATE;
        int currSecLevel = (int)securityLevel;

        while (information != 0)
        {
            short opcode = information;
            if (information == HSV2_SSL_NEGOTIATE)
            {
                _logger?.LogDebug("Security Level requested = {SecurityLevel}", securityLevel);
            }

            await WriteInt32Async( 2 + 4 + 4, cancellationToken).ConfigureAwait(false);
            await WriteInt16Async( opcode, cancellationToken).ConfigureAwait(false);
            await WriteInt32Async( currSecLevel, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);

            if (information == HSV2_SSL_CONNECT)
            {
                try
                {
                    var sslStream = new SslStream(new NetworkStream(_usocket));
                    await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = _usocket!.RemoteEndPoint!.ToString()!
                    }, cancellationToken).ConfigureAwait(false);

                    _stream = sslStream;
                    _logger?.LogInformation("Secured Connect Success");
                }
                catch (AuthenticationException)
                {
                    _logger?.LogWarning("Problem establishing secured session");
                    return false;
                }
            }

            if (information != 0)
            {
                var beresp = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
                _logger?.LogDebug("Got response = {Response}", beresp);

                if (beresp == (byte)'S')
                {
                    await WriteInt32Async( 2 + 4, cancellationToken).ConfigureAwait(false);
                    await WriteInt16Async( HSV2_SSL_CONNECT, cancellationToken).ConfigureAwait(false);

                    var sslStream = new SslStream(_stream, false);
                    if (_sslCerFilePath is null)
                    {
                        throw new NetezzaException("_sslCerFilePath hould not be empty");
                    }

                    var certFromPem = X509Certificate2.CreateFromPem(System.IO.File.ReadAllText(_sslCerFilePath));
                    await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions()
                    {
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                        AllowRenegotiation = false,
                        TargetHost = _host,
                        ClientCertificates = [(X509Certificate)certFromPem],
                        EnabledSslProtocols = SslProtocols.Tls12,
                        RemoteCertificateValidationCallback = UserCertificateValidationCallback,
                        LocalCertificateSelectionCallback = UserCertificateSelectionCallback
                    }, cancellationToken).ConfigureAwait(false);
                    _stream = sslStream;
                    await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    beresp = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
                }
                if (beresp == (byte)'N')
                {
                    if (information == HSV2_SSL_NEGOTIATE)
                    {
                        _logger?.LogDebug("Attempting unsecured session");
                    }
                    information = 0;
                    return true;
                }
                else if (beresp == (byte)'E')
                {
                    _logger?.LogWarning("Error: connection failed");
                    return false;
                }
            }
        }

        return true;
    }

    private X509Certificate UserCertificateSelectionCallback(object sender, string targetHost, X509CertificateCollection localCertificates, X509Certificate? remoteCertificate, string[] acceptableIssuers)
    {
        return localCertificates[0];
    }

    private bool UserCertificateValidationCallback(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
    {
        if (sslPolicyErrors == SslPolicyErrors.None)
        {
            return true;
        }

        _logger?.LogError("TLS certificate validation failed with policy errors: {SslPolicyErrors}", sslPolicyErrors);
        if (chain is not null)
        {
            foreach (var item in chain.ChainStatus)
            {
                var message = item.StatusInformation;
                _logger?.LogError("ValidateServerCertificate {message}", message);
            }
        }

        return false;
    }



    private bool ConnSendHandshakeVersion2(int? hsVersion, string user,object? pgOptions)
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
        await WriteInt32Async( 2 + userBytes.Length + 1 + 4, cancellationToken).ConfigureAwait(false);
        await WriteInt16Async( information, cancellationToken).ConfigureAwait(false);
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
                        await WriteInt32Async( 2 + 2 + 2 + 4, cancellationToken).ConfigureAwait(false);
                        await WriteInt16Async( information, cancellationToken).ConfigureAwait(false);
                        await WriteInt16Async( _protocol1, cancellationToken).ConfigureAwait(false);
                        await WriteInt16Async( _protocol2, cancellationToken).ConfigureAwait(false);
                        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                        information = HSV2_REMOTE_PID;
                        continue;
                    case HSV2_REMOTE_PID:
                        await WriteInt32Async( 2 + 4 + 4, cancellationToken).ConfigureAwait(false);
                        await WriteInt16Async( information, cancellationToken).ConfigureAwait(false);
                        await WriteInt32Async( Environment.ProcessId, cancellationToken).ConfigureAwait(false);
                        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                        information = pgOptions is null ? HSV2_CLIENT_TYPE : HSV2_OPTIONS;
                        continue;
                    case HSV2_OPTIONS:
                        information = HSV2_CLIENT_TYPE;
                        continue;
                    case HSV2_CLIENT_TYPE:
                        await WriteInt32Async( 2 + 2 + 4, cancellationToken).ConfigureAwait(false);
                        await WriteInt16Async( information, cancellationToken).ConfigureAwait(false);
                        await WriteInt16Async( (short)NPSCLIENT_TYPE_PYTHON, cancellationToken).ConfigureAwait(false);
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
                        await WriteInt32Async( 2 + 2 + 4, cancellationToken).ConfigureAwait(false);
                        await WriteInt16Async( information, cancellationToken).ConfigureAwait(false);
                        await WriteInt16Async( IPS_CLIENT, cancellationToken).ConfigureAwait(false);
                        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                        information = HSV2_CLIENT_DONE;
                        continue;
                    case HSV2_CLIENT_DONE:
                        await WriteInt32Async( 2 + 4, cancellationToken).ConfigureAwait(false);
                        await WriteInt16Async( information, cancellationToken).ConfigureAwait(false);
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
        await WriteInt32Async( 2 + userBytes.Length + 1 + 4, cancellationToken).ConfigureAwait(false);
        await WriteInt16Async( information, cancellationToken).ConfigureAwait(false);
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
                        await WriteInt32Async( 2 + guardiumAppNameBytes.Length + 1 + 4, cancellationToken).ConfigureAwait(false);
                        await WriteInt16Async( information, cancellationToken).ConfigureAwait(false);
                        await _stream.WriteAsync(guardiumAppNameBytes.AsMemory(), cancellationToken).ConfigureAwait(false);
                        await _stream.WriteAsync(new byte[] { 0 }, cancellationToken).ConfigureAwait(false);
                        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                        _logger?.LogDebug("Appname: {_guardiumAppName}", _guardiumAppName);
                        information = HSV2_CLIENT_OS;
                        continue;
                    case HSV2_CLIENT_OS:
                        var guardiumClientOSBytes = Encoding.UTF8.GetBytes(_guardiumClientOS);
                        await WriteInt32Async( 2 + guardiumClientOSBytes.Length + 1 + 4, cancellationToken).ConfigureAwait(false);
                        await WriteInt16Async( information, cancellationToken).ConfigureAwait(false);
                        await _stream.WriteAsync(guardiumClientOSBytes.AsMemory(), cancellationToken).ConfigureAwait(false);
                        await _stream.WriteAsync(new byte[] { 0 }, cancellationToken).ConfigureAwait(false);
                        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                        _logger?.LogDebug("Client OS: {guardium_clientOS}", _guardiumClientOS);
                        information = HSV2_CLIENT_HOST_NAME;
                        continue;
                    case HSV2_CLIENT_HOST_NAME:
                        var guardiumClientHostNameBytes = Encoding.UTF8.GetBytes(_guardiumClientHostName);
                        await WriteInt32Async( 2 + guardiumClientHostNameBytes.Length + 1 + 4, cancellationToken).ConfigureAwait(false);
                        await WriteInt16Async( information, cancellationToken).ConfigureAwait(false);
                        await _stream.WriteAsync(guardiumClientHostNameBytes.AsMemory(), cancellationToken).ConfigureAwait(false);
                        await _stream.WriteAsync(new byte[] { 0 }, cancellationToken).ConfigureAwait(false);
                        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                        _logger?.LogDebug("Client Host Name: {guardium_clientHostName}", _guardiumClientHostName);
                        information = HSV2_CLIENT_OS_USER;
                        continue;
                    case HSV2_CLIENT_OS_USER:
                        var guardiumClientOSUserBytes = Encoding.UTF8.GetBytes(_guardiumClientOSUser);
                        await WriteInt32Async( 2 + guardiumClientOSUserBytes.Length + 1 + 4, cancellationToken).ConfigureAwait(false);
                        await WriteInt16Async( information, cancellationToken).ConfigureAwait(false);
                        await _stream.WriteAsync(guardiumClientOSUserBytes.AsMemory(), cancellationToken).ConfigureAwait(false);
                        await _stream.WriteAsync(new byte[] { 0 }, cancellationToken).ConfigureAwait(false);
                        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                        _logger?.LogDebug("Client OS User: {guardium_clientOSUser}", _guardiumClientOSUser);
                        information = HSV2_PROTOCOL;
                        continue;
                    case HSV2_PROTOCOL:
                        await WriteInt32Async( 2 + 2 + 2 + 4, cancellationToken).ConfigureAwait(false);
                        await WriteInt16Async( information, cancellationToken).ConfigureAwait(false);
                        await WriteInt16Async( _protocol1, cancellationToken).ConfigureAwait(false);
                        await WriteInt16Async( _protocol2, cancellationToken).ConfigureAwait(false);
                        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                        information = HSV2_REMOTE_PID;
                        continue;
                    case HSV2_REMOTE_PID:
                        await WriteInt32Async( 2 + 4 + 4, cancellationToken).ConfigureAwait(false);
                        await WriteInt16Async( information, cancellationToken).ConfigureAwait(false);
                        await WriteInt32Async( Environment.ProcessId, cancellationToken).ConfigureAwait(false);
                        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                        information = pgOptions is null ? HSV2_CLIENT_TYPE : HSV2_OPTIONS;
                        continue;
                    case HSV2_OPTIONS:
                        information = HSV2_CLIENT_TYPE;
                        continue;
                    case HSV2_CLIENT_TYPE:
                        await WriteInt32Async( 2 + 2 + 4, cancellationToken).ConfigureAwait(false);
                        await WriteInt16Async( information, cancellationToken).ConfigureAwait(false);
                        await WriteInt16Async( (short)NPSCLIENT_TYPE_PYTHON, cancellationToken).ConfigureAwait(false);
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
                        await WriteInt32Async( 2 + 2 + 4, cancellationToken).ConfigureAwait(false);
                        await WriteInt16Async( information, cancellationToken).ConfigureAwait(false);
                        await WriteInt16Async( IPS_CLIENT, cancellationToken).ConfigureAwait(false);
                        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                        information = HSV2_CLIENT_DONE;
                        continue;

                    case HSV2_CLIENT_DONE:
                        await WriteInt32Async( 2 + 4, cancellationToken).ConfigureAwait(false);
                        await WriteInt16Async( information, cancellationToken).ConfigureAwait(false);
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
        while (true)
        {
            var response = _stream.ReadByte();
            _logger?.LogInformation("backend response: {Response}", response);

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
                int length = PGUtil.ReadInt32(_stream);

                byte[] bytes = new byte[length];
                _stream.ReadExactly(bytes, 0, length);
                string notices = Encoding.UTF8.GetString(bytes);
                _logger?.LogDebug("Response received from backend: {Notices}", notices);
            }

            if (response == (byte)BackendMessageCode.BackendKeyData)
            {
                BackendKeyData = new BackendKeyDataMessage(_stream);
                _logger?.LogDebug("Backend response PID: {Pid}", BackendKeyData.BackendProcessId);
                _logger?.LogDebug("Backend response KEY: {Key}", BackendKeyData.BackendSecretKey);
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
                string error = Encoding.UTF8.GetString(bytes,0, readed);
                ArrayPool<byte>.Shared.Return(bytes);
                _logger?.LogWarning("Error occurred, server response: {Error}", error);
                throw new NetezzaException(error);
                //return false;
            }
        }
    }

    private async Task<bool> ConnConnectionCompleteAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var response = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
            _logger?.LogInformation("backend response: {Response}", response);

            if (response != (byte)BackendMessageCode.AuthenticationRequest && response != (byte)BackendMessageCode.ErrorResponse)
            {
                await Skip4BytesAsync( cancellationToken: cancellationToken).ConfigureAwait(false);
                await Skip4BytesAsync( cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            if (response == (byte)BackendMessageCode.AuthenticationRequest)
            {
                int areq = await ReadInt32Async( cancellationToken).ConfigureAwait(false);
                _logger?.LogInformation("backend response: {Areq}", areq);
            }

            if (response == (byte)BackendMessageCode.NoticeResponse)
            {
                int length = await ReadInt32Async( cancellationToken).ConfigureAwait(false);

                byte[] bytes = new byte[length];
                await _stream.ReadExactlyAsync(bytes.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                string notices = Encoding.UTF8.GetString(bytes);
                _logger?.LogDebug("Response received from backend: {Notices}", notices);
            }

            if (response == (byte)BackendMessageCode.BackendKeyData)
            {
                BackendKeyData = await BackendKeyDataMessage.CreateAsync(_stream, cancellationToken).ConfigureAwait(false);
                _logger?.LogDebug("Backend response PID: {Pid}", BackendKeyData.BackendProcessId);
                _logger?.LogDebug("Backend response KEY: {Key}", BackendKeyData.BackendSecretKey);
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
    }

}

