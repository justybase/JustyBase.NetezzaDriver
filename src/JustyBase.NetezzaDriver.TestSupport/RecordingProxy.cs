using System.Net;
using System.Net.Sockets;

namespace JustyBase.NetezzaDriver.TestSupport;

/// <summary>
/// Loopback TCP proxy that forwards a Netezza connection to a real server and
/// records the server-to-client byte stream, split into one response segment per
/// frontend query packet. Segment 0 is the handshake response; each later
/// segment is the response to a query. Used offline to produce
/// <see cref="NzReplayFixture"/> files.
/// </summary>
public sealed class RecordingProxy : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly string _targetHost;
    private readonly int _targetPort;
    private readonly object _sync = new();
    private readonly List<List<byte>> _segments = new() { new List<byte>() };
    private readonly List<byte> _clientPending = new();
    private volatile bool _stopped;
    private Task? _acceptTask;

    public int Port { get; }

    private RecordingProxy(string targetHost, int targetPort)
    {
        _targetHost = targetHost;
        _targetPort = targetPort;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptTask = Task.Run(AcceptLoopAsync);
    }

    public static RecordingProxy Start(string targetHost, int targetPort) => new(targetHost, targetPort);

    public byte[][] Segments
    {
        get
        {
            lock (_sync)
            {
                return _segments.Select(s => s.ToArray()).ToArray();
            }
        }
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            using var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
            using var upstream = new TcpClient();
            await upstream.ConnectAsync(_targetHost, _targetPort).ConfigureAwait(false);

            NetworkStream clientStream = client.GetStream();
            NetworkStream upstreamStream = upstream.GetStream();

            await Task.WhenAll(
                PumpClientToServerAsync(clientStream, upstreamStream),
                PumpServerToClientAsync(upstreamStream, clientStream)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Capture is best-effort; the caller inspects the recorded segments.
        }
    }

    private async Task PumpClientToServerAsync(NetworkStream client, NetworkStream server)
    {
        byte[] buffer = new byte[16 * 1024];
        try
        {
            while (!_stopped)
            {
                int read = await client.ReadAsync(buffer).ConfigureAwait(false);
                if (read <= 0)
                    break;

                lock (_sync)
                {
                    for (int i = 0; i < read; i++)
                        _clientPending.Add(buffer[i]);

                    // Queries are strictly sequential (the driver waits for each
                    // response), so at most one complete packet per read. Each
                    // one starts a new response segment.
                    while (ClientQueryPacket.Find(_clientPending, out int packetEnd) >= 0)
                    {
                        _clientPending.RemoveRange(0, packetEnd);
                        _segments.Add(new List<byte>());
                    }
                }

                await server.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            try { server.Socket.Shutdown(SocketShutdown.Send); } catch { }
        }
    }

    private async Task PumpServerToClientAsync(NetworkStream server, NetworkStream client)
    {
        byte[] buffer = new byte[64 * 1024];
        try
        {
            while (!_stopped)
            {
                int read = await server.ReadAsync(buffer).ConfigureAwait(false);
                if (read <= 0)
                    break;

                lock (_sync)
                {
                    _segments[^1].AddRange(buffer.AsSpan(0, read));
                }

                await client.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            try { client.Socket.Shutdown(SocketShutdown.Send); } catch { }
        }
    }

    public async Task WaitForCompletionAsync()
    {
        if (_acceptTask is not null)
        {
            try { await _acceptTask.ConfigureAwait(false); } catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stopped = true;
        try { _listener.Stop(); } catch { }
        await WaitForCompletionAsync().ConfigureAwait(false);
    }
}
