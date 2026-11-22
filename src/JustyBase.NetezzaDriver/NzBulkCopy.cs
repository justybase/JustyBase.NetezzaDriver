using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;

namespace JustyBase.NetezzaDriver;

/// <summary>
/// Writes rows to a Netezza table using the external-table import protocol.
/// </summary>
/// <remarks>
/// Rows are supplied in destination-column order. Set <see cref="ColumnNames"/>
/// to target a subset or a specific column order. Values that cannot be
/// represented safely by the text import format are inserted row by row.
/// </remarks>
public sealed class NzBulkCopy
{
    private readonly NzConnection _connection;
    private readonly List<string> _columnNames = [];

    internal NzBulkCopy(NzConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    /// <summary>
    /// Gets or sets the destination table. A schema-qualified name is allowed.
    /// </summary>
    public string DestinationTableName { get; set; } = string.Empty;

    /// <summary>
    /// Optional target column names, in the same order as each source row.
    /// When empty, values are assigned in the table's physical column order.
    /// </summary>
    public IList<string> ColumnNames => _columnNames;

    /// <summary>
    /// Writes a collection of positional rows and returns the number of rows
    /// inserted. Each row must contain the same number of values.
    /// </summary>
    public int WriteToServer(IEnumerable<object?[]> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var materialized = rows.ToList();
        return WriteMaterialized(materialized);
    }

    /// <summary>
    /// Writes all rows from a <see cref="DataTable"/> in source-column order.
    /// </summary>
    public int WriteToServer(DataTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return WriteToServer(ReadDataTable(table));
    }

    /// <summary>
    /// Writes all remaining rows from a data reader in field order.
    /// </summary>
    public int WriteToServer(IDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var rows = new List<object?[]>();
        while (reader.Read())
            rows.Add(ReadCurrentRow(reader));
        return WriteMaterialized(rows);
    }

    /// <summary>
    /// Asynchronously writes positional rows and returns the number inserted.
    /// Enumeration is performed before the network operation begins.
    /// </summary>
    public Task<int> WriteToServerAsync(
        IEnumerable<object?[]> rows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        cancellationToken.ThrowIfCancellationRequested();
        return WriteMaterializedAsync(rows.ToList(), cancellationToken);
    }

    /// <summary>
    /// Asynchronously writes all rows from a <see cref="DataTable"/>.
    /// </summary>
    public Task<int> WriteToServerAsync(
        DataTable table,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        cancellationToken.ThrowIfCancellationRequested();
        return WriteMaterializedAsync(ReadDataTable(table).ToList(), cancellationToken);
    }

    /// <summary>
    /// Asynchronously reads and writes all remaining rows from a data reader.
    /// </summary>
    public async Task<int> WriteToServerAsync(
        DbDataReader reader,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var rows = new List<object?[]>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(ReadCurrentRow(reader));
        }
        return await WriteMaterializedAsync(rows, cancellationToken).ConfigureAwait(false);
    }

    private int WriteMaterialized(IReadOnlyList<object?[]> rows)
    {
        ValidateConnectionAndDestination();
        int columnCount = ValidateRows(rows);
        if (rows.Count == 0)
            return 0;

        var sqlTarget = BuildInsertTarget(columnCount);
        var data = _connection.BulkInsertEnabled && rows.Count >= 2
            ? BulkInsertData.Create(columnCount, rows)
            : null;

        using var command = (NzCommand)_connection.CreateCommand();
        if (data is not null)
        {
            string sql = BuildBulkInsertSql(sqlTarget, data);
            int affected = command.ExecuteBulkCopy(sql, data);
            return affected >= 0 ? affected : rows.Count;
        }

        int total = 0;
        foreach (var row in rows)
        {
            command.CommandText = BuildRowInsertSql(sqlTarget, row);
            int affected = command.ExecuteNonQuery();
            if (affected > 0)
                total += affected;
            else
                total++;
        }
        return total;
    }

    private async Task<int> WriteMaterializedAsync(
        IReadOnlyList<object?[]> rows,
        CancellationToken cancellationToken)
    {
        ValidateConnectionAndDestination();
        cancellationToken.ThrowIfCancellationRequested();
        int columnCount = ValidateRows(rows);
        if (rows.Count == 0)
            return 0;

        var sqlTarget = BuildInsertTarget(columnCount);
        var data = _connection.BulkInsertEnabled && rows.Count >= 2
            ? BulkInsertData.Create(columnCount, rows)
            : null;

        await using var command = (NzCommand)_connection.CreateCommand();
        if (data is not null)
        {
            string sql = BuildBulkInsertSql(sqlTarget, data);
            int affected = await command.ExecuteBulkCopyAsync(sql, data, cancellationToken).ConfigureAwait(false);
            return affected >= 0 ? affected : rows.Count;
        }

        int total = 0;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            command.CommandText = BuildRowInsertSql(sqlTarget, row);
            int affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affected > 0)
                total += affected;
            else
                total++;
        }
        return total;
    }

    private void ValidateConnectionAndDestination()
    {
        if (_connection.State != ConnectionState.Open)
            throw new InvalidOperationException("The Netezza connection must be open before writing rows.");
        if (string.IsNullOrWhiteSpace(DestinationTableName))
            throw new InvalidOperationException("DestinationTableName must be set before writing rows.");
        _ = FormatIdentifierPath(DestinationTableName, allowQualified: true);
    }

    private int ValidateRows(IReadOnlyList<object?[]> rows)
    {
        if (rows.Count == 0)
            return 0;
        if (rows[0] is null || rows[0].Length == 0)
            throw new ArgumentException("Source rows must contain at least one value.", nameof(rows));

        int columnCount = rows[0].Length;
        if (_columnNames.Count > 0 && _columnNames.Count != columnCount)
            throw new InvalidOperationException(
                $"ColumnNames contains {_columnNames.Count} columns, but source rows contain {columnCount} values.");

        for (int index = 0; index < rows.Count; index++)
        {
            if (rows[index] is null || rows[index].Length != columnCount)
                throw new ArgumentException(
                    $"Source row {index} has a different number of values than the first row.",
                    nameof(rows));
        }
        return columnCount;
    }

    private string BuildInsertTarget(int columnCount)
    {
        string tableName = FormatIdentifierPath(DestinationTableName, allowQualified: true);
        if (_columnNames.Count == 0)
            return $"INSERT INTO {tableName}";

        var columns = new string[columnCount];
        for (int i = 0; i < columnCount; i++)
            columns[i] = FormatIdentifierPath(_columnNames[i], allowQualified: false);
        return $"INSERT INTO {tableName} ({string.Join(", ", columns)})";
    }

    private static string BuildBulkInsertSql(string insertTarget, BulkInsertData data)
    {
        var select = new StringBuilder(data.ColumnCount * 8);
        for (int i = 0; i < data.ColumnCount; i++)
        {
            if (i > 0)
                select.Append(", ");
            select.Append('c').Append(i);
        }

        string marker = "NZ_BULK_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        return $"{insertTarget} SELECT {select} FROM EXTERNAL '{marker}' ({data.ColumnDefinitions}) USING ({BulkInsertData.UsingClause})";
    }

    private static string BuildRowInsertSql(string insertTarget, object?[] row)
    {
        var values = new string[row.Length];
        for (int i = 0; i < row.Length; i++)
            values[i] = NzParameter.ValueToSqlLiteral(row[i]);
        return $"{insertTarget} VALUES ({string.Join(", ", values)})";
    }

    private static string FormatIdentifierPath(string value, bool allowQualified)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("An SQL identifier cannot be empty.", nameof(value));

        var parts = new List<(string Value, bool WasQuoted)>();
        int index = 0;
        while (index < value.Length)
        {
            while (index < value.Length && char.IsWhiteSpace(value[index]))
                index++;
            if (index >= value.Length)
                throw new ArgumentException("Invalid SQL identifier path.", nameof(value));

            bool quoted = value[index] == '"';
            var part = new StringBuilder();
            if (quoted)
            {
                index++;
                bool closed = false;
                while (index < value.Length)
                {
                    char c = value[index++];
                    if (c != '"')
                    {
                        part.Append(c);
                        continue;
                    }
                    if (index < value.Length && value[index] == '"')
                    {
                        part.Append('"');
                        index++;
                        continue;
                    }
                    closed = true;
                    break;
                }
                if (!closed)
                    throw new ArgumentException("Unterminated quoted SQL identifier.", nameof(value));
                while (index < value.Length && char.IsWhiteSpace(value[index]))
                    index++;
                if (part.Length == 0)
                    throw new ArgumentException("An SQL identifier cannot be empty.", nameof(value));
            }
            else
            {
                int start = index;
                while (index < value.Length && value[index] != '.')
                    index++;
                string unquoted = value[start..index].Trim();
                if (unquoted.Length == 0 || unquoted.Contains('"'))
                    throw new ArgumentException("Invalid SQL identifier.", nameof(value));
                part.Append(unquoted);
            }

            parts.Add((part.ToString(), quoted));
            if (index == value.Length)
                break;
            if (value[index] != '.')
                throw new ArgumentException("Invalid SQL identifier path.", nameof(value));
            if (!allowQualified)
                throw new ArgumentException("ColumnNames entries must be single identifiers.", nameof(value));
            index++;
            if (index == value.Length)
                throw new ArgumentException("Invalid SQL identifier path.", nameof(value));
        }

        return string.Join('.', parts.Select(part =>
        {
            if (!part.WasQuoted && IsSimpleIdentifier(part.Value))
                return part.Value;
            return "\"" + part.Value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }));
    }

    private static bool IsSimpleIdentifier(string value)
    {
        if (value.Length == 0 || !(char.IsLetter(value[0]) || value[0] == '_'))
            return false;
        for (int i = 1; i < value.Length; i++)
        {
            if (!(char.IsLetterOrDigit(value[i]) || value[i] is '_' or '$'))
                return false;
        }
        return true;
    }

    private static IEnumerable<object?[]> ReadDataTable(DataTable table)
    {
        foreach (DataRow source in table.Rows)
        {
            if (source.RowState == DataRowState.Deleted)
                continue;
            var row = new object?[table.Columns.Count];
            for (int i = 0; i < row.Length; i++)
            {
                object value = source[i];
                row[i] = value is DBNull ? null : value;
            }
            yield return row;
        }
    }

    private static object?[] ReadCurrentRow(IDataRecord reader)
    {
        var row = new object?[reader.FieldCount];
        for (int i = 0; i < row.Length; i++)
        {
            object value = reader.GetValue(i);
            row[i] = value is DBNull ? null : value;
        }
        return row;
    }
}
