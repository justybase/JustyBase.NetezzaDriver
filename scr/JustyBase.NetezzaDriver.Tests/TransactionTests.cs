
namespace JustyBase.NetezzaDriver.Tests;

[Collection("Sequential")]
[Trait("Category", "Integration")]
public class TransactionTests
{

    [Fact]
    public void BasicTransactionsTests()
    {
        using NzConnection connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName);
        connection.Open();
        using var command = connection.CreateCommand();

        connection.AutoCommit = false; // autocommit is on by default. It can be turned off by using the autocommit property of the connection.

        command.CommandText = "DROP TABLE T2 IF EXISTS";
        command.ExecuteNonQuery();
        command.CommandText = "create table t2(c1 numeric (10,5), c2 varchar(10),c3 nchar(5))";
        command.ExecuteNonQuery();
        command.CommandText = "insert into t2 values (123.54,'xcfd','xyz')";
        command.ExecuteNonQuery();
        connection.Rollback();
        command.CommandText = "DROP TABLE T5 IF EXISTS";
        command.ExecuteNonQuery();
        command.CommandText = "create table t5(c1 numeric (10,5), c2 varchar(10),c3 nchar(5))";
        command.ExecuteNonQuery();
        command.CommandText = "insert into t5 values (123.54,'xcfd','xyz')";
        command.ExecuteNonQuery();
        connection.Commit();

        command.CommandText = "SELECT * FROM T2";
        Assert.Throws<NetezzaException>(() => command.ExecuteNonQuery());
        try
        {
            command.CommandText = "SELECT * FROM T5";
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Assert.Fail( $"Expected no exception, but got: {ex.Message}");
        }

    }

    [Fact]
    public void BeginTransaction_ShouldManageConnectionTransactionState()
    {
        using NzConnection connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName);
        connection.Open();

        using (var transaction = connection.BeginTransaction())
        {
            Assert.True(connection.InTransaction);
            Assert.False(connection.AutoCommit);
            transaction.Commit();
        }

        Assert.False(connection.InTransaction);
        Assert.True(connection.AutoCommit);

        using (var transaction = connection.BeginTransaction())
        {
            Assert.True(connection.InTransaction);
            Assert.False(connection.AutoCommit);
            transaction.Rollback();
        }

        Assert.False(connection.InTransaction);
        Assert.True(connection.AutoCommit);
    }

    [Fact]
    public void ChangeDatabase_WhenTransactionIsActive_ShouldThrowInvalidOperationException()
    {
        using NzConnection connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName);
        connection.Open();

        using var transaction = connection.BeginTransaction();

        Assert.Throws<InvalidOperationException>(() => connection.ChangeDatabase(Config.DbName));
    }


}
