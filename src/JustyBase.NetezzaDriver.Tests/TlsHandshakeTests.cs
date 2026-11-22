using JustyBase.NetezzaDriver.TestSupport;
using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace JustyBase.NetezzaDriver.Tests;

[Trait("Category", "Unit")]
public sealed class TlsHandshakeTests
{
    [Fact]
    public async Task Require_AcceptsSelfSignedServerWithoutCaBundle()
    {
        using var material = TlsTestCertificate.Create();
        await using var server = StartServer(material.ServerCertificate);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        var handshake = new Handshake(
            client.Client,
            client.GetStream(),
            "127.0.0.1",
            sslCerFilePath: null,
            loggerFactory: null,
            rejectUnauthorized: false,
            verifyHostname: false);

        Stream? stream = handshake.Startup(
            "db", SecurityLevelCode.OnlySecuredSession, "u", "p", pgOptions: null);

        Assert.NotNull(stream);
        stream.Dispose();
    }

    [Fact]
    public async Task VerifyCa_TrustsTheConfiguredCaAndIgnoresHostName()
    {
        using var material = TlsTestCertificate.Create();
        await using var server = StartServer(material.ServerCertificate);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        var handshake = new Handshake(
            client.Client,
            client.GetStream(),
            "127.0.0.1", // certificate is CN=localhost: verify-ca ignores the name
            material.CaPath,
            loggerFactory: null,
            rejectUnauthorized: true,
            verifyHostname: false);

        Stream? stream = handshake.Startup(
            "db", SecurityLevelCode.OnlySecuredSession, "u", "p", pgOptions: null);

        Assert.NotNull(stream);
        stream.Dispose();
    }

    [Fact]
    public async Task VerifyFull_AcceptsMatchingHostName()
    {
        using var material = TlsTestCertificate.Create();
        await using var server = StartServer(material.ServerCertificate);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        var handshake = new Handshake(
            client.Client,
            client.GetStream(),
            "localhost",
            material.CaPath,
            loggerFactory: null,
            rejectUnauthorized: true,
            verifyHostname: true);

        Stream? stream = handshake.Startup(
            "db", SecurityLevelCode.OnlySecuredSession, "u", "p", pgOptions: null);

        Assert.NotNull(stream);
        stream.Dispose();
    }

    [Fact]
    public async Task VerifyFull_RejectsHostNameMismatch()
    {
        using var material = TlsTestCertificate.Create();
        await using var server = StartServer(material.ServerCertificate);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        var handshake = new Handshake(
            client.Client,
            client.GetStream(),
            "127.0.0.1",
            material.CaPath,
            loggerFactory: null,
            rejectUnauthorized: true,
            verifyHostname: true);

        Assert.ThrowsAny<Exception>(() => handshake.Startup(
            "db", SecurityLevelCode.OnlySecuredSession, "u", "p", pgOptions: null));
    }

    [Fact]
    public async Task VerifyCa_RejectsUntrustedCertificate()
    {
        using var material = TlsTestCertificate.Create();
        await using var server = StartServer(material.ServerCertificate);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        var handshake = new Handshake(
            client.Client,
            client.GetStream(),
            "localhost",
            sslCerFilePath: null,
            loggerFactory: null,
            rejectUnauthorized: true,
            verifyHostname: false);

        Assert.ThrowsAny<Exception>(() => handshake.Startup(
            "db", SecurityLevelCode.OnlySecuredSession, "u", "p", pgOptions: null));
    }

    private static ScriptedNzServer StartServer(X509Certificate2 serverCertificate)
        => ScriptedNzServer.Start(async (stream, cancellationToken) =>
        {
            await HandshakeWire.NegotiateVersion2AndReadSslRequestAsync(stream, cancellationToken)
                .ConfigureAwait(false);
            await HandshakeWire.WriteAsync(stream, cancellationToken, (byte)'S').ConfigureAwait(false);
            await ReadPacketAsync(stream, cancellationToken).ConfigureAwait(false); // SSL connect

            using var tls = new SslStream(stream, leaveInnerStreamOpen: false);
            await tls.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = serverCertificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ClientCertificateRequired = false,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                },
                cancellationToken).ConfigureAwait(false);

            await WriteAsync(tls, cancellationToken, (byte)'N').ConfigureAwait(false);
            await ReadPacketAsync(tls, cancellationToken).ConfigureAwait(false); // user
            for (int i = 0; i < 4; i++)
            {
                await WriteAsync(tls, cancellationToken, (byte)'N').ConfigureAwait(false);
            }

            // AuthenticationRequest(AUTH_REQ_OK), then ReadyForQuery.
            await WriteAsync(
                tls,
                cancellationToken,
                (byte)'R', 0, 0, 0, 0,
                (byte)'Z', 0, 0, 0, 5, 0, 0, 0, 0).ConfigureAwait(false);

            await DrainUntilClosedAsync(tls, cancellationToken).ConfigureAwait(false);
        });

    private static async Task<byte[]> ReadPacketAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 4 || length > 1_000_000)
        {
            throw new InvalidDataException($"Invalid handshake packet length {length}.");
        }

        byte[] payload = new byte[length - 4];
        if (payload.Length > 0)
        {
            await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        return payload;
    }

    private static async Task WriteAsync(
        Stream stream,
        CancellationToken cancellationToken,
        params byte[] bytes)
    {
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DrainUntilClosedAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[256];
        try
        {
            while (await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) > 0)
            {
            }
        }
        catch
        {
            // The client closed the socket after a successful handshake.
        }
    }

    private sealed class TlsTestCertificate : IDisposable
    {
        public required X509Certificate2 ServerCertificate { get; init; }
        public required string CaPath { get; init; }

        public static TlsTestCertificate Create()
        {
            DateTimeOffset notBefore = DateTimeOffset.UtcNow.AddDays(-1);
            DateTimeOffset notAfter = DateTimeOffset.UtcNow.AddYears(1);

            using var caKey = RSA.Create(2048);
            var caRequest = new CertificateRequest(
                "CN=Netezza Driver Test CA",
                caKey,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            caRequest.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(certificateAuthority: true, false, 0, critical: true));
            caRequest.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
                    critical: true));
            using X509Certificate2 ca = caRequest.CreateSelfSigned(notBefore, notAfter);

            using var serverKey = RSA.Create(2048);
            var serverRequest = new CertificateRequest(
                "CN=localhost",
                serverKey,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            serverRequest.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(certificateAuthority: false, false, 0, critical: true));
            serverRequest.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                    critical: true));
            serverRequest.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(
                    new OidCollection { new("1.3.6.1.5.5.7.3.1") },
                    critical: false));

            byte[] serial = new byte[8];
            RandomNumberGenerator.Fill(serial);
            using X509Certificate2 leaf = serverRequest.Create(ca, notBefore, notAfter, serial);
            using X509Certificate2 ephemeral = leaf.CopyWithPrivateKey(serverKey);
            // SChannel cannot use ephemeral private keys for a server certificate.
            // Round-trip through PKCS#12 with a persisted key set for the test.
            byte[] pfx = ephemeral.Export(X509ContentType.Pfx);
            X509Certificate2 server = X509CertificateLoader.LoadPkcs12(
                pfx,
                password: null,
                X509KeyStorageFlags.DefaultKeySet);

            string caPath = Path.Combine(
                Path.GetTempPath(),
                $"nz-driver-test-ca-{Guid.NewGuid():N}.pem");
            File.WriteAllText(caPath, ca.ExportCertificatePem());

            return new TlsTestCertificate
            {
                ServerCertificate = server,
                CaPath = caPath,
            };
        }

        public void Dispose()
        {
            ServerCertificate.Dispose();
            try { File.Delete(CaPath); } catch { }
        }
    }
}
