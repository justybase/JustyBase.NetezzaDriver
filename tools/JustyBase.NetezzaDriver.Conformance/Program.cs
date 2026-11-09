using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using JustyBase.NetezzaDriver;

namespace JustyBase.NetezzaDriver.Conformance;

internal static class Program
{
    private static readonly HashSet<string> IntegerClasses =
        new(StringComparer.Ordinal) { "int1", "int2", "int4", "int8" };

    private static readonly Dictionary<int, string> OidClass = new()
    {
        [16] = "bool",
        [17] = "bytes",
        [18] = "char",
        [19] = "text",
        [20] = "int8",
        [21] = "int2",
        [23] = "int4",
        [25] = "text",
        [26] = "int4",
        [700] = "float4",
        [701] = "float8",
        [705] = "unknown",
        [1042] = "char",
        [1043] = "text",
        [1082] = "date",
        [1083] = "time",
        [1114] = "timestamp",
        [1184] = "timestamp",
        [1186] = "interval",
        [1266] = "timetz",
        [1700] = "numeric",
        [2500] = "int1",
        [2522] = "nchar",
        [2530] = "nvarchar",
        [2569] = "bytes",
        [2950] = "text",
        [3614] = "text",
        [3802] = "text",
        [114] = "text",
    };

    private sealed record Settings(
        string Host,
        int Port,
        string Database,
        string User,
        string Password,
        string Schema,
        int CommandTimeout);

    private static string Classify(int oid) => OidClass.TryGetValue(oid, out var value) ? value : "unknown";

    public static async Task<int> Main(string[] args)
    {
        var profile = string.Empty;
        var filters = new List<string>();
        var casesDir = string.Empty;
        string? output = null;
        var failFast = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--profile": profile = args[++i]; break;
                case "--case": filters.Add(args[++i]); break;
                case "--cases-dir": casesDir = args[++i]; break;
                case "--out": output = args[++i]; break;
                case "--fail-fast": failFast = true; break;
                default:
                    Console.Error.WriteLine($"csharp runner: unknown argument {args[i]}");
                    return 2;
            }
        }

        if (string.IsNullOrEmpty(profile))
        {
            Console.Error.WriteLine("csharp runner: --profile is required");
            return 2;
        }

        if (string.IsNullOrEmpty(casesDir))
        {
            casesDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "conformance", "cases");
            casesDir = Path.GetFullPath(casesDir);
        }

        var settings = LoadSettings();
        if (settings is null) return 2;

        var cases = LoadCases(casesDir)
            .Where(item => filters.Count == 0
                ? (profile == "full"
                    ? item.Profile is "full" or "smoke"
                    : item.Profile == profile)
                : filters.Any(needle => item.Id.Contains(needle, StringComparison.Ordinal)))
            .ToList();
        if (cases.Count == 0)
        {
            Console.Error.WriteLine("csharp runner: no cases selected");
            return 2;
        }

        await using var connection = new NzConnection(
            settings.User,
            settings.Password,
            settings.Host,
            settings.Database,
            settings.Port);
        await connection.OpenAsync();

        await using var writer = output is null
            ? Console.Out
            : new StreamWriter(output, false, new UTF8Encoding(false));

        var failures = 0;
        foreach (var item in cases)
        {
            var result = item.Api is null
                ? await ExecuteCaseAsync(connection, settings, item)
                : await ExecuteApiCaseAsync(connection, settings, item);
            await writer.WriteLineAsync(JsonSerializer.Serialize(result, JsonOptions));
            await writer.FlushAsync();
            if (result.Status != "pass")
            {
                Console.Error.WriteLine($"{result.Status,-5} {item.Id} ({result.DurationMs} ms)");
            }
            if (result.Status == "error")
            {
                failures++;
                if (failFast) break;
            }
        }

        return failures == 0 ? 0 : 1;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    private sealed class Case
    {
        public string Id { get; init; } = string.Empty;
        public string Profile { get; init; } = "full";
        public string Sql { get; init; } = string.Empty;
        public bool MultiStatement { get; init; }
        public double? Timeout { get; init; }
        public JsonElement? Api { get; init; }
        public List<Param> Params { get; init; } = new();
        public List<string> Setup { get; init; } = new();
        public List<string> Cleanup { get; init; } = new();
        public Dictionary<string, string> SqlOverrides { get; init; } = new();
    }

    private sealed class Param
    {
        public string Type { get; init; } = string.Empty;
        public JsonElement Value { get; init; }
    }

    private static Settings? LoadSettings()
    {
        var host = Environment.GetEnvironmentVariable("NZ_DEV_HOST");
        var user = Environment.GetEnvironmentVariable("NZ_DEV_USER");
        var password = Environment.GetEnvironmentVariable("NZ_DEV_PASSWORD");
        var database = Environment.GetEnvironmentVariable("NZ_DEV_DB")
            ?? Environment.GetEnvironmentVariable("NZ_DEV_DATABASE");
        var missing = new List<string>();
        if (string.IsNullOrEmpty(host)) missing.Add("NZ_DEV_HOST");
        if (string.IsNullOrEmpty(user)) missing.Add("NZ_DEV_USER");
        if (string.IsNullOrEmpty(password)) missing.Add("NZ_DEV_PASSWORD");
        if (string.IsNullOrEmpty(database)) missing.Add("NZ_DEV_DB (or NZ_DEV_DATABASE)");
        if (missing.Count > 0)
        {
            Console.Error.WriteLine($"csharp runner: missing configuration: {string.Join(", ", missing)}");
            return null;
        }

        if (host is "localhost" or "::1") host = "127.0.0.1";
        var port = int.TryParse(Environment.GetEnvironmentVariable("NZ_DEV_PORT"), out var parsedPort)
            ? parsedPort
            : 5480;
        var schema = Environment.GetEnvironmentVariable("NZ_CONFORMANCE_SCHEMA") ?? "CONFORMANCE";
        var timeout = int.TryParse(Environment.GetEnvironmentVariable("NZ_CONFORMANCE_COMMAND_TIMEOUT"), out var parsedTimeout)
            ? parsedTimeout
            : 30;
        return new Settings(host!, port, database!, user!, password!, schema, timeout);
    }

    private static IEnumerable<Case> LoadCases(string casesDir)
    {
        foreach (var path in Directory.EnumerateFiles(casesDir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (!root.TryGetProperty("kit_version", out var version) || version.GetInt32() != 1)
            {
                throw new InvalidOperationException($"unsupported kit_version in {Path.GetFileName(path)}");
            }
            foreach (var element in root.GetProperty("cases").EnumerateArray())
            {
                yield return new Case
                {
                    Id = element.GetProperty("id").GetString() ?? string.Empty,
                    Profile = element.TryGetProperty("profile", out var profile) ? profile.GetString() ?? "full" : "full",
                    Sql = element.TryGetProperty("sql", out var sql) ? sql.GetString() ?? string.Empty : string.Empty,
                    Api = element.TryGetProperty("api", out var api) ? api.Clone() : null,
                    MultiStatement = element.TryGetProperty("multi_statement", out var multi) && multi.GetBoolean(),
                    Timeout = element.TryGetProperty("timeout", out var timeout) && timeout.TryGetDouble(out var timeoutSeconds)
                        ? timeoutSeconds
                        : null,
                    Params = element.TryGetProperty("params", out var parameters)
                        ? parameters.EnumerateArray().Select(p => new Param
                        {
                            Type = p.TryGetProperty("type", out var type) ? type.GetString() ?? string.Empty : string.Empty,
                            Value = p.TryGetProperty("value", out var value) ? value.Clone() : default,
                        }).ToList()
                        : new List<Param>(),
                    Setup = ReadStringArray(element, "setup"),
                    Cleanup = ReadStringArray(element, "cleanup"),
                    SqlOverrides = element.TryGetProperty("sql_overrides", out var overrides)
                        ? overrides.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty)
                        : new Dictionary<string, string>(),
                };
            }
        }
    }

    private static List<string> ReadStringArray(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            ? value.EnumerateArray().Select(v => v.GetString() ?? string.Empty).ToList()
            : new List<string>();

    private static string RenderSql(string sql, string schema) =>
        string.Equals(schema, "CONFORMANCE", StringComparison.OrdinalIgnoreCase)
            ? sql
            : sql.Replace("CONFORMANCE.", schema.ToUpperInvariant() + ".", StringComparison.Ordinal)
                 .Replace("'CONFORMANCE'", "'" + schema.ToUpperInvariant() + "'", StringComparison.Ordinal);

    private sealed class CaseResult
    {
        public string CaseId { get; init; } = string.Empty;
        public string Status { get; set; } = "pass";
        public double DurationMs { get; set; }
        public List<ColumnResult> Columns { get; set; } = new();
        public List<List<object?>> Rows { get; set; } = new();
        public int RowCount { get; set; }
        public List<ResultSet> ResultSets { get; set; } = new();
        public ErrorResult? Error { get; set; }
        public string Profile { get; init; } = "full";
    }

    private sealed record ColumnResult(string Name, string TypeName, string TypeClass);
    private sealed record ResultSet(List<ColumnResult> Columns, List<List<object?>> Rows, int RowCount);
    private sealed record ErrorResult(string Class, string Message, string? Code);

    private static async Task<CaseResult> ExecuteApiCaseAsync(NzConnection connection, Settings settings, Case item)
    {
        var started = DateTime.UtcNow;
        var result = new CaseResult { CaseId = item.Id, Profile = item.Profile };
        var api = item.Api!.Value;
        var operation = api.TryGetProperty("operation", out var operationElement)
            ? operationElement.GetString() ?? string.Empty
            : string.Empty;

        try
        {
            foreach (var statement in item.Setup)
            {
                try
                {
                    await ExecuteNonQueryAsync(connection, RenderSql(statement, settings.Schema), settings.CommandTimeout);
                }
                catch (Exception error) when (IsMissingObject(error))
                {
                }
            }

            var (columns, rows) = operation switch
            {
                "metadata_ddl" => await ApiMetadataDdlAsync(connection, settings, api),
                "pool_reuse" => await ApiPoolReuseAsync(settings, api),
                "external_roundtrip" => await ApiExternalRoundtripAsync(connection, settings, api),
                "tls_fail_closed" => await ApiTlsFailClosedAsync(settings),
                _ => throw new InvalidOperationException($"unsupported api operation {operation}"),
            };
            result.Columns = columns;
            result.Rows = rows;
            result.RowCount = rows.Count;
            result.ResultSets = new List<ResultSet> { new(columns, rows, rows.Count) };
        }
        catch (Exception error)
        {
            result.Status = "error";
            result.Error = ClassifyError(error);
        }
        finally
        {
            foreach (var statement in item.Cleanup)
            {
                try
                {
                    await ExecuteNonQueryAsync(connection, RenderSql(statement, settings.Schema), settings.CommandTimeout);
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine($"cleanup warning [{item.Id}]: {error.Message}");
                }
            }
            result.DurationMs = Math.Round((DateTime.UtcNow - started).TotalMilliseconds, 3);
        }

        return result;
    }

    private static async Task<(List<ColumnResult> Columns, List<List<object?>> Rows)> ApiMetadataDdlAsync(
        NzConnection connection,
        Settings settings,
        JsonElement api)
    {
        var schema = api.TryGetProperty("schema", out var schemaElement)
            ? schemaElement.GetString() ?? settings.Schema
            : settings.Schema;
        var table = api.TryGetProperty("table", out var tableElement)
            ? tableElement.GetString() ?? string.Empty
            : string.Empty;
        var checks = new List<List<object?>>();

        var tables = await connection.Meta.GetTablesAsync(schema);
        var tableNames = tables.Select(info => info.TableName.ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
        foreach (var name in ReadStringArray(api, "tables"))
        {
            checks.Add(new List<object?> { "tables", name, tableNames.Contains(name.ToUpperInvariant()) });
        }

        var columns = await connection.Meta.GetColumnsAsync(table, schema);
        var columnNames = columns.Select(info => info.ColumnName.ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
        foreach (var name in ReadStringArray(api, "columns"))
        {
            checks.Add(new List<object?> { "columns", name, columnNames.Contains(name.ToUpperInvariant()) });
        }

        var ddl = await connection.Meta.GetTableDdlAsync(table, schema);
        var haystack = NormalizeDdl(ddl);
        foreach (var fragment in ReadStringArray(api, "fragments"))
        {
            checks.Add(new List<object?> { "ddl", fragment, haystack.Contains(NormalizeDdl(fragment), StringComparison.Ordinal) });
        }

        var resultColumns = new List<ColumnResult>
        {
            new("check", "VARCHAR", "text"),
            new("name", "VARCHAR", "text"),
            new("ok", "BOOLEAN", "bool"),
        };
        return (resultColumns, checks);
    }

    private static async Task<(List<ColumnResult> Columns, List<List<object?>> Rows)> ApiPoolReuseAsync(
        Settings settings,
        JsonElement api)
    {
        var maxSize = api.TryGetProperty("max_size", out var maxElement) && maxElement.TryGetInt32(out var parsed)
            ? Math.Max(1, parsed)
            : 1;
        var checks = new List<List<object?>>();

        await using var pool = new NzConnectionPool(
            settings.Host, settings.Database, settings.User, settings.Password,
            settings.Port, minPoolSize: 0, maxPoolSize: maxSize);

        int firstSid;
        await using (var first = await pool.RentAsync())
        {
            firstSid = await QueryIntAsync(first.Connection, "SELECT CURRENT_SID AS value");
        }

        int secondSid;
        await using (var second = await pool.RentAsync())
        {
            secondSid = await QueryIntAsync(second.Connection, "SELECT CURRENT_SID AS value");
        }

        checks.Add(new List<object?> { "same_session", firstSid == secondSid });
        checks.Add(new List<object?> { "stats_within_limit", pool.ActiveCount + pool.IdleCount <= pool.MaxPoolSize });

        var resultColumns = new List<ColumnResult>
        {
            new("check", "VARCHAR", "text"),
            new("ok", "BOOLEAN", "bool"),
        };
        return (resultColumns, checks);
    }

    private static async Task<(List<ColumnResult> Columns, List<List<object?>> Rows)> ApiExternalRoundtripAsync(
        NzConnection connection,
        Settings settings,
        JsonElement api)
    {
        var schema = (api.TryGetProperty("schema", out var schemaElement)
            ? schemaElement.GetString() ?? settings.Schema
            : settings.Schema).ToUpperInvariant();
        var source = $"{schema}.CASE_EXT_SRC";
        var destination = $"{schema}.CASE_EXT_DST";
        var directory = Path.Combine(Path.GetTempPath(), "nz-conformance-external-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var sqlDirectory = directory.Replace('\\', '/');
        var externalFile = sqlDirectory + "/export.txt";

        try
        {
            var statements = new[]
            {
                $"DROP TABLE {source} IF EXISTS",
                $"DROP TABLE {destination} IF EXISTS",
                $"CREATE TABLE {source} (ID INTEGER, LABEL VARCHAR(20)) DISTRIBUTE ON (ID)",
                $"INSERT INTO {source} VALUES (1, 'one')",
                $"INSERT INTO {source} VALUES (2, 'two')",
                $"CREATE EXTERNAL TABLE '{externalFile}' USING (REMOTESOURCE 'jdbc' DELIMITER '|' LOGDIR '{sqlDirectory}') AS SELECT * FROM {source}",
                $"CREATE TABLE {destination} (ID INTEGER, LABEL VARCHAR(20)) DISTRIBUTE ON (ID)",
                $"INSERT INTO {destination} SELECT * FROM EXTERNAL '{externalFile}' USING (REMOTESOURCE 'jdbc' DELIMITER '|' LOGDIR '{sqlDirectory}')",
            };
            foreach (var statement in statements)
            {
                await ExecuteNonQueryAsync(connection, statement, settings.CommandTimeout);
            }

            var set = await ExecuteQueryAsync(
                connection,
                $"SELECT COUNT(*)::INTEGER AS row_count, COALESCE(SUM(ID), 0)::INTEGER AS checksum FROM {destination}",
                null,
                settings.CommandTimeout);
            var resultColumns = new List<ColumnResult>
            {
                new("row_count", "INTEGER", "int4"),
                new("checksum", "INTEGER", "int4"),
            };
            return (resultColumns, set.Rows);
        }
        finally
        {
            foreach (var statement in new[] { $"DROP TABLE {source} IF EXISTS", $"DROP TABLE {destination} IF EXISTS" })
            {
                try { await ExecuteNonQueryAsync(connection, statement, settings.CommandTimeout); }
                catch { /* best effort */ }
            }
            try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
        }
    }

    private static async Task<(List<ColumnResult> Columns, List<List<object?>> Rows)> ApiTlsFailClosedAsync(Settings settings)
    {
        await using var tls = new NzConnection(
            settings.User, settings.Password, settings.Host, settings.Database,
            settings.Port, securityLevel: SecurityLevelCode.OnlySecuredSession);
        await tls.OpenAsync();
        return (
            new List<ColumnResult> { new("check", "VARCHAR", "text") },
            new List<List<object?>> { new() { "unexpected_tls_success" } }
        );
    }

    private static async Task<int> QueryIntAsync(NzConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static string NormalizeDdl(string text) =>
        string.Join(' ', text.ToUpperInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static async Task<CaseResult> ExecuteCaseAsync(NzConnection connection, Settings settings, Case item)
    {
        var started = DateTime.UtcNow;
        var sql = RenderSql(item.Sql, settings.Schema);
        var result = new CaseResult { CaseId = item.Id, Profile = item.Profile };
        var previousTimeout = connection.CommandTimeout;
        if (item.Timeout is > 0)
        {
            connection.CommandTimeout = TimeSpan.FromSeconds(item.Timeout.Value);
        }

        try
        {
            foreach (var statement in item.Setup)
            {
                try
                {
                    await ExecuteNonQueryAsync(connection, RenderSql(statement, settings.Schema), settings.CommandTimeout);
                }
                catch (Exception error) when (IsMissingObject(error))
                {
                }
            }

            var sets = new List<ResultSet>();
            if (item.MultiStatement)
            {
                foreach (var statement in sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!IsQuery(statement))
                    {
                        await ExecuteNonQueryAsync(connection, statement, settings.CommandTimeout);
                        continue;
                    }
                    var set = await ExecuteQueryAsync(connection, statement, null, item.Timeout is > 0 ? item.Timeout.Value : settings.CommandTimeout);
                    if (set.Columns.Count > 0) sets.Add(set);
                }
            }
            else
            {
                var values = item.Params.Select(ToDbValue).ToList();
                var overrideSql = item.SqlOverrides.TryGetValue("csharp", out var csharpSql)
                    ? RenderSql(csharpSql, settings.Schema)
                    : sql;
                if (!IsQuery(overrideSql))
                {
                    await ExecuteNonQueryAsync(connection, overrideSql, settings.CommandTimeout);
                }
                else
                {
                    var set = await ExecuteQueryAsync(connection, overrideSql, values, item.Timeout is > 0 ? item.Timeout.Value : settings.CommandTimeout);
                    if (set.Columns.Count > 0) sets.Add(set);
                }
            }

            var primary = sets.Count > 0 ? sets[0] : new ResultSet(new List<ColumnResult>(), new List<List<object?>>(), 0);
            result.Columns = primary.Columns;
            result.Rows = primary.Rows;
            result.RowCount = primary.Rows.Count;
            result.ResultSets = sets;
        }
        catch (Exception error)
        {
            result.Status = "error";
            result.Error = ClassifyError(error);
        }
        finally
        {
            connection.CommandTimeout = previousTimeout;
            foreach (var statement in item.Cleanup)
            {
                try
                {
                    await ExecuteNonQueryAsync(connection, RenderSql(statement, settings.Schema), settings.CommandTimeout);
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine($"cleanup warning [{item.Id}]: {error.Message}");
                }
            }
            result.DurationMs = Math.Round((DateTime.UtcNow - started).TotalMilliseconds, 3);
        }

        return result;
    }

    private static object? ToDbValue(Param parameter) =>
        parameter.Value.ValueKind switch
        {
            JsonValueKind.Null => DBNull.Value,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => parameter.Value.TryGetInt64(out var integer) ? integer : parameter.Value.GetDouble(),
            JsonValueKind.String => parameter.Value.GetString(),
            _ => parameter.Value.GetRawText(),
        };

    private static bool IsQuery(string sql)
    {
        var trimmed = sql.TrimStart();
        return trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("WITH", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("SHOW", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("VALUES", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("EXPLAIN", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("(", StringComparison.Ordinal);
    }

    private static async Task ExecuteNonQueryAsync(NzConnection connection, string sql, double timeout)
    {
        connection.CommandTimeout = TimeSpan.FromSeconds(timeout);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<ResultSet> ExecuteQueryAsync(
        NzConnection connection,
        string sql,
        IReadOnlyList<object?>? parameters,
        double timeout)
    {
        connection.CommandTimeout = TimeSpan.FromSeconds(timeout);
        await using var command = (NzCommand)connection.CreateCommand();
        command.CommandText = sql;
        if (parameters is not null)
        {
            for (var index = 0; index < parameters.Count; index++)
            {
                command.Parameters.AddWithValue($"@p{index + 1}", parameters[index]);
            }
        }

        await using var reader = (NzDataReader)await command.ExecuteReaderAsync();
        var columns = new List<ColumnResult>(reader.FieldCount);
        var columnTypes = new List<string>(reader.FieldCount);
        for (var index = 0; index < reader.FieldCount; index++)
        {
            var oid = reader.GetProviderType(index);
            var declared = reader.GetDeclaredTypeName(index);
            var typeClass = Classify(oid);
            columnTypes.Add(typeClass);
            columns.Add(new ColumnResult(reader.GetName(index), declared, typeClass));
        }

        var rows = new List<List<object?>>();
        while (await reader.ReadAsync())
        {
            var row = new List<object?>(reader.FieldCount);
            for (var index = 0; index < reader.FieldCount; index++)
            {
                row.Add(Canonical(reader.GetValue(index), columnTypes[index]));
            }
            rows.Add(row);
        }

        return new ResultSet(columns, rows, rows.Count);
    }

    private static object? Canonical(object? value, string typeClass)
    {
        if (value is null or DBNull) return null;
        if (IntegerClasses.Contains(typeClass))
        {
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }
        if (typeClass == "numeric")
        {
            return value is decimal number
                ? number.ToString(CultureInfo.InvariantCulture)
                : Convert.ToString(value, CultureInfo.InvariantCulture);
        }
        if (typeClass is "float4" or "float8")
        {
            var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (double.IsNaN(number)) return "NaN";
            if (double.IsPositiveInfinity(number)) return "Infinity";
            if (double.IsNegativeInfinity(number)) return "-Infinity";
            return number;
        }
        if (typeClass == "bool") return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        if (typeClass == "date")
        {
            return value is DateTime date
                ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : Convert.ToString(value, CultureInfo.InvariantCulture);
        }
        if (typeClass == "time")
        {
            return value switch
            {
                TimeSpan time => FormatTime(time),
                DateTime dateTime => dateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                _ => Convert.ToString(value, CultureInfo.InvariantCulture),
            };
        }
        if (typeClass is "timestamp" or "timestamptz")
        {
            if (value is DateTime timestamp)
            {
                var text = timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                var fraction = timestamp.Ticks % TimeSpan.TicksPerSecond;
                if (fraction != 0)
                {
                    text += "." + (fraction / 10).ToString("D6", CultureInfo.InvariantCulture);
                }
                return text;
            }
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }
        if (typeClass == "timetz")
        {
            var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            if (System.Text.RegularExpressions.Regex.IsMatch(text, @"[+-]\d{2}$"))
            {
                text += ":00";
            }
            return text;
        }
        if (typeClass == "interval")
        {
            return ParseInterval(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
        }
        if (typeClass == "bytes")
        {
            if (value is byte[] bytes) return Convert.ToBase64String(bytes);
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }
        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Parses the Netezza interval text form ("1 year 2 mons 3 days
    /// 04:05:06") and the .NET TimeSpan form ("3.04:05:06") into the kit's
    /// canonical months/days/microseconds triple.
    /// </summary>
    private static Dictionary<string, long> ParseInterval(string text)
    {
        long months = 0;
        long days = 0;
        long microseconds = 0;
        var body = text.Trim();
        var negative = false;
        if (body.StartsWith('-'))
        {
            negative = true;
            body = body[1..].Trim();
        }

        var parts = body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < parts.Length; index++)
        {
            var token = parts[index];

            if (token.Contains(':'))
            {
                var sign = 1L;
                if (token.StartsWith('-')) { sign = -1; token = token[1..]; }
                else if (token.StartsWith('+')) { token = token[1..]; }

                long tokenDays = 0;
                var firstColon = token.IndexOf(':');
                var dot = token.IndexOf('.');
                if (dot >= 0 && dot < firstColon)
                {
                    tokenDays = long.Parse(token[..dot], CultureInfo.InvariantCulture);
                    token = token[(dot + 1)..];
                }

                var timeParts = token.Split(':');
                var hours = long.Parse(timeParts[0], CultureInfo.InvariantCulture);
                var minutes = long.Parse(timeParts[1], CultureInfo.InvariantCulture);
                var seconds = decimal.Parse(timeParts[2], CultureInfo.InvariantCulture);
                var total = ((tokenDays * 24 + hours) * 60 + minutes) * 60_000_000L
                    + (long)decimal.Round(seconds * 1_000_000m, 0, MidpointRounding.AwayFromZero);
                microseconds += sign * total;
                continue;
            }

            if (long.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
                && index + 1 < parts.Length)
            {
                var unit = parts[index + 1].ToLowerInvariant();
                if (unit.StartsWith("year", StringComparison.Ordinal))
                {
                    months += value * 12;
                    index++;
                }
                else if (unit.StartsWith("mon", StringComparison.Ordinal))
                {
                    months += value;
                    index++;
                }
                else if (unit.StartsWith("day", StringComparison.Ordinal))
                {
                    days += value;
                    index++;
                }
            }
        }

        if (negative)
        {
            months = -months;
            days = -days;
            microseconds = -microseconds;
        }

        return new Dictionary<string, long>
        {
            ["months"] = months,
            ["days"] = days,
            ["microseconds"] = microseconds,
        };
    }

    private static string FormatTime(TimeSpan time)
    {
        var text = time.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
        var fraction = time.Ticks % TimeSpan.TicksPerSecond;
        if (fraction != 0) text += "." + (fraction / 10).ToString("D6", CultureInfo.InvariantCulture);
        return text;
    }

    private static bool IsMissingObject(Exception error) =>
        error.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase);

    private static ErrorResult ClassifyError(Exception error)
    {
        var message = error.Message;
        var lowered = message.ToLowerInvariant();
        for (var inner = error.InnerException; inner is not null; inner = inner.InnerException)
        {
            lowered += " " + inner.Message.ToLowerInvariant();
        }
        string kind;
        if (lowered.Contains("ssl") || lowered.Contains("tls") || lowered.Contains("certificate") || lowered.Contains("handshake"))
            kind = "connection";
        else if (lowered.Contains("timeout")) kind = "timeout";
        else if (lowered.Contains("cancel")) kind = "cancelled";
        else if (lowered.Contains("syntax") || lowered.Contains("expecting")) kind = "syntax";
        else if (lowered.Contains("permission") || lowered.Contains("denied")) kind = "permission";
        else if (lowered.Contains("function") && lowered.Contains("does not exist")) kind = "function";
        else if (lowered.Contains("cannot contain null") || lowered.Contains("not null")) kind = "constraint";
        else if (lowered.Contains("attribute") || lowered.Contains("column")) kind = "column";
        else if (lowered.Contains("does not exist") || lowered.Contains("not found")) kind = "relation";
        else if (lowered.Contains("divide by 0") || lowered.Contains("out of range") || lowered.Contains("invalid") || lowered.Contains("can't parse")) kind = "type";
        else kind = "other";

        var code = error is NetezzaException netezza ? netezza.SqlState : null;
        return new ErrorResult(kind, message, code);
    }
}
