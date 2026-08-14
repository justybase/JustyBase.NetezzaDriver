using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using JustyBase.NetezzaDriver;
using Microsoft.Extensions.Logging;

namespace JustyBase.NetezzaDriver.Repro;

internal static class ScenarioRunner
{
    private const int PreviewRowLimit = 500;
    private const string DefaultSql = "SELECT * FROM JUST_DATA.ADMIN.DIMDATE";

    public static async Task<int> RunAsync(string implementation, string[] args)
    {
        Options options = Options.Parse(args);

        if (options.Mode is "help" or "--help" or "-h")
        {
            PrintUsage();
            return 0;
        }

        Console.WriteLine($"Implementation: {implementation}");
        Console.WriteLine($"Mode: {options.Mode}");
        Console.WriteLine($"SQL: {options.Sql}");
        Console.WriteLine($"Database: {options.Database.Host}:{options.Database.Port}/{options.Database.Database}");
        Console.WriteLine($"Preview boundary: {PreviewRowLimit} rows");

        return options.Mode switch
        {
            "matrix" => await RunMatrixAsync(options),
            "minimal-sync" => await RunScenarioAsync("minimal-sync", options, RunMinimalSyncAsync),
            "app-sync" => await RunScenarioAsync("app-sync", options, RunAppSyncAsync),
            "app-async" => await RunScenarioAsync("app-async", options, RunAppAsyncAsync),
            "reuse" => await RunScenarioAsync("reuse", options, RunReuseAsync),
            "close-reuse" => await RunScenarioAsync("close-reuse", options, RunCloseReuseAsync),
            "cancel-reuse" => await RunScenarioAsync("cancel-reuse", options, RunCancelReuseAsync),
            "repeated" => await RunScenarioAsync("repeated", options, RunRepeatedAsync),
            "multi-result" => await RunScenarioAsync("multi-result", options, RunMultiResultAsync),
            "slow-async" => await RunScenarioAsync("slow-async", options, RunSlowAsync),
            "stress" => await RunScenarioAsync("stress", options, RunStressAsync),
            _ => UnknownMode(options.Mode)
        };
    }

    private static async Task<int> RunMatrixAsync(Options options)
    {
        string[] modes =
        [
            "minimal-sync",
            "app-sync",
            "app-async",
            "reuse",
            "close-reuse",
            "repeated",
            "multi-result"
        ];

        bool allPassed = true;
        foreach (string mode in modes)
        {
            allPassed &= await RunScenarioAsync(
                mode,
                options with { Mode = mode },
                GetScenario(mode)) == 0;
        }

        Console.WriteLine(allPassed ? "MATRIX PASS" : "MATRIX FAIL");
        return allPassed ? 0 : 1;
    }

    private static ScenarioDelegate GetScenario(string mode) => mode switch
    {
        "minimal-sync" => RunMinimalSyncAsync,
        "app-sync" => RunAppSyncAsync,
        "app-async" => RunAppAsyncAsync,
        "reuse" => RunReuseAsync,
        "close-reuse" => RunCloseReuseAsync,
        "cancel-reuse" => RunCancelReuseAsync,
        "repeated" => RunRepeatedAsync,
        "multi-result" => RunMultiResultAsync,
        "slow-async" => RunSlowAsync,
        "stress" => RunStressAsync,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown scenario")
    };

    private static async Task<int> RunScenarioAsync(
        string name,
        Options options,
        ScenarioDelegate scenario)
    {
        var context = new ScenarioContext(name);
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            ScenarioResult result = await scenario(options, context);
            Console.WriteLine(
                $"PASS {name}: rows={result.Rows}, columns={result.Columns}, " +
                $"elapsed={stopwatch.Elapsed.TotalSeconds:F2}s{result.Details}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine(
                $"FAIL {name}: phase={context.Phase}, row={context.Row}, " +
                $"column={context.Column}, elapsed={stopwatch.Elapsed.TotalSeconds:F2}s");
            Console.WriteLine(exception);
            return 1;
        }
    }

    private static Task<ScenarioResult> RunMinimalSyncAsync(Options options, ScenarioContext context)
    {
        using NzConnection connection = Open(options, context);
        return Task.FromResult(ReadMinimalSync(connection, options, context));
    }

    private static Task<ScenarioResult> RunAppSyncAsync(Options options, ScenarioContext context)
    {
        using NzConnection connection = Open(options, context);
        return Task.FromResult(ReadAppSync(connection, options, context));
    }

    private static async Task<ScenarioResult> RunAppAsyncAsync(Options options, ScenarioContext context)
    {
        using NzConnection connection = await OpenAsync(options, context, CancellationToken.None);
        return await ReadAppAsync(connection, options, context, CancellationToken.None);
    }

    private static Task<ScenarioResult> RunReuseAsync(Options options, ScenarioContext context)
    {
        using NzConnection connection = Open(options, context);

        ScenarioResult first = ReadAppSync(connection, options, context);
        ReadScalar(connection, context, "after fully draining the first result");
        ScenarioResult second = ReadAppSync(connection, options, context);

        return Task.FromResult(new ScenarioResult(
            second.Rows,
            second.Columns,
            $"; first-read-rows={first.Rows}; scalar-after-first=PASS"));
    }

    private static Task<ScenarioResult> RunCloseReuseAsync(Options options, ScenarioContext context)
    {
        using NzConnection connection = Open(options, context);

        int columns;
        int rows = 0;
        context.Phase = "first command ExecuteReader";
        using (NzCommand command = connection.CreateCommand(options.Sql))
        using (DbDataReader reader = command.ExecuteReader(CommandBehavior.SequentialAccess))
        {
            columns = PrepareReader(reader, context, inspectSchema: true);
            for (int i = 0; i < PreviewRowLimit; i++)
            {
                ReadCurrentRow(reader, context, ++rows, columns, materialize: true, "preview-before-close");
            }

            context.Phase = "first reader.Dispose at preview boundary";
        }

        ReadScalar(connection, context, "after disposing at row 500 without draining the result");
        ScenarioResult second = ReadAppSync(connection, options, context);

        return Task.FromResult(new ScenarioResult(
            second.Rows,
            second.Columns,
            $"; rows-before-close={rows}; scalar-after-close=PASS"));
    }

    private static Task<ScenarioResult> RunCancelReuseAsync(Options options, ScenarioContext context)
    {
        using NzConnection connection = Open(options, context);

        int columns;
        int rows = 0;
        context.Phase = "first command ExecuteReader";
        using (NzCommand command = connection.CreateCommand(options.Sql))
        using (DbDataReader reader = command.ExecuteReader(CommandBehavior.SequentialAccess))
        {
            columns = PrepareReader(reader, context, inspectSchema: true);
            for (int i = 0; i < PreviewRowLimit; i++)
            {
                ReadCurrentRow(reader, context, ++rows, columns, materialize: true, "preview-before-cancel");
            }

            context.Phase = "command.Cancel at preview boundary";
            command.Cancel();
            context.Phase = "reader.Dispose after command.Cancel";
        }

        ReadScalar(connection, context, "after cancelling at row 500");
        ScenarioResult second = ReadAppSync(connection, options, context);

        return Task.FromResult(new ScenarioResult(
            second.Rows,
            second.Columns,
            $"; rows-before-cancel={rows}; scalar-after-cancel=PASS"));
    }

    private static Task<ScenarioResult> RunRepeatedAsync(Options options, ScenarioContext context)
    {
        using NzConnection connection = Open(options, context);

        ScenarioResult first = ReadAppSync(connection, options, context);
        ScenarioResult second = ReadAppSync(connection, options, context);

        return Task.FromResult(new ScenarioResult(
            second.Rows,
            second.Columns,
            $"; first-read-rows={first.Rows}; second-read-rows={second.Rows}"));
    }

    private static Task<ScenarioResult> RunMultiResultAsync(Options options, ScenarioContext context)
    {
        using NzConnection connection = Open(options, context);
        context.Phase = "multi-result ExecuteReader";

        using NzCommand command = connection.CreateCommand($"{options.Sql}; SELECT 1");
        using DbDataReader reader = command.ExecuteReader(CommandBehavior.SequentialAccess);

        ScenarioResult first = DrainSync(reader, context, inspectSchema: true, materialize: true);

        context.Phase = "reader.NextResult after exact DIMDATE result";
        if (!reader.NextResult())
        {
            throw new InvalidOperationException("The exact query did not expose the expected second result set.");
        }

        context.Phase = "second result FieldCount";
        ValidateScalarOne(reader, context, "second result SELECT 1");

        return Task.FromResult(new ScenarioResult(
            first.Rows,
            first.Columns,
            "; second-result=SELECT 1 PASS"));
    }

    private static async Task<ScenarioResult> RunSlowAsync(Options options, ScenarioContext context)
    {
        using NzConnection connection = await OpenAsync(options, context, CancellationToken.None);
        context.Phase = "slow ExecuteReaderAsync";
        using NzCommand command = connection.CreateCommand(options.Sql);
        using DbDataReader reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess,
            CancellationToken.None);

        int columns = PrepareReader(reader, context, inspectSchema: true);
        int rows = 0;

        while (rows < PreviewRowLimit)
        {
            ReadAsyncRowContext(context, ++rows, "slow-preview");
            if (!await reader.ReadAsync(CancellationToken.None))
            {
                throw new InvalidOperationException($"The exact query ended before row {rows} in slow preview.");
            }

            MaterializeCurrentRow(reader, context, rows, columns);
            await DelayBetweenRowsAsync(options, CancellationToken.None);
        }

        int firstRestRow = rows + 1;
        ReadAsyncRowContext(context, firstRestRow, "slow first rest reader.ReadAsync");
        if (!await reader.ReadAsync(CancellationToken.None))
        {
            throw new InvalidOperationException(
                $"The exact query ended at the preview boundary; row {firstRestRow} was required.");
        }

        rows++;
        MaterializeCurrentRow(reader, context, rows, columns);
        await DelayBetweenRowsAsync(options, CancellationToken.None);

        while (true)
        {
            ReadAsyncRowContext(context, rows + 1, "slow-rest");
            if (!await reader.ReadAsync(CancellationToken.None))
            {
                break;
            }

            rows++;
            MaterializeCurrentRow(reader, context, rows, columns);
            await DelayBetweenRowsAsync(options, CancellationToken.None);
        }

        return new ScenarioResult(rows, columns, $"; row-delay-ms={options.RowDelayMilliseconds}");
    }

    private static async Task<ScenarioResult> RunStressAsync(Options options, ScenarioContext context)
    {
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromSeconds(options.StressSeconds));
        var failures = new ConcurrentBag<string>();
        long totalRounds = 0;
        var workers = new Task[options.StressWorkers];

        for (int workerId = 0; workerId < workers.Length; workerId++)
        {
            int capturedWorkerId = workerId;
            workers[workerId] = StressWorkerAsync(
                capturedWorkerId,
                options,
                cancellation,
                failures,
                () => Interlocked.Increment(ref totalRounds));
        }

        await Task.WhenAll(workers);

        if (!failures.IsEmpty)
        {
            throw new InvalidOperationException(
                "Stress failures:\n" + string.Join("\n", failures));
        }

        if (totalRounds == 0)
        {
            throw new InvalidOperationException("Stress finished without completing one full query round.");
        }

        return new ScenarioResult(
            checked((int)Math.Min(totalRounds, int.MaxValue)),
            0,
            $"; workers={options.StressWorkers}; seconds={options.StressSeconds}; rounds={totalRounds}");
    }

    private static async Task StressWorkerAsync(
        int workerId,
        Options options,
        CancellationTokenSource cancellation,
        ConcurrentBag<string> failures,
        Action completedRound)
    {
        var context = new ScenarioContext($"stress-worker-{workerId}");

        try
        {
            // Let an active result finish before stopping the worker. Passing the
            // deadline token into ReadAsync would make reader.Dispose() run while
            // the protocol is still being cancelled, which is a separate abort
            // scenario covered by cancel-reuse.
            using NzConnection connection = await OpenAsync(options, context, CancellationToken.None);

            while (!cancellation.IsCancellationRequested)
            {
                ScenarioResult result = await ReadAppAsync(
                    connection,
                    options,
                    context,
                    CancellationToken.None);

                ReadScalar(connection, context, "between stress rounds");
                completedRound();

                if (result.Rows < PreviewRowLimit + 1)
                {
                    throw new InvalidOperationException(
                        $"Stress query returned only {result.Rows} rows; row 501 is required.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            failures.Add(
                $"worker={workerId}, phase={context.Phase}, row={context.Row}, " +
                $"column={context.Column}: {exception}");
            cancellation.Cancel();
        }
    }

    private static ScenarioResult ReadMinimalSync(
        NzConnection connection,
        Options options,
        ScenarioContext context)
    {
        context.Phase = "minimal ExecuteReader";
        using NzCommand command = connection.CreateCommand(options.Sql);
        using DbDataReader reader = command.ExecuteReader(CommandBehavior.Default);

        int columns = reader.FieldCount;
        int rows = 0;

        for (int i = 0; i < PreviewRowLimit; i++)
        {
            ReadCurrentRow(reader, context, ++rows, columns, materialize: false, "minimal-preview");
        }

        context.Phase = "minimal first rest reader.Read";
        context.Row = rows + 1;
        if (!reader.Read())
        {
            throw new InvalidOperationException(
                $"The exact query ended at the preview boundary; row {rows + 1} was required.");
        }

        rows++;
        while (true)
        {
            context.Phase = "minimal rest reader.Read";
            context.Row = rows + 1;
            if (!reader.Read())
            {
                break;
            }

            rows++;
        }

        return new ScenarioResult(rows, columns, "; no GetValue/schema calls");
    }

    private static ScenarioResult ReadAppSync(
        NzConnection connection,
        Options options,
        ScenarioContext context)
    {
        context.Phase = "app ExecuteReader SequentialAccess";
        using NzCommand command = connection.CreateCommand(options.Sql);
        using DbDataReader reader = command.ExecuteReader(CommandBehavior.SequentialAccess);
        return DrainSync(reader, context, inspectSchema: true, materialize: true);
    }

    private static async Task<ScenarioResult> ReadAppAsync(
        NzConnection connection,
        Options options,
        ScenarioContext context,
        CancellationToken cancellationToken)
    {
        context.Phase = "app ExecuteReaderAsync SequentialAccess";
        using NzCommand command = connection.CreateCommand(options.Sql);
        using DbDataReader reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess,
            cancellationToken);

        return await DrainAsync(
            reader,
            context,
            inspectSchema: true,
            materialize: true,
            cancellationToken,
            rowDelayMilliseconds: 0);
    }

    private static ScenarioResult DrainSync(
        DbDataReader reader,
        ScenarioContext context,
        bool inspectSchema,
        bool materialize)
    {
        int columns = PrepareReader(reader, context, inspectSchema);
        int rows = 0;

        for (int i = 0; i < PreviewRowLimit; i++)
        {
            ReadCurrentRow(reader, context, ++rows, columns, materialize, "preview");
        }

        context.Phase = "rest first reader.Read (row 501)";
        context.Row = rows + 1;
        if (!reader.Read())
        {
            throw new InvalidOperationException(
                $"The exact query ended at the preview boundary; row {rows + 1} was required.");
        }

        rows++;
        if (materialize)
        {
            MaterializeCurrentRow(reader, context, rows, columns);
        }

        while (true)
        {
            context.Phase = "rest reader.Read";
            context.Row = rows + 1;
            if (!reader.Read())
            {
                break;
            }

            rows++;
            if (materialize)
            {
                MaterializeCurrentRow(reader, context, rows, columns);
            }
        }

        return new ScenarioResult(rows, columns);
    }

    private static async Task<ScenarioResult> DrainAsync(
        DbDataReader reader,
        ScenarioContext context,
        bool inspectSchema,
        bool materialize,
        CancellationToken cancellationToken,
        int rowDelayMilliseconds)
    {
        int columns = PrepareReader(reader, context, inspectSchema);
        int rows = 0;

        for (int i = 0; i < PreviewRowLimit; i++)
        {
            int expectedRow = rows + 1;
            ReadAsyncRowContext(context, expectedRow, "preview reader.ReadAsync");
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException($"The exact query ended before row {expectedRow}.");
            }

            rows++;
            if (materialize)
            {
                MaterializeCurrentRow(reader, context, rows, columns);
            }

            if (rowDelayMilliseconds > 0)
            {
                await Task.Delay(rowDelayMilliseconds, cancellationToken);
            }
        }

        int firstRestRow = rows + 1;
        ReadAsyncRowContext(context, firstRestRow, "rest first reader.ReadAsync (row 501)");
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                $"The exact query ended at the preview boundary; row {firstRestRow} was required.");
        }

        rows++;
        if (materialize)
        {
            MaterializeCurrentRow(reader, context, rows, columns);
        }

        if (rowDelayMilliseconds > 0)
        {
            await Task.Delay(rowDelayMilliseconds, cancellationToken);
        }

        while (true)
        {
            int expectedRow = rows + 1;
            ReadAsyncRowContext(context, expectedRow, "rest reader.ReadAsync");
            if (!await reader.ReadAsync(cancellationToken))
            {
                break;
            }

            rows++;
            if (materialize)
            {
                MaterializeCurrentRow(reader, context, rows, columns);
            }

            if (rowDelayMilliseconds > 0)
            {
                await Task.Delay(rowDelayMilliseconds, cancellationToken);
            }
        }

        return new ScenarioResult(rows, columns);
    }

    private static int PrepareReader(
        DbDataReader reader,
        ScenarioContext context,
        bool inspectSchema)
    {
        context.Phase = "reader.HasRows";
        if (!reader.HasRows)
        {
            throw new InvalidOperationException("The exact query returned no rows.");
        }

        context.Phase = "reader.FieldCount";
        int columns = reader.FieldCount;
        if (columns <= 0)
        {
            throw new InvalidOperationException("The exact query returned no columns.");
        }

        if (inspectSchema)
        {
            context.Phase = "reader.GetSchemaTable";
            _ = reader.GetSchemaTable();

            for (int column = 0; column < columns; column++)
            {
                context.Column = column;
                context.Phase = "reader metadata";
                _ = reader.GetName(column);
                _ = reader.GetDataTypeName(column);
                _ = reader.GetFieldType(column);
            }
        }

        context.Column = -1;
        return columns;
    }

    private static void ReadCurrentRow(
        DbDataReader reader,
        ScenarioContext context,
        int row,
        int columns,
        bool materialize,
        string phase)
    {
        context.Phase = $"{phase} reader.Read";
        context.Row = row;
        context.Column = -1;

        if (!reader.Read())
        {
            throw new InvalidOperationException($"The exact query ended before row {row}.");
        }

        if (materialize)
        {
            MaterializeCurrentRow(reader, context, row, columns);
        }
    }

    private static void ReadAsyncRowContext(ScenarioContext context, int row, string phase)
    {
        context.Phase = phase;
        context.Row = row;
        context.Column = -1;
    }

    private static void MaterializeCurrentRow(
        DbDataReader reader,
        ScenarioContext context,
        int row,
        int columns)
    {
        context.Phase = "row GetValue for every column";
        context.Row = row;

        for (int column = 0; column < columns; column++)
        {
            context.Column = column;
            _ = reader.GetValue(column);
        }

        context.Column = -1;
    }

    private static void ReadScalar(
        NzConnection connection,
        ScenarioContext context,
        string description)
    {
        context.Phase = $"scalar SELECT 1 ({description})";
        using NzCommand command = connection.CreateCommand("SELECT 1");
        using DbDataReader reader = command.ExecuteReader();

        ValidateScalarOne(reader, context, description);
    }

    private static void ValidateScalarOne(
        DbDataReader reader,
        ScenarioContext context,
        string description)
    {
        context.Phase = $"{description} FieldCount";
        if (reader.FieldCount != 1)
        {
            throw new InvalidOperationException(
                $"{description} returned {reader.FieldCount} columns; exactly one was required.");
        }

        context.Row = 1;
        if (!reader.Read())
        {
            throw new InvalidOperationException($"{description} returned no row.");
        }

        context.Phase = $"{description} GetValue";
        context.Column = 0;
        object value = reader.GetValue(0);
        int actualValue = Convert.ToInt32(value, CultureInfo.InvariantCulture);
        if (actualValue != 1)
        {
            throw new InvalidOperationException(
                $"{description} returned value {value}; expected numeric value 1.");
        }

        context.Column = -1;

        context.Phase = $"{description} EOF";
        if (reader.Read())
        {
            throw new InvalidOperationException($"{description} returned more than one row.");
        }
    }

    private static NzConnection Open(Options options, ScenarioContext context)
    {
        context.Phase = "connection.Open";
        NzConnection connection = CreateConnection(options);
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static async Task<NzConnection> OpenAsync(
        Options options,
        ScenarioContext context,
        CancellationToken cancellationToken)
    {
        context.Phase = "connection.OpenAsync";
        NzConnection connection = CreateConnection(options);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static NzConnection CreateConnection(Options options)
    {
        return new NzConnection(
            options.Database.User,
            options.Database.Password,
            options.Database.Host,
            options.Database.Database,
            options.Database.Port,
            loggerFactory: TraceLoggerFactory.CreateIfEnabled());
    }

    private static async Task DelayBetweenRowsAsync(Options options, CancellationToken cancellationToken)
    {
        if (options.RowDelayMilliseconds > 0)
        {
            await Task.Delay(options.RowDelayMilliseconds, cancellationToken);
        }
    }

    private static int UnknownMode(string mode)
    {
        Console.Error.WriteLine($"Unknown mode '{mode}'. Use 'help' to list scenarios.");
        return 2;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: dotnet run --project <project> -- <mode>");
        Console.WriteLine();
        Console.WriteLine("Modes:");
        Console.WriteLine("  matrix         Run the normal and lifecycle scenarios sequentially.");
        Console.WriteLine("  minimal-sync   Exact SQL, sync Read(), 500-row boundary, no GetValue().");
        Console.WriteLine("  app-sync       App-like sync path: SequentialAccess, HasRows, schema, GetValue().");
        Console.WriteLine("  app-async      App-like async path with ReadAsync() and GetValue().");
        Console.WriteLine("  reuse          Drain exact result, SELECT 1, then drain it again on one connection.");
        Console.WriteLine("  close-reuse    Dispose reader exactly at row 500, reuse connection, then read again.");
        Console.WriteLine("  cancel-reuse   Cancel exactly at row 500, reuse connection, then read again.");
        Console.WriteLine("  repeated       Read the exact query twice on one connection.");
        Console.WriteLine("  multi-result   Exact query followed by SELECT 1, then NextResult().");
        Console.WriteLine("  slow-async     App-like async path with a delay after every row.");
        Console.WriteLine("  stress         Multiple independent connections repeatedly run app-like reads.");
        Console.WriteLine();
        Console.WriteLine("Environment:");
        Console.WriteLine("  NZ_DEV_HOST, NZ_DEV_PORT, NZ_DEV_DB/NZ_DEV_DATABASE, NZ_DEV_USER, NZ_DEV_PASSWORD");
        Console.WriteLine("  NZ_REPRO_SQL (defaults to SELECT * FROM JUST_DATA.ADMIN.DIMDATE)");
        Console.WriteLine("  NZ_PROTOCOL_TRACE=1, NZ_REPRO_STRESS_SECONDS=30, NZ_REPRO_STRESS_WORKERS=4");
        Console.WriteLine("  NZ_REPRO_ROW_DELAY_MS=5 (used by slow-async; default is 0)");
    }

    private delegate Task<ScenarioResult> ScenarioDelegate(Options options, ScenarioContext context);

    private sealed record ScenarioResult(int Rows, int Columns, string Details = "");

    private sealed class ScenarioContext(string name)
    {
        public string Name { get; } = name;
        public string Phase { get; set; } = "not started";
        public int Row { get; set; } = 0;
        public int Column { get; set; } = -1;
    }

    private sealed record DatabaseSettings(
        string Host,
        int Port,
        string Database,
        string User,
        string Password);

    private sealed record Options(
        string Mode,
        DatabaseSettings Database,
        string Sql,
        int StressSeconds,
        int StressWorkers,
        int RowDelayMilliseconds)
    {
        public static Options Parse(string[] args)
        {
            string mode = GetOption(args, "mode")
                ?? args.FirstOrDefault(argument => !argument.StartsWith("--", StringComparison.Ordinal))
                ?? "matrix";

            string host = Environment.GetEnvironmentVariable("NZ_DEV_HOST") ?? "192.168.0.144";
            int port = GetInt(args, "port", "NZ_DEV_PORT", 5480);
            string database = Environment.GetEnvironmentVariable("NZ_DEV_DB")
                ?? Environment.GetEnvironmentVariable("NZ_DEV_DATABASE")
                ?? "JUST_DATA";
            string user = Environment.GetEnvironmentVariable("NZ_DEV_USER") ?? "admin";
            string password = Environment.GetEnvironmentVariable("NZ_DEV_PASSWORD") ?? "password";
            string? configuredSql = Environment.GetEnvironmentVariable("NZ_REPRO_SQL");
            string sql;
            if (string.IsNullOrWhiteSpace(configuredSql))
            {
                sql = DefaultSql;
            }
            else
            {
                sql = configuredSql;
            }

            int stressSeconds = GetInt(args, "seconds", "NZ_REPRO_STRESS_SECONDS", 30, minimum: 1);
            int stressWorkers = GetInt(args, "workers", "NZ_REPRO_STRESS_WORKERS", 4, minimum: 1);
            int rowDelayMilliseconds = GetInt(args, "row-delay-ms", "NZ_REPRO_ROW_DELAY_MS", 0, minimum: 0);

            return new Options(
                mode.ToLowerInvariant(),
                new DatabaseSettings(host, port, database, user, password),
                sql,
                stressSeconds,
                stressWorkers,
                rowDelayMilliseconds);
        }

        private static string? GetOption(string[] args, string name)
        {
            string prefix = $"--{name}=";
            string? value = args.FirstOrDefault(argument =>
                argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            return value is null ? null : value[prefix.Length..];
        }

        private static int GetInt(
            string[] args,
            string argumentName,
            string environmentName,
            int fallback,
            int minimum = int.MinValue)
        {
            string? value = GetOption(args, argumentName)
                ?? Environment.GetEnvironmentVariable(environmentName);

            if (!int.TryParse(value, out int parsed) || parsed < minimum)
            {
                return fallback;
            }

            return parsed;
        }
    }

    private static class TraceLoggerFactory
    {
        public static ILoggerFactory? CreateIfEnabled()
        {
            return string.Equals(
                Environment.GetEnvironmentVariable("NZ_PROTOCOL_TRACE"),
                "1",
                StringComparison.Ordinal)
                ? new ConsoleFactory()
                : null;
        }

        private sealed class ConsoleFactory : ILoggerFactory
        {
            public ILogger CreateLogger(string categoryName) => new ConsoleLogger();

            public void AddProvider(ILoggerProvider provider)
            {
            }

            public void Dispose()
            {
            }
        }

        private sealed class ConsoleLogger : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                string message = formatter(state, exception);
                if (message.Contains("Backend response:", StringComparison.Ordinal)
                    || message.Contains("Backend protocol length", StringComparison.Ordinal)
                    || message.Contains("RowStandard descriptor", StringComparison.Ordinal)
                    || message.Contains("RowStandard payload", StringComparison.Ordinal))
                {
                    Console.WriteLine(message);
                }
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
