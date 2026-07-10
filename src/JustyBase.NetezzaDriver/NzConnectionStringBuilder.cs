using Microsoft.Extensions.Logging;
using System.Text;

namespace JustyBase.NetezzaDriver;

public sealed class NzConnectionStringBuilder
{
    private const string RedactedPassword = "********";

    public string Host { get; set; } = string.Empty;
    public string Database { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public int Port { get; set; } = 5480;
    public int Timeout { get; set; }
    public ILoggerFactory? LoggerFactory { get; set; }
    public bool Pooling { get; set; } = true;
    public int MinPoolSize { get; set; } = 0;
    public int MaxPoolSize { get; set; } = 10;
    public int ConnectionIdleTimeout { get; set; } = 30;
    public int ConnectionLifetime { get; set; } = 0;

    public override string ToString()
        => BuildConnectionString(redactPassword: false);

    public string ToSafeString()
        => BuildConnectionString(redactPassword: true);

    private string BuildConnectionString(bool redactPassword)
    {
        var sb = new StringBuilder();
        sb.Append($"Host={Host};");
        sb.Append($"Database={Database};");
        sb.Append($"User={UserName};");
        sb.Append($"Password={(redactPassword ? RedactedPassword : Password)};");
        sb.Append($"Port={Port};");
        sb.Append($"Timeout={Timeout};");
        sb.Append($"Pooling={Pooling};");
        sb.Append($"MinPoolSize={MinPoolSize};");
        sb.Append($"MaxPoolSize={MaxPoolSize};");
        sb.Append($"ConnectionIdleTimeout={ConnectionIdleTimeout};");
        if (ConnectionLifetime > 0)
            sb.Append($"ConnectionLifetime={ConnectionLifetime};");
        return sb.ToString();
    }
    public string ConnectionString => ToString();
    public string SafeConnectionString => ToSafeString();
}
