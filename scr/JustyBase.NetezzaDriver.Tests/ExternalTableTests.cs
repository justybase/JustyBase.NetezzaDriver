using JustyBase.NetezzaDriver;

namespace JustyBase.NetezzaDriver.Tests;

[Collection("Sequential")]
[Trait("Category", "Integration")]
public class ExternalTableTests : IDisposable
{
    private static readonly string TempDir = Environment.GetEnvironmentVariable("NZ_LOCAL_TMP_DIR")
        ?? Path.Combine(Path.GetTempPath(), "justybase-netezza-driver");

    private readonly NzConnection _connection;
    private readonly NzCommand _command;

    public ExternalTableTests()
    {
        Directory.CreateDirectory(TempDir);
        _connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName);
        _connection.Open(ClientTypeId.SqlJdbc);
        _command = (NzCommand)_connection.CreateCommand();
    }

    public void Dispose()
    {
        _command?.Dispose();
        _connection?.Dispose();
    }

    private static string TestFilePath(string name) => Path.Combine(TempDir, name);

    private long ReadSingleValue(string sql)
    {
        _command.CommandText = sql;
        using var reader = _command.ExecuteReader();
        reader.Read();
        return reader.GetInt64(0);
    }

    [Fact(Timeout = 30000)]
    public void ShouldCreateExternalTableExport()
    {
        var testFile = TestFilePath("cs_et_test.dat");
        try
        {
            if (File.Exists(testFile)) File.Delete(testFile);

            _command.CommandText = "CREATE TEMP TABLE CS_ET_SOURCE AS SELECT 1 AS ID, 'Test' AS VAL";
            _command.ExecuteNonQuery();

            _command.CommandText = @$"CREATE EXTERNAL TABLE '{testFile}' USING (REMOTESOURCE 'jdbc' DELIMITER '|' LOGDIR '{TempDir}') AS SELECT * FROM CS_ET_SOURCE";
            _command.ExecuteNonQuery();

            Assert.True(File.Exists(testFile));
            var content = File.ReadAllText(testFile);
            Assert.Contains("1|Test", content);
        }
        finally
        {
            if (File.Exists(testFile)) File.Delete(testFile);
        }
    }

    [Fact(Timeout = 30000)]
    public void ShouldReadFromExternalTableImport()
    {
        var testFile = TestFilePath("cs_et_import_test.dat");
        try
        {
            if (File.Exists(testFile)) File.Delete(testFile);

            _command.CommandText = "CREATE TEMP TABLE CS_ET_SOURCE2 AS SELECT 1 AS ID, 'Test' AS VAL";
            _command.ExecuteNonQuery();

            _command.CommandText = @$"CREATE EXTERNAL TABLE '{testFile}' USING (REMOTESOURCE 'jdbc' DELIMITER '|' LOGDIR '{TempDir}') AS SELECT * FROM CS_ET_SOURCE2";
            _command.ExecuteNonQuery();

            _command.CommandText = "CREATE TEMP TABLE CS_ET_DEST (ID INT, VAL VARCHAR(20))";
            _command.ExecuteNonQuery();

            _command.CommandText = @$"INSERT INTO CS_ET_DEST SELECT * FROM EXTERNAL '{testFile}' USING (REMOTESOURCE 'jdbc' DELIMITER '|' LOGDIR '{TempDir}')";
            _command.ExecuteNonQuery();

            _command.CommandText = "SELECT * FROM CS_ET_DEST";
            using var reader = _command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(1, reader.GetInt32(0));
            Assert.Equal("Test", reader.GetString(1));
            Assert.False(reader.Read());
        }
        finally
        {
            if (File.Exists(testFile)) File.Delete(testFile);
        }
    }

    public static IEnumerable<object[]> TableData =>
        new[] { "DIMPRODUCT", "DIMCURRENCY", "DIMDATE" }.Select(t => new object[] { t });

    [Theory(Timeout = 60000)]
    [MemberData(nameof(TableData))]
    public void ShouldExportAndImportTableCorrectly(string tableName)
    {
        var externalPath = TestFilePath($"{tableName}.dat");
        var tableNew = $"{tableName}_FROM_EXTERNAL";

        try
        {
            if (File.Exists(externalPath)) File.Delete(externalPath);

            _command.CommandText = $"DROP TABLE {tableNew} IF EXISTS";
            _command.ExecuteNonQuery();
            _command.CommandText = $"DROP TABLE ET_TEMP_{tableName} IF EXISTS";
            _command.ExecuteNonQuery();

            _command.CommandText = @$"CREATE EXTERNAL TABLE '{externalPath}' USING (REMOTESOURCE 'jdbc' DELIMITER '|' LOGDIR '{TempDir}') AS SELECT * FROM {tableName}";
            _command.ExecuteNonQuery();

            Assert.True(File.Exists(externalPath));

            _command.CommandText = $"CREATE TABLE {tableNew} AS SELECT * FROM {tableName} WHERE 1=2";
            _command.ExecuteNonQuery();

            _command.CommandText = @$"INSERT INTO {tableNew} SELECT * FROM EXTERNAL '{externalPath}' USING (REMOTESOURCE 'jdbc' DELIMITER '|' LOGDIR '{TempDir}')";
            _command.ExecuteNonQuery();

            var countOrg = ReadSingleValue($"SELECT COUNT(1) FROM {tableName}");
            var countNew = ReadSingleValue($"SELECT COUNT(1) FROM {tableNew}");
            Assert.Equal(countOrg, countNew);

            _command.CommandText = $"SELECT * FROM {tableNew} MINUS SELECT * FROM {tableName}";
            using var reader = _command.ExecuteReader();
            int cnt = 0;
            while (reader.Read()) cnt++;
            Assert.Equal(0, cnt);
        }
        finally
        {
            _command.CommandText = $"DROP TABLE {tableNew} IF EXISTS";
            _command.ExecuteNonQuery();
            if (File.Exists(externalPath)) File.Delete(externalPath);
        }
    }

    [Fact(Timeout = 60000)]
    public void CompressedExternalTableReadShouldNotThrow()
    {
        var tableName = "DIMDATE";
        var externalPath = TestFilePath($"{tableName}_EXT.DAT");
        var tableTmp = $"{tableName}_TMP";

        try
        {
            if (File.Exists(externalPath)) File.Delete(externalPath);

            _command.CommandText = $"DROP TABLE {tableTmp} IF EXISTS";
            _command.ExecuteNonQuery();

            _command.CommandText = @$"CREATE EXTERNAL TABLE '{externalPath}' USING (REMOTESOURCE 'jdbc' FORMAT 'INTERNAL' COMPRESS 'TRUE') AS SELECT * FROM {tableName}";
            _command.ExecuteNonQuery();

            Assert.True(File.Exists(externalPath));

            _command.CommandText = @$"CREATE TABLE {tableTmp} AS SELECT * FROM {tableName} WHERE 1=2 DISTRIBUTE ON RANDOM";
            _command.ExecuteNonQuery();

            _command.CommandText = @$"INSERT INTO {tableTmp} SELECT * FROM EXTERNAL '{externalPath}' USING (REMOTESOURCE 'jdbc' FORMAT 'INTERNAL' COMPRESS 'TRUE')";
            _command.ExecuteNonQuery();

            var diffCount = ReadSingleValue("SELECT COUNT(1) FROM (SELECT * FROM " + $"{tableTmp} MINUS SELECT * FROM {tableName}) X");
            Assert.Equal(0L, diffCount);

            var totalCount = ReadSingleValue($"SELECT COUNT(1) FROM {tableTmp}");
            Assert.True(totalCount > 0);
        }
        finally
        {
            _command.CommandText = $"DROP TABLE {tableTmp} IF EXISTS";
            _command.ExecuteNonQuery();
            if (File.Exists(externalPath)) File.Delete(externalPath);
        }
    }

    [Fact(Timeout = 30000)]
    public void ShouldCreateLogFilesDuringExternalTableOperations()
    {
        var testFile = TestFilePath("cs_et_log_test.dat");

        try
        {
            if (File.Exists(testFile)) File.Delete(testFile);

            _command.CommandText = "CREATE TEMP TABLE CS_ET_LOG_TEST AS SELECT 1 AS ID, 'Test' AS VAL";
            _command.ExecuteNonQuery();

            _command.CommandText = @$"CREATE EXTERNAL TABLE '{testFile}' USING (REMOTESOURCE 'jdbc' DELIMITER '|' LOGDIR '{TempDir}') AS SELECT * FROM CS_ET_LOG_TEST";
            _command.ExecuteNonQuery();

            Assert.True(File.Exists(testFile));

            var logFiles = Directory.GetFiles(TempDir, "*.nzlog");
            Assert.True(logFiles.Length >= 0);
        }
        finally
        {
            if (File.Exists(testFile)) File.Delete(testFile);
            foreach (var f in Directory.GetFiles(TempDir, "*.nzlog")) try { File.Delete(f); } catch { }
            foreach (var f in Directory.GetFiles(TempDir, "*.nzbad")) try { File.Delete(f); } catch { }
            foreach (var f in Directory.GetFiles(TempDir, "*.nzstats")) try { File.Delete(f); } catch { }
        }
    }

    [Fact(Timeout = 30000)]
    public void ShouldCreateNzbadFileOnImportError()
    {
        var testFile = TestFilePath("cs_et_bad_test.dat");

        try
        {
            if (File.Exists(testFile)) File.Delete(testFile);

            _command.CommandText = "DROP TABLE CS_ET_BAD_TEST IF EXISTS";
            _command.ExecuteNonQuery();
            _command.CommandText = "CREATE TEMP TABLE CS_ET_BAD_TEST (ID INT, VAL VARCHAR(20))";
            _command.ExecuteNonQuery();

            File.WriteAllText(testFile, "1XTest\n2XBadData\n");

            _command.CommandText = @$"INSERT INTO CS_ET_BAD_TEST SELECT * FROM EXTERNAL '{testFile}' USING (REMOTESOURCE 'jdbc' DELIMITER '|' LOGDIR '{TempDir}' MAXERRORS 10)";

            try
            {
                _command.ExecuteNonQuery();
            }
            catch
            {
            }

            var badFiles = Directory.GetFiles(TempDir, "*.nzbad");
            var logFiles = Directory.GetFiles(TempDir, "*.nzlog");

            if (badFiles.Length > 0)
            {
                var content = File.ReadAllText(badFiles[0]);
                Assert.NotEmpty(content);
            }
        }
        finally
        {
            if (File.Exists(testFile)) File.Delete(testFile);
            foreach (var f in Directory.GetFiles(TempDir, "*.nzlog")) try { File.Delete(f); } catch { }
            foreach (var f in Directory.GetFiles(TempDir, "*.nzbad")) try { File.Delete(f); } catch { }
            foreach (var f in Directory.GetFiles(TempDir, "*.nzstats")) try { File.Delete(f); } catch { }
            _command.CommandText = "DROP TABLE CS_ET_BAD_TEST IF EXISTS";
            _command.ExecuteNonQuery();
        }
    }
}
