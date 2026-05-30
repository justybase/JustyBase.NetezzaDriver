namespace JustyBase.NetezzaDriver.Examples.Examples;

public static class MetadataIntrospection
{
    public static async Task RunAsync()
    {
        await using var conn = await ConnectionHelper.OpenAsync();
        var meta = conn.Meta;

        // ── Schemas ──
        var schemas = await meta.GetSchemasAsync();
        Console.WriteLine($"Schemas ({schemas.Count}): {string.Join(", ", schemas)}");

        // ── Tables in ADMIN schema ──
        var tables = await meta.GetTablesAsync("ADMIN");
        Console.WriteLine($"\nTables in ADMIN ({tables.Count} total) — first 10:");
        foreach (var t in tables.Take(10))
            Console.WriteLine($"  {t.Schema}.{t.TableName,-30} rows: {t.RowCount}");

        // ── Columns ──
        if (tables.Count > 0)
        {
            var firstTable = tables[0];
            Console.WriteLine($"\nColumns of {firstTable.Schema}.{firstTable.TableName}:");
            var columns = await meta.GetColumnsAsync(firstTable.TableName, firstTable.Schema);
            foreach (var c in columns)
            {
                Console.WriteLine($"  {c.ColumnName,-25} {c.DataType,-20} nullable={c.IsNullable}");
            }
        }

        // ── Views ──
        var views = await meta.GetViewsAsync();
        Console.WriteLine($"\nViews: {views.Count} total. First 5:");
        foreach (var v in views.Take(5))
            Console.WriteLine($"  {v.Schema}.{v.ViewName}");

        // ── Distribution key ──
        if (tables.Count > 0)
        {
            var tgt = tables[0];
            var distKey = await meta.GetDistributionKeyAsync(tgt.TableName, tgt.Schema);
            Console.WriteLine($"\nDistribution key for {tgt.Schema}.{tgt.TableName}: " +
                (distKey.Count == 0 ? "RANDOM" : string.Join(", ", distKey)));
        }

        // ── Table sizes ──
        var sizes = await meta.GetTableSizesAsync();
        Console.WriteLine($"\nTable sizes ({sizes.Count} total). First 5 by size:");
        foreach (var s in sizes.OrderByDescending(s => s.SizeMb ?? 0).Take(5))
            Console.WriteLine($"  {s.Schema}.{s.TableName,-30} {s.SizeMb,8:N0} MB  (skew: {s.Skew?.ToString("F2") ?? "N/A"})");

        // ── Search ──
        var search = await meta.SearchObjectsAsync("%SALES%");
        Console.WriteLine($"\nObjects matching '%SALES%': {search.Count}");
        foreach (var obj in search.Take(10))
            Console.WriteLine($"  {obj.Type,-10} {obj.Schema}.{obj.Name}");

        Console.WriteLine("MetadataIntrospection completed.");
    }
}
