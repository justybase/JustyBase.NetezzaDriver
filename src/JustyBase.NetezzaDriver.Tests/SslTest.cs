using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Authentication;

namespace JustyBase.NetezzaDriver.Tests;

[Collection("Sequential")]
[Trait("Category", "Integration")]
public class SslTest
{
    private readonly ITestOutputHelper _output;
    public SslTest(ITestOutputHelper output)
    {
        _output = output;
    }
    [Fact]
    public void BasicTests()
    {
        using NzConnection connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName,
            securityLevel: SecurityLevelCode.OnlySecuredSession, sslCerFilePath: @"C:\DEV\DEV\Others\keys\server-cert.pem");
        //this cert file is invalid
        Assert.Throws<AuthenticationException>(() =>
        {
            connection.Open();
            using var command = connection.CreateCommand();
        });
    }

    [Fact]
    public void BasicTests2()
    {
        using NzConnection connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName,
            securityLevel: SecurityLevelCode.OnlySecuredSession, sslCerFilePath: @"C:\DEV\DEV\Others\keys\server-cert.pem", loggerFactory: new NullLoggerFactory());
        // Logger presence must not bypass TLS certificate validation.
        Assert.Throws<AuthenticationException>(() =>
        {
            connection.Open();
            using var command = connection.CreateCommand();
        });
    }
}


// https://www.ibm.com/docs/en/netezza?topic=npssac-netezza-performance-server-client-encryption-security-1
// https://www.ibm.com/docs/en/netezza?topic=cnpshac-show-connection-records-1
//sudo nano /nz/simdata/postgresql.conf
//./simdata/postgresql.conf
//server_cert_file='/nz/simdata/security/server-cert.pem'
//server_key_file='/nz/simdata/security/server-key.pem'
