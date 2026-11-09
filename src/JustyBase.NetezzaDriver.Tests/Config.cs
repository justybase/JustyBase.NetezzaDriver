using System;

namespace JustyBase.NetezzaDriver.Tests;

public static class Config
{
    private static bool AllowLabDefaults =>
        string.Equals(Environment.GetEnvironmentVariable("NZ_DEV_ALLOW_LAB_DEFAULTS"), "1", StringComparison.Ordinal);

    private static string Required(string name, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value)) return value;
        if (AllowLabDefaults) return fallback;
        throw new InvalidOperationException(
            $"Set {name}, or set NZ_DEV_ALLOW_LAB_DEFAULTS=1 for the local lab defaults. " +
            "Live tests must not silently use a hard-coded password.");
    }

    public static readonly string Host = Required("NZ_DEV_HOST", "127.0.0.1");
    public static readonly int Port = int.TryParse(Environment.GetEnvironmentVariable("NZ_DEV_PORT"), out var port) ? port : 5480;
    public static readonly string DbName =
        Environment.GetEnvironmentVariable("NZ_DEV_DB")
        ?? Environment.GetEnvironmentVariable("NZ_DEV_DATABASE")
        ?? (AllowLabDefaults ? "JUST_DATA" : throw new InvalidOperationException("Set NZ_DEV_DB (or NZ_DEV_DATABASE)."));
    public static readonly string ChangeDatabaseDbName = Environment.GetEnvironmentVariable("NZ_DEV_CHANGE_DB")
        ?? (AllowLabDefaults ? "SYSTEM" : "SYSTEM");
    public static readonly string UserName = Required("NZ_DEV_USER", "admin");
    public static readonly string Password = Required("NZ_DEV_PASSWORD", "password");
}
