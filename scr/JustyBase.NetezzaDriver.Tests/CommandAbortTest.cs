using Microsoft.Extensions.Logging.Abstractions;
using System.Data.Common;
using System.Diagnostics;
using System.Security.Authentication;


namespace JustyBase.NetezzaDriver.Tests;

[Collection("Sequential")]
[Trait("Category", "Integration")]
public class CommandAbortTest
{
    private readonly ITestOutputHelper _output;
    public CommandAbortTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task AbortTest1()
    {
        using NzConnection connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName);
        await AbortTestHelper(connection);
    }

    [Fact]
    public void AbortTestWithSSL()
    {
        using NzConnection connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName, securityLevel: SecurityLevelCode.OnlySecuredSession, sslCerFilePath: @"C:\DEV\DEV\Others\keys\server-cert.pem", loggerFactory: new NullLoggerFactory());
        Assert.Throws<AuthenticationException>(() => connection.Open());
    }

    private async Task AbortTestHelper(NzConnection connection)
    {
        connection.Open();
        connection.CommandTimeout = TimeSpan.FromSeconds(120);

        using var command = connection.CreateCommand();
        command.CommandText = "show connection";
        command.ExecuteNonQuery();
        using var rdr1 = command.ExecuteReader();
        while (rdr1.Read())
        {
            for (int i = 0; i < rdr1.FieldCount; i++)
            {
                Debug.WriteLine(rdr1.GetValue(i)?.ToString() ?? "" + "|");
            }
            Debug.WriteLine("");
        }

        command.CommandText = "create temp table abc as (select now() as col1, random() as col2)";
        command.ExecuteNonQuery();



        Stopwatch stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < 3; i++)
        {
            var longRunningQuery = Task.Run(async () =>
            {
                try
                {
                    stopwatch.Restart();
                    _output.WriteLine($"STARTED PID = {connection.Pid}");
                    command.CommandText = _heavySql;
                    command.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    Assert.True(stopwatch.Elapsed > TimeSpan.FromSeconds(5.5));
                    Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(9.5));
                    _output.WriteLine(ex.Message);
                    _output.WriteLine("AFTER : " + stopwatch.Elapsed.ToString());
                    await Task.Delay(500);
                    command.CommandText = $"SELECT count(1) FROM _V_SESSION where STATUS = 'active' and PID = {connection.Pid} AND COMMAND LIKE '{_heavySql[0..10]}%'";
                    using var rdr = command.ExecuteReader();
                    rdr.Read();
                    Assert.Equal(0, rdr.GetInt64(0));
                    //Console.WriteLine("CHECK QUERIES ON DATABASE SIDE TO COFIRM !! (3 s)");
                    //await Task.Delay(3_000);
                    //Console.WriteLine();
                    //Console.WriteLine();
                    await Task.Delay(50);
                }
            });
            await Task.Delay(6000);
            connection.CancelQuery();
            await longRunningQuery;
        }

        _output.WriteLine($"END {stopwatch.Elapsed}");
        //await Task.Delay(200);
        command.CommandText = "SELECT * FROM ABC";
        using var rdr = command.ExecuteReader();
        rdr.Read();//do not throws.
    }


    private const string _heavySql =
    """
        SELECT     
        F1.PRODUCTKEY    
        , COUNT(DISTINCT (F1.PRODUCTKEY / F2.PRODUCTKEY))    
        FROM     
        ( SELECT * FROM JUST_DATA..FACTPRODUCTINVENTORY LIMIT 30000) F1,    
        ( SELECT * FROM JUST_DATA..FACTPRODUCTINVENTORY LIMIT 30000) F2    
        GROUP BY 1    
        LIMIT 500    
    """;

    [Fact]
    public void CancelDuringReaderRead_Test()
    {
        using NzConnection connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName);
        connection.Open();
        connection.CommandTimeout = TimeSpan.FromSeconds(120);

        using var command = connection.CreateCommand();
        EnsureTempTable(command);

        command.CommandText = _readerHeavySql;
        using var activeReader = command.ExecuteReader();
        int rowsReadBeforeCancel = ReadSomeRows(activeReader, 200000);
        Assert.True(rowsReadBeforeCancel > 0, "Reader should return at least one row before cancel.");

        var cancelToNextSql = Stopwatch.StartNew();
        connection.CancelQuery();

        command.CommandText = "SELECT COLUMN_ONE FROM TT1";
        using var rdr = command.ExecuteReader();
        cancelToNextSql.Stop();

        _output.WriteLine($"Cancel->next SQL latency: {cancelToNextSql.Elapsed.TotalMilliseconds} ms; rowsReadBeforeCancel={rowsReadBeforeCancel}");
        Assert.True(cancelToNextSql.Elapsed <= _cancelSla, $"Expected cancel->next SQL <= {_cancelSla.TotalSeconds}s, actual {cancelToNextSql.Elapsed.TotalMilliseconds} ms.");

        Assert.True(rdr.Read());
        Assert.Equal(1, rdr.GetInt32(0));
    }

    [Fact]
    public void ReaderCloseAfterCancel_CompletesWithinSla_Test()
    {
        using NzConnection connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName);
        connection.Open();
        connection.CommandTimeout = TimeSpan.FromSeconds(120);

        using var command = connection.CreateCommand();
        EnsureTempTable(command);

        command.CommandText = _readerHeavySql;
        using var activeReader = command.ExecuteReader();
        int rowsReadBeforeCancel = ReadSomeRows(activeReader, 1000);
        Assert.True(rowsReadBeforeCancel > 0, "Reader should return at least one row before cancel.");

        connection.CancelQuery();
        var closeTime = Stopwatch.StartNew();
        var closeException = Record.Exception(() => activeReader.Close());
        closeTime.Stop();

        _output.WriteLine($"reader.Close() after cancel latency: {closeTime.Elapsed.TotalMilliseconds} ms; rowsReadBeforeCancel={rowsReadBeforeCancel}");
        Assert.True(closeTime.Elapsed <= _cancelSla, $"Expected reader.Close() after cancel <= {_cancelSla.TotalSeconds}s, actual {closeTime.Elapsed.TotalMilliseconds} ms.");
        if (closeException is not null)
        {
            Assert.IsType<NetezzaException>(closeException);
        }

        command.CommandText = "SELECT COLUMN_ONE FROM TT1";
        using var rdr = command.ExecuteReader();
        Assert.True(rdr.Read());
        Assert.Equal(1, rdr.GetInt32(0));
    }

    private static int ReadSomeRows(DbDataReader reader, int maxRows)
    {
        int rowsRead = 0;
        while (rowsRead < maxRows && reader.Read())
        {
            rowsRead++;
        }
        return rowsRead;
    }

    private static void EnsureTempTable(DbCommand command)
    {
        command.CommandText = "DROP TABLE TT1 IF EXISTS";
        command.ExecuteNonQuery();

        command.CommandText = """
            CREATE TEMP TABLE TT1 AS
            (
                SELECT 1 AS COLUMN_ONE
            )
            DISTRIBUTE ON RANDOM
            """;
        command.ExecuteNonQuery();

        command.CommandText = "SELECT COLUMN_ONE FROM TT1";
        using var rdr = command.ExecuteReader();
        Assert.True(rdr.Read());
        Assert.Equal(1, rdr.GetInt32(0));
        Assert.False(rdr.Read());
    }

    private static readonly TimeSpan _cancelSla = TimeSpan.FromSeconds(2);

    private const string _readerHeavySql =
    """
        SELECT 2,F1.* FROM JUST_DATA..FACTPRODUCTINVENTORY F1
        JOIN JUST_DATA..DIMDATE D1 ON 1=1 
        LIMIT 50000000
    """;

}
