namespace JustyBase.NetezzaDriver.Tests;

[Trait("Category", "Unit")]
public class ChangeDatabaseTests
{
    private const string TestUser = "user";
    private const string TestPassword = "password";
    private const string TestHost = "127.0.0.1";
    private const string TestDatabase = "JUST_DATA";

    private static NzConnection CreateConnection() =>
        new(TestUser, TestPassword, TestHost, TestDatabase);

    [Fact]
    public void ChangeDatabase_WhenDatabaseNameIsNullOrWhiteSpace_ShouldThrowArgumentException()
    {
        using var connection = CreateConnection();

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
        using var connection = CreateConnection();

        Assert.Throws<ArgumentException>(() => connection.ChangeDatabase(databaseName));
    }

    [Fact]
    public void ChangeDatabase_WhenConnectionIsClosed_ShouldThrowInvalidOperationException()
    {
        using var connection = CreateConnection();

        Assert.Throws<InvalidOperationException>(() => connection.ChangeDatabase(TestDatabase));
    }
}
