using System.Data;
using System.Data.Common;

namespace JustyBase.NetezzaDriver;

public sealed class NzMetadata
{
    private readonly NzConnection _connection;

    internal NzMetadata(NzConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    public async Task<IReadOnlyList<string>> GetSchemasAsync()
    {
        var result = new List<string>();
        await using var cmd = _connection.CreateCommand("SELECT schema FROM _v_schema ORDER BY schema");
        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
            result.Add(reader.GetString(0));
        return result.AsReadOnly();
    }

    public async Task<IReadOnlyList<NzDatabaseInfo>> GetDatabasesAsync()
    {
        const string sql = "SELECT database, owner, defschema FROM _v_database ORDER BY database";
        return await ExecuteQueryAsync(sql, reader => new NzDatabaseInfo(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2)
        )).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<NzTableInfo>> GetTablesAsync(string? schema = null, string? pattern = null)
    {
        var sql = "SELECT schema, tablename, owner, objtype, objid, reltuples FROM _v_table WHERE tablename IS NOT NULL";
        if (schema is not null)
            sql += $" AND schema = {SqlStringLiteral(schema)}";
        if (pattern is not null)
            sql += $" AND tablename LIKE {SqlStringLiteral(pattern)}";
        sql += " AND schema NOT IN ('DEFINITION_SCHEMA', 'INZA', 'NZ_QUERY_HISTORY')";
        sql += " AND objtype <> 'SYSTEM_TABLE'";
        sql += " ORDER BY schema, tablename";

        return await ExecuteQueryAsync(sql, reader => new NzTableInfo(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : Convert.ToInt64(reader.GetValue(4)),
            reader.IsDBNull(5) ? null : Convert.ToInt64(reader.GetValue(5))
        )).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<NzColumnInfo>> GetColumnsAsync(string table, string? schema = null)
    {
        var sql = $"SELECT attname, attnum, format_type, CASE WHEN attnotnull THEN 'N' ELSE 'Y' END, objid, description FROM _v_relation_column WHERE name = {SqlStringLiteral(table)}";
        if (schema is not null)
            sql += $" AND schema = {SqlStringLiteral(schema)}";
        sql += " ORDER BY attnum";

        return await ExecuteQueryAsync(sql, reader => new NzColumnInfo(
            reader.GetString(0),
            (int)reader.GetInt16(1),
            reader.GetString(2),
            reader.GetString(3) == "Y",
            reader.IsDBNull(4) ? null : Convert.ToInt64(reader.GetValue(4)),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            null /* colddefault — column may not exist on some Netezza versions */
        )).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<NzViewInfo>> GetViewsAsync(string? schema = null)
    {
        var sql = "SELECT schema, viewname, owner, objid, definition FROM _v_view WHERE viewname IS NOT NULL";
        if (schema is not null)
            sql += $" AND schema = {SqlStringLiteral(schema)}";
        sql += " ORDER BY schema, viewname";

        return await ExecuteQueryAsync(sql, reader => new NzViewInfo(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : Convert.ToInt64(reader.GetValue(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4)
        )).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<NzProcedureInfo>> GetProceduresAsync(string? schema = null)
    {
        var sql = "SELECT schema, procedure, owner, objid, proceduresignature, returns, builtin, proceduresource, executedasowner, arguments, description FROM _v_procedure WHERE procedure IS NOT NULL";
        if (schema is not null)
            sql += $" AND schema = {SqlStringLiteral(schema)}";
        sql += " ORDER BY schema, procedure";

        return await ExecuteQueryAsync(sql, reader => new NzProcedureInfo(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : Convert.ToInt64(reader.GetValue(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6) == "t",
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : Convert.ToBoolean(reader.GetValue(8)),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10)
        )).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> GetDistributionKeyAsync(string table, string? schema = null)
    {
        var sql = $"SELECT attname FROM _v_table_dist_map WHERE tablename = {SqlStringLiteral(table)}";
        if (schema is not null)
            sql += $" AND schema = {SqlStringLiteral(schema)}";
        sql += " ORDER BY distattnum";

        var result = new List<string>();
        await using var cmd = _connection.CreateCommand(sql);
        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
            result.Add(reader.GetString(0));
        return result.AsReadOnly();
    }

    public async Task<IReadOnlyList<NzTableSizeInfo>> GetTableSizesAsync(string? schema = null)
    {
        var sql = "SELECT schema, tablename, used_bytes, allocated_bytes, (used_bytes / 1048576)::BIGINT AS size_mb, skew FROM _v_table_storage_stat WHERE tablename IS NOT NULL";
        if (schema is not null)
            sql += $" AND schema = {SqlStringLiteral(schema)}";
        sql += " ORDER BY used_bytes DESC";

        return await ExecuteQueryAsync(sql, reader => new NzTableSizeInfo(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : Convert.ToInt64(reader.GetValue(2)),
            reader.IsDBNull(3) ? null : Convert.ToInt64(reader.GetValue(3)),
            reader.IsDBNull(4) ? null : Convert.ToInt64(reader.GetValue(4)),
            reader.IsDBNull(5) ? null : Convert.ToDouble(reader.GetValue(5))
        )).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<NzSessionInfo>> GetSessionsAsync()
    {
        const string sql = "SELECT id, username, dbname, conntime, priority, status, type, client_os_username FROM _v_session ORDER BY conntime DESC";

        return await ExecuteQueryAsync(sql, reader => new NzSessionInfo(
            Convert.ToInt64(reader.GetValue(0)),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetDateTime(3),
            reader.IsDBNull(4) ? null : reader.GetValue(4).ToString(),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7)
        )).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<NzObjectInfo>> SearchObjectsAsync(string pattern, string? schema = null)
    {
        var results = new List<NzObjectInfo>();
        var tables = await GetTablesAsync(schema, $"%{pattern}%").ConfigureAwait(false);
        foreach (var t in tables)
            results.Add(new NzObjectInfo(t.Schema, t.TableName, "TABLE", t.Owner, t.ObjId));

        var views = await GetViewsAsync(schema).ConfigureAwait(false);
        foreach (var v in views)
        {
            if (schema is null || v.Schema == schema)
                if (v.ViewName.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    results.Add(new NzObjectInfo(v.Schema, v.ViewName, "VIEW", v.Owner, v.ObjId));
        }

        var procs = await GetProceduresAsync(schema).ConfigureAwait(false);
        foreach (var p in procs)
        {
            if (schema is null || p.Schema == schema)
                if (p.ProcName.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    results.Add(new NzObjectInfo(p.Schema, p.ProcName, "PROCEDURE", p.Owner, p.ObjId));
        }

        return results.AsReadOnly();
    }

    public async Task<IReadOnlyList<NzFunctionInfo>> GetFunctionsAsync(string? schema = null)
    {
        var sql = "SELECT schema, function, owner, objid, functionsignature, returns, env FROM _v_function WHERE function IS NOT NULL";
        if (schema is not null)
            sql += $" AND schema = {SqlStringLiteral(schema)}";
        sql += " ORDER BY schema, function";

        return await ExecuteQueryAsync(sql, reader =>
        {
            var env = reader.IsDBNull(6) ? null : reader.GetString(6);
            return new NzFunctionInfo(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : Convert.ToInt64(reader.GetValue(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                env,
                env is not null && env.Contains("com.ibm.nz.fq.SqlReadLauncher", StringComparison.OrdinalIgnoreCase)
            );
        }).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<NzSynonymInfo>> GetSynonymsAsync(string? schema = null)
    {
        var sql = "SELECT schema, synonym_name, refobjname, refdatabase, refschema, description FROM _v_synonym WHERE synonym_name IS NOT NULL";
        if (schema is not null)
            sql += $" AND schema = {SqlStringLiteral(schema)}";
        sql += " ORDER BY schema, synonym_name";

        return await ExecuteQueryAsync(sql, reader => new NzSynonymInfo(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5)
        )).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<NzConstraintInfo>> GetConstraintsAsync(string? schema = null)
    {
        var sql = "SELECT schema, relation, constraintname, contype, attname, pkdatabase, pkschema, pkrelation, pkattname, updt_type, del_type FROM _v_relation_keydata WHERE relation IS NOT NULL";
        if (schema is not null)
            sql += $" AND schema = {SqlStringLiteral(schema)}";
        sql += " ORDER BY schema, relation, conseq";

        return await ExecuteQueryAsync(sql, reader => new NzConstraintInfo(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3)[0],
            reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10)
        )).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<NzDistKeyInfo>> GetAllDistributionKeysAsync(string? schema = null)
    {
        var sql = "SELECT schema, tablename, attname, distattnum FROM _v_table_dist_map WHERE tablename IS NOT NULL";
        if (schema is not null)
            sql += $" AND schema = {SqlStringLiteral(schema)}";
        sql += " ORDER BY schema, tablename, distseqno";

        return await ExecuteQueryAsync(sql, reader => new NzDistKeyInfo(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3)
        )).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<NzOrganizeKeyInfo>> GetOrganizeKeysAsync(string? schema = null)
    {
        var sql = "SELECT schema, tablename, attname, attnum FROM _v_table_organize_column WHERE tablename IS NOT NULL";
        if (schema is not null)
            sql += $" AND schema = {SqlStringLiteral(schema)}";
        sql += " ORDER BY schema, tablename, orgseqno";

        return await ExecuteQueryAsync(sql, reader => new NzOrganizeKeyInfo(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3)
        )).ConfigureAwait(false);
    }

    /// <summary>Returns all non-system object types (tables, views, external tables, sequences, synonyms, functions, etc.) with descriptions and creation dates.</summary>
    public async Task<IReadOnlyList<NzObjectDetailInfo>> GetObjectDetailsAsync(string? schema = null)
    {
        var sql = "SELECT schema, objname, objtype, owner, objid, description, createdate FROM _v_object_data WHERE objname IS NOT NULL";
        if (schema is not null)
            sql += $" AND schema = {SqlStringLiteral(schema)}";
        sql += " AND objtype NOT IN ('AGGREGATE','CONSTRAINT','DATABASE','DATATYPE','GROUP','MANAGEMENT INDEX','MANAGEMENT SEQ','MANAGEMENT TABLE','MANAGEMENT VIEW','SCHEDULER RULE','SCHEMA','SYSTEM INDEX','SYSTEM SEQ','SYSTEM TABLE','SYSTEM VIEW','USER')";
        sql += " ORDER BY schema, objtype, objname";

        return await ExecuteQueryAsync(sql, reader => new NzObjectDetailInfo(
            reader.IsDBNull(0) ? "ADMIN" : reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : Convert.ToInt64(reader.GetValue(4)),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetDateTime(6)
        )).ConfigureAwait(false);
    }

    /// <summary>Enhanced cross-object search that includes all object types, descriptions, and creation dates.</summary>
    public async Task<IReadOnlyList<NzObjectDetailInfo>> SearchObjectsDetailedAsync(string pattern, string? schema = null)
    {
        var sql = $"SELECT schema, objname, objtype, owner, objid, description, createdate FROM _v_object_data WHERE UPPER(objname) LIKE UPPER({SqlLikeContainsLiteral(pattern)})";
        if (schema is not null)
            sql += $" AND schema = {SqlStringLiteral(schema)}";
        sql += " AND objtype NOT IN ('AGGREGATE','CONSTRAINT','DATABASE','DATATYPE','GROUP','MANAGEMENT INDEX','MANAGEMENT SEQ','MANAGEMENT TABLE','MANAGEMENT VIEW','SCHEDULER RULE','SCHEMA','SYSTEM INDEX','SYSTEM SEQ','SYSTEM TABLE','SYSTEM VIEW','USER')";
        sql += " ORDER BY schema, objtype, objname";

        return await ExecuteQueryAsync(sql, reader => new NzObjectDetailInfo(
            reader.IsDBNull(0) ? "ADMIN" : reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : Convert.ToInt64(reader.GetValue(4)),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetDateTime(6)
        )).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<T>> ExecuteQueryAsync<T>(string sql, Func<DbDataReader, T> mapper)
    {
        var result = new List<T>();
        await using var cmd = _connection.CreateCommand(sql);
        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
            result.Add(mapper(reader));
        return result.AsReadOnly();
    }

    private static string SqlStringLiteral(string value) => NzParameter.ValueToSqlLiteral(value);

    private static string SqlLikeContainsLiteral(string value)
    {
        var escaped = value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        return NzParameter.ValueToSqlLiteral($"%{escaped}%") + " ESCAPE '\\'";
    }
}
