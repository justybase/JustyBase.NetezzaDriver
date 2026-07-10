using System;

namespace JustyBase.NetezzaDriver.Tests;

public static class Config
{
    public static readonly string Host = Environment.GetEnvironmentVariable("NZ_DEV_HOST") ?? "192.168.0.144";
    public static readonly int Port = int.TryParse(Environment.GetEnvironmentVariable("NZ_DEV_PORT"), out var port) ? port : 5480;
    public static readonly string DbName = Environment.GetEnvironmentVariable("NZ_DEV_DB") ?? "JUST_DATA";
    public static readonly string ChangeDatabaseDbName = Environment.GetEnvironmentVariable("NZ_DEV_CHANGE_DB") ?? "SYSTEM";
    public static readonly string UserName = Environment.GetEnvironmentVariable("NZ_DEV_USER") ?? "admin";
    public static readonly string Password = Environment.GetEnvironmentVariable("NZ_DEV_PASSWORD") ?? "password";
}
