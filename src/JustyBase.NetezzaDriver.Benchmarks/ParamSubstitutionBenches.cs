using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using JustyBase.NetezzaDriver;
using System.Text;

namespace JustyBase.NetezzaDriver.Benchmarks;

/// <summary>Synthetic parameter-substitution benchmarks (no live server). Baseline = reparse per execute.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class ParamSubstitutionBenches
{
    private NzParameterCollection _onePositional = null!;
    private NzParameterCollection _tenPositional = null!;
    private NzParameterCollection _oneNamed = null!;
    private NzParameterCollection _tenNamed = null!;
    private string _largeSql = null!;
    private NzParameterHelper.SqlTemplatePlan _cachedTenNamedPlan = null!;
    private string _tenNamedSql = null!;

    [GlobalSetup]
    public void Setup()
    {
        _onePositional = Positional(42);
        _tenPositional = Positional(1, 2, 3, 4, 5, 6, 7, 8, 9, 10);
        _oneNamed = Named(("id", 42));
        _tenNamed = Named(("p0", 0), ("p1", 1), ("p2", 2), ("p3", 3), ("p4", 4), ("p5", 5), ("p6", 6), ("p7", 7), ("p8", 8), ("p9", 9));
        var sb = new StringBuilder("SELECT * FROM t WHERE ");
        for (int i = 0; i < 50; i++)
        {
            if (i > 0) sb.Append(" AND ");
            sb.Append($"c{i} = :p{i % 10}");
        }
        sb.Append(" /* comment with :fake */ -- :fake2\n AND lit = ':fake3'");
        _largeSql = sb.ToString();
        _tenNamedSql = "SELECT * FROM t WHERE c0=:p0 AND c1=:p1 AND c2=:p2 AND c3=:p3 AND c4=:p4 AND c5=:p5 AND c6=:p6 AND c7=:p7 AND c8=:p8 AND c9=:p9";
        _cachedTenNamedPlan = NzParameterHelper.ParseTemplate(_tenNamedSql);
    }

    private static NzParameterCollection Positional(params object?[] values)
    {
        var c = new NzParameterCollection();
        foreach (var v in values)
            c.Add(new NzParameter(null, v) { IsPositional = true });
        return c;
    }

    private static NzParameterCollection Named(params (string Name, object? Value)[] items)
    {
        var c = new NzParameterCollection();
        foreach (var (name, value) in items)
            c.Add(new NzParameter(name, value));
        return c;
    }

    [Benchmark(Description = "0 params passthrough")]
    public string Subst_ZeroParams() => NzParameterHelper.SubstituteParameters("SELECT * FROM t WHERE a = 1", new NzParameterCollection());

    [Benchmark(Description = "1 positional")]
    public string Subst_OnePositional() => NzParameterHelper.SubstituteParameters("SELECT * FROM t WHERE a = ?", _onePositional);

    [Benchmark(Description = "10 positional")]
    public string Subst_TenPositional() => NzParameterHelper.SubstituteParameters(
        "SELECT * FROM t WHERE c0=? AND c1=? AND c2=? AND c3=? AND c4=? AND c5=? AND c6=? AND c7=? AND c8=? AND c9=?", _tenPositional);

    [Benchmark(Description = "1 named")]
    public string Subst_OneNamed() => NzParameterHelper.SubstituteParameters("SELECT * FROM t WHERE a = :id", _oneNamed);

    [Benchmark(Description = "10 named")]
    public string Subst_TenNamed() => NzParameterHelper.SubstituteParameters(_tenNamedSql, _tenNamed);

    [Benchmark(Description = "large SQL 50 refs")]
    public string Subst_LargeSql() => NzParameterHelper.SubstituteParameters(_largeSql, _tenNamed);

    [Benchmark(Baseline = true, Description = "repeated: reparse each execute (old)")]
    public string Subst_RepeatedReparse()
    {
        string r = string.Empty;
        for (int i = 0; i < 20; i++)
            r = NzParameterHelper.SubstituteParameters(_tenNamedSql, _tenNamed);
        return r;
    }

    [Benchmark(Description = "repeated: cached plan + render (new)")]
    public string Subst_RepeatedCached()
    {
        string r = string.Empty;
        for (int i = 0; i < 20; i++)
            r = NzParameterHelper.RenderWithPlan(_tenNamedSql, _cachedTenNamedPlan, _tenNamed);
        return r;
    }
}
