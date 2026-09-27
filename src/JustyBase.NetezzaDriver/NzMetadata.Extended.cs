using System.Data.Common;

namespace JustyBase.NetezzaDriver;

public sealed record NzSequenceInfo(string Schema, string Name, string? Owner, long? ObjId);
public sealed record NzUserInfo(string Name, long? ObjId);
public sealed record NzGroupInfo(string Name, long? ObjId);
public sealed record NzQueryHistoryInfo(
    long? SessionId, string? Username, string? Database, string? QueryText,
    string? SubmitTime, string? StartTime, long? ResultRows);
public sealed record NzDetailedColumnInfo(
    string Schema, string Name, int Ordinal, string TypeName, bool NotNull,
    string? DefaultValue, string? Description);
public sealed record NzTableKeyInfo(
    string Name, string KeyType, char TypeChar, IReadOnlyList<string> Columns,
    string? PkDatabase, string? PkSchema, string? PkRelation,
    IReadOnlyList<string> PkColumns, string UpdateType, string DeleteType);
public sealed record NzDdlBatchResult(string Schema, string Name, string Ddl, string? Error);

public sealed partial class NzMetadata
{
    public async Task<string?> GetCurrentDatabaseAsync()
    {
        await using var command = _connection.CreateCommand("SELECT current_catalog");
        return (await command.ExecuteScalarAsync().ConfigureAwait(false))?.ToString();
    }

    public async Task<string?> GetCurrentSchemaAsync()
    {
        await using var command = _connection.CreateCommand("SELECT current_schema");
        return (await command.ExecuteScalarAsync().ConfigureAwait(false))?.ToString();
    }

    public Task<IReadOnlyList<NzSequenceInfo>> GetSequencesAsync(string? schema = null)
    {
        var sql = "SELECT schema, seqname, owner, objid FROM _v_sequence WHERE seqname IS NOT NULL";
        if (schema is not null) sql += $" AND schema = {SqlStringLiteral(schema)}";
        sql += " ORDER BY schema, seqname";
        return ExecuteQueryAsync(sql, r => new NzSequenceInfo(
            Text(r, 0)!, Text(r, 1)!, Text(r, 2), Number(r, 3)));
    }

    public Task<IReadOnlyList<NzUserInfo>> GetUsersAsync() =>
        ExecuteQueryAsync("SELECT username, objid FROM _v_user ORDER BY username",
            r => new NzUserInfo(Text(r, 0)!, Number(r, 1)));

    public Task<IReadOnlyList<NzGroupInfo>> GetGroupsAsync() =>
        ExecuteQueryAsync("SELECT groupname, objid FROM _v_group ORDER BY groupname",
            r => new NzGroupInfo(Text(r, 0)!, Number(r, 1)));

    public async Task<IReadOnlyList<NzQueryHistoryInfo>> GetQueryHistoryAsync(int limit = 100, string? username = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        var sql = "SELECT qh_sessionid, qh_user, qh_database, qh_sql, qh_tsubmit, qh_tstart, qh_resrows FROM _v_qryhist WHERE 1=1";
        if (username is not null) sql += $" AND qh_user = {SqlStringLiteral(username)}";
        sql += $" ORDER BY qh_tsubmit DESC LIMIT {limit}";
        try
        {
            return await ExecuteQueryAsync(sql, r => new NzQueryHistoryInfo(
                Number(r, 0), Text(r, 1), Text(r, 2), Text(r, 3),
                Text(r, 4), Text(r, 5), Number(r, 6))).ConfigureAwait(false);
        }
        catch (NetezzaException error) when (error.SqlState == "42P01")
        {
            return Array.Empty<NzQueryHistoryInfo>();
        }
    }

    public async Task<IReadOnlyList<NzDetailedColumnInfo>> GetDetailedColumnsAsync(
        string table, string? schema = null)
    {
        (schema, table) = NormalizeObjectName(table, schema);
        var sql = "SELECT schema, attname, attnum, format_type, attnotnull, coldefault, description "
            + $"FROM _v_relation_column WHERE name = {SqlStringLiteral(table)}";
        if (schema is not null) sql += $" AND schema = {SqlStringLiteral(schema)}";
        sql += " ORDER BY attnum";
        var rows = await ExecuteQueryAsync(sql, r => new NzDetailedColumnInfo(
            Text(r, 0)!, Text(r, 1)!, Convert.ToInt32(r.GetValue(2)),
            Text(r, 3)!, Boolean(r, 4), Text(r, 5), Text(r, 6))).ConfigureAwait(false);
        if (schema is null && rows.Select(row => row.Schema).Distinct(StringComparer.Ordinal).Skip(1).Any())
            throw new ArgumentException($"Table {table} exists in several schemas; pass schema explicitly");
        return rows;
    }

    public async Task<IReadOnlyList<string>> GetOrganizeColumnsAsync(string table, string? schema = null)
    {
        (schema, table) = NormalizeObjectName(table, schema);
        var sql = $"SELECT attname FROM _v_table_organize_column WHERE tablename = {SqlStringLiteral(table)}";
        if (schema is not null) sql += $" AND schema = {SqlStringLiteral(schema)}";
        sql += " ORDER BY orgseqno";
        try
        {
            return await ExecuteQueryAsync(sql, r => Text(r, 0)!).ConfigureAwait(false);
        }
        catch (NetezzaException error) when (error.SqlState == "42P01")
        {
            return Array.Empty<string>();
        }
    }

    public async Task<IReadOnlyList<NzTableKeyInfo>> GetTableKeysAsync(
        string table, string? schema = null)
    {
        (schema, table) = NormalizeObjectName(table, schema);
        var sql = "SELECT constraintname, contype, attname, pkdatabase, pkschema, pkrelation, "
            + $"pkattname, updt_type, del_type FROM _v_relation_keydata WHERE relation = {SqlStringLiteral(table)}";
        if (schema is not null) sql += $" AND schema = {SqlStringLiteral(schema)}";
        sql += " ORDER BY constraintname, conseq";
        IReadOnlyList<KeyRow> rows;
        try
        {
            rows = await ExecuteQueryAsync(sql, r => new KeyRow(
                Text(r, 0)!, Text(r, 1)!, Text(r, 2), Text(r, 3),
                Text(r, 4), Text(r, 5), Text(r, 6), Text(r, 7), Text(r, 8))).ConfigureAwait(false);
        }
        catch (NetezzaException error) when (error.SqlState == "42P01")
        {
            return Array.Empty<NzTableKeyInfo>();
        }
        var result = new List<NzTableKeyInfo>();
        foreach (var group in rows.GroupBy(row => row.Name))
        {
            var first = group.First();
            var typeChar = first.Type.FirstOrDefault('?');
            result.Add(new NzTableKeyInfo(
                first.Name,
                typeChar switch { 'p' => "PRIMARY KEY", 'f' => "FOREIGN KEY", 'u' => "UNIQUE", _ => "UNKNOWN" },
                typeChar,
                group.Select(row => row.Column).OfType<string>().ToArray(),
                first.PkDatabase, first.PkSchema, first.PkRelation,
                group.Select(row => row.PkColumn).OfType<string>().ToArray(),
                first.UpdateType ?? "NO ACTION", first.DeleteType ?? "NO ACTION"));
        }
        return result.AsReadOnly();
    }

    public async Task<string?> GetTableCommentAsync(string table, string? schema = null)
    {
        (schema, table) = NormalizeObjectName(table, schema);
        var where = $"objname = {SqlStringLiteral(table)}";
        if (schema is not null) where += $" AND schema = {SqlStringLiteral(schema)}";
        foreach (var suffix in new[] { " AND objtype = 'TABLE'", "" })
        {
            try
            {
                var rows = await ExecuteQueryAsync(
                    $"SELECT description FROM _v_object_data WHERE {where}{suffix}",
                    r => Text(r, 0)).ConfigureAwait(false);
                var comment = rows.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                if (comment is not null) return comment;
            }
            catch (NetezzaException error) when (error.SqlState == "42P01")
            {
                return null;
            }
        }
        return null;
    }

    public async Task<string?> GetTableOwnerAsync(string table, string? schema = null)
    {
        (schema, table) = NormalizeObjectName(table, schema);
        var sql = $"SELECT owner FROM _v_table WHERE tablename = {SqlStringLiteral(table)}";
        if (schema is not null) sql += $" AND schema = {SqlStringLiteral(schema)}";
        try
        {
            var owners = await ExecuteQueryAsync(sql, r => Text(r, 0)).ConfigureAwait(false);
            return owners.FirstOrDefault();
        }
        catch (NetezzaException error) when (error.SqlState == "42P01")
        {
            return null;
        }
    }

    private sealed record KeyRow(
        string Name, string Type, string? Column, string? PkDatabase,
        string? PkSchema, string? PkRelation, string? PkColumn,
        string? UpdateType, string? DeleteType);

    private static string? Text(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);

    private static long? Number(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToInt64(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);

    private static bool Boolean(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return false;
        return Convert.ToString(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture)
            ?.Trim().ToLowerInvariant() is "true" or "t" or "1" or "yes" or "on";
    }

    private static (string? Schema, string Name) NormalizeObjectName(string name, string? schema)
    {
        var parts = new List<string>();
        var part = new System.Text.StringBuilder();
        var quoted = false;
        var parentheses = 0;
        for (var index = 0; index < name.Length; index++)
        {
            var ch = name[index];
            if (ch == '"')
            {
                if (quoted && index + 1 < name.Length && name[index + 1] == '"')
                {
                    part.Append("\"\"");
                    index++;
                }
                else
                {
                    quoted = !quoted;
                    part.Append(ch);
                }
            }
            else if (!quoted && ch == '(') { parentheses++; part.Append(ch); }
            else if (!quoted && ch == ')') { parentheses = Math.Max(0, parentheses - 1); part.Append(ch); }
            else if (!quoted && parentheses == 0 && ch == '.')
            {
                parts.Add(part.ToString());
                part.Clear();
            }
            else part.Append(ch);
        }
        parts.Add(part.ToString());
        if (quoted || parentheses != 0 || parts.Count is < 1 or > 3)
            throw new ArgumentException($"Invalid qualified object name: {name}", nameof(name));
        var normalizedName = NormalizeIdentifier(parts[^1]);
        var normalizedSchema = parts.Count >= 2 ? NormalizeIdentifier(parts[^2])
            : schema is null ? null : NormalizeIdentifier(schema);
        return (normalizedSchema, normalizedName);
    }

    private static string NormalizeIdentifier(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0) throw new ArgumentException("Empty SQL identifier", nameof(value));
        return trimmed.StartsWith('"') && trimmed.EndsWith('"') && trimmed.Length >= 2
            ? trimmed[1..^1].Replace("\"\"", "\"")
            : trimmed.ToUpperInvariant();
    }

    private static string QuoteIdentifier(string value)
    {
        const string reserved = "ABORT ALL ALLOCATE ANALYSE ANALYZE AND ANY AS ASC AUTOMAINT AWSS3 AZUREBLOB BETWEEN BINARY BIT BOTH CASE CAST CHAR CHARACTER CHECK CLUSTER COALESCE COLLATE COLLATION COLUMN CONSTRAINT COPY CROSS CURRENT CURRENT_CATALOG CURRENT_DATE CURRENT_DB CURRENT_SCHEMA CURRENT_SID CURRENT_TIME CURRENT_TIMESTAMP CURRENT_USER CURRENT_USERID CURRENT_USEROID DAYSPERROW DEALLOCATE DEC DECIMAL DECODE DEFAULT DEREGISTER DESC DISTINCT DISTRIBUTE DO ELSE END EXCEPT EXCLUDE EXISTS EXPLAIN EXPRESS EXTEND EXTERNAL EXTRACT FALSE FIRST FLOAT FOLLOWING FOR FOREIGN FROM FULL FUNCTION GENSTATS GLOBAL GROUP HAVING HISTOGRAM IDENTIFIER_CASE ILIKE IN INDEX INITIALLY INNER INOUT INTERSECT INTERVAL INTO JOURNAL LEADING LEFT LIKE LIMIT LOAD LOCAL LOCK MINUS MOVE NATURAL NCHAR NEW NOCASCADE NOT NOTNULL NULL NULLS NUMERIC NVL NVL2 OFFSET OFF OLD ON ONLINE ONLY OR ORDER OTHERS OUT OUTER OVER OVERLAPS PAUSESTEPS PAUSETIME PARTITION POSITION PRECEDING PRECISION PRESERVE PRIMARY REGISTER RESET REUSE RIGHT ROWS SELECT SESSION_USER SETOF SHOW SOME TABLE TEMPORAL THEN TIES TIME TIME_TRAVEL_ENABLE TIMESTAMP TO TRAILING TRANSACTION TRIGGER TRIM TRUE UNBOUNDED UNION UNIQUE USER USING VACUUM VARCHAR VERBOSE VERSION VIEW WHEN WHERE WITH WRITE CTID OID XMIN CMIN XMAX CMAX TABLEOID ROWID DATASLICEID CREATEXID DELETEXID";
        if (value.Length > 0 && value[0] is >= 'A' and <= 'Z'
            && value.All(ch => ch is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')
            && !reserved.Split(' ').Contains(value, StringComparer.Ordinal))
            return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static string Qualified(string database, string schema, string name) =>
        $"{QuoteIdentifier(database)}.{QuoteIdentifier(schema)}.{QuoteIdentifier(name)}";
}
