namespace JustyBase.NetezzaDriver.Examples.Examples;

public static class ParameterizedQueries
{
    public static async Task RunAsync()
    {
        await using var conn = await ConnectionHelper.OpenAsync();

        // Create a temporary table for the demo
        await using (var setup = conn.CreateCommand(
            "CREATE TEMP TABLE demo_params (id INTEGER, name VARCHAR(50), age INTEGER, salary NUMERIC(10,2))"))
        {
            await setup.ExecuteNonQueryAsync();
        }

        await using (var insert1 = conn.CreateCommand("INSERT INTO demo_params VALUES (1, 'Alice', 30, 75000.00)"))
            await insert1.ExecuteNonQueryAsync();
        await using (var insert2 = conn.CreateCommand("INSERT INTO demo_params VALUES (2, 'Bob', 25, 65000.50)"))
            await insert2.ExecuteNonQueryAsync();
        await using (var insert3 = conn.CreateCommand("INSERT INTO demo_params VALUES (3, 'Charlie', 35, 85000.00)"))
            await insert3.ExecuteNonQueryAsync();

        // ── Named parameters ──
        Console.WriteLine("Named parameters:");
        await using (var cmd = conn.CreateCommand(
            "SELECT id, name FROM demo_params WHERE age > :minAge AND salary > :minSalary ORDER BY id"))
        {
            cmd.Parameters.AddWithValue(":minAge", 28);
            cmd.Parameters.AddWithValue(":minSalary", 70000m);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                Console.WriteLine($"  id={reader.GetInt32(0)}, name={reader.GetString(1)}");
            }
        }

        // ── @-prefix named parameters ──
        Console.WriteLine("@-prefix parameters:");
        await using (var cmd = conn.CreateCommand(
            "SELECT name FROM demo_params WHERE id = @id"))
        {
            cmd.Parameters.AddWithValue("@id", 1);
            var name = await cmd.ExecuteScalarAsync();
            Console.WriteLine($"  id=1 => name={name}");
        }

        // ── Positional parameters ──
        Console.WriteLine("Positional parameters:");
        await using (var cmd = conn.CreateCommand(
            "SELECT name, age FROM demo_params WHERE age > ? AND salary > ?"))
        {
            cmd.Parameters.Add(new NzParameter { Value = 20, IsPositional = true });
            cmd.Parameters.Add(new NzParameter { Value = 60000m, IsPositional = true });

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                Console.WriteLine($"  name={reader.GetString(0)}, age={reader.GetInt32(1)}");
            }
        }

        // ── NULL parameter ──
        Console.WriteLine("NULL parameter:");
        await using (var cmd = conn.CreateCommand(
            "INSERT INTO demo_params (id, name) VALUES (4, :name)"))
        {
            cmd.Parameters.AddWithValue(":name", DBNull.Value);
            var rows = await cmd.ExecuteNonQueryAsync();
            Console.WriteLine($"  Inserted {rows} row with NULL name");
        }

        // Cleanup
        await using (var cleanup = conn.CreateCommand("DELETE FROM demo_params WHERE id = 4"))
            await cleanup.ExecuteNonQueryAsync();

        Console.WriteLine("ParameterizedQueries completed.");
    }
}
