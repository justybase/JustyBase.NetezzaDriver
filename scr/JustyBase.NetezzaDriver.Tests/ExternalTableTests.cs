using JustyBase.NetezzaDriver;

namespace JustyBase.NetezzaDriver.Tests;

[Collection("Sequential")]
[Trait("Category", "Integration")]
public class ExternalTableTests
{

    [Fact(Timeout = 30000)]
    public void TestExternalTable()
    {
        using NzConnection connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName);
        connection.Open(ClientTypeId.SqlJdbc);
        using NzCommand command = (NzCommand)connection.CreateCommand();
        command.CommandText = "SELECT TABLENAME FROM JUST_DATA.._V_TABLE WHERE OBJTYPE = 'TABLE' AND TABLENAME NOT LIKE '%EXTERNAL' ORDER BY CREATEDATE ASC";
        using var reader = command.ExecuteReader();
        var tableNames = new List<string>();
        while (reader.Read())
        {
            tableNames.Add(reader.GetString(0));
        }

        Assert.True(tableNames.Count > 10);
        Assert.True(tableNames.Count < 10000);
        //foreach (var tn in tableNames.Take(5))
        foreach (var tn in new string[] { "DIMPRODUCT", "DIMCURRENCY", "DIMDATE" })
        {
            Assert.NotNull(tn);
            TestOneTable(command, tn, "jdbc");
        }
    }


    private static void TestOneTable(NzCommand command, string tablename, string driverName = "python")
    {
        var externalPath = $"D:\\TMP\\{tablename}.dat";
        var tablenameOrg = $"JUST_DATA..{tablename}";
        var tablenameNew = $"{tablenameOrg}_FROM_EXTERNAL";

        command.CommandText = $"DROP TABLE {tablenameNew} IF EXISTS";
        command.ExecuteNonQuery();
        command.CommandText = $"DROP TABLE ET_TEMP IF EXISTS";
        command.ExecuteNonQuery();
        command.CommandText = $"create external table ET_TEMP '{externalPath}' using ( remotesource '{driverName}' delimiter '|') as select * from {tablenameOrg}";
        command.ExecuteNonQuery();
        command.CommandText = $"DROP TABLE {tablenameNew} IF EXISTS";
        command.ExecuteNonQuery();
        command.CommandText = $"CREATE TABLE {tablenameNew} AS (SELECT * FROM {tablenameOrg} WHERE 1=2)";
        command.ExecuteNonQuery();
        command.CommandText = $"INSERT INTO {tablenameNew}  SELECT * FROM EXTERNAL '{externalPath}' " +
            @$"using ( remotesource '{driverName}' delimiter '|' socketbufsize 8388608 ctrlchars 'yes'  encoding 'internal' timeroundnanos 'yes' crinstring 'off' logdir d:\tmp\logs\)";
        command.ExecuteNonQuery();

        command.CommandText = $"SELECT count(1) FROM {tablenameOrg}";
        using var rd1 = command.ExecuteReader();
        rd1.Read();
        long cnt_org = rd1.GetInt64(0);
        command.CommandText = $"SELECT count(1) FROM {tablenameNew}";
        using var rd2 = command.ExecuteReader();
        rd2.Read();
        long cnt_new = rd2.GetInt64(0);

        Assert.Equal(cnt_org, cnt_new);
        command.CommandText = $"SELECT *  FROM {tablenameNew} minus SELECT *  FROM {tablenameOrg}";
        using var rd3 = command.ExecuteReader();
        int cnt = 0;
        while (rd3.Read())
        {
            cnt++;
        }

        Assert.Equal(0, cnt);
    }
}
