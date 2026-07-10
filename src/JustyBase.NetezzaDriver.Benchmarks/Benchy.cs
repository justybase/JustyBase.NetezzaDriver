using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using System.Data.Common;
using System.Data.Odbc;

namespace JustyBase.NetezzaDriver.Benchmarks;


[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0)]

public class Benchy
{
    private NzConnection _nzConnectionDefault = null!;
    private NzConnection _nzConnectionNoStreamBuffer = null!;
    private NzConnection _nzConnectionNoStreamBufferNoSocketBuffer = null!;
    private NzConnection _nzConnectionStreamBufferSocketBuffer2 = null!;
    private DbConnection _odbcConnection = null!;

    [GlobalSetup]
    public void Setup()
    {
        _nzConnectionDefault = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName, Config.Port);
        _nzConnectionDefault.Open();

        _nzConnectionNoStreamBuffer = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName, Config.Port);
        _nzConnectionNoStreamBuffer.Open(useBufferedStream:false, setSocketBufferSizes:true);

        _nzConnectionNoStreamBufferNoSocketBuffer = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName, Config.Port);
        _nzConnectionNoStreamBufferNoSocketBuffer.Open(useBufferedStream: false, setSocketBufferSizes: false);

        _nzConnectionStreamBufferSocketBuffer2 = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName, Config.Port);
        _nzConnectionStreamBufferSocketBuffer2.Open(useBufferedStream: true, setSocketBufferSizes: true);


        _odbcConnection = new OdbcConnection($"Driver={{NetezzaSQL}};servername={Config.Host};port={Config.Port};database={Config.DbName};username={Config.UserName};password={Config.Password}");
        _odbcConnection.Open();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _nzConnectionDefault.Dispose();
        _nzConnectionNoStreamBuffer.Dispose();
        _nzConnectionNoStreamBufferNoSocketBuffer.Dispose();
        _nzConnectionStreamBufferSocketBuffer2.Dispose();
        _odbcConnection.Dispose();
    }
    [Params(
        "/*1*/SELECT * FROM JUST_DATA..FACTPRODUCTINVENTORY FI ORDER BY ROWID LIMIT 500000"
        //"/*2*/SELECT * FROM JUST_DATA..DIMDATE DD ORDER BY ROWID LIMIT 10000"
        //"/*3*/SELECT 1,2,3,'abc'::char(10),'ąężźń'::nchar(12),'aaaa' || ((RANDOM()*100)::INT),'xaaa' || ((RANDOM()*100)::INT),'yaaa' || ((RANDOM()*100)::INT),'zaaa' || ((RANDOM()*100)::INT) FROM JUST_DATA..FACTPRODUCTINVENTORY DD ORDER BY ROWID LIMIT 50000"
        //"/*4*/SELECT 1,2,3,'a' || ((RANDOM()*100)::INT),'b' || ((RANDOM()*100)::INT),'c' || ((RANDOM()*100)::INT) FROM JUST_DATA.._V_RELATION_COLUMN ORDER BY NAME,ATTNUM LIMIT 10000"
        //"/*5*/SELECT '12:00:00'::TIMETZ,'14:13:12.4321+11:15'::TIMETZ FROM JUST_DATA..DIMCURRENCY ORDER BY ROWID",
        //"/*6*/SELECT '12:00:00'::TIME, '12:00:00-12'::TIMETZ, '12:00:00+12'::TIMETZ,'14:13:12.4321+11:15'::TIMETZ FROM JUST_DATA..DIMACCOUNT LIMIT 1",
        //"/*7*/SELECT RANDOM() FROM JUST_DATA..FACTPRODUCTINVENTORY ORDER BY ROWID LIMIT 10000"
        //"/*8*/SELECT * FROM JUST_DATA..DIMCURRENCY ORDER BY ROWID",
        )]
    public string Query { get; set; } = "";

    [Benchmark]
    public void JustyNzDriver()
    {
        ReadedTyped(_nzConnectionDefault, Query);
    }
    [Benchmark]
    public void JustyNzDriverNoTyped()
    {
        ReaderValues(_nzConnectionDefault, Query);
    }

    [Benchmark]
    public void NzOdbcTyped()
    {
        ReadedTyped(_odbcConnection, Query);
    }

    //[Benchmark]
    public void JustyNzDriver2()
    {
        ReadedTyped(_nzConnectionNoStreamBuffer, Query);
    }

    //[Benchmark]
    public void JustyNzDriver3()
    {
        ReadedTyped(_nzConnectionNoStreamBufferNoSocketBuffer, Query);
    }

   //[Benchmark]
    public void JustyNzDriver4()
    {
        ReadedTyped(_nzConnectionStreamBufferSocketBuffer2, Query);
    }

    //[Benchmark]
    public void NzOdbc()
    {
        ReaderValues(_odbcConnection, Query);
    }

    private static void ReaderValues(DbConnection dbConnection, string query)
    {
        using var nzCommand = dbConnection.CreateCommand();
        nzCommand.CommandText = query;
        using var reader = nzCommand.ExecuteReader();
        while (reader.Read())
        {
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var o = reader.GetValue(i);
            }
        }
    }

    private static void ReadedTyped(DbConnection dbConnection, string query)
    {
        using var cmd = dbConnection.CreateCommand();
        cmd.CommandText = query;
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            for (int i = 0; i < reader.FieldCount; i++)
            {
                if (reader.IsDBNull(i))
                {
                    continue;
                }

                if (reader.GetFieldType(i) == typeof(int))
                {
                    var o = reader.GetInt32(i);
                }
                else if (reader.GetFieldType(i) == typeof(long))
                {
                    var o = reader.GetInt64(i);
                }
                else if (reader.GetFieldType(i) == typeof(DateTime))
                {
                    var o = reader.GetDateTime(i);
                }
                else if (reader.GetFieldType(i) == typeof(decimal))
                {
                    var o = reader.GetDecimal(i);
                }
                else if (reader.GetFieldType(i) == typeof(float))
                {
                    var o = reader.GetFloat(i);
                }
                else if (reader.GetFieldType(i) == typeof(double))
                {
                    var o = reader.GetDouble(i);
                }
                else if (reader.GetFieldType(i) == typeof(string))
                {
                    var o = reader.GetString(i);
                }
                else if (reader.GetFieldType(i) == typeof(bool))
                {
                    var o = reader.GetBoolean(i);
                }
                else if (reader.GetFieldType(i) == typeof(byte))
                {
                    var o = reader.GetByte(i);
                }
                else if (reader.GetFieldType(i) == typeof(char))
                {
                    var o = reader.GetChar(i);
                }
                else if (reader.GetFieldType(i) == typeof(Guid))
                {
                    var o = reader.GetGuid(i);
                }
                else if (reader.GetFieldType(i) == typeof(short))
                {
                    var o = reader.GetInt16(i);
                }
                else
                {
                    var o = reader.GetValue(i);
                }
            }
        }
    }
}


