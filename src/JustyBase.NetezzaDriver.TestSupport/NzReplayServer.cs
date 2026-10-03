using System.Net;
using System.Net.Sockets;

namespace JustyBase.NetezzaDriver.TestSupport;

/// <summary>
/// A fake Netezza server that replays an <see cref="NzReplayFixture"/> over a
/// real loopback TCP socket. It sends the recorded handshake segment once, then
/// answers each frontend query with the next recorded response segment (and
/// repeats the final segment for further identical queries), so the driver
/// follows its normal socket code paths with no database or network latency.
/// </summary>
public sealed class NzReplayServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly NzReplayFixture _fixture;
    private readonly List<Task> _clients = new();
    private readonly object _sync = new();
    private volatile bool _stopped;
    private Task? _acceptTask;

    public int Port { get; }

    private NzReplayServer(NzReplayFixture fixture)
    {
        _fixture = fixture;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptTask = Task.Run(AcceptLoopAsync);
    }

    public static NzReplayServer Start(NzReplayFixture fixture) => new(fixture);

    private async Task AcceptLoopAsync()
    {
        while (!_stopped)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                break;
            }

            var task = Task.Run(() => HandleClientAsync(client));
            lock (_sync) _clients.Add(task);
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                await stream.WriteAsync(_fixture.Segments[0]).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
                await ServeQueriesAsync(stream).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Client disconnect / shutdown during cleanup.
            }
        }
    }

    private async Task ServeQueriesAsync(NetworkStream stream)
    {
        byte[] readBuffer = new byte[16 * 1024];
        var pending = new List<byte>(16 * 1024);
        int nextSegment = 1;

        while (!_stopped)
        {
            int read = await stream.ReadAsync(readBuffer).ConfigureAwait(false);
            if (read <= 0)
                break;

            for (int i = 0; i < read; i++)
                pending.Add(readBuffer[i]);

            while (ClientQueryPacket.Find(pending, out int packetEnd) >= 0)
            {
                pending.RemoveRange(0, packetEnd);

                byte[] response = _fixture.Segments[Math.Min(nextSegment, _fixture.Segments.Length - 1)];
                nextSegment++;

                if (response.Length > 0)
                {
                    await stream.WriteAsync(response).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                }
            }

            if (pending.Count > 1_000_000)
                pending.RemoveRange(0, pending.Count - 64 * 1024);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stopped = true;
        try { _listener.Stop(); } catch { }

        Task[] clients;
        lock (_sync) clients = _clients.ToArray();

        if (_acceptTask is not null)
        {
            try { await _acceptTask.ConfigureAwait(false); } catch { }
        }

        try { await Task.WhenAll(clients).ConfigureAwait(false); } catch { }
    }
}
