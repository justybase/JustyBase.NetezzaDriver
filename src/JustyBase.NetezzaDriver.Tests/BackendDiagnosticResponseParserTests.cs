using System.Text;

namespace JustyBase.NetezzaDriver.Tests;

[Trait("Category", "Unit")]
public sealed class BackendDiagnosticResponseParserTests
{
    [Fact]
    public void Parse_ReadsStructuredErrorAndPreservesUnknownDiagnosticFields()
    {
        const string payload = "SERROR\0VERROR\0C42601\0Msyntax error\0Dunexpected token\0Hcheck the query\0Zserver extension\0\0";
        var response = BackendDiagnosticResponseParser.Parse(Encoding.UTF8.GetBytes(payload));

        Assert.Equal("syntax error", response.Message);
        Assert.Equal("ERROR", response.Severity);
        Assert.Equal("42601", response.SqlState);
        Assert.Equal("unexpected token", response.Detail);
        Assert.Equal("check the query", response.Hint);
        Assert.Equal("server extension", response.Diagnostics['Z']);
        Assert.Equal(payload, response.RawResponse);

        var exception = new NetezzaException(response);
        Assert.Equal("syntax error", exception.Message);
        Assert.Equal("42601", exception.SqlState);
        Assert.Equal(payload, exception.RawResponse);
    }

    [Fact]
    public void Parse_PrefersNonLocalizedSeverity()
    {
        var response = BackendDiagnosticResponseParser.Parse(
            Encoding.UTF8.GetBytes("Slocalized\0Vnonlocalized\0Mmessage\0\0"));

        Assert.Equal("nonlocalized", response.Severity);
    }

    [Theory]
    [InlineData("permission denied\0", "permission denied")]
    [InlineData("\0\0", "Netezza backend returned an empty error response")]
    public void Parse_UsesPlainTextFallback(string payload, string expectedMessage)
    {
        var response = BackendDiagnosticResponseParser.Parse(Encoding.UTF8.GetBytes(payload));

        Assert.Equal(expectedMessage, response.Message);
        Assert.Equal(payload, response.RawResponse);
        Assert.Empty(response.Diagnostics);
        Assert.Null(response.SqlState);
    }
}
