using Xunit;
using JustyBase.NetezzaDriver;

namespace JustyBase.NetezzaDriver.Tests;

[Trait("Category", "Integration")]
public class AuthenticationTests
{
    private const string _invalidPassword = "WrongPassword123!";

    [Fact]
    public void Open_WithInvalidPassword_ThrowsNetezzaException()
    {
        // Arrange
        var connectionString = $"Host={Config.Host};Database={Config.DbName};User={Config.UserName};Password={_invalidPassword};Port={Config.Port};Timeout=5";
        using var connection = new NzConnection(connectionString);

        // Act & Assert
        var exception = Assert.Throws<NetezzaException>(() => connection.Open());
        // The driver currently throws "Error in handshake" on auth failure
        Assert.Contains("Password authentication failed", exception.Message);
    }
}
