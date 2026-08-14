using System.Data.Common;

namespace JustyBase.NetezzaDriver.Tests;

[Collection("Sequential")]
[Trait("Category", "Integration")]
[Trait("Category", "Stress")]
public sealed class BackendLengthStressTests
{
    private const string ExactDimDateSql = "SELECT * FROM JUST_DATA.ADMIN.DIMDATE";
    private const int ReconnectEveryQueries = 5;

    private static readonly StressQuery[] Workload =
    [
        new("dimdate-exact", ExactDimDateSql, null, 501, RequiresPreviewBoundary: true),
        new("dimdate-limit-0", DimDateLimit(0), 0, 0),
        new("dimdate-limit-499", DimDateLimit(499), 499, 499),
        new("dimdate-limit-500", DimDateLimit(500), 500, 500),
        new("dimdate-limit-501", DimDateLimit(501), 501, 501),
        new("dimdate-limit-1000", DimDateLimit(1000), 1000, 1000),
        new("dimdate-limit-1500", DimDateLimit(1500), 1500, 1500),
        new(
            "factproductinventory-limit-5000",
            "SELECT PRODUCTKEY FROM JUST_DATA..FACTPRODUCTINVENTORY ORDER BY ROWID LIMIT 5000",
            null,
            1000),
        new("dimdate-followed-by-scalar", $"{ExactDimDateSql}; SELECT 1", null, 502)
    ];

    [Fact(Timeout = 360_000)]
    public async Task ExactDimDate_RemainsReadableUnderReadOnlyBackgroundLoad()
    {
        var settings = StressSettings.FromEnvironment();
        if (!settings.Enabled)
        {
            Assert.Skip("Set NZ_STRESS=1 to run the bounded live-database stress test.");
            return;
        }

        var testCancellationToken = TestContext.Current.CancellationToken;
        using var deadlineCancellation = new CancellationTokenSource(settings.Duration);
        using var failureCancellation = new CancellationTokenSource();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            testCancellationToken,
            deadlineCancellation.Token,
            failureCancellation.Token);

        var workerTasks = Enumerable
            .Range(0, settings.Workers)
            .Select(workerId => RunWorkerAsync(
                workerId,
                settings,
                linkedCancellation.Token,
                failureCancellation))
            .ToArray();

        try
        {
            // Give the background connections a chance to establish and start
            // issuing queries before the foreground repro begins.
            await Task.Delay(TimeSpan.FromMilliseconds(100), linkedCancellation.Token);

            int foregroundRows = 0;
            await using (var connection = CreateConnection())
            {
                await connection.OpenAsync(linkedCancellation.Token);

                for (int round = 1; round <= settings.ForegroundRounds; round++)
                {
                    await using var command = connection.CreateCommand(ExactDimDateSql);
                    await using var reader = await command.ExecuteReaderAsync(linkedCancellation.Token);

                    int rows = await ReadExactBeyondPreviewBoundaryAsync(
                        reader,
                        $"foreground round {round}",
                        linkedCancellation.Token);

                    Assert.True(rows > 500, $"Foreground round {round} read only {rows} rows.");
                    foregroundRows += rows;
                }
            }

            var workerResults = await Task.WhenAll(workerTasks);
            Assert.All(
                workerResults,
                result => Assert.True(
                    result.CompletedQueries > 0,
                    $"Worker {result.WorkerId} completed no queries."));

            Console.WriteLine(
                $"Read-only stress completed: duration={settings.Duration.TotalSeconds:F0}s, " +
                $"workers={settings.Workers}, foregroundRows={foregroundRows}, " +
                $"workerQueries={workerResults.Sum(result => result.CompletedQueries)}, " +
                $"workerRows={workerResults.Sum(result => result.RowsRead)}");
        }
        catch
        {
            failureCancellation.Cancel();

            try
            {
                await Task.WhenAll(workerTasks);
            }
            catch
            {
                // The original worker exception is selected below so that a
                // foreground cancellation cannot hide the protocol failure.
            }

            Exception? workerFailure = workerTasks
                .Where(task => task.IsFaulted)
                .Select(task => task.Exception?.GetBaseException())
                .FirstOrDefault(error => error is not null);

            if (workerFailure is not null)
            {
                throw workerFailure;
            }

            throw;
        }
    }

    private static async Task<WorkerResult> RunWorkerAsync(
        int workerId,
        StressSettings settings,
        CancellationToken cancellationToken,
        CancellationTokenSource failureCancellation)
    {
        NzConnection? connection = null;
        int cycle = 0;
        int completedQueries = 0;
        long rowsRead = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (connection is null || cycle % ReconnectEveryQueries == 0)
                {
                    if (connection is not null)
                    {
                        await connection.DisposeAsync();
                    }

                    connection = CreateConnection();
                    await connection.OpenAsync(cancellationToken);
                }

                StressQuery query = Workload[cycle % Workload.Length];
                int queryRows;

                await using (var command = connection.CreateCommand(query.Sql))
                await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                {
                    queryRows = query.RequiresPreviewBoundary
                        ? await ReadExactBeyondPreviewBoundaryAsync(
                            reader,
                            $"worker {workerId}, cycle {cycle}, {query.Name}",
                            cancellationToken)
                        : await DrainAndValidateAsync(reader, query, cancellationToken);
                }

                rowsRead += queryRows;
                completedQueries++;
                cycle++;
            }

            return new WorkerResult(workerId, completedQueries, rowsRead);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new WorkerResult(workerId, completedQueries, rowsRead);
        }
        catch (Exception exception)
        {
            failureCancellation.Cancel();
            StressQuery query = Workload[cycle % Workload.Length];
            throw new Xunit.Sdk.XunitException(
                $"Read-only stress worker {workerId} failed at cycle {cycle}, " +
                $"query '{query.Name}', completedQueries={completedQueries}, rowsRead={rowsRead}. " +
                $"SQL: {query.Sql}",
                exception);
        }
        finally
        {
            if (connection is not null)
            {
                try
                {
                    await connection.DisposeAsync();
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    // Cancellation cleanup must not hide the original failure.
                }
            }
        }
    }

    private static async Task<int> DrainAndValidateAsync(
        DbDataReader reader,
        StressQuery query,
        CancellationToken cancellationToken)
    {
        int rows = 0;

        do
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rows++;
            }
        }
        while (await reader.NextResultAsync(cancellationToken));

        if (query.ExpectedRows is int expectedRows)
        {
            Assert.Equal(expectedRows, rows);
        }
        else
        {
            Assert.True(rows >= query.MinimumRows, $"Query '{query.Name}' returned {rows} rows.");
        }

        return rows;
    }

    private static async Task<int> ReadExactBeyondPreviewBoundaryAsync(
        DbDataReader reader,
        string context,
        CancellationToken cancellationToken)
    {
        int rows = 0;

        while (rows < 500)
        {
            Assert.True(
                await reader.ReadAsync(cancellationToken),
                $"The exact DIMDATE query ended at row {rows + 1} ({context}).");
            rows++;
        }

        Assert.True(
            await reader.ReadAsync(cancellationToken),
            $"The exact DIMDATE query has no row 501 ({context}).");
        rows++;

        while (await reader.ReadAsync(cancellationToken))
        {
            rows++;
        }

        return rows;
    }

    private static NzConnection CreateConnection()
    {
        return new NzConnection(
            Config.UserName,
            Config.Password,
            Config.Host,
            Config.DbName,
            Config.Port,
            loggerFactory: ProtocolTraceTestLogger.CreateIfEnabled());
    }

    private static string DimDateLimit(int limit) =>
        $"SELECT * FROM JUST_DATA.ADMIN.DIMDATE ORDER BY ROWID LIMIT {limit}";

    private sealed record StressQuery(
        string Name,
        string Sql,
        int? ExpectedRows,
        int MinimumRows,
        bool RequiresPreviewBoundary = false);

    private sealed record WorkerResult(int WorkerId, int CompletedQueries, long RowsRead);

    private readonly record struct StressSettings(
        bool Enabled,
        TimeSpan Duration,
        int Workers,
        int ForegroundRounds)
    {
        internal static StressSettings FromEnvironment()
        {
            bool enabled = string.Equals(
                Environment.GetEnvironmentVariable("NZ_STRESS"),
                "1",
                StringComparison.Ordinal);

            if (!enabled)
            {
                return new StressSettings(false, TimeSpan.Zero, 0, 0);
            }

            int durationSeconds = ReadBoundedInt("NZ_STRESS_DURATION_SECONDS", 30, 1, 300);
            int workers = ReadBoundedInt("NZ_STRESS_WORKERS", 4, 1, 16);
            int foregroundRounds = ReadBoundedInt("NZ_STRESS_FOREGROUND_ROUNDS", 10, 1, 100);

            return new StressSettings(
                true,
                TimeSpan.FromSeconds(durationSeconds),
                workers,
                foregroundRounds);
        }

        private static int ReadBoundedInt(string variableName, int defaultValue, int minimum, int maximum)
        {
            string? raw = Environment.GetEnvironmentVariable(variableName);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return defaultValue;
            }

            if (!int.TryParse(raw, out int value) || value < minimum || value > maximum)
            {
                throw new Xunit.Sdk.XunitException(
                    $"Environment variable {variableName} must be an integer in range " +
                    $"{minimum}..{maximum}; received '{raw}'.");
            }

            return value;
        }
    }
}
