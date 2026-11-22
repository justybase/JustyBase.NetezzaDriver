using System.Text;

namespace JustyBase.NetezzaDriver.TestSupport;

/// <summary>One statement and the byte-exact backend response recorded for it.</summary>
public sealed class WireRecord
{
    public required string Sql { get; init; }
    public required byte[] Response { get; init; }
}

/// <summary>
/// Reads a golden wire fixture captured from a real appliance
/// (<c>NZWIRE01</c> format, see the shared <c>conformance/fixtures</c> corpus).
/// The parser is deliberately strict: a truncated or malformed fixture fails
/// instead of silently yielding a partial response, because replaying a partial
/// response would exercise the wrong protocol path.
/// </summary>
public sealed class WireFixture
{
    public const string Magic = "NZWIRE01";
    private const int MaxRecords = 10_000;
    private const int MaxSqlLength = 16 * 1024 * 1024;
    private const int MaxResponseLength = 64 * 1024 * 1024;

    public required string Name { get; init; }
    public required IReadOnlyList<WireRecord> Records { get; init; }

    public static string ResolvePath(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Wire", fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Wire fixture '{fileName}' was not found at '{path}'. " +
                "Copy it from the shared conformance corpus.",
                path);
        }
        return path;
    }

    public static WireFixture LoadShipped(string fileName) => Load(ResolvePath(fileName), fileName);

    public static WireFixture Load(string path) => Load(path, Path.GetFileName(path));

    public static WireFixture Load(string path, string name)
    {
        using var stream = File.OpenRead(path);
        return Load(stream, name);
    }

    public static WireFixture Load(Stream stream, string name)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            return LoadCore(stream, name);
        }
        catch (EndOfStreamException error)
        {
            throw new InvalidDataException($"'{name}' ends before the fixture is complete.", error);
        }
        catch (DecoderFallbackException error)
        {
            throw new InvalidDataException($"'{name}' contains SQL that is not valid UTF-8.", error);
        }
    }

    private static WireFixture LoadCore(Stream stream, string name)
    {
        using var reader = new BinaryReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true), leaveOpen: true);

        byte[] magic = reader.ReadBytes(Magic.Length);
        if (magic.Length != Magic.Length || !magic.AsSpan().SequenceEqual(Encoding.ASCII.GetBytes(Magic)))
        {
            throw new InvalidDataException($"'{name}' is not an NZWIRE01 fixture.");
        }

        uint recordCount = reader.ReadUInt32();
        if (recordCount > MaxRecords)
        {
            throw new InvalidDataException($"'{name}' declares {recordCount} records, more than the {MaxRecords} limit.");
        }

        var records = new WireRecord[recordCount];
        for (int i = 0; i < records.Length; i++)
        {
            int sqlLength = ReadBoundedLength(reader, MaxSqlLength, name, "SQL");
            byte[] sqlBytes = ReadExactly(reader, sqlLength, name, "SQL");
            string sql = Encoding.UTF8.GetString(sqlBytes);

            int responseLength = ReadBoundedLength(reader, MaxResponseLength, name, "response");
            byte[] response = ReadExactly(reader, responseLength, name, "response");

            records[i] = new WireRecord { Sql = sql, Response = response };
        }

        if (stream.CanSeek && stream.Position != stream.Length)
        {
            throw new InvalidDataException(
                $"'{name}' has {stream.Length - stream.Position} trailing byte(s) after {recordCount} record(s).");
        }

        return new WireFixture { Name = name, Records = records };
    }

    private static int ReadBoundedLength(BinaryReader reader, int maximum, string name, string section)
    {
        uint value = reader.ReadUInt32();
        if (value > maximum)
        {
            throw new InvalidDataException(
                $"'{name}' declares a {section} length of {value}, larger than the {maximum} byte limit.");
        }
        return checked((int)value);
    }

    private static byte[] ReadExactly(BinaryReader reader, int length, string name, string section)
    {
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new InvalidDataException(
                $"'{name}' ends after {bytes.Length} of {length} {section} byte(s).");
        }
        return bytes;
    }
}
