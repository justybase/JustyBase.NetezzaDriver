using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.Data.Common;
using System.Globalization;
using System.Text;

namespace JustyBase.NetezzaDriver;

/// <summary>
/// Builds and parses Netezza connection strings. Derives from
/// <see cref="DbConnectionStringBuilder"/> so the standard ADO.NET surface
/// (indexer, <see cref="DbConnectionStringBuilder.TryGetValue"/>,
/// <see cref="DbConnectionStringBuilder.Remove"/>, quoting and
/// <see cref="DbProviderFactory.CreateConnectionStringBuilder"/>) works as
/// expected.
/// </summary>
public sealed class NzConnectionStringBuilder : DbConnectionStringBuilder
{
    internal const string RedactedPassword = "********";

    private static readonly string[] UserKeys = ["User", "Username"];
    private static readonly string[] PasswordKeys = ["Password", "Pwd"];
    private static readonly string[] HostKeys = ["Host", "Server"];

    public NzConnectionStringBuilder()
    {
        Host = string.Empty;
        Database = string.Empty;
        UserName = string.Empty;
        Password = string.Empty;
        Port = 5480;
        Timeout = 0;
        Pooling = true;
        MinPoolSize = 0;
        MaxPoolSize = 10;
        ConnectionIdleTimeout = 30;
        ConnectionLifetime = 0;
        BulkInsert = true;
        ConnectionValidationInterval = DefaultConnectionValidationInterval;
    }

    /// <summary>
    /// Parses an existing connection string. Legacy <c>{value}</c> quoting is
    /// still accepted, in addition to the standard double-quote escaping
    /// performed by <see cref="DbConnectionStringBuilder"/>.
    /// </summary>
    public NzConnectionStringBuilder(string connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        ConnectionString = NormalizeLegacyBraces(connectionString);
    }

    public string Host
    {
        get => GetString(HostKeys, string.Empty);
        set => this["Host"] = value;
    }

    public string Database
    {
        get => GetString("Database", string.Empty);
        set => this["Database"] = value;
    }

    public string UserName
    {
        get => GetString(UserKeys, string.Empty);
        set => this["User"] = value;
    }

    public string Password
    {
        get => GetString(PasswordKeys, string.Empty);
        set => this["Password"] = value;
    }

    public int Port
    {
        get => GetInt("Port", 5480);
        set => this["Port"] = value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Connect timeout in seconds (0 keeps the driver default).</summary>
    public int Timeout
    {
        get => GetInt("Timeout", 0);
        set => this["Timeout"] = value.ToString(CultureInfo.InvariantCulture);
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ILoggerFactory? LoggerFactory { get; set; }

    public bool Pooling
    {
        get => GetBool("Pooling", true);
        set => this["Pooling"] = value.ToString();
    }

    public int MinPoolSize
    {
        get => GetInt("MinPoolSize", 0);
        set => this["MinPoolSize"] = value.ToString(CultureInfo.InvariantCulture);
    }

    public int MaxPoolSize
    {
        get => GetInt("MaxPoolSize", 10);
        set => this["MaxPoolSize"] = value.ToString(CultureInfo.InvariantCulture);
    }

    public int ConnectionIdleTimeout
    {
        get => GetInt("ConnectionIdleTimeout", 30);
        set => this["ConnectionIdleTimeout"] = value.ToString(CultureInfo.InvariantCulture);
    }

    public int ConnectionLifetime
    {
        get => GetInt("ConnectionLifetime", 0);
        set
        {
            // Keep the historical shape: an unlimited lifetime is omitted.
            if (value > 0)
                this["ConnectionLifetime"] = value.ToString(CultureInfo.InvariantCulture);
            else
                Remove("ConnectionLifetime");
        }
    }

    /// <summary>Enables the external-table path used by <see cref="NzBulkCopy"/>.</summary>
    public bool BulkInsert
    {
        get => GetBool("BulkInsert", true);
        set => this["BulkInsert"] = value.ToString();
    }

    /// <summary>Default seconds a pooled connection may remain idle before it is probed with SELECT 1 on checkout.</summary>
    public const int DefaultConnectionValidationInterval = 30;

    /// <summary>
    /// Seconds a connection may remain idle before the pool probes it with SELECT 1 on checkout.
    /// Defaults to <see cref="DefaultConnectionValidationInterval"/>; set to zero to validate on every checkout.
    /// </summary>
    public int ConnectionValidationInterval
    {
        get => GetInt("ConnectionValidationInterval", DefaultConnectionValidationInterval);
        // Always persisted: 0 is a valid explicit setting and must survive
        // builder -> string -> parser round trips.
        set => this["ConnectionValidationInterval"] = value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Transport security policy. Setting this removes any <c>SecurityLevel</c>
    /// key because the two options are mutually exclusive.
    /// </summary>
    public SslMode SslMode
    {
        get => TryGetString(NzOptionParser.SslModeKeys, out var value)
            && NzOptionParser.TryParseSslMode(value, out var mode)
                ? mode
                : SslMode.Prefer;
        set
        {
            Remove("SecurityLevel");
            this["SslMode"] = NzOptionParser.ToConnectionString(value);
        }
    }

    /// <summary>
    /// Low-level handshake security level. Prefer <see cref="SslMode"/> for new
    /// applications; setting this removes any <c>SslMode</c> key.
    /// </summary>
    public SecurityLevelCode SecurityLevel
    {
        get => TryGetString(NzOptionParser.SecurityLevelKeys, out var value)
            && NzOptionParser.TryParseSecurityLevel(value, out var level)
                ? level
                : SecurityLevelCode.PreferredUnsecured;
        set
        {
            Remove("SslMode");
            this["SecurityLevel"] = value.ToString();
        }
    }

    /// <summary>PEM file with one or more trusted CA certificates for TLS.</summary>
    public string SslCertFilePath
    {
        get => GetString(NzOptionParser.SslCertificateKeys, string.Empty);
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                Remove("SslCertFilePath");
                Remove("SslCerFilePath");
                Remove("Ssl_Cert");
                Remove("SslCert");
            }
            else
            {
                this["SslCertFilePath"] = value;
            }
        }
    }

    /// <summary>
    /// Validates the server certificate. Defaults to true. <c>SslMode=require</c>
    /// implies false unless this option is set explicitly.
    /// </summary>
    public bool RejectUnauthorized
    {
        get => GetBool("RejectUnauthorized", true);
        set => this["RejectUnauthorized"] = value.ToString();
    }

    /// <summary>Encoding of CHAR/VARCHAR values on the binary result path.</summary>
    public CharEncoding CharVarcharEncoding
    {
        get => TryGetString(NzOptionParser.CharVarcharEncodingKeys, out var value)
            && NzOptionParser.TryParseCharEncoding(value, out var encoding)
                ? encoding
                : CharEncoding.Latin1;
        set
        {
            if (value == CharEncoding.Latin1)
            {
                Remove("CharVarcharEncoding");
            }
            else
            {
                this["CharVarcharEncoding"] = NzOptionParser.ToConnectionString(value);
            }
        }
    }

    public string SafeConnectionString => ToSafeString();

    public override string ToString() => ConnectionString;

    /// <summary>Connection string with the password replaced by a fixed mask.</summary>
    public string ToSafeString()
    {
        // Rebuild through the strongly typed properties so the canonical key
        // casing is preserved even when this instance was parsed from a string
        // (DbConnectionStringBuilder normalizes parsed keywords to lowercase).
        var safe = new NzConnectionStringBuilder
        {
            Host = Host,
            Database = Database,
            UserName = UserName,
            Password = RedactedPassword,
            Port = Port,
            Timeout = Timeout,
            Pooling = Pooling,
            MinPoolSize = MinPoolSize,
            MaxPoolSize = MaxPoolSize,
            ConnectionIdleTimeout = ConnectionIdleTimeout,
            ConnectionLifetime = ConnectionLifetime,
            BulkInsert = BulkInsert,
            ConnectionValidationInterval = ConnectionValidationInterval,
            CharVarcharEncoding = CharVarcharEncoding,
        };

        if (SslMode != SslMode.Prefer)
        {
            safe.SslMode = SslMode;
        }
        else if (SecurityLevel != SecurityLevelCode.PreferredUnsecured)
        {
            safe.SecurityLevel = SecurityLevel;
        }

        if (!string.IsNullOrEmpty(SslCertFilePath))
        {
            safe.SslCertFilePath = SslCertFilePath;
        }

        if (!RejectUnauthorized)
        {
            safe.RejectUnauthorized = false;
        }

        return safe.ConnectionString;
    }

    private string GetString(string key, string fallback)
        => GetString([key], fallback);

    private string GetString(string[] keys, string fallback)
    {
        foreach (var key in keys)
        {
            if (TryGetValue(key, out var value) && value is not null)
            {
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? fallback;
            }
        }

        return fallback;
    }

    private bool TryGetString(string[] keys, out string value)
    {
        foreach (var key in keys)
        {
            if (TryGetValue(key, out var raw) && raw is not null)
            {
                value = Convert.ToString(raw, CultureInfo.InvariantCulture) ?? string.Empty;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private int GetInt(string key, int fallback)
        => TryGetValue(key, out var value)
            && int.TryParse(
                Convert.ToString(value, CultureInfo.InvariantCulture),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed)
            ? parsed
            : fallback;

    private bool GetBool(string key, bool fallback)
        => TryGetValue(key, out var value)
            && bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var parsed)
            ? parsed
            : fallback;

    /// <summary>
    /// Converts the legacy <c>{value;with;semicolons}</c> quoting understood by
    /// earlier versions into standard double-quoted values.
    /// </summary>
    private static string NormalizeLegacyBraces(string connectionString)
    {
        if (connectionString.IndexOf('{') < 0)
            return connectionString;

        var builder = new StringBuilder(connectionString.Length + 8);
        bool inBraces = false;
        foreach (char c in connectionString)
        {
            if (!inBraces && c == '{')
            {
                inBraces = true;
                builder.Append('"');
            }
            else if (inBraces && c == '}')
            {
                inBraces = false;
                builder.Append('"');
            }
            else if (inBraces && c == '"')
            {
                builder.Append("\"\"");
            }
            else
            {
                builder.Append(c);
            }
        }

        if (inBraces)
            throw new NetezzaException("Unterminated curly brace in connection string");

        return builder.ToString();
    }
}
