using System.IO.Compression;
using System.Text;

namespace JustyBase.NetezzaDriver.TestSupport;

/// <summary>
/// A recorded Netezza server stream split into request-paced segments:
/// <see cref="Segments"/>[0] is the handshake response, and each following
/// segment is the response to one frontend query. Replayed by
/// <see cref="NzReplayServer"/> so the client sees exactly one response per
/// request, matching live server behavior. No database is needed.
/// </summary>
public sealed class NzReplayFixture
{
    private const uint Magic = 0x4E5A5250; // "NZRP"
    private const int FormatVersion = 2;

    public required string Query { get; init; }
    public required int ExpectedRows { get; init; }
    public required int ExpectedColumns { get; init; }

    /// <summary>Segments[0] = handshake; Segments[n] = response to query n.</summary>
    public required byte[][] Segments { get; init; }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.SmallestSize);
        using var writer = new BinaryWriter(gzip, Encoding.UTF8, leaveOpen: true);

        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(Query);
        writer.Write(ExpectedRows);
        writer.Write(ExpectedColumns);
        writer.Write(Segments.Length);
        foreach (byte[] segment in Segments)
        {
            writer.Write(segment.Length);
            writer.Write(segment);
        }
        writer.Flush();
    }

    public static NzReplayFixture Load(string path)
    {
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new BinaryReader(gzip, Encoding.UTF8, leaveOpen: true);

        if (reader.ReadUInt32() != Magic)
            throw new InvalidDataException($"'{path}' is not an NZ replay fixture.");
        int version = reader.ReadInt32();
        if (version != FormatVersion)
            throw new InvalidDataException($"Unsupported replay fixture version {version}.");

        string query = reader.ReadString();
        int rows = reader.ReadInt32();
        int columns = reader.ReadInt32();
        int segmentCount = reader.ReadInt32();
        var segments = new byte[segmentCount][];
        for (int i = 0; i < segmentCount; i++)
            segments[i] = reader.ReadBytes(reader.ReadInt32());

        return new NzReplayFixture
        {
            Query = query,
            ExpectedRows = rows,
            ExpectedColumns = columns,
            Segments = segments,
        };
    }

    /// <summary>Resolves a fixture shipped in the TestSupport output directory.</summary>
    public static string ResolvePath(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Replay fixture '{fileName}' was not found at '{path}'.", path);
        return path;
    }

    public static NzReplayFixture LoadShipped(string fileName) => Load(ResolvePath(fileName));
}
