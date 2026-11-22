using JustyBase.NetezzaDriver.TestSupport;
using JustyBase.NetezzaDriver.TypeConvertions;
using System.Buffers.Binary;
using System.Text.RegularExpressions;

namespace JustyBase.NetezzaDriver.Tests;

/// <summary>
/// Deterministic, seeded property/fuzz tests. They run in CI on every build;
/// set <c>NZ_FUZZ_ITERATIONS</c> to run longer locally.
/// </summary>
[Trait("Category", "Unit")]
public sealed partial class ProtocolFuzzTests
{
    private static readonly int Iterations = ReadIterations();

    [Fact]
    public void WireFixtureParser_RandomBytes_OnlyReportsInvalidData()
    {
        var random = new Random(0x4E5A); // "NZ"
        for (int iteration = 0; iteration < Iterations; iteration++)
        {
            byte[] bytes = new byte[random.Next(0, 512)];
            random.NextBytes(bytes);

            try
            {
                WireFixture.Load(new MemoryStream(bytes, writable: false), $"fuzz-{iteration}");
            }
            catch (InvalidDataException)
            {
                // Expected for every non-fixture input.
            }
        }
    }

    [Fact]
    public void NzReadBuffer_RandomSuccessfulOperations_MatchReferenceAndTrackConsumedBytes()
    {
        var random = new Random(0x5250); // "RP"
        for (int iteration = 0; iteration < Iterations; iteration++)
        {
            byte[] source = new byte[random.Next(0, 8192)];
            random.NextBytes(source);
            int bufferSize = random.Next(1, 129);

            using var reader = new NzReadBuffer(new MemoryStream(source, writable: false), bufferSize);
            int position = 0;

            for (int step = 0; step < 200; step++)
            {
                int remaining = source.Length - position;
                int operation = random.Next(0, 7);
                switch (operation)
                {
                    case 0:
                        if (remaining == 0)
                        {
                            Assert.Throws<EndOfStreamException>(() => reader.ReadByte());
                            break;
                        }
                        Assert.Equal(source[position], reader.ReadByte());
                        position++;
                        break;

                    case 1:
                        if (remaining < 4)
                        {
                            break;
                        }
                        Assert.Equal(
                            BinaryPrimitives.ReadInt32BigEndian(source.AsSpan(position)),
                            reader.ReadInt32BigEndian());
                        position += 4;
                        break;

                    case 2:
                        if (remaining < 8)
                        {
                            break;
                        }
                        Assert.Equal(
                            BinaryPrimitives.ReadInt64BigEndian(source.AsSpan(position)),
                            reader.ReadInt64BigEndian());
                        position += 8;
                        break;

                    case 3:
                        {
                            int count = random.Next(0, Math.Min(remaining, bufferSize) + 1);
                            ReadOnlySpan<byte> actual = reader.ReadSpan(count);
                            Assert.True(actual.SequenceEqual(source.AsSpan(position, count)));
                            position += count;
                            break;
                        }

                    case 4:
                        {
                            int count = random.Next(0, Math.Min(remaining, bufferSize) + 1);
                            if (count > 0)
                            {
                                reader.Skip(count);
                                position += count;
                            }
                            break;
                        }

                    case 5:
                        {
                            int count = random.Next(0, Math.Min(remaining, 512) + 1);
                            reader.Discard(count);
                            position += count;
                            break;
                        }

                    case 6:
                        {
                            int count = random.Next(0, Math.Min(remaining, bufferSize) + 1);
                            if (count > 0)
                            {
                                ReadOnlyMemory<byte> actual = reader.ReadMemory(count);
                                Assert.True(actual.Span.SequenceEqual(source.AsSpan(position, count)));
                                position += count;
                            }
                            break;
                        }
                }

                Assert.Equal(position, reader.ConsumedBytes);
                Assert.True(reader.BytesBuffered >= 0);
            }
        }
    }

    [Fact]
    public void ProtocolLengthValidator_RandomValues_NeverReturnsAnInvalidLength()
    {
        var random = new Random(0x4C56); // "LV"
        var values = new List<int>
        {
            int.MinValue, -1, 0, 1, ProtocolLengthValidator.MaxPayloadLength - 1,
            ProtocolLengthValidator.MaxPayloadLength, ProtocolLengthValidator.MaxPayloadLength + 1,
            int.MaxValue,
        };
        for (int i = 0; i < Iterations; i++)
        {
            values.Add(random.Next(int.MinValue, int.MaxValue));
        }

        foreach (int value in values)
        {
            if (value < 0 || value > ProtocolLengthValidator.MaxPayloadLength)
            {
                Assert.Throws<NetezzaException>(() => ProtocolLengthValidator.Validate(value, "fuzz"));
            }
            else
            {
                Assert.Equal(value, ProtocolLengthValidator.Validate(value, "fuzz"));
            }

            if (value == 0)
            {
                Assert.Throws<NetezzaException>(() => ProtocolLengthValidator.Validate(0, "fuzz", allowZero: false));
            }
        }
    }

    [Fact]
    public void ProtocolLengthValidator_ValidateAfterOverhead_IsNeverNegative()
    {
        var random = new Random(0x4F48); // "OH"
        for (int i = 0; i < Iterations; i++)
        {
            int frame = random.Next(1, ProtocolLengthValidator.MaxPayloadLength + 1);
            int overhead = random.Next(0, 64);
            if (frame < overhead)
            {
                Assert.Throws<NetezzaException>(() =>
                    ProtocolLengthValidator.ValidateAfterOverhead(frame, overhead, "frame", "payload"));
            }
            else
            {
                int payload = ProtocolLengthValidator.ValidateAfterOverhead(frame, overhead, "frame", "payload");
                Assert.Equal(frame - overhead, payload);
                Assert.True(payload >= 0);
            }
        }
    }

    [Fact]
    public void DateTypes_RandomIntervals_AlwaysProduceStableText()
    {
        var random = new Random(0x4454); // "DT"
        for (int i = 0; i < Iterations; i++)
        {
            long micros = random.NextInt64(-400L * 86_400_000_000L, 400L * 86_400_000_000L);
            int months = random.Next(-1_000, 1_001);

            string interval = DateTypes.FormatInterval(micros, months);
            Assert.NotEmpty(interval);
            Assert.DoesNotContain("  ", interval, StringComparison.Ordinal);

            var clock = new TimeSpan(random.NextInt64(0, TimeSpan.TicksPerDay));
            Assert.Matches(ClockPattern(), DateTypes.FormatClock(clock));
        }
    }

    [Fact]
    public void Numeric_RandomValues_OnlyThrowDocumentedConversionErrors()
    {
        var random = new Random(0x4E55); // "NU"
        for (int i = 0; i < Iterations; i++)
        {
            int precision = random.Next(1, 39);
            int scale = random.Next(0, precision + 1);
            int digitCount = random.Next(1, 5);
            byte[] data = new byte[16];
            random.NextBytes(data);

            try
            {
                string text = Numeric.GetNumericString(data, precision, scale, digitCount);
                Assert.NotEmpty(text);
            }
            catch (Exception error) when (error is FormatException or OverflowException or ArgumentOutOfRangeException)
            {
                // Documented conversion failures for malformed numeric payloads.
            }

            try
            {
                _ = Numeric.GetCsNumeric(data, precision, scale, digitCount);
            }
            catch (Exception error) when (error is FormatException or OverflowException or ArgumentOutOfRangeException)
            {
            }
        }
    }

    [GeneratedRegex(@"^\d{2}:\d{2}:\d{2}(\.\d{1,6})?$", RegexOptions.CultureInvariant)]
    private static partial Regex ClockPattern();

    private static int ReadIterations()
    {
        string? configured = Environment.GetEnvironmentVariable("NZ_FUZZ_ITERATIONS");
        return int.TryParse(configured, out int value) && value > 0 ? value : 250;
    }
}
