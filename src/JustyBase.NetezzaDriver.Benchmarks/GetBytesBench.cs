using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using JustyBase.NetezzaDriver;
using System.Text;

namespace JustyBase.NetezzaDriver.Benchmarks;

/// <summary>Repeated chunked GetBytes on one string value (same row/column).</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class GetBytesBench
{
    [Params(100 * 1024, 1024 * 1024)]
    public int TextLength { get; set; }

    [Params(4 * 1024, 8 * 1024)]
    public int ChunkSize { get; set; }

    private NzDataReader _reader = null!;
    private byte[] _chunk = null!;

    [GlobalSetup]
    public void Setup()
    {
        var conn = new NzConnection("Host=localhost;Database=t;User=u;Password=p");
        var cmd = new NzCommand(conn);
        var row = new RowValue[1];
        var sb = new StringBuilder(TextLength);
        for (int i = 0; i < TextLength; i++)
            sb.Append((char)('a' + (i % 26)));
        row[0].typeCode = TypeCodeEx.String;
        row[0].stringValue = sb.ToString();
        cmd.AddRow(row);
        _reader = NzDataReader.CreateForTests(cmd);
        _chunk = new byte[ChunkSize];
    }

    [Benchmark]
    public long Chunked_GetBytes()
    {
        long total = 0;
        for (long offset = 0; ; offset += ChunkSize)
        {
            long read = _reader.GetBytes(0, offset, _chunk, 0, ChunkSize);
            if (read <= 0)
                break;
            total += read;
        }
        return total;
    }
}
