using Xunit;

namespace JustyBase.NetezzaDriver.Tests;

[Trait("Category", "Unit")]
public sealed class ConnectionOptionsTests
{
    private const string BaseConnectionString = "Host=nz.example;Database=JUST_DATA;User=admin;Password=secret";

    [Fact]
    public void Defaults_MatchTheOtherDrivers()
    {
        using var connection = new NzConnection(BaseConnectionString);

        Assert.Null(connection.ConfiguredSslMode);
        Assert.Equal(SecurityLevelCode.PreferredUnsecured, connection.SecurityLevel);
        Assert.True(connection.RejectUnauthorized);
        Assert.True(connection.VerifyHostname);
        Assert.Equal(CharEncoding.Latin1, connection.CharVarcharEncoding);
        Assert.Null(connection.SslCertFilePath);
    }

    [Theory]
    [InlineData("disable", SecurityLevelCode.OnlyUnsecuredSession, true, false)]
    [InlineData("prefer", SecurityLevelCode.PreferredUnsecured, true, true)]
    [InlineData("require", SecurityLevelCode.OnlySecuredSession, false, false)]
    [InlineData("verify-ca", SecurityLevelCode.OnlySecuredSession, true, false)]
    [InlineData("verify-full", SecurityLevelCode.OnlySecuredSession, true, true)]
    [InlineData("VERIFY_FULL", SecurityLevelCode.OnlySecuredSession, true, true)]
    public void SslMode_MapsToHandshakeSecurityAndValidation(
        string value,
        SecurityLevelCode expectedSecurity,
        bool expectedValidation,
        bool expectedHostname)
    {
        using var connection = new NzConnection($"{BaseConnectionString};SslMode={value}");

        Assert.Equal(expectedSecurity, connection.SecurityLevel);
        Assert.Equal(expectedValidation, connection.RejectUnauthorized);
        Assert.Equal(expectedHostname, connection.VerifyHostname);
    }

    [Fact]
    public void Require_CanOptIntoCertificateValidation()
    {
        using var connection = new NzConnection(
            $"{BaseConnectionString};SslMode=require;RejectUnauthorized=true");

        Assert.True(connection.RejectUnauthorized);
        Assert.True(connection.VerifyHostname);
    }

    [Fact]
    public void LowercaseCrossDriverOptionNames_AreAccepted()
    {
        using var connection = new NzConnection(
            $"{BaseConnectionString};sslmode=require;char_varchar_encoding=utf8");

        Assert.Equal(SslMode.Require, connection.ConfiguredSslMode);
        Assert.Equal(CharEncoding.Utf8, connection.CharVarcharEncoding);
    }

    [Fact]
    public void VerifyCa_CanBeDowngradedOnlyByDroppingTheMode()
    {
        Assert.Throws<NetezzaException>(() => new NzConnection(
            $"{BaseConnectionString};SslMode=verify-ca;RejectUnauthorized=false"));
        Assert.Throws<NetezzaException>(() => new NzConnection(
            $"{BaseConnectionString};SslMode=verify-full;RejectUnauthorized=false"));
    }

    [Fact]
    public void SecurityLevel_SpellingRemainsAvailableForCompatibility()
    {
        using var connection = new NzConnection(
            $"{BaseConnectionString};SecurityLevel=OnlySecuredSession");

        Assert.Null(connection.ConfiguredSslMode);
        Assert.Equal(SecurityLevelCode.OnlySecuredSession, connection.SecurityLevel);
        Assert.True(connection.RejectUnauthorized);
        Assert.True(connection.VerifyHostname);
    }

    [Fact]
    public void SslModeAndSecurityLevel_CannotBeCombined()
    {
        var error = Assert.Throws<NetezzaException>(() => new NzConnection(
            $"{BaseConnectionString};SslMode=require;SecurityLevel=OnlySecuredSession"));

        Assert.Contains("not both", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("sometimes")]
    [InlineData("verify")]
    public void InvalidSslMode_IsRejected(string value)
    {
        var error = Assert.Throws<NetezzaException>(() => new NzConnection(
            $"{BaseConnectionString};SslMode={value}"));

        Assert.Contains("Invalid SslMode", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidSecurityLevel_IsRejected()
    {
        Assert.Throws<NetezzaException>(() => new NzConnection(
            $"{BaseConnectionString};SecurityLevel=Root"));
    }

    [Theory]
    [InlineData("latin", CharEncoding.Latin1)]
    [InlineData("latin1", CharEncoding.Latin1)]
    [InlineData("ISO-8859-1", CharEncoding.Latin1)]
    [InlineData("utf8", CharEncoding.Utf8)]
    [InlineData("UTF-8", CharEncoding.Utf8)]
    public void CharVarcharEncoding_ParsesCrossDriverSpellings(string value, CharEncoding expected)
    {
        using var connection = new NzConnection(
            $"{BaseConnectionString};CharVarcharEncoding={value}");

        Assert.Equal(expected, connection.CharVarcharEncoding);
    }

    [Fact]
    public void CharVarcharEncoding_MapsToTheExpectedDotnetEncoding()
    {
        byte[] latin1 = [0xF3]; // ó in ISO-8859-1
        Assert.Equal("ó", NzOptionParser.ToEncoding(CharEncoding.Latin1).GetString(latin1));
        Assert.NotEqual("ó", NzOptionParser.ToEncoding(CharEncoding.Utf8).GetString(latin1));
    }

    [Fact]
    public void InvalidCharVarcharEncoding_IsRejected()
    {
        Assert.Throws<NetezzaException>(() => new NzConnection(
            $"{BaseConnectionString};CharVarcharEncoding=cp1252"));
    }

    [Fact]
    public void ConnectionString_RoundTripsTlsAndEncodingOptions()
    {
        using var original = new NzConnection(
            $"{BaseConnectionString};SslMode=verify-full;SslCertFilePath=ca.pem;" +
            "CharVarcharEncoding=utf8;RejectUnauthorized=true");

        using var parsed = new NzConnection(original.ConnectionString);

        Assert.Equal(SslMode.VerifyFull, parsed.ConfiguredSslMode);
        Assert.Equal("ca.pem", parsed.SslCertFilePath);
        Assert.Equal(CharEncoding.Utf8, parsed.CharVarcharEncoding);
        Assert.True(parsed.RejectUnauthorized);
        Assert.True(parsed.VerifyHostname);
    }

    [Fact]
    public void ConnectionString_RoundTripsLegacySecurityLevel()
    {
        using var original = new NzConnection(
            BaseConnectionString,
            SecurityLevelCode.OnlySecuredSession);

        using var parsed = new NzConnection(original.ConnectionString);

        Assert.Equal(SecurityLevelCode.OnlySecuredSession, parsed.SecurityLevel);
        Assert.Null(parsed.ConfiguredSslMode);
    }

    [Fact]
    public void Builder_OptionsAreMutuallyExclusiveAndSurviveSafeString()
    {
        var builder = new NzConnectionStringBuilder
        {
            Host = "h",
            Database = "d",
            UserName = "u",
            Password = "p",
            SslMode = SslMode.VerifyCA,
            SslCertFilePath = "ca.pem",
            CharVarcharEncoding = CharEncoding.Utf8,
        };

        Assert.False(builder.ContainsKey("SecurityLevel"));
        string safe = builder.SafeConnectionString;
        Assert.Contains("SslMode=verify-ca", safe, StringComparison.Ordinal);
        Assert.Contains("SslCertFilePath=ca.pem", safe, StringComparison.Ordinal);
        Assert.Contains("CharVarcharEncoding=utf8", safe, StringComparison.Ordinal);
        Assert.Contains("Password=********", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("Password=p", safe, StringComparison.Ordinal);

        builder.SecurityLevel = SecurityLevelCode.OnlySecuredSession;
        Assert.False(builder.ContainsKey("SslMode"));
        Assert.Equal(SecurityLevelCode.OnlySecuredSession, builder.SecurityLevel);
    }

    [Fact]
    public void TimeoutZero_KeepsTheDriverDefault()
    {
        using var connection = new NzConnection($"{BaseConnectionString};Timeout=0");
        Assert.Equal(15, connection.ConnectionTimeout);
    }

    [Fact]
    public void RejectUnauthorized_AcceptsNumericBooleanSpellings()
    {
        using var zero = new NzConnection(
            $"{BaseConnectionString};RejectUnauthorized=0");
        using var one = new NzConnection(
            $"{BaseConnectionString};RejectUnauthorized=1");

        Assert.False(zero.RejectUnauthorized);
        Assert.True(one.RejectUnauthorized);
    }
}
