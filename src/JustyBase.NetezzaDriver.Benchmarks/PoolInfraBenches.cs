using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace JustyBase.NetezzaDriver.Benchmarks;

/// <summary>Synthetic timeout/pool-infra benchmarks (no live server). Baseline = old allocation.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class PoolInfraBenches
{
    private CancellationTokenSource? _reusedCts;

    [GlobalCleanup]
    public void Cleanup() => _reusedCts?.Dispose();

    [Benchmark(Baseline = true, Description = "linked CTS per command (old)")]
    public bool Timeout_LinkedPerCommand()
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        linked.CancelAfter(TimeSpan.FromSeconds(60));
        return linked.Token.CanBeCanceled;
    }

    [Benchmark(Description = "reused CTS via TryReset (new)")]
    public bool Timeout_ReusedTryReset()
    {
        _reusedCts ??= new CancellationTokenSource();
        _reusedCts.TryReset();
        _reusedCts.CancelAfter(TimeSpan.FromSeconds(60));
        return _reusedCts.Token.CanBeCanceled;
    }
}
