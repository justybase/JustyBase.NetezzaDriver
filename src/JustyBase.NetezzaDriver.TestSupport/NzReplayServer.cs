using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace JustyBase.NetezzaDriver.TestSupport;

/// <summary>
/// Optional fault injection for <see cref="NzReplayServer"/>. Callbacks receive
/// the SQL exactly as the driver sent it and the zero-based query index after
/// the handshake, so a test can assert the frontend traffic and replace or
/// delay a response without giving up the recorded protocol framing.
/// </summary>
public sealed class NzReplayOptions
{
    /// <summary>Returns a replacement response, or <c>null</c> to use the fixture segment.</summary>
    public Func<string, int, byte[]?>? ResponseOverride { get; init; }

    /// <summary>Returns how long the server should wait before answering a query.</summary>
    public Func<string, int, TimeSpan>? DelayBeforeResponse { get; init; }

    /// <summary>Observes every query before its response is produced.</summary>
    public Action<string, int>? OnQuery { get; init; }

    /// <summary>
    /// Returns <c>true</c> to close the socket after the response, simulating a
    /// backend that dies in the middle of a result set.
    /// </summary>
    public Func<string, int, bool>? CloseAfterResponse { get; init; }
}

/// <summary>
/// A fake Netezza server that replays an <see cref="NzReplayFixture"/> over a
/// real loopback TCP socket. It sends the recorded handshake segment once, then
/// answers each frontend query with the next recorded response segment (and
/// repeats the final segment for further identical queries), so the driver
/// follows its normal socket code paths with no database or network latency.
/// <see cref="Queries"/> records the SQL actually sent by the driver.
/// </summary>
public sealed class NzReplayServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly NzReplayFixture _fixture;
    private readonly NzReplayOptions _options;
    private readonly List<Task> _clients = new();
    private readonly List<string> _queries = new();
    private readonly object _sync = new();
    private volatile bool _stopped;
    private int _responsesSent;
    private Task? _acceptTask;

    public int Port { get; }

    /// <summary>Number of query responses written to clients.</summary>
    public int ResponsesSent => Volatile.Read(ref _responsesSent);

    /// <summary>SQL statements received from clients, in order.</summary>
    public IReadOnlyList<string> Queries
    {
        get
        {
            lock (_sync)
            {
                return _queries.ToArray();
            }
        }
    }

    private NzReplayServer(NzReplayFixture fixture, NzReplayOptions? options)
    {
        _fixture = fixture;
        _options = options ?? new NzReplayOptions();
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptTask = Task.Run(AcceptLoopAsync);
    }

    public static NzReplayServer Start(NzReplayFixture fixture, NzReplayOptions? options = null)
        => new(fixture, options);

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

            int packetStart;
            while ((packetStart = ClientQueryPacket.Find(pending, out int packetEnd)) >= 0)
            {
                string sql = Encoding.UTF8.GetString(
                    CollectionsMarshal.AsSpan(pending).Slice(packetStart + 5, packetEnd - packetStart - 6));
                pending.RemoveRange(0, packetEnd);

                int queryIndex = nextSegment - 1;
                lock (_sync)
                {
                    _queries.Add(sql);
                }
                _options.OnQuery?.Invoke(sql, queryIndex);

                byte[] response = _options.ResponseOverride?.Invoke(sql, queryIndex)
                    ?? _fixture.Segments[Math.Min(nextSegment, _fixture.Segments.Length - 1)];
                nextSegment++;

                TimeSpan delay = _options.DelayBeforeResponse?.Invoke(sql, queryIndex) ?? TimeSpan.Zero;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay).ConfigureAwait(false);
                }

                if (response.Length > 0)
                {
                    await stream.WriteAsync(response).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                }
                Interlocked.Increment(ref _responsesSent);

                if (_options.CloseAfterResponse?.Invoke(sql, queryIndex) == true)
                {
                    return;
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
