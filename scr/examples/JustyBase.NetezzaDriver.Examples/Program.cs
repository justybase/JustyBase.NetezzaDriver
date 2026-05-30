using System.Diagnostics;
using JustyBase.NetezzaDriver.Examples.Examples;

// Example runner — each example reads connection settings from environment variables:
//   NZ_DEV_HOST, NZ_DEV_PORT (default 5480), NZ_DEV_DB, NZ_DEV_USER, NZ_DEV_PASSWORD

var examples = new Dictionary<string, (string Description, Func<Task> Run)>
{
    ["1"] = ("Basic Query (sync + async)",          BasicQuery.RunAsync),
    ["2"] = ("Parameterized Queries",                ParameterizedQueries.RunAsync),
    ["3"] = ("Transactions (commit / rollback)",     Transactions.RunAsync),
    ["4"] = ("Connection Pooling",                   ConnectionPooling.RunAsync),
    ["5"] = ("Metadata / Catalog Introspection",     MetadataIntrospection.RunAsync),
    ["6"] = ("Async Operations + Cancellation",      AsyncOperations.RunAsync),
    ["7"] = ("Timeout and Query Cancel",             TimeoutAndCancel.RunAsync),
    ["8"] = ("Error Handling & Exception Types",     ErrorHandling.RunAsync),
};

Console.WriteLine("JustyBase.NetezzaDriver — Examples");
Console.WriteLine(new string('=', 50));
Console.WriteLine("Connection read from NZ_DEV_HOST, NZ_DEV_PORT, NZ_DEV_DB,");
Console.WriteLine("NZ_DEV_USER, NZ_DEV_PASSWORD environment variables.\n");

while (true)
{
    Console.WriteLine("\nAvailable examples:");
    foreach (var kv in examples)
        Console.WriteLine($"  [{kv.Key}] {kv.Value.Description}");
    Console.WriteLine("  [a]  Run ALL examples");
    Console.WriteLine("  [q]  Quit");
    Console.Write("\nSelect: ");
    var input = Console.ReadLine()?.Trim().ToLowerInvariant();

    if (input == "q" || input == "quit") break;

    if (input == "a")
    {
        foreach (var kv in examples)
        {
            Console.WriteLine($"\n--- Running: {kv.Value.Description} ---");
            try { await kv.Value.Run(); }
            catch (Exception ex) { Console.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message}"); }
        }
        continue;
    }

    if (examples.TryGetValue(input ?? "", out var selected))
    {
        Console.WriteLine($"\n--- {selected.Description} ---");
        var sw = Stopwatch.StartNew();
        try { await selected.Run(); }
        catch (Exception ex) { Console.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message}"); }
        Console.WriteLine($"Done in {sw.Elapsed.TotalSeconds:F2}s");
    }
}
