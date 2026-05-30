// Example: Using parameterized queries with NzCommand
// Supports named (:param, @param) and positional (?) parameters

using JustyBase.NetezzaDriver;

// Named parameters
await using var conn = new NzConnection("user", "password", "host", "database");
await conn.OpenAsync();

await using var cmd = conn.CreateCommand("SELECT id, name FROM users WHERE age > :minAge AND city = :city");
cmd.Parameters.AddWithValue(":minAge", 18);
cmd.Parameters.AddWithValue(":city", "New York");

await using var reader = await cmd.ExecuteReaderAsync();
while (await reader.ReadAsync())
{
    Console.WriteLine($"{reader.GetInt32(0)}: {reader.GetString(1)}");
}

// Positional parameters (?)
await using var cmd2 = conn.CreateCommand("SELECT count(*) FROM users WHERE status = ?");
var p = new NzParameter { Value = "active" };
p.IsPositional = true;
cmd2.Parameters.Add(p);
var count = await cmd2.ExecuteScalarAsync();
Console.WriteLine($"Active users: {count}");
