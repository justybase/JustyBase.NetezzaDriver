namespace JustyBase.NetezzaDriver.Examples.Examples;

internal static class ConnectionHelper
{
    // Heavy query for testing timeout / cancel
    public const string HEAVY_SQL =
        """
        SELECT F1.PRODUCTKEY, COUNT(DISTINCT (F1.PRODUCTKEY / F2.PRODUCTKEY))
        FROM
        ( SELECT * FROM JUST_DATA..FACTPRODUCTINVENTORY LIMIT 30000) F1,
        ( SELECT * FROM JUST_DATA..FACTPRODUCTINVENTORY LIMIT 30000) F2
        GROUP BY 1
        LIMIT 500
        """;

    private static string? _host, _db, _user, _password;
    private static int _port;

    static ConnectionHelper()
    {
        _host = GetEnv("NZ_DEV_HOST") ?? throw new InvalidOperationException("NZ_DEV_HOST not set");
        _db = GetEnv("NZ_DEV_DB") ?? throw new InvalidOperationException("NZ_DEV_DB not set");
        _user = GetEnv("NZ_DEV_USER") ?? throw new InvalidOperationException("NZ_DEV_USER not set");
        _password = GetEnv("NZ_DEV_PASSWORD") ?? throw new InvalidOperationException("NZ_DEV_PASSWORD not set");
        _port = int.TryParse(GetEnv("NZ_DEV_PORT"), out var p) ? p : 5480;
    }

    private static string? GetEnv(string name)
        => Environment.GetEnvironmentVariable(name);

    public static NzConnection Open()
    {
        var conn = new NzConnection(_user!, _password!, _host!, _db!, port: _port);
        conn.Open();
        return conn;
    }

    public static async Task<NzConnection> OpenAsync()
    {
        var conn = new NzConnection(_user!, _password!, _host!, _db!, port: _port);
        await conn.OpenAsync();
        return conn;
    }

    public static string Host => _host!;
    public static int Port => _port;
    public static string Database => _db!;
    public static string User => _user!;
    public static string Password => _password!;
}
