using System.Globalization;

namespace JustyBase.NetezzaDriver.Tests;

[Collection("Sequential")]
[Trait("Category", "Integration")]
public sealed class NzpyExtendedResultCompatibilityTests
{
    private const string RepresentativeQuery = """
        SELECT
            10::BIGINT AS I64_VALUE,
            10::INTEGER AS I32_VALUE,
            10::SMALLINT AS I16_VALUE,
            5::BYTEINT AS I8_VALUE,
            true::BOOLEAN AS BOOL_TRUE,
            false::BOOLEAN AS BOOL_FALSE,
            null::BIGINT AS NULL_VALUE,
            'abc'::VARCHAR(10) AS TEXT_VALUE,
            1.54::NUMERIC(10,2) AS DECIMAL_VALUE,
            1.5::REAL AS FLOAT_VALUE,
            3.5::DOUBLE AS DOUBLE_VALUE,
            '2026-09-23'::DATE AS DATE_VALUE,
            '2026-09-23 10:12:13'::TIMESTAMP AS TIMESTAMP_VALUE,
            '10:12:13'::TIME AS TIME_VALUE
        """;

    [Fact(Timeout = 60000)]
    public async Task Representative_scalar_results_match_nzpy_extended()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        NzpyExtendedReference.ReferenceResultSet[] referenceResults =
            await NzpyExtendedReference.ExecuteAsync([RepresentativeQuery], cancellationToken);
        var reference = Assert.Single(referenceResults);

        await using var connection = new NzConnection(
            Config.UserName,
            Config.Password,
            Config.Host,
            Config.DbName,
            Config.Port);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = RepresentativeQuery;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        Assert.Equal(reference.Columns, Enumerable.Range(0, reader.FieldCount).Select(reader.GetName));

        int rowIndex = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            Assert.True(rowIndex < reference.Rows.Length, "The .NET driver returned extra rows.");
            string?[] expectedRow = reference.Rows[rowIndex];
            Assert.Equal(reader.FieldCount, expectedRow.Length);

            for (int ordinal = 0; ordinal < reader.FieldCount; ordinal++)
            {
                Assert.Equal(
                    expectedRow[ordinal],
                    NormalizeValue(reader.GetValue(ordinal), reader.GetName(ordinal)));
            }

            rowIndex++;
        }

        Assert.Equal(reference.Rows.Length, rowIndex);
    }

    private static string? NormalizeValue(object value, string columnName)
    {
        if (value is DBNull)
        {
            return null;
        }

        return value switch
        {
            bool boolean => boolean ? "true" : "false",
            DateTime dateTime when columnName == "DATE_VALUE" => dateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateTime dateTime => TrimFraction(dateTime.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture)),
            TimeSpan time => TrimFraction(time.ToString(@"hh\:mm\:ss\.fffffff", CultureInfo.InvariantCulture)),
            IFormattable formatted => formatted.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        };
    }

    private static string TrimFraction(string value) => value.TrimEnd('0').TrimEnd('.');
}
