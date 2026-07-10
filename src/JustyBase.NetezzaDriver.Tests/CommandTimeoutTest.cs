using System.Diagnostics;

namespace JustyBase.NetezzaDriver.Tests;

[Collection("Sequential")]
[Trait("Category", "Integration")]
public class CommandTimeoutTest
{

    private readonly ITestOutputHelper _output;
    public CommandTimeoutTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task CommandTimeoutTest1()
    {
        Console.WriteLine("### CommandTimeoutManualTest ###");
        using NzConnection connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName);
        connection.Open();
        connection.CommandTimeout = TimeSpan.FromSeconds(4);

        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TEMP TABLE ABC AS (SELECT NOW() AS COL1, RANDOM() AS COL2)";
        command.ExecuteNonQuery();

        for (int i = 0; i < 2; i++)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                stopwatch.Restart();
                _output.WriteLine($"STARTED PID = {connection.Pid}");
                command.CommandText = _heavySql;
                command.ExecuteNonQuery();
            }
            catch (Exception ex1)
            {
                _output.WriteLine(ex1.Message);
                _output.WriteLine("AFTER : " + stopwatch.Elapsed.ToString());
                //aborted after about connection.ConnectionTimeout seconds
                Assert.True(stopwatch.Elapsed > TimeSpan.FromSeconds(3.5));
                Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(8.5));

                command.CommandText = $"SELECT COUNT(1) FROM _V_SESSION WHERE STATUS = 'active' AND PID = {connection.Pid} AND COMMAND LIKE '{_heavySql[0..10]}%'";
                using var rdr2 = command.ExecuteReader();
                rdr2.Read();
                var sessionCount = rdr2.GetInt64(0);
                Assert.Equal(0, sessionCount);
       
                //_output.WriteLine("CHECK QUERIES ON DATABASE SIDE TO COFIRM !! (3 s)");
                //await Task.Delay(3_000);
                //_output.WriteLine();
                //_output.WriteLine();
                await Task.Delay(50);
            }
        }

        //await Task.Delay(200);
        // TEST: check that command can be executed after timeout and table still exists
        // (aborting should not drop session)
        command.CommandText = "SELECT * FROM ABC";
        using var rdr = command.ExecuteReader();
        rdr.Read();//do not throws.

    }

    //proxmox NPS VM with 1 vCPUs and 2 GB RAM
    //must run at least 4 seconds
    private readonly string _heavySql =
    """
        SELECT F1.PRODUCTKEY, COUNT(DISTINCT (F1.PRODUCTKEY / F2.PRODUCTKEY))    
        FROM     
        ( SELECT * FROM JUST_DATA..FACTPRODUCTINVENTORY LIMIT 30000) F1,    
        ( SELECT * FROM JUST_DATA..FACTPRODUCTINVENTORY LIMIT 30000) F2    
        GROUP BY 1
        LIMIT 500    
    """;


}
