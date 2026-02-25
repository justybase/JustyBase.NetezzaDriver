namespace JustyBase.NetezzaDriver.Tests;

[Collection("Sequential")]
[Trait("Category", "Integration")]
public class InvalidSqlTests
{
    [Fact]
    public void ReaderShouldThrow()
    {
        using NzConnection connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName);
        connection.Open();
        connection.CommandTimeout = TimeSpan.FromSeconds(120);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1,,2;SELECT 1,2";
        Assert.Throws<NetezzaException>(() => command.ExecuteReader());
    }

    [Fact]
    public void ExecuteNonQueryShouldThrow()
    {
        using NzConnection connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName);
        connection.Open();
        connection.CommandTimeout = TimeSpan.FromSeconds(120);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1,,2;SELECT 1,2";
        Assert.Throws<NetezzaException>(() => command.ExecuteNonQuery());
    }

    [Fact]
    public void ExecuteScalarShouldThrow()
    {
        using NzConnection connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName);
        connection.Open();
        connection.CommandTimeout = TimeSpan.FromSeconds(120);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1,,2;SELECT 1,2";
        Assert.Throws<NetezzaException>(() => command.ExecuteScalar());
    }



    [Theory]
    [InlineData("SELECT 1/0 FROM TEST_NUM_TXT")]
    [InlineData("SELECT SUM(X.COL::INT) FROM TEST_NUM_TXT X")]
    [InlineData("SELECT * FROM TEST_NUM_TXT X JOIN TEST_NUM_TXT X2 ON X.COL::INT = X2.COL::INT")]
    [InlineData("SELECT 'X'::INT FROM TEST_NUM_TXT")]
    [InlineData("SELECT 'X'::INT")]
    public void SqlQueries_WithExpectedExceptions_ShouldThrowException(string sql)
    {
        using NzConnection connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName);
        connection.Open();
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;

        Assert.ThrowsAny<Exception>(() =>
        {
            var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                // Just iterate through the results if no exception is thrown
            }
        });

    }
}
    
