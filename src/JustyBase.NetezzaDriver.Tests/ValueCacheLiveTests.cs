namespace JustyBase.NetezzaDriver.Tests;

/// <summary>
/// Live checks for the per-column string pool on the benchmark tables used by
/// the performance work: repeated STATUS values must return the interned
/// instance, while high-cardinality results must still decode correctly.
/// </summary>
[Collection("Sequential")]
[Trait("Category", "Integration")]
public sealed class ValueCacheLiveTests
{
    private const string Events400k = """
        SELECT
            T1.EVENT_ID
            , T1.CUSTOMER_ID
            , T1.EVENT_TS
            , T1.AMOUNT
            , T1.TAX
            , T1.QUANTITY
            , T1.STATUS
            , T1.SKU
            , T1.NOTE
        FROM JUST_DATA.PERF.EVENTS_400K T1
        """;

    private const string FactProductInventory = """
        SELECT
            T1.PRODUCTKEY
            , T1.DATEKEY
            , T1.MOVEMENTDATE
            , T1.UNITCOST
            , T1.UNITSIN
            , T1.UNITSOUT
            , T1.UNITSBALANCE
        FROM JUST_DATA.ADMIN.FACTPRODUCTINVENTORY T1
        """;

    [Fact]
    public void Events400k_ReusesRepeatedStringInstances()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand(Events400k);
        using var reader = command.ExecuteReader();

        var status = new Dictionary<string, string>(StringComparer.Ordinal);
        long shared = 0;
        int rows = 0;
        while (reader.Read())
        {
            rows++;
            for (int column = 0; column < reader.FieldCount; column++)
            {
                if (reader.GetFieldType(column) != typeof(string))
                {
                    continue;
                }

                string value = reader.GetString(column);
                // Values longer than the pool's 32-character cap are decoded,
                // not interned; only STATUS is guaranteed short and repeated.
                if (reader.GetName(column) != "STATUS")
                {
                    continue;
                }

                if (status.TryGetValue(value, out var previous))
                {
                    Assert.Same(previous, value);
                    shared++;
                }
                else
                {
                    status[value] = value;
                }
            }
        }

        Assert.Equal(400_000, rows);
        Assert.True(shared > 0, "EVENTS_400K should repeat STATUS values");
    }

    [Fact]
    public void FactProductInventory_DecodesAllRows()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand(FactProductInventory);
        using var reader = command.ExecuteReader();

        int rows = 0;
        while (reader.Read())
        {
            rows++;
            _ = reader.GetValue(0);
            _ = reader.GetValue(2);
            _ = reader.GetValue(3);
        }

        Assert.True(rows > 0);
    }

    private static NzConnection OpenConnection()
    {
        var connection = new NzConnection(
            Config.UserName,
            Config.Password,
            Config.Host,
            Config.DbName,
            Config.Port);
        connection.Open();
        return connection;
    }
}
