using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using JustyBase.NetezzaDriver;
using JustyBase.NetezzaDriver.StringPool;

namespace JustyBase.NetezzaDriver.Benchmarks;

/// <summary>Synthetic reader-access benchmarks (no live server). Baseline = boxing GetValue.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class ReaderAccessBenches
{
    private NzDataReader _intReader = null!;
    private NzDataReader _strReader = null!;

    [GlobalSetup]
    public void Setup()
    {
        var conn = new NzConnection("Host=localhost;Database=t;User=u;Password=p");
        var cmdInt = new NzCommand(conn);
        var rowInt = new RowValue[1];
        rowInt[0].typeCode = TypeCodeEx.Int32;
        rowInt[0].int32Value = 12345;
        cmdInt.AddRow(rowInt);
        _intReader = NzDataReader.CreateForTests(cmdInt);

        var cmdStr = new NzCommand(conn);
        var rowStr = new RowValue[1];
        rowStr[0].typeCode = TypeCodeEx.String;
        rowStr[0].stringValue = "hello world";
        cmdStr.AddRow(rowStr);
        _strReader = NzDataReader.CreateForTests(cmdStr);
    }

    [Benchmark(Baseline = true, Description = "GetValue boxes int")]
    public object GetValue_Boxes() => _intReader.GetValue(0);

    [Benchmark(Description = "GetFieldValue<int> no box")]
    public int GetFieldValue_Int_NoBox() => _intReader.GetFieldValue<int>(0);

    [Benchmark(Description = "GetInt32 typed")]
    public int GetInt32_Typed() => _intReader.GetInt32(0);

    [Benchmark(Description = "GetValue string")]
    public object GetValue_String() => _strReader.GetValue(0);

    [Benchmark(Description = "GetFieldValue<string>")]
    public string GetFieldValue_String() => _strReader.GetFieldValue<string>(0);

    [Benchmark(Description = "pool low-cardinality 10k/8 values")]
    public int Pool_LowCardinality()
    {
        var pool = new Sylvan();
        int acc = 0;
        for (int i = 0; i < 10000; i++)
        {
            string s = pool.GetString($"v{i % 8}".AsSpan());
            acc += s.Length;
        }
        return acc;
    }

    [Benchmark(Description = "pool high-cardinality 10k unique (capped)")]
    public int Pool_HighCardinality()
    {
        var pool = new Sylvan();
        int acc = 0;
        for (int i = 0; i < 10000; i++)
        {
            string s = pool.GetString($"key{i:00000}".AsSpan());
            acc += s.Length;
        }
        return acc;
    }
}
