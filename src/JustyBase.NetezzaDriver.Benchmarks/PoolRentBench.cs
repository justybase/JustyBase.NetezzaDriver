using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using JustyBase.NetezzaDriver;
using JustyBase.NetezzaDriver.TestSupport;

namespace JustyBase.NetezzaDriver.Benchmarks;

/// <summary>
/// Rent → Return cost over the replay server. The tokenless case is the
/// common path (no caller cancellation): it reuses the pool dispose token and
/// should allocate no linked CTS. The cancelable case forces the linked CTS so
/// the delta isolates that allocation.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class PoolRentBench
{
    private NzReplayFixture _fixture = null!;
    private NzReplayServer _server = null!;
    private NzConnectionPool _pool = null!;
    private CancellationTokenSource _callerCts = null!;

    [GlobalSetup]
    public void Setup()
    {
        _fixture = NzReplayFixture.LoadShipped("dimdate.nzreplay.gz");
        _server = NzReplayServer.Start(_fixture);
        _pool = new NzConnectionPool(new NzConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Database = "JUST_DATA",
            UserName = "replay",
            Password = "replay",
            Port = _server.Port,
            MaxPoolSize = 4,
            ConnectionValidationInterval = 60,
        });
        _callerCts = new CancellationTokenSource();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _callerCts.Dispose();
        _pool.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _server.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Benchmark(Baseline = true, Description = "Rent+Return (CancellationToken.None)")]
    public async Task Rent_Return_NoToken()
    {
        var lease = await _pool.RentAsync().ConfigureAwait(false);
        await lease.DisposeAsync().ConfigureAwait(false);
    }

    [Benchmark(Description = "Rent+Return (cancelable token -> linked CTS)")]
    public async Task Rent_Return_CancelableToken()
    {
        var lease = await _pool.RentAsync(_callerCts.Token).ConfigureAwait(false);
        await lease.DisposeAsync().ConfigureAwait(false);
    }
}
