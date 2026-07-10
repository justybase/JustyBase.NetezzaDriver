using Xunit;

namespace JustyBase.NetezzaDriver.Tests;

[Trait("Category", "Unit")]
public class NzConnectionStringBuilderTests
{
    [Fact]
    public void ToString_ReturnsCorrectConnectionString()
    {
        // Arrange
        var builder = new NzConnectionStringBuilder
        {
            Host = "localhost",
            Database = "testdb",
            UserName = "user",
            Password = "password",
            Port = 5480,
            Timeout = 30
        };

        // Act
        var connectionString = builder.ToString();

        // Assert
        Assert.Contains("Host=localhost;", connectionString);
        Assert.Contains("Database=testdb;", connectionString);
        Assert.Contains("User=user;", connectionString);
        Assert.Contains("Password=password;", connectionString);
        Assert.Contains("Port=5480;", connectionString);
        Assert.Contains("Timeout=30;", connectionString);
    }

    [Fact]
    public void ConnectionString_Property_ReturnsSameAsToString()
    {
        // Arrange
        var builder = new NzConnectionStringBuilder
        {
            Host = "192.168.1.1",
            Database = "prod",
            UserName = "admin",
            Password = "secure",
            Port = 5481
        };

        // Act
        var connectionStringProp = builder.ConnectionString;
        var toString = builder.ToString();

        // Assert
        Assert.Equal(toString, connectionStringProp);
    }

    [Fact]
    public void ToSafeString_RedactsPassword()
    {
        var builder = new NzConnectionStringBuilder
        {
            Host = "localhost",
            Database = "testdb",
            UserName = "user",
            Password = "password"
        };

        var safeConnectionString = builder.ToSafeString();

        Assert.Contains("Password=********;", safeConnectionString);
        Assert.DoesNotContain("Password=password;", safeConnectionString);
        Assert.Equal(safeConnectionString, builder.SafeConnectionString);
    }

    [Fact]
    public void Defaults_AreCorrect()
    {
        // Arrange
        var builder = new NzConnectionStringBuilder();

        // Act
        var connectionString = builder.ToString();

        // Assert
        Assert.Contains("Port=5480;", connectionString);
        Assert.Contains("Timeout=0;", connectionString);
    }

    [Fact]
    public void NzConnection_SafeConnectionString_RedactsPassword()
    {
        using var connection = new NzConnection("user", "password", "host", "db");

        Assert.Contains("Password=********;", connection.SafeConnectionString);
        Assert.DoesNotContain("Password=password;", connection.SafeConnectionString);
        Assert.Contains("Password=password;", connection.ConnectionString);
    }
}
