namespace JustyBase.NetezzaDriver;

/// <summary>
/// Canonical connection-string spellings for the cross-driver options. The
/// parsers accept the aliases used by the Rust, Node and Python drivers so a
/// connection string can be moved between drivers without edits.
/// </summary>
internal static class NzOptionParser
{
    internal static readonly string[] SslModeKeys = ["SslMode", "Ssl_Mode"];
    internal static readonly string[] SecurityLevelKeys = ["SecurityLevel", "Security_Level"];
    internal static readonly string[] SslCertificateKeys =
        ["SslCertFilePath", "SslCerFilePath", "Ssl_Cert", "SslCert"];
    internal static readonly string[] RejectUnauthorizedKeys =
        ["RejectUnauthorized", "Reject_Unauthorized"];
    internal static readonly string[] CharVarcharEncodingKeys =
        ["CharVarcharEncoding", "CharVarCharEncoding", "Char_Varchar_Encoding", "CharVarEncoding"];

    public static bool TryParseSslMode(string? value, out SslMode mode)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "disable":
                mode = SslMode.Disable;
                return true;
            case "prefer":
                mode = SslMode.Prefer;
                return true;
            case "require":
                mode = SslMode.Require;
                return true;
            case "verify-ca":
            case "verify_ca":
            case "verifyca":
                mode = SslMode.VerifyCA;
                return true;
            case "verify-full":
            case "verify_full":
            case "verifyfull":
                mode = SslMode.VerifyFull;
                return true;
            default:
                mode = default;
                return false;
        }
    }

    public static string ToConnectionString(SslMode mode) => mode switch
    {
        SslMode.Disable => "disable",
        SslMode.Prefer => "prefer",
        SslMode.Require => "require",
        SslMode.VerifyCA => "verify-ca",
        SslMode.VerifyFull => "verify-full",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public static bool TryParseSecurityLevel(string? value, out SecurityLevelCode level)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "preferredunsecured":
            case "0":
                level = SecurityLevelCode.PreferredUnsecured;
                return true;
            case "onlyunsecuredsession":
            case "1":
                level = SecurityLevelCode.OnlyUnsecuredSession;
                return true;
            case "preferredsecuredsession":
            case "2":
                level = SecurityLevelCode.PreferredSecuredSession;
                return true;
            case "onlysecuredsession":
            case "3":
                level = SecurityLevelCode.OnlySecuredSession;
                return true;
            default:
                level = default;
                return false;
        }
    }

    public static bool TryParseCharEncoding(string? value, out CharEncoding encoding)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "latin":
            case "latin1":
            case "latin-1":
            case "iso-8859-1":
            case "iso8859-1":
                encoding = CharEncoding.Latin1;
                return true;
            case "utf8":
            case "utf-8":
                encoding = CharEncoding.Utf8;
                return true;
            default:
                encoding = default;
                return false;
        }
    }

    public static string ToConnectionString(CharEncoding encoding) => encoding switch
    {
        CharEncoding.Latin1 => "latin1",
        CharEncoding.Utf8 => "utf8",
        _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
    };

    internal static System.Text.Encoding ToEncoding(CharEncoding encoding) => encoding switch
    {
        CharEncoding.Utf8 => System.Text.Encoding.UTF8,
        _ => System.Text.Encoding.Latin1,
    };
}

/// <summary>
/// Parsed connection-string values. Nullable TLS fields mean "not specified",
/// which lets constructor defaults keep working while explicit connection
/// string options take precedence.
/// </summary>
internal sealed record NzConnectionSettings(
    string User,
    string Password,
    string Host,
    string? Database,
    int? Port,
    int? Timeout,
    bool Pooling,
    int MinPoolSize,
    int MaxPoolSize,
    int ConnectionIdleTimeout,
    int ConnectionLifetime,
    int ConnectionValidationInterval,
    bool BulkInsert,
    SslMode? SslMode,
    SecurityLevelCode? SecurityLevel,
    string? SslCertFilePath,
    bool? RejectUnauthorized,
    CharEncoding CharVarcharEncoding);
