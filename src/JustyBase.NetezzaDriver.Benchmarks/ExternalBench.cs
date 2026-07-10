using BenchmarkDotNet.Attributes;
using System.Data.Common;
using System.Data.Odbc;

namespace JustyBase.NetezzaDriver.Benchmarks;

[MemoryDiagnoser]
public class ExternalBench
{
    private const string _host = "linux.local";
    private const string _dbName = "JUST_DATA";
    private const string _userName = "admin";
    private static readonly string _password = Environment.GetEnvironmentVariable("NZ_DEV_PASSWORD") ?? throw new InvalidOperationException("Environment variable NZ_PASSWORD is not set.");

    private const int _port = 5480;

    private NzConnection _nzNewConnection = null!;//default = python
    private DbConnection _odbcConnection = null!;

    [GlobalSetup]
    public void Setup()
    {
        _nzNewConnection = new NzConnection(_userName, _password, _host, _dbName, _port);
        _nzNewConnection.Open(ClientTypeId.SqlDotnet);

        _odbcConnection = new OdbcConnection($"Driver={{NetezzaSQL}};servername={_host};port={_port};database={_dbName};username={_userName};password={_password}");
        _odbcConnection.Open();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _nzNewConnection.Dispose();
        _odbcConnection.Dispose();
    }
    //private readonly string _tableName = "DIMCUSTOMER";
    private readonly string _tableName = "FACTPRODUCTINVENTORY";

    [Benchmark]
    public void ExternalUnloadAndLoadNz()
    {
        TestOneExternalTable(_nzNewConnection, _tableName, "dotnet");
    }

    [Benchmark]
    public void ExternalUnloadAndLoadOriginalOdbc()
    {
        TestOneExternalTable(_odbcConnection, _tableName, "odbc");
    }
    public static void TestOneExternalTable(DbConnection connection, string tablename, string driverName = "odbc")
    {
        using var command = connection.CreateCommand();
        var externalPath = $"D:\\TMP\\{tablename}.dat";
        var tablenameOrg = $"JUST_DATA..{tablename}";
        var tablenameNew = $"{tablenameOrg}_FROM_EXTERNAL";

        command.CommandText = $"DROP TABLE {tablenameNew} IF EXISTS";
        command.ExecuteNonQuery();
        command.CommandText = "DROP TABLE ET_TEMP IF EXISTS";
        command.ExecuteNonQuery();
        command.CommandText = $"create external table ET_TEMP '{externalPath}' using ( remotesource '{driverName}' delimiter '|') as select * from {tablenameOrg}";
        command.ExecuteNonQuery();
        command.CommandText = $"DROP TABLE {tablenameNew} IF EXISTS";
        command.ExecuteNonQuery();
        command.CommandText = $"CREATE TABLE {tablenameNew} AS (SELECT * FROM {tablenameOrg} WHERE 1=2)";
        command.ExecuteNonQuery();
        command.CommandText = $"INSERT INTO {tablenameNew}  SELECT * FROM EXTERNAL '{externalPath}' " +
            @$"using ( remotesource '{driverName}' delimiter '|' socketbufsize 8388608 ctrlchars 'yes'  encoding 'internal' timeroundnanos 'yes' crinstring 'off' logdir E:\logs\)";
        command.ExecuteNonQuery();
    }

    //[Benchmark]
    public void ExternalUnloadAndLoadNz2()
    {
        TestOneExternalTable1(_nzNewConnection, _tableName, "dotnet");
    }
    public static void TestOneExternalTable1(DbConnection connection, string tablename, string driverName = "odbc")
    {
        using var command = connection.CreateCommand();
        var externalPath = $"D:\\TMP\\{tablename}.dat";
        var tablenameOrg = $"JUST_DATA..{tablename}";
        var tablenameNew = $"{tablenameOrg}_FROM_EXTERNAL";

        command.CommandText = $"DROP TABLE {tablenameNew} IF EXISTS";
        command.ExecuteNonQuery();
        command.CommandText = "DROP TABLE ET_TEMP IF EXISTS";
        command.ExecuteNonQuery();
        command.CommandText = $"create external table ET_TEMP '{externalPath}' using ( remotesource '{driverName}' delimiter '|') as select * from {tablenameOrg}";
        command.ExecuteNonQuery();
        command.CommandText = $"DROP TABLE {tablenameNew} IF EXISTS";
        command.ExecuteNonQuery();
        command.CommandText = $"CREATE TABLE {tablenameNew} AS (SELECT * FROM {tablenameOrg} WHERE 1=2)";
        command.ExecuteNonQuery();
    }

    //[Benchmark]
    public void ExternalUnloadAndLoadNz3()
    {
        TestOneExternalTable3(_nzNewConnection, _tableName, "dotnet");
    }
    public static void TestOneExternalTable3(DbConnection connection, string tablename, string driverName = "odbc")
    {
        using var command = connection.CreateCommand();
        var externalPath = $"D:\\TMP\\{tablename}.dat";
        var tablenameOrg = $"JUST_DATA..{tablename}";
        var tablenameNew = $"{tablenameOrg}_FROM_EXTERNAL";

        command.CommandText = $"INSERT INTO {tablenameNew}  SELECT * FROM EXTERNAL '{externalPath}' " +
            @$"using ( remotesource '{driverName}' delimiter '|' socketbufsize 8388608 ctrlchars 'yes'  encoding 'internal' timeroundnanos 'yes' crinstring 'off' logdir E:\logs\)";
        command.ExecuteNonQuery();
    }
}
