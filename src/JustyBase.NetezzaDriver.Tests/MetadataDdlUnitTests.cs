namespace JustyBase.NetezzaDriver.Tests;

public class MetadataDdlUnitTests
{
    [Fact]
    public void ExternalLayoutZonesReconstructOrderedClausesAndNullRules()
    {
        var zones = new[]
        {
            new NzMetadata.ExternalLayoutZone("FILLER", "F1", "CHAR(2)", "INTERNAL", "BYTES 2", "", "", "", "", "", ""),
            new NzMetadata.ExternalLayoutZone("", "SELECT", "INT4", "DECIMAL", "BYTES 4", "", "", "&&2 = ''", "", "", ""),
            new NzMetadata.ExternalLayoutZone("", "DT", "DATE", "YMD", "BYTES 10", "-", "", "", "", "", ""),
            new NzMetadata.ExternalLayoutZone("", " DATE FIELD ", "DATE", "YMD", "BYTES 10", " ", "", "", "", "", ""),
        };

        Assert.Equal(
            "FILLER F1 CHAR(2) INTERNAL BYTES 2, \"SELECT\" INT4 DECIMAL BYTES 4 NULLIF &&2 = '', DT DATE YMD '-' BYTES 10, \" DATE FIELD \" DATE YMD ' ' BYTES 10",
            NzMetadata.FormatExternalLayoutZones(zones));
    }

    [Fact]
    public void ExternalLayoutZonesRejectUnmappedCatalogMetadata()
    {
        var zones = new[]
        {
            new NzMetadata.ExternalLayoutZone("", "C", "INT4", "DECIMAL", "BYTES 4", "", "", "", "BIG", "", ""),
        };

        Assert.Throws<InvalidOperationException>(() => NzMetadata.FormatExternalLayoutZones(zones));
    }

    [Fact]
    public void SynonymTargetParserTrimsOutsideWhitespaceAndPreservesQuotedWhitespace()
    {
        Assert.Equal(
            new[] { " Schema Name ", " Target Name " },
            NzMetadata.SplitIdentifierPath("  \" Schema Name \" . \" Target Name \"  "));
    }
}
