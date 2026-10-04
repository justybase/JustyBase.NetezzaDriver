using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using JustyBase.NetezzaDriver;
using System.Text;

namespace JustyBase.NetezzaDriver.Benchmarks;

/// <summary>Measures named-placeholder binding cost as the parameter count grows.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class NamedLookupBench
{
    [Params(1, 4, 8, 16, 32, 64)]
    public int ParamCount { get; set; }

    private NzParameterCollection _parameters = null!;
    private string _sql = null!;
    private NzParameterHelper.SqlTemplatePlan _plan = null!;

    [GlobalSetup]
    public void Setup()
    {
        var sb = new StringBuilder("SELECT ");
        var c = new NzParameterCollection();
        for (int i = 0; i < ParamCount; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(":parameter_").Append(i);
            c.Add(new NzParameter("parameter_" + i, i));
        }
        _sql = sb.ToString();
        _parameters = c;
        _plan = NzParameterHelper.ParseTemplate(_sql);
    }

    [Benchmark]
    public string Render_Named() => NzParameterHelper.RenderWithPlan(_sql, _plan, _parameters);
}
