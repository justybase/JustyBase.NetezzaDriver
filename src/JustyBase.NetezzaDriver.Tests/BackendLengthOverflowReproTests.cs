namespace JustyBase.NetezzaDriver.Tests;

[Collection("Sequential")]
[Trait("Category", "Integration")]
public sealed class BackendLengthOverflowReproTests
{
    private const string ReproSql = "SELECT * FROM JUST_DATA.ADMIN.DIMDATE";

    [Fact]
    public void FailingQuery_CanReadBeyondPreviewBoundary()
    {
        using var connection = new NzConnection(
            Config.UserName,
            Config.Password,
            Config.Host,
            Config.DbName,
            Config.Port,
            loggerFactory: ProtocolTraceTestLogger.CreateIfEnabled());

        int rowsRead = 0;

        try
        {
            connection.Open();

            using var command = connection.CreateCommand(ReproSql);
            using var reader = command.ExecuteReader();

            while (rowsRead < 500)
            {
                if (!reader.Read())
                {
                    throw new Xunit.Sdk.XunitException(
                        $"The exact repro query returned only {rowsRead} rows; at least 501 are required. SQL: {ReproSql}");
                }

                rowsRead++;
            }

            if (!reader.Read())
            {
                throw new Xunit.Sdk.XunitException(
                    $"The exact repro query has no row 501. SQL: {ReproSql}");
            }

            rowsRead++;

            while (reader.Read())
            {
                rowsRead++;
            }
        }
        catch (Xunit.Sdk.XunitException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new Xunit.Sdk.XunitException(
                $"Driver failed while reading row {rowsRead + 1}. SQL: {ReproSql}",
                ex);
        }

        Assert.True(rowsRead > 500, $"Expected to read beyond row 500, but read {rowsRead} rows.");
    }

}
