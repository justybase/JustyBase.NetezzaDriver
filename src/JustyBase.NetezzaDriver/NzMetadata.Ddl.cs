using System.Data.Common;

namespace JustyBase.NetezzaDriver;

public sealed partial class NzMetadata
{
    public async Task<string> GetTableDdlAsync(string table, string? schema = null, string? database = null)
    {
        (schema, table) = NormalizeObjectName(table, schema);
        var columns = await GetDetailedColumnsAsync(QuoteIdentifier(table),
            schema is null ? null : QuoteIdentifier(schema)).ConfigureAwait(false);
        if (columns.Count == 0) throw new ArgumentException($"Table {table} not found");
        schema ??= columns[0].Schema;
        database ??= await GetCurrentDatabaseAsync().ConfigureAwait(false) ?? "UNKNOWN";
        var distribution = await GetDistributionKeyAsync(table, schema).ConfigureAwait(false);
        var organization = await GetOrganizeColumnsAsync(QuoteIdentifier(table), QuoteIdentifier(schema)).ConfigureAwait(false);
        var keys = await GetTableKeysAsync(QuoteIdentifier(table), QuoteIdentifier(schema)).ConfigureAwait(false);
        var comment = await GetTableCommentAsync(QuoteIdentifier(table), QuoteIdentifier(schema)).ConfigureAwait(false);
        return BuildTableDdl(database, schema, table, columns, distribution, organization, keys, comment);
    }

    public async Task<string> GetViewDdlAsync(string view, string? schema = null, string? database = null)
    {
        (schema, view) = NormalizeObjectName(view, schema);
        var sql = $"SELECT schema, viewname, definition FROM _v_view WHERE viewname = {SqlStringLiteral(view)}";
        if (schema is not null) sql += $" AND schema = {SqlStringLiteral(schema)}";
        sql += " ORDER BY schema, viewname";
        var rows = await ExecuteQueryAsync(sql, r => new ViewDdlRow(Text(r, 0)!, Text(r, 1)!, Text(r, 2))).ConfigureAwait(false);
        RequireUniqueSchema(rows.Select(row => row.Schema), view);
        var selected = rows.FirstOrDefault() ?? throw new ArgumentException($"View {view} not found");
        if (string.IsNullOrWhiteSpace(selected.Definition))
            throw new ArgumentException($"View {selected.Schema}.{view} has no definition");
        database ??= await GetCurrentDatabaseAsync().ConfigureAwait(false) ?? "UNKNOWN";
        var body = selected.Definition.Trim().TrimEnd(';').TrimEnd();
        return $"CREATE OR REPLACE VIEW {Qualified(database, selected.Schema, view)} AS\n{body};";
    }

    public async Task<string> GetProcedureDdlAsync(string procedure, string? schema = null, string? database = null)
    {
        (schema, procedure) = NormalizeObjectName(procedure, schema);
        var column = procedure.Contains('(') ? "proceduresignature" : "procedure";
        var sql = "SELECT schema, procedure, proceduresignature, arguments, returns, "
            + $"executedasowner, description, proceduresource FROM _v_procedure WHERE {column} = {SqlStringLiteral(procedure)}";
        if (schema is not null) sql += $" AND schema = {SqlStringLiteral(schema)}";
        sql += " ORDER BY proceduresignature";
        var rows = await ExecuteQueryAsync(sql, r => new ProcedureDdlRow(
            Text(r, 0)!, Text(r, 1)!, Text(r, 2), Text(r, 3), Text(r, 4),
            r.IsDBNull(5) || Boolean(r, 5), Text(r, 6), Text(r, 7))).ConfigureAwait(false);
        RequireUniqueSchema(rows.Select(row => row.Schema), procedure);
        if (rows.Count > 1)
            throw new ArgumentException($"Procedure {procedure} has multiple overloads; pass its signature");
        var selected = rows.FirstOrDefault() ?? throw new ArgumentException($"Procedure {procedure} not found");
        database ??= await GetCurrentDatabaseAsync().ConfigureAwait(false) ?? "UNKNOWN";
        var arguments = selected.Arguments?.Trim() ?? "";
        var argumentsClause = arguments.Length == 0 ? "()"
            : arguments.StartsWith('(') && arguments.EndsWith(')') ? arguments : $"({arguments})";
        var name = Qualified(database, selected.Schema, selected.Name);
        var lines = new List<string>
        {
            $"CREATE OR REPLACE PROCEDURE {name}{argumentsClause}",
            $"RETURNS {FixProcedureReturnType(selected.Returns ?? "INTEGER")}",
            selected.ExecutedAsOwner ? "EXECUTE AS OWNER" : "EXECUTE AS CALLER",
            "LANGUAGE NZPLSQL AS", "BEGIN_PROC", selected.Source ?? "", "END_PROC;"
        };
        if (!string.IsNullOrEmpty(selected.Description))
            lines.Add($"COMMENT ON PROCEDURE {name} IS '{EscapeSqlString(selected.Description)}';");
        return string.Join("\n", lines);
    }

    public Task<IReadOnlyList<NzDdlBatchResult>> GetTablesDdlAsync(
        string? schema = null, string? pattern = null, IReadOnlyList<string>? tables = null) =>
        GetBatchDdlAsync(DdlKind.Table, schema, pattern, tables);

    public Task<IReadOnlyList<NzDdlBatchResult>> GetViewsDdlAsync(
        string? schema = null, string? pattern = null, IReadOnlyList<string>? views = null) =>
        GetBatchDdlAsync(DdlKind.View, schema, pattern, views);

    public Task<IReadOnlyList<NzDdlBatchResult>> GetProceduresDdlAsync(
        string? schema = null, string? pattern = null, IReadOnlyList<string>? procedures = null) =>
        GetBatchDdlAsync(DdlKind.Procedure, schema, pattern, procedures);

    private async Task<IReadOnlyList<NzDdlBatchResult>> GetBatchDdlAsync(
        DdlKind kind, string? schema, string? pattern, IReadOnlyList<string>? names)
    {
        List<(string? Schema, string Name)> targets;
        if (names is not null)
        {
            targets = names.Select(name => NormalizeObjectName(name, schema)).ToList();
        }
        else
        {
            var (catalog, column) = kind switch
            {
                DdlKind.Table => ("_v_table", "tablename"),
                DdlKind.View => ("_v_view", "viewname"),
                _ => ("_v_procedure", "procedure")
            };
            var selectedColumn = kind == DdlKind.Procedure ? "proceduresignature" : column;
            var sql = $"SELECT schema, {selectedColumn} FROM {catalog} WHERE {column} IS NOT NULL";
            if (schema is not null) sql += $" AND schema = {SqlStringLiteral(schema)}";
            if (pattern is not null) sql += $" AND {column} LIKE {SqlStringLiteral(pattern)}";
            sql += $" ORDER BY schema, {selectedColumn}";
            targets = (await ExecuteQueryAsync(sql, r => (Text(r, 0), Text(r, 1)!)).ConfigureAwait(false)).ToList();
        }
        var result = new List<NzDdlBatchResult>(targets.Count);
        foreach (var (targetSchema, name) in targets)
        {
            try
            {
                var quotedName = QuoteIdentifier(name);
                var quotedSchema = targetSchema is null ? null : QuoteIdentifier(targetSchema);
                var ddl = kind switch
                {
                    DdlKind.Table => await GetTableDdlAsync(quotedName, quotedSchema).ConfigureAwait(false),
                    DdlKind.View => await GetViewDdlAsync(quotedName, quotedSchema).ConfigureAwait(false),
                    _ => await GetProcedureDdlAsync(quotedName, quotedSchema).ConfigureAwait(false)
                };
                result.Add(new NzDdlBatchResult(targetSchema ?? "UNKNOWN", name, ddl, null));
            }
            catch (Exception error)
            {
                result.Add(new NzDdlBatchResult(targetSchema ?? "UNKNOWN", name, "", error.Message));
            }
        }
        return result.AsReadOnly();
    }

    private enum DdlKind { Table, View, Procedure }
    private sealed record ViewDdlRow(string Schema, string Name, string? Definition);
    private sealed record ProcedureDdlRow(
        string Schema, string Name, string? Signature, string? Arguments,
        string? Returns, bool ExecutedAsOwner, string? Description, string? Source);

    private static void RequireUniqueSchema(IEnumerable<string> schemas, string name)
    {
        if (schemas.Distinct(StringComparer.Ordinal).Skip(1).Any())
            throw new ArgumentException($"{name} exists in several schemas; pass schema explicitly");
    }

    private static string EscapeSqlString(string value) => value.Replace("'", "''");

    private static string FixProcedureReturnType(string value) =>
        value.Trim().ToUpperInvariant() switch
        {
            "CHARACTER VARYING" or "NATIONAL CHARACTER VARYING"
                or "NATIONAL CHARACTER" or "CHARACTER" => value.Trim().ToUpperInvariant() + "(ANY)",
            _ => value
        };

    private static string BuildTableDdl(
        string database, string schema, string table,
        IReadOnlyList<NzDetailedColumnInfo> columns, IReadOnlyList<string> distribution,
        IReadOnlyList<string> organization, IReadOnlyList<NzTableKeyInfo> keys, string? comment)
    {
        var name = Qualified(database, schema, table);
        var columnLines = columns.Select(column =>
        {
            var line = $"    {QuoteIdentifier(column.Name)} {column.TypeName}";
            if (column.NotNull) line += " NOT NULL";
            if (column.DefaultValue is not null) line += $" DEFAULT {column.DefaultValue}";
            return line;
        });
        var lines = new List<string> { $"CREATE TABLE {name}", "(", string.Join(",\n", columnLines) };
        lines.Add(distribution.Count > 0
            ? $")\nDISTRIBUTE ON ({string.Join(", ", distribution.Select(QuoteIdentifier))})"
            : ")\nDISTRIBUTE ON RANDOM");
        if (organization.Count > 0)
            lines.Add($"ORGANIZE ON ({string.Join(", ", organization.Select(QuoteIdentifier))})");
        lines.Add(";");
        lines.Add("");
        foreach (var key in keys)
        {
            var keyName = QuoteIdentifier(key.Name);
            var keyColumns = string.Join(", ", key.Columns.Select(QuoteIdentifier));
            if (key.TypeChar is 'p' or 'u')
                lines.Add($"ALTER TABLE {name} ADD CONSTRAINT {keyName} {key.KeyType} ({keyColumns});");
            else if (key.TypeChar == 'f' && key.PkColumns.Count > 0
                && key.PkDatabase is not null && key.PkSchema is not null && key.PkRelation is not null)
            {
                var reference = Qualified(key.PkDatabase, key.PkSchema, key.PkRelation);
                var pkColumns = string.Join(", ", key.PkColumns.Select(QuoteIdentifier));
                lines.Add($"ALTER TABLE {name} ADD CONSTRAINT {keyName} {key.KeyType} ({keyColumns}) REFERENCES {reference} ({pkColumns}) ON DELETE {key.DeleteType} ON UPDATE {key.UpdateType};");
            }
        }
        if (!string.IsNullOrEmpty(comment))
        {
            lines.Add("");
            lines.Add($"COMMENT ON TABLE {name} IS '{EscapeSqlString(comment)}';");
        }
        foreach (var column in columns)
        {
            if (!string.IsNullOrEmpty(column.Description))
                lines.Add($"COMMENT ON COLUMN {name}.{QuoteIdentifier(column.Name)} IS '{EscapeSqlString(column.Description)}';");
        }
        return string.Join("\n", lines);
    }

    public async Task<string> GetExternalTableDdlAsync(
        string table, string? schema = null, string? database = null)
    {
        (schema, table) = NormalizeObjectName(table, schema);
        var fields = string.Join(", ", ExternalOptions.Select(option => $"E.{option.Column}"));
        var sql = "SELECT E.SCHEMA, E.TABLENAME, X.EXTOBJNAME, " + fields
            + " FROM _v_external E JOIN _v_extobject X ON E.RELID = X.OBJID"
            + $" WHERE E.TABLENAME = {SqlStringLiteral(table)}";
        if (schema is not null) sql += $" AND E.SCHEMA = {SqlStringLiteral(schema)}";
        var rows = await ExecuteQueryAsync(sql, r => new ExternalDdlRow(
            Text(r, 0)!, Text(r, 1)!, Text(r, 2),
            Enumerable.Range(0, ExternalOptions.Length)
                .Select(index => r.IsDBNull(index + 3) ? null : r.GetValue(index + 3)).ToArray())).ConfigureAwait(false);
        RequireUniqueSchema(rows.Select(row => row.Schema), table);
        var selected = rows.FirstOrDefault() ?? throw new ArgumentException($"External table {table} not found");
        database ??= await GetCurrentDatabaseAsync().ConfigureAwait(false) ?? "UNKNOWN";
        var columnSql = "SELECT C.ATTNAME, C.FORMAT_TYPE, C.ATTNOTNULL"
            + " FROM _v_relation_column C JOIN _v_external E ON C.OBJID = E.RELID"
            + $" WHERE E.SCHEMA = {SqlStringLiteral(selected.Schema)}"
            + $" AND E.TABLENAME = {SqlStringLiteral(table)} ORDER BY C.ATTNUM";
        var columns = await ExecuteQueryAsync(columnSql, r => new ExternalColumn(
            Text(r, 0)!, Text(r, 1)!, Boolean(r, 2))).ConfigureAwait(false);
        if (columns.Count == 0) throw new ArgumentException($"External table {selected.Schema}.{table} has no columns");
        var lines = new List<string>
        {
            $"CREATE EXTERNAL TABLE {Qualified(database, selected.Schema, table)}",
            "(",
            string.Join(",\n", columns.Select(column =>
                $"    {QuoteIdentifier(column.Name)} {column.TypeName}{(column.NotNull ? " NOT NULL" : "")}")),
            ")", "USING", "("
        };
        if (selected.DataObject is not null)
            lines.Add($"    DATAOBJECT('{EscapeSqlString(selected.DataObject)}')");
        for (var index = 0; index < ExternalOptions.Length; index++)
        {
            var value = selected.Options[index];
            if (value is null) continue;
            var option = ExternalOptions[index];
            var rendered = option.Kind switch
            {
                ExternalKind.String => $"'{EscapeSqlString(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "")}'",
                ExternalKind.Boolean => BooleanValue(value) ? "true" : "false",
                _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? ""
            };
            lines.Add($"    {option.Keyword} {rendered}");
        }
        lines.Add(");");
        return string.Join("\n", lines);
    }

    public async Task<string> GetSynonymDdlAsync(
        string synonym, string? schema = null, string? database = null)
    {
        (schema, synonym) = NormalizeObjectName(synonym, schema);
        var sql = "SELECT schema, owner, synonym_name, refobjname, description, refdatabase, refschema"
            + $" FROM _v_synonym WHERE synonym_name = {SqlStringLiteral(synonym)}";
        if (schema is not null) sql += $" AND schema = {SqlStringLiteral(schema)}";
        var rows = await ExecuteQueryAsync(sql, r => new SynonymDdlRow(
            Text(r, 0)!, Text(r, 1), Text(r, 2)!, Text(r, 3)!,
            Text(r, 4), Text(r, 5), Text(r, 6))).ConfigureAwait(false);
        RequireUniqueSchema(rows.Select(row => row.Schema), synonym);
        var selected = rows.FirstOrDefault() ?? throw new ArgumentException($"Synonym {synonym} not found");
        database ??= await GetCurrentDatabaseAsync().ConfigureAwait(false) ?? "UNKNOWN";
        var target = selected.Reference.Contains('.')
            ? string.Join(".", selected.Reference.Split('.').Select(QuoteIdentifier))
            : selected.RefDatabase is not null && selected.RefSchema is not null
                ? Qualified(selected.RefDatabase, selected.RefSchema, selected.Reference)
                : QuoteIdentifier(selected.Reference);
        var ddl = $"CREATE SYNONYM {Qualified(database, selected.Schema, synonym)} FOR {target};";
        if (selected.Description is not null)
            ddl += $"\nCOMMENT ON SYNONYM {QuoteIdentifier(synonym)} IS '{EscapeSqlString(selected.Description)}';";
        return ddl;
    }

    private static bool BooleanValue(object value) =>
        Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
            ?.Trim().ToLowerInvariant() is "true" or "t" or "1" or "yes" or "on";

    private sealed record ExternalDdlRow(string Schema, string Name, string? DataObject, object?[] Options);
    private sealed record ExternalColumn(string Name, string TypeName, bool NotNull);
    private sealed record SynonymDdlRow(
        string Schema, string? Owner, string Name, string Reference,
        string? Description, string? RefDatabase, string? RefSchema);
    private enum ExternalKind { String, Number, Boolean }
    private sealed record ExternalOption(string Keyword, string Column, ExternalKind Kind);
    private static readonly ExternalOption[] ExternalOptions =
    [
        new("DELIMITER", "DELIM", ExternalKind.String),
        new("ENCODING", "ENCODING", ExternalKind.String),
        new("TIMESTYLE", "TIMESTYLE", ExternalKind.String),
        new("REMOTESOURCE", "REMOTESOURCE", ExternalKind.String),
        new("SKIPROWS", "SKIPROWS", ExternalKind.Number),
        new("MAXERRORS", "MAXERRORS", ExternalKind.Number),
        new("ESCAPECHAR", "ESCAPE", ExternalKind.String),
        new("DECIMALDELIM", "DECIMALDELIM", ExternalKind.String),
        new("LOGDIR", "LOGDIR", ExternalKind.String),
        new("QUOTEDVALUE", "QUOTEDVALUE", ExternalKind.String),
        new("NULLVALUE", "NULLVALUE", ExternalKind.String),
        new("CRINSTRING", "CRINSTRING", ExternalKind.Boolean),
        new("TRUNCSTRING", "TRUNCSTRING", ExternalKind.Boolean),
        new("CTRLCHARS", "CTRLCHARS", ExternalKind.Boolean),
        new("IGNOREZERO", "IGNOREZERO", ExternalKind.Boolean),
        new("TIMEEXTRAZEROS", "TIMEEXTRAZEROS", ExternalKind.Boolean),
        new("Y2BASE", "Y2BASE", ExternalKind.Number),
        new("FILLRECORD", "FILLRECORD", ExternalKind.Boolean),
        new("COMPRESS", "COMPRESS", ExternalKind.Boolean),
        new("INCLUDEHEADER", "INCLUDEHEADER", ExternalKind.Boolean),
        new("LFINSTRING", "LFINSTRING", ExternalKind.Boolean),
        new("DATESTYLE", "DATESTYLE", ExternalKind.String),
        new("DATEDELIM", "DATEDELIM", ExternalKind.String),
        new("TIMEDELIM", "TIMEDELIM", ExternalKind.String),
        new("BOOLSTYLE", "BOOLSTYLE", ExternalKind.String),
        new("FORMAT", "FORMAT", ExternalKind.String),
        new("SOCKETBUFSIZE", "SOCKETBUFSIZE", ExternalKind.Number),
        new("RECORDDELIM", "RECORDDELIM", ExternalKind.String),
        new("MAXROWS", "MAXROWS", ExternalKind.Number),
        new("REQUIREQUOTES", "REQUIREQUOTES", ExternalKind.Boolean),
        new("RECORDLENGTH", "RECORDLENGTH", ExternalKind.Number),
        new("DATETIMEDELIM", "DATETIMEDELIM", ExternalKind.String),
        new("REJECTFILE", "REJECTFILE", ExternalKind.String)
    ];
}
