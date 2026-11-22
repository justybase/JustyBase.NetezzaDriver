using JustyBase.NetezzaDriver.TestSupport;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace JustyBase.NetezzaDriver.Tests;

/// <summary>
/// Replays the shared golden wire corpus captured from a real appliance. These
/// tests prove that the C# decoder agrees with the Rust, Node and Python
/// drivers on the exact server bytes, without requiring a live appliance.
/// </summary>
[Trait("Category", "Unit")]
public sealed class WireReplayTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private const int StartupSegments = 8; // recorded handshake + seven session-setup responses

    private sealed record QueryOutcome(
        string Sql,
        int FieldCount,
        List<object?[]> Rows,
        Exception? Error,
        IReadOnlyList<string> Notices);

    [Fact]
    public void WireFixtureManifest_MatchesEveryShippedCopy()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Wire");
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "wire-manifest.json")));

        int checkedFiles = 0;
        foreach (JsonElement file in manifest.RootElement.GetProperty("files").EnumerateArray())
        {
            string name = file.GetProperty("name").GetString()!;
            string expected = file.GetProperty("sha256").GetString()!;
            int bytes = file.GetProperty("bytes").GetInt32();
            string path = Path.Combine(directory, name);

            Assert.True(File.Exists(path), $"Missing wire fixture {name}.");
            Assert.Equal(bytes, new FileInfo(path).Length);
            Assert.Equal(expected, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
            checkedFiles++;
        }

        Assert.True(checkedFiles >= 10, "The wire manifest should pin the whole consumed corpus.");
    }

    [Fact]
    public void WireFixtureParser_RejectsTruncatedInput()
    {
        string path = WireFixture.ResolvePath("text_types.bin");
        byte[] bytes = File.ReadAllBytes(path);

        foreach (int cut in new[] { 0, 4, 8, 12, 20, bytes.Length / 2, bytes.Length - 1 })
        {
            using var stream = new MemoryStream(bytes, 0, cut, writable: false);
            Assert.Throws<InvalidDataException>(() => WireFixture.Load(stream, $"truncated-{cut}"));
        }
    }

    [Fact]
    public async Task Wire_TextTypes_DecodesTextProtocol()
    {
        QueryOutcome outcome = Assert.Single(await ExecuteAll(Load("text_types.bin")));
        Assert.Null(outcome.Error);
        Assert.Equal(8, outcome.FieldCount);
        object?[] row = Assert.Single(outcome.Rows);

        Assert.Equal("-2147483648", Text(row[0]));
        Assert.Equal("Zażółć gęślą jaźń", Assert.IsType<string>(row[1]));
        Assert.Equal("3.1400", Text(row[2]));
        Assert.Equal("2024-02-29", Date(row[3]));
        Assert.Equal("2024-02-29 12:34:56.123456", Timestamp(row[4]));
        Assert.True(Assert.IsType<bool>(row[5]));
        Assert.Equal(1.5, Assert.IsType<double>(row[6]));
        Assert.Equal("12:34:56", Time(row[7]));
    }

    [Fact]
    public async Task Wire_DbosTypes_DecodesBinaryProtocol()
    {
        var outcomes = await ExecuteAll(Load("dbos_types.bin"));
        Assert.Equal(4, outcomes.Count);
        Assert.All(outcomes.Take(3), outcome => Assert.Null(outcome.Error));

        QueryOutcome select = outcomes[3];
        Assert.Null(select.Error);
        Assert.Equal(11, select.FieldCount);
        Assert.Equal(2, select.Rows.Count);

        object?[] first = select.Rows[0];
        Assert.Equal("-2147483648", Text(first[0]));
        Assert.Equal("Zażółć gęślą jaźń", Assert.IsType<string>(first[1]));
        Assert.Equal("3.1400", Text(first[2]));
        Assert.Equal("2024-02-29", Date(first[3]));
        Assert.Equal("2024-02-29 12:34:56.123456", Timestamp(first[4]));
        Assert.True(Assert.IsType<bool>(first[5]));
        Assert.Equal(1.5, Assert.IsType<double>(first[6]));
        Assert.Equal("12:34:56", Time(first[7]));
        Assert.Equal("-9223372036854775807", Text(first[8]));
        Assert.Equal("-32768", Text(first[9]));
        Assert.Equal("-128", Text(first[10]));

        object?[] second = select.Rows[1];
        Assert.Equal("2147483647", Text(second[0]));
        Assert.Equal(string.Empty, Assert.IsType<string>(second[1]));
        Assert.Equal("-0.0001", Text(second[2]));
        Assert.Equal("0001-01-01", Date(second[3]));
        Assert.Equal("1999-12-31 23:59:59.999999", Timestamp(second[4]));
        Assert.False(Assert.IsType<bool>(second[5]));
        Assert.Equal(-2.5, Assert.IsType<double>(second[6]));
        Assert.Equal("00:00:00", Time(second[7]));
        Assert.Equal("9223372036854775807", Text(second[8]));
        Assert.Equal("32767", Text(second[9]));
        Assert.Equal("127", Text(second[10]));
    }

    [Fact]
    public async Task Wire_NullMatrix_PreservesNumberOfColumnsAndNulls()
    {
        QueryOutcome[] outcomes = (await ExecuteAll(Load("null_matrix.bin"))).ToArray();
        QueryOutcome select = outcomes[^1];
        Assert.Null(select.Error);
        Assert.Equal(33, select.FieldCount);
        object?[] row = Assert.Single(select.Rows);

        for (int i = 0; i < row.Length; i++)
        {
            if (i % 2 == 0)
            {
                Assert.True(row[i] is null or DBNull, $"column {i} should be NULL but was {row[i]}");
            }
            else
            {
                Assert.Equal(i % 3 == 2 ? $"v{i}" : i.ToString(Invariant), Text(row[i]));
            }
        }
    }

    [Fact]
    public async Task Wire_NumericEdges_PreservesFullPrecision()
    {
        QueryOutcome[] outcomes = (await ExecuteAll(Load("numeric_edges.bin"))).ToArray();
        QueryOutcome binary = outcomes[5];
        Assert.Null(binary.Error);
        Assert.Equal(4, binary.Rows.Count);
        Assert.Equal(
            ["1", "2", "3", "4"],
            binary.Rows.Select(row => Text(row[0])).ToArray());
        Assert.Equal(
            [
                "0.0000000000000000000",
                "-0.0000000000000000001",
                "9999999999999999999.9999999999999999999",
                "-9999999999999999999.9999999999999999999",
            ],
            binary.Rows.Select(row => Text(row[1])).ToArray());

        QueryOutcome text = outcomes[6];
        Assert.Null(text.Error);
        object?[] textRow = Assert.Single(text.Rows);
        Assert.Equal("9999999999999999999.9999999999999999999", Text(textRow[0]));
        Assert.Equal("-0.0000000000000000001", Text(textRow[1]));
    }

    [Fact]
    public async Task Wire_Varchar32767_HandlesMaximumPayloads()
    {
        QueryOutcome[] outcomes = (await ExecuteAll(Load("varchar_32767.bin"))).ToArray();
        Assert.All(outcomes, outcome => Assert.Null(outcome.Error));

        int[] lengths = [32_766, 32_767, 32_768];
        for (int i = 0; i < lengths.Length; i++)
        {
            object?[] row = Assert.Single(outcomes[2 + 2 * i].Rows);
            string text = Assert.IsType<string>(row[0]);
            Assert.Equal(lengths[i], text.Length);
            Assert.StartsWith("A", text, StringComparison.Ordinal);
            Assert.EndsWith("Z", text, StringComparison.Ordinal);
        }

        QueryOutcome table = outcomes[^1];
        Assert.Equal(3, table.Rows.Count);
        for (int i = 0; i < lengths.Length; i++)
        {
            Assert.Equal(lengths[i].ToString(Invariant), Text(table.Rows[i][0]));
            string text = Assert.IsType<string>(table.Rows[i][1]);
            Assert.Equal(lengths[i], text.Length);
            Assert.StartsWith("A", text, StringComparison.Ordinal);
            Assert.EndsWith("Z", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Wire_LatinVarchar_DecodesTheRecordedEncoding()
    {
        QueryOutcome[] outcomes = (await ExecuteAll(Load("latin_varchar.bin"))).ToArray();
        QueryOutcome select = outcomes[^1];
        Assert.Null(select.Error);
        Assert.Equal(2, select.Rows.Count);
        Assert.Equal(["1", "ó", "ó", "Zażółć gęślą jaźń"], select.Rows[0].Select(Text).ToArray());
        Assert.Equal(["2", "café", "café", "¡Hola! ¿Qué tal?"], select.Rows[1].Select(Text).ToArray());
    }

    [Fact]
    public async Task Wire_TextEncoding_DecodesUtf8AndCharacterPadding()
    {
        QueryOutcome outcome = Assert.Single(await ExecuteAll(Load("text_encoding.bin")));
        Assert.Null(outcome.Error);
        object?[] row = Assert.Single(outcome.Rows);
        Assert.Equal("ó", Text(row[0]));
        Assert.Equal("Zażółć gęślą jaźń", Text(row[1]));
        Assert.Equal("ab   ", Text(row[2]));
        Assert.Equal("ab   ", Text(row[3]));
    }

    [Fact]
    public async Task Wire_TemporalBoundaries_DecodesDateAndTimeRanges()
    {
        QueryOutcome[] outcomes = (await ExecuteAll(Load("temporal_boundaries.bin"))).ToArray();
        Assert.All(outcomes, outcome => Assert.Null(outcome.Error));

        Assert.Collection(
            outcomes[3].Rows,
            row =>
            {
                Assert.Equal("1", Text(row[0]));
                Assert.Equal("0001-01-01", Date(row[1]));
                Assert.Equal("0001-01-01 00:00:00", Timestamp(row[2]));
                Assert.Equal("00:00:00", Time(row[3]));
                Assert.Equal("12:34:56+05:30", Text(row[4]));
                Assert.Equal("-1 year -2 mons -3 days -04:05:06.789012", Text(row[5]));
            },
            row =>
            {
                Assert.Equal("2", Text(row[0]));
                Assert.Equal("9999-12-31", Date(row[1]));
                Assert.Equal("9999-12-31 23:59:59.999999", Timestamp(row[2]));
                Assert.Equal("23:59:59.999999", Time(row[3]));
                Assert.Equal("00:00:00-00", Text(row[4]));
                Assert.Equal("1 year 2 mons 3 days 04:05:06.789012", Text(row[5]));
            });

        Assert.Collection(
            outcomes[4].Rows,
            row =>
            {
                Assert.Equal("0001-01-01", Date(row[0]));
                Assert.Equal("9999-12-31 23:59:59.999999", Timestamp(row[1]));
                Assert.Equal("23:59:59.999999", Time(row[2]));
                Assert.Equal("-1 year -2 mons -3 days -04:05:06.789012", Text(row[3]));
            });
    }

    [Fact]
    public async Task Wire_Errors_LeaveSessionUsable()
    {
        QueryOutcome[] outcomes = (await ExecuteAll(Load("error.bin"))).ToArray();
        Assert.Equal(3, outcomes.Length);
        Assert.Contains("found a keyword", Assert.IsType<NetezzaException>(outcomes[0].Error).Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Divide by 0", Assert.IsType<NetezzaException>(outcomes[1].Error).Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(outcomes[2].Error);
        Assert.Equal("1", Text(Assert.Single(Assert.Single(outcomes[2].Rows))));
    }

    [Fact]
    public async Task Wire_ErrorDetails_PreserveDiagnosticsAndSession()
    {
        QueryOutcome[] outcomes = (await ExecuteAll(Load("error_details.bin"))).ToArray();
        Assert.Equal(4, outcomes.Length);
        Assert.All(outcomes.Take(3), outcome => Assert.IsType<NetezzaException>(outcome.Error));
        Assert.Null(outcomes[3].Error);
        Assert.Equal("1", Text(Assert.Single(Assert.Single(outcomes[3].Rows))));
    }

    [Fact]
    public async Task Wire_Notices_AreAttachedToTheCommandAndEvent()
    {
        var events = new List<string>();
        WireFixture wire = Load("notice.bin");
        QueryOutcome[] outcomes;
        await using (var server = Start(wire, onQuery: null))
        {
            using var connection = OpenConnection(server.Port);
            connection.NoticeReceived += (_, args) => events.Add(args.Message);
            outcomes = ExecuteAll(connection, wire).ToArray();
        }

        Assert.Contains(outcomes[0].Notices, notice => notice.Contains("ROLLBACK", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(outcomes[1].Notices, notice => notice.Contains("COMMIT", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(outcomes[2].Notices);
        Assert.Null(outcomes[3].Error);
        Assert.Contains(events, notice => notice.Contains("ROLLBACK", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(events, notice => notice.Contains("COMMIT", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Wire_MultiResult_ExposesTheFirstResultWithoutCorruptingTheSession()
    {
        QueryOutcome[] outcomes = (await ExecuteAll(Load("multi_result.bin"))).ToArray();
        QueryOutcome outcome = Assert.Single(outcomes);
        Assert.Null(outcome.Error);
        Assert.True(outcome.FieldCount >= 1);
        Assert.Equal("1", Text(Assert.Single(outcome.Rows)[0]));
    }

    private static WireFixture Load(string name) => WireFixture.LoadShipped(name);

    private static NzReplayServer Start(WireFixture wire, Action<string, int>? onQuery)
    {
        NzReplayFixture startup = NzReplayFixture.LoadShipped("dimdate.nzreplay.gz");
        if (startup.Segments.Length <= StartupSegments)
        {
            throw new InvalidDataException("The replay handshake fixture does not contain the session-setup responses.");
        }

        var combined = new NzReplayFixture
        {
            Query = wire.Records.Count > 0 ? wire.Records[0].Sql : string.Empty,
            ExpectedRows = 0,
            ExpectedColumns = 0,
            Segments = startup.Segments
                .Take(StartupSegments)
                .Concat(wire.Records.Select(record => record.Response))
                .ToArray(),
        };

        return NzReplayServer.Start(combined, new NzReplayOptions { OnQuery = onQuery });
    }

    private static async Task<List<QueryOutcome>> ExecuteAll(WireFixture wire)
    {
        await using var server = Start(wire, onQuery: null);
        using var connection = OpenConnection(server.Port);
        return ExecuteAll(connection, wire);
    }

    private static List<QueryOutcome> ExecuteAll(NzConnection connection, WireFixture wire)
    {
        var outcomes = new List<QueryOutcome>(wire.Records.Count);
        foreach (WireRecord record in wire.Records)
        {
            using var command = connection.CreateCommand(record.Sql);
            try
            {
                using var reader = command.ExecuteReader();
                int fields = reader.FieldCount;
                var rows = new List<object?[]>();
                while (reader.Read())
                {
                    var row = new object?[fields];
                    for (int i = 0; i < fields; i++)
                    {
                        row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    }
                    rows.Add(row);
                }
                outcomes.Add(new QueryOutcome(record.Sql, fields, rows, null, command.Notices.ToArray()));
            }
            catch (Exception error)
            {
                outcomes.Add(new QueryOutcome(record.Sql, 0, [], error, command.Notices.ToArray()));
            }
        }
        return outcomes;
    }

    private static NzConnection OpenConnection(int port)
    {
        var connection = new NzConnection(
            "wire",
            "wire",
            "127.0.0.1",
            "JUST_DATA",
            port,
            SecurityLevelCode.OnlyUnsecuredSession)
        {
            CommandTimeout = Timeout.InfiniteTimeSpan,
            ConnectionTimeoutDuration = TimeSpan.FromSeconds(5),
        };
        connection.Open();
        return connection;
    }

    private static string Text(object? value) => value switch
    {
        null or DBNull => "NULL",
        string text => text,
        bool boolean => boolean ? "true" : "false",
        DateTime dateTime => dateTime.TimeOfDay == TimeSpan.Zero
            ? Date(dateTime)
            : Timestamp(dateTime),
        TimeSpan timeSpan => TimeText(timeSpan),
        decimal number => number.ToString(Invariant),
        double number => number.ToString("R", Invariant),
        float number => number.ToString("R", Invariant),
        IFormattable formattable => formattable.ToString(null, Invariant),
        _ => value.ToString() ?? string.Empty,
    };

    private static string Date(object? value) => Assert.IsType<DateTime>(value).ToString("yyyy-MM-dd", Invariant);

    private static string Timestamp(object? value)
    {
        string text = Assert.IsType<DateTime>(value).ToString("yyyy-MM-dd HH:mm:ss.ffffff", Invariant);
        return text.TrimEnd('0').TrimEnd('.');
    }

    private static string Time(object? value) => TimeText(Assert.IsType<TimeSpan>(value));

    private static string TimeText(TimeSpan value)
    {
        string text = value.ToString();
        if (text.Contains('.', StringComparison.Ordinal))
        {
            text = text.TrimEnd('0').TrimEnd('.');
        }
        return text;
    }
}
