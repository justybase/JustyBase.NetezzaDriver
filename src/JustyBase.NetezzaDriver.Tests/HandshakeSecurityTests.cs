using JustyBase.NetezzaDriver.TestSupport;
using System.Data;

namespace JustyBase.NetezzaDriver.Tests;

[Trait("Category", "Unit")]
public sealed class HandshakeSecurityTests
{
    private static NzConnection CreateConnection(int port, SecurityLevelCode securityLevel)
        => new("u", "p", "127.0.0.1", "db", port, securityLevel);

    private static ScriptedNzServer StartTlsRefusalServer()
        => ScriptedNzServer.Start(async (stream, cancellationToken) =>
        {
            await HandshakeWire.NegotiateVersion2AndReadSslRequestAsync(stream, cancellationToken)
                .ConfigureAwait(false);
            await HandshakeWire.WriteAsync(stream, cancellationToken, (byte)'N').ConfigureAwait(false);
        });

    [Fact]
    public async Task TlsRefusal_WithOnlySecuredSession_FailsClosed()
    {
        await using var server = StartTlsRefusalServer();
        using var connection = CreateConnection(server.Port, SecurityLevelCode.OnlySecuredSession);

        var error = Assert.Throws<NetezzaException>(() => connection.Open());

        Assert.Contains("TLS session", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task TlsRefusal_WithOnlySecuredSession_FailsClosedAsync()
    {
        await using var server = StartTlsRefusalServer();
        await using var connection = CreateConnection(server.Port, SecurityLevelCode.OnlySecuredSession);

        var error = await Assert.ThrowsAsync<NetezzaException>(() => connection.OpenAsync());

        Assert.Contains("TLS session", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task TlsRequiredByServer_WithOnlyUnsecuredSession_FailsClosed()
    {
        await using var server = ScriptedNzServer.Start(async (stream, cancellationToken) =>
        {
            await HandshakeWire.NegotiateVersion2AndReadSslRequestAsync(stream, cancellationToken)
                .ConfigureAwait(false);
            await HandshakeWire.WriteAsync(stream, cancellationToken, (byte)'S').ConfigureAwait(false);
        });

        using var connection = CreateConnection(server.Port, SecurityLevelCode.OnlyUnsecuredSession);

        var error = Assert.Throws<NetezzaException>(() => connection.Open());

        Assert.Contains("OnlyUnsecuredSession", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2, "Kerberos")]
    [InlineData(99, "Unsupported authentication")]
    public async Task UnsupportedAuthenticationMethod_FailsClosed(int authCode, string expected)
    {
        await using var server = ScriptedNzServer.Start(async (stream, cancellationToken) =>
        {
            await HandshakeWire.CompleteVersion2HandshakeAsync(stream, cancellationToken)
                .ConfigureAwait(false);
            await HandshakeWire.WriteAsync(stream, cancellationToken, (byte)'R').ConfigureAwait(false);
            await HandshakeWire.WriteInt32Async(stream, authCode, cancellationToken).ConfigureAwait(false);
            await HandshakeWire.FinishAndDrainAsync(stream, cancellationToken, halfClose: false)
                .ConfigureAwait(false);
        });

        await using var connection = CreateConnection(server.Port, SecurityLevelCode.PreferredUnsecured);

        var error = await Assert.ThrowsAsync<NetezzaException>(() => connection.OpenAsync());

        Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData('6')]
    [InlineData('9')]
    public async Task InvalidHandshakeVersionDowngrade_FailsClosed(char requestedVersion)
    {
        await using var server = ScriptedNzServer.Start(async (stream, cancellationToken) =>
        {
            await HandshakeWire.ReadPacketAsync(stream, cancellationToken).ConfigureAwait(false);
            await HandshakeWire.WriteAsync(
                stream,
                cancellationToken,
                (byte)'M',
                (byte)requestedVersion).ConfigureAwait(false);
        });

        await using var connection = CreateConnection(server.Port, SecurityLevelCode.PreferredUnsecured);

        await Assert.ThrowsAsync<NetezzaException>(() => connection.OpenAsync());
    }

    [Fact]
    public async Task UnexpectedConnectionCompleteMessage_FailsClosed()
    {
        await using var server = ScriptedNzServer.Start(async (stream, cancellationToken) =>
        {
            await HandshakeWire.CompleteVersion2HandshakeAsync(stream, cancellationToken)
                .ConfigureAwait(false);
            await HandshakeWire.WriteAsync(stream, cancellationToken, (byte)'R').ConfigureAwait(false);
            await HandshakeWire.WriteInt32Async(stream, 0, cancellationToken).ConfigureAwait(false);
            await HandshakeWire.WriteAsync(stream, cancellationToken, (byte)'?').ConfigureAwait(false);
            await HandshakeWire.FinishAndDrainAsync(stream, cancellationToken, halfClose: false)
                .ConfigureAwait(false);
        });

        await using var connection = CreateConnection(server.Port, SecurityLevelCode.PreferredUnsecured);

        var error = await Assert.ThrowsAsync<NetezzaException>(() => connection.OpenAsync());

        Assert.Contains("Unexpected backend message", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectionClosedDuringHandshake_FailsClosed()
    {
        await using var server = ScriptedNzServer.Start(async (stream, cancellationToken) =>
        {
            await HandshakeWire.CompleteVersion2HandshakeAsync(stream, cancellationToken)
                .ConfigureAwait(false);
            await HandshakeWire.WriteAsync(stream, cancellationToken, (byte)'R').ConfigureAwait(false);
            await HandshakeWire.WriteInt32Async(stream, 0, cancellationToken).ConfigureAwait(false);
            // Graceful half-close: the client must observe end-of-stream
            // before ReadyForQuery instead of a reset that discards the bytes.
            await HandshakeWire.FinishAndDrainAsync(stream, cancellationToken, halfClose: true)
                .ConfigureAwait(false);
        });

        await using var connection = CreateConnection(server.Port, SecurityLevelCode.PreferredUnsecured);

        var error = await Assert.ThrowsAsync<NetezzaException>(() => connection.OpenAsync());

        Assert.Contains("closed the connection", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
