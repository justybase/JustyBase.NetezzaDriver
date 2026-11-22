using System.Net;
using System.Net.Sockets;

namespace JustyBase.NetezzaDriver.TestSupport;

/// <summary>
/// Minimal scripted TCP server for handshake-failure tests. The handler gets
/// the accepted stream and is expected to read the client handshake packets
/// and write raw backend bytes. Unlike <see cref="NzReplayServer"/>, this can
/// emit malformed or refusal responses that a real appliance would not
/// produce in a portable test.
/// </summary>
public sealed class ScriptedNzServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<NetworkStream, CancellationToken, Task> _handler;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _clients = [];
    private readonly object _sync = new();
    private readonly Task _acceptTask;

    public int Port { get; }

    private ScriptedNzServer(Func<NetworkStream, CancellationToken, Task> handler)
    {
        _handler = handler;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptTask = Task.Run(AcceptLoopAsync);
    }

    public static ScriptedNzServer Start(Func<NetworkStream, CancellationToken, Task> handler)
        => new(handler);

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch
            {
                break;
            }

            var task = Task.Run(async () =>
            {
                using (client)
                {
                    try
                    {
                        await _handler(client.GetStream(), _cts.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                        // The driver is expected to abort the handshake.
                    }
                }
            });

            lock (_sync)
            {
                _clients.Add(task);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }

        try { await _acceptTask.ConfigureAwait(false); } catch { }

        Task[] clients;
        lock (_sync)
        {
            clients = _clients.ToArray();
        }

        try
        {
            await Task.WhenAll(clients).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch
        {
            // A stuck handler must not hang test teardown.
        }

        _cts.Dispose();
    }
}

/// <summary>Small big-endian framing helpers used by handshake mock scripts.</summary>
public static class HandshakeWire
{
    /// <summary>
    /// Reads a client handshake packet. The client writes a big-endian length
    /// that includes the four-byte length field itself.
    /// </summary>
    public static async Task<byte[]> ReadPacketAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header);
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

    public static async Task WriteAsync(NetworkStream stream, CancellationToken cancellationToken, params byte[] bytes)
    {
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task WriteInt32Async(NetworkStream stream, int value, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        await WriteAsync(stream, cancellationToken, buffer).ConfigureAwait(false);
    }

    /// <summary>
    /// Drains the client's remaining bytes before the script closes the socket.
    /// Closing a socket with unread data can send RST, which discards the
    /// response the test is about to assert on.
    /// </summary>
    public static async Task DrainUntilClosedAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[1024];
        try
        {
            while (await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) > 0)
            {
            }
        }
        catch
        {
            // The driver aborted the handshake; expected.
        }
    }

    /// <summary>
    /// Optionally half-closes the send side (graceful FIN) and then drains the
    /// client. Use <paramref name="halfClose"/> when the test wants the client
    /// to observe end-of-stream.
    /// </summary>
    public static async Task FinishAndDrainAsync(
        NetworkStream stream,
        CancellationToken cancellationToken,
        bool halfClose)
    {
        if (halfClose)
        {
            try
            {
                stream.Socket.Shutdown(System.Net.Sockets.SocketShutdown.Send);
            }
            catch
            {
                // Socket already gone.
            }
        }

        await DrainUntilClosedAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Completes a version-2 handshake prefix: negotiation, database, and the
    /// security answer is left to the caller. Returns after the client's SSL
    /// negotiation packet has been read.
    /// </summary>
    public static async Task NegotiateVersion2AndReadSslRequestAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        await ReadPacketAsync(stream, cancellationToken).ConfigureAwait(false); // version 6 begin
        await WriteAsync(stream, cancellationToken, (byte)'M', (byte)'2').ConfigureAwait(false);
        await ReadPacketAsync(stream, cancellationToken).ConfigureAwait(false); // version 2 begin
        await WriteAsync(stream, cancellationToken, (byte)'N').ConfigureAwait(false);
        await ReadPacketAsync(stream, cancellationToken).ConfigureAwait(false); // database
        await WriteAsync(stream, cancellationToken, (byte)'N').ConfigureAwait(false);
        await ReadPacketAsync(stream, cancellationToken).ConfigureAwait(false); // SSL negotiate
    }

    /// <summary>
    /// Completes the version-2 handshake up to the authentication request and
    /// returns after the client's user packet has been acknowledged.
    /// </summary>
    public static async Task CompleteVersion2HandshakeAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        await NegotiateVersion2AndReadSslRequestAsync(stream, cancellationToken).ConfigureAwait(false);
        await WriteAsync(stream, cancellationToken, (byte)'N').ConfigureAwait(false); // no TLS

        await ReadPacketAsync(stream, cancellationToken).ConfigureAwait(false); // user
        for (int step = 0; step < 4; step++)
        {
            await WriteAsync(stream, cancellationToken, (byte)'N').ConfigureAwait(false);
        }
    }
}
