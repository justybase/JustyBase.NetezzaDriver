using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using JustyBase.NetezzaDriver;

namespace JustyBase.NetezzaDriver.Benchmarks;

/// <summary>Compares the former repeated-prefix scan with the new one-pass offset table.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class VariableFieldOffsetBench
{
    [Params(8, 64, 256)]
    public int VariableFields { get; set; }

    private byte[] _row = null!;
    private int[] _offsets = null!;

    [GlobalSetup]
    public void Setup()
    {
        _offsets = new int[VariableFields];
        _row = new byte[VariableFields * 18];
        int position = 0;
        for (int i = 0; i < VariableFields; i++)
        {
            int length = 2 + i % 15;
            BitConverter.GetBytes((short)length).CopyTo(_row, position);
            for (int j = sizeof(short); j < length; j++)
                _row[position + j] = (byte)(i + j);
            position += length + (length & 1);
        }
        Array.Resize(ref _row, position);
    }

    [Benchmark(Baseline = true)]
    public int RepeatedPrefixScan()
    {
        int checksum = 0;
        for (int field = 0; field < VariableFields; field++)
        {
            int offset = 0;
            for (int previous = 0; previous < field; previous++)
            {
                int length = BitConverter.ToInt16(_row.AsSpan(offset, sizeof(short)));
                offset += length + (length & 1);
            }
            checksum += _row[offset];
        }
        return checksum;
    }

    [Benchmark]
    public int PrecomputedOffsets()
    {
        NzConnection.FillVariableFieldOffsets(_row, 0, _offsets);
        int checksum = 0;
        for (int field = 0; field < VariableFields; field++)
            checksum += _row[_offsets[field]];
        return checksum;
    }
}
