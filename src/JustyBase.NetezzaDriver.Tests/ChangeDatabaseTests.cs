namespace JustyBase.NetezzaDriver.Tests;

[Trait("Category", "Unit")]
public class ChangeDatabaseTests
{
    [Fact]
    public void ChangeDatabase_WhenDatabaseNameIsNullOrWhiteSpace_ShouldThrowArgumentException()
    {
        using var connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName);

        Assert.Throws<ArgumentException>(() => connection.ChangeDatabase(null!));
        Assert.Throws<ArgumentException>(() => connection.ChangeDatabase(""));
        Assert.Throws<ArgumentException>(() => connection.ChangeDatabase("   "));
    }

    [Theory]
    [InlineData("1db")]
    [InlineData("db-name")]
    [InlineData("db name")]
    [InlineData("db;select")]
    [InlineData("\"db\"")]
    public void ChangeDatabase_WhenDatabaseNameIsNotUnquotedIdentifier_ShouldThrowArgumentException(string databaseName)
    {
        using var connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName);

        Assert.Throws<ArgumentException>(() => connection.ChangeDatabase(databaseName));
    }

    [Fact]
    public void ChangeDatabase_WhenConnectionIsClosed_ShouldThrowInvalidOperationException()
    {
        using var connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName);

        Assert.Throws<InvalidOperationException>(() => connection.ChangeDatabase(Config.DbName));
    }
}
