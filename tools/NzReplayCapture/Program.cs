using System.Diagnostics;
using JustyBase.NetezzaDriver;
using JustyBase.NetezzaDriver.TestSupport;

// Records byte-exact Netezza responses through RecordingProxy and stores them
// as NzReplayFixture files that ReplayReaderBench / the replay tests can use
// without a database.
//
// Usage:
//   NZ_DEV_HOST=... NZ_DEV_USER=... NZ_DEV_PASSWORD=... NZ_DEV_DB=... \
//     dotnet run -c Release --project tools/NzReplayCapture -- [outputDir]

string host = Environment.GetEnvironmentVariable("NZ_DEV_HOST") ?? "192.168.0.144";
int port = int.TryParse(Environment.GetEnvironmentVariable("NZ_DEV_PORT"), out var p) ? p : 5480;
string database = Environment.GetEnvironmentVariable("NZ_DEV_DB") ?? "JUST_DATA";
string user = Environment.GetEnvironmentVariable("NZ_DEV_USER") ?? "admin";
string password = Environment.GetEnvironmentVariable("NZ_DEV_PASSWORD") ?? "password";

if (args.Length > 0 && args[0] == "replay-check")
{
    await ReplayCheckAsync();
    return;
}

string outputDir = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.Combine(FindRepoRoot(), "src", "JustyBase.NetezzaDriver.TestSupport", "Fixtures");
Directory.CreateDirectory(outputDir);

(string Name, string Sql)[] scenarios =
[
    ("dimdate", "SELECT * FROM JUST_DATA.ADMIN.DIMDATE"),
    ("fact200k", "SELECT * FROM JUST_DATA..FACTPRODUCTINVENTORY ORDER BY ROWID LIMIT 200000"),
];

Console.WriteLine($"Recording {scenarios.Length} scenario(s) to {outputDir}");

foreach (var (name, sql) in scenarios)
{
    Console.WriteLine();
    Console.WriteLine($"[{name}] {sql}");

    await using var proxy = RecordingProxy.Start(host, port);
    var connection = new NzConnection(user, password, "127.0.0.1", database, proxy.Port,
        SecurityLevelCode.OnlyUnsecuredSession)
    {
        CommandTimeout = Timeout.InfiniteTimeSpan,
    };

    connection.Open();

    int rows = 0;
    int columns;
    var stopwatch = Stopwatch.StartNew();
    using (var command = connection.CreateCommand(sql))
    using (var reader = command.ExecuteReader())
    {
        columns = reader.FieldCount;
        while (reader.Read())
        {
            for (int i = 0; i < columns; i++)
            {
                _ = reader.GetValue(i);
            }
            rows++;
        }
    }
    stopwatch.Stop();
    connection.Close();

    await proxy.WaitForCompletionAsync();

    byte[][] segments = proxy.Segments;
    var fixture = new NzReplayFixture
    {
        Query = sql,
        ExpectedRows = rows,
        ExpectedColumns = columns,
        Segments = segments,
    };

    string path = Path.Combine(outputDir, $"{name}.nzreplay.gz");
    fixture.Save(path);

    long length = new FileInfo(path).Length;
    long responseBytes = segments.Skip(1).Sum(s => (long)s.Length);
    Console.WriteLine(
        $"  rows={rows} columns={columns} " +
        $"segments={segments.Length} response={responseBytes:N0}B " +
        $"file={length:N0}B live={stopwatch.ElapsedMilliseconds}ms");
}

Console.WriteLine();
Console.WriteLine("Done.");

static async Task ReplayCheckAsync()
{
    var fixture = NzReplayFixture.LoadShipped("dimdate.nzreplay.gz");
    Console.WriteLine($"fixture rows={fixture.ExpectedRows} cols={fixture.ExpectedColumns} segments={fixture.Segments.Length}");

    await using var server = NzReplayServer.Start(fixture);
    Console.WriteLine($"server port={server.Port}");

    var connection = new NzConnection("replay", "replay", "127.0.0.1", "JUST_DATA", server.Port,
        SecurityLevelCode.OnlyUnsecuredSession,
        loggerFactory: Environment.GetEnvironmentVariable("NZ_REPLAY_TRACE") == "1" ? new SimpleLoggerFactory() : null)
    {
        CommandTimeout = Timeout.InfiniteTimeSpan,
    };
    Console.WriteLine("opening...");
    connection.Open();
    Console.WriteLine("opened");

    for (int i = 0; i < 3; i++)
    {
        Console.WriteLine($"execute #{i + 1}...");
        using var command = connection.CreateCommand(fixture.Query);
        using var reader = command.ExecuteReader();
        int rows = 0;
        while (reader.Read())
        {
            rows++;
        }
        Console.WriteLine($"  rows={rows}");
    }

    connection.Close();
    Console.WriteLine("closed");
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "src", "JustyBase.NetezzaDriver.slnx")))
            return dir.FullName;
        dir = dir.Parent;
    }
    return Directory.GetCurrentDirectory();
}

sealed class SimpleLoggerFactory : Microsoft.Extensions.Logging.ILoggerFactory
{
    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new SimpleLogger();
    public void AddProvider(Microsoft.Extensions.Logging.ILoggerProvider provider) { }
    public void Dispose() { }
}

sealed class SimpleLogger : Microsoft.Extensions.Logging.ILogger
{
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => new NoopScope();
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Console.Error.WriteLine($"[nz] {formatter(state, exception)}");

    private sealed class NoopScope : IDisposable
    {
        public void Dispose() { }
    }
}
