using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using JustyBase.NetezzaDriver;
using System.Buffers.Binary;

namespace JustyBase.NetezzaDriver.Benchmarks;

/// <summary>Synthetic protocol microbenchmarks (no live server). Baseline = old behavior.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class ProtocolBenches
{
    private MemoryStream _protoStream = null!;
    private NzReadBuffer _readBuffer = null!;
    private byte[] _protoPayload = null!;

    [GlobalSetup]
    public void Setup()
    {
        _protoPayload = new byte[4096];
        new Random(42).NextBytes(_protoPayload);
        _protoStream = new MemoryStream(_protoPayload, writable: false);
        _readBuffer = new NzReadBuffer(_protoStream, size: 65536);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _readBuffer.Dispose();
        _protoStream.Dispose();
    }

    [Benchmark(Baseline = true, Description = "Stream.ReadExactly per int32 (old)")]
    public int Stream_ReadExactly_Int32()
    {
        _protoStream.Position = 0;
        int checksum = 0;
        Span<byte> tmp = stackalloc byte[4];
        for (int i = 0; i < 1024; i++)
        {
            _protoStream.ReadExactly(tmp);
            checksum += BinaryPrimitives.ReadInt32BigEndian(tmp);
        }
        return checksum;
    }

    [Benchmark(Description = "NzReadBuffer in-buffer int32 (new)")]
    public int ReadBuffer_Int32()
    {
        _protoStream.Position = 0;
        _readBuffer.Ensure(4096);
        int checksum = 0;
        for (int i = 0; i < 1024; i++)
            checksum += _readBuffer.ReadInt32BigEndian();
        return checksum;
    }

    [Benchmark(Description = "Interpolated context per Validate (old)")]
    public int Validate_Old_InterpolatesContext()
    {
        int acc = 0;
        for (int i = 0; i < 1000; i++)
        {
            var context = $"response=T (0x54), row={i}, offset={i * 4}";
            acc += ProtocolLengthValidator.Validate(100, "rowPayload", true, context);
        }
        return acc;
    }

    [Benchmark(Description = "Numeric fast check, no context (new)")]
    public int Validate_New_FastCheck()
    {
        int acc = 0;
        for (int i = 0; i < 1000; i++)
        {
            int length = 100;
            if ((uint)length <= (uint)ProtocolLengthValidator.MaxPayloadLength && length >= 0)
                acc += length;
            else
                acc += ProtocolLengthValidator.Validate(length, "rowPayload");
        }
        return acc;
    }
}
