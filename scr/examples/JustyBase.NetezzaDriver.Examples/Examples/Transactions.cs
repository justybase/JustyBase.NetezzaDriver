namespace JustyBase.NetezzaDriver.Examples.Examples;

public static class Transactions
{
    public static async Task RunAsync()
    {
        await using var conn = await ConnectionHelper.OpenAsync();

        // Create the table first (outside any transaction, with AutoCommit on)
        await using (var cmd = conn.CreateCommand(
            "CREATE TEMP TABLE txn_demo (id INTEGER, val VARCHAR(20))"))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        // ── Rollback demo ──
        Console.WriteLine("Rollback demo:");
        conn.AutoCommit = false;

        await using (var insert = conn.CreateCommand(
            "INSERT INTO txn_demo VALUES (1, 'will-be-rolled-back')"))
        {
            await insert.ExecuteNonQueryAsync();
        }

        conn.Rollback();
        Console.WriteLine("  Rolled back.");

        // Verify rollback
        conn.AutoCommit = true;
        await using (var check = conn.CreateCommand("SELECT count(*) FROM txn_demo"))
        {
            var count = await check.ExecuteScalarAsync();
            Console.WriteLine($"  Rows after rollback: {count}");
        }

        // ── Commit demo ──
        Console.WriteLine("Commit demo:");
        conn.AutoCommit = false;
        await using (var insert = conn.CreateCommand(
            "INSERT INTO txn_demo VALUES (1, 'committed')"))
        {
            await insert.ExecuteNonQueryAsync();
        }

        await using (var insert2 = conn.CreateCommand(
            "INSERT INTO txn_demo VALUES (2, 'also-committed')"))
        {
            await insert2.ExecuteNonQueryAsync();
        }

        conn.Commit();
        Console.WriteLine("  Committed.");

        conn.AutoCommit = true;
        await using (var check = conn.CreateCommand("SELECT val FROM txn_demo ORDER BY id"))
        {
            await using var reader = await check.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                Console.WriteLine($"  val={reader.GetString(0)}");
        }

        Console.WriteLine("Transactions completed.");
    }
}
