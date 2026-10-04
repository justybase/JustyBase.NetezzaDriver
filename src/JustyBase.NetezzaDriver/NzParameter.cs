using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace JustyBase.NetezzaDriver;

public sealed class NzParameter : DbParameter
{
    private string? _parameterName;
    private object? _value;
    private DbType _dbType = DbType.Object;
    private ParameterDirection _direction = ParameterDirection.Input;
    private int _size;
    private bool _nullable;
    private bool _isPositional;
    private byte _precision;
    private byte _scale;
    private bool _sourceColumnNullMapping;
    private string? _sourceColumn;
    private DataRowVersion _sourceVersion = DataRowVersion.Current;

    public NzParameter() { }

    public NzParameter(string? name, object? value)
    {
        _parameterName = name;
        _value = value;
        if (value is not null && value is not DBNull)
        {
            _dbType = ValueToDbType(value);
        }
    }

    public NzParameter(string? name, DbType dbType)
    {
        _parameterName = name;
        _dbType = dbType;
    }

    public bool IsPositional
    {
        get => _isPositional;
        set => _isPositional = value;
    }

    internal string? ResolvedName
    {
        get
        {
            if (_parameterName is null) return null;
            if (_parameterName.Length > 0 && (_parameterName[0] == ':' || _parameterName[0] == '@'))
                return _parameterName[1..];
            return _parameterName;
        }
    }

    internal ReadOnlySpan<char> GetResolvedNameSpan()
    {
        var name = _parameterName;
        if (string.IsNullOrEmpty(name))
            return ReadOnlySpan<char>.Empty;
        if (name[0] == ':' || name[0] == '@')
            return name.AsSpan(1);
        return name.AsSpan();
    }

    [AllowNull]
    public override string ParameterName
    {
        get => _parameterName ?? string.Empty;
        set => _parameterName = value;
    }

    public override object? Value
    {
        get => _value;
        set
        {
            _value = value;
            if (value is not null && value is not DBNull)
                _dbType = ValueToDbType(value);
        }
    }

    public override DbType DbType
    {
        get => _dbType;
        set => _dbType = value;
    }

    public override ParameterDirection Direction
    {
        get => _direction;
        set
        {
            if (value != ParameterDirection.Input)
                throw new NotSupportedException("Only Input direction is supported.");
            _direction = value;
        }
    }

    public override int Size
    {
        get => _size;
        set => _size = value;
    }

    public override bool IsNullable
    {
        get => _nullable;
        set => _nullable = value;
    }

    public override byte Precision
    {
        get => _precision;
        set => _precision = value;
    }

    public override byte Scale
    {
        get => _scale;
        set => _scale = value;
    }

    [AllowNull]
    public override string SourceColumn
    {
        get => _sourceColumn ?? string.Empty;
        set => _sourceColumn = value;
    }

    public override bool SourceColumnNullMapping
    {
        get => _sourceColumnNullMapping;
        set => _sourceColumnNullMapping = value;
    }

    public override DataRowVersion SourceVersion
    {
        get => _sourceVersion;
        set => _sourceVersion = value;
    }

    public override void ResetDbType()
    {
        _dbType = DbType.Object;
    }

    internal string ToSqlLiteral()
    {
        return ValueToSqlLiteral(_value);
    }

    internal void AppendSqlLiteral(ref ValueStringBuilder sb)
    {
        var value = _value;
        if (value is null || value is DBNull)
        {
            sb.Append("NULL");
            return;
        }

        switch (value)
        {
            case bool b:
                sb.Append(b ? "TRUE" : "FALSE");
                return;
            case int i:
                AppendFormattable(ref sb, i);
                return;
            case long l:
                AppendFormattable(ref sb, l);
                return;
            case short s:
                AppendFormattable(ref sb, s);
                return;
            case byte bt:
                AppendFormattable(ref sb, bt);
                return;
            case sbyte sbv:
                AppendFormattable(ref sb, sbv);
                return;
            case ushort us:
                AppendFormattable(ref sb, us);
                return;
            case uint ui:
                AppendFormattable(ref sb, ui);
                return;
            case ulong ul:
                AppendFormattable(ref sb, ul);
                return;
            case string str:
                AppendStringLiteral(ref sb, str);
                return;
            case char c:
                AppendCharLiteral(ref sb, c);
                return;
            case byte[] bytes:
                sb.Append("x'");
                AppendHex(ref sb, bytes);
                sb.Append('\'');
                return;
            case float f:
                AppendFormattable(ref sb, f, "G");
                return;
            case double d:
                AppendFormattable(ref sb, d, "G");
                return;
            case decimal m:
                AppendFormattable(ref sb, m);
                return;
            case DateTime dt:
                AppendQuoted(ref sb, dt, "yyyy-MM-dd HH:mm:ss.ffffff");
                return;
            case DateOnly d:
                AppendQuoted(ref sb, d, "yyyy-MM-dd");
                return;
            case TimeOnly t:
                AppendQuoted(ref sb, t, "HH:mm:ss");
                return;
            case TimeSpan ts:
                AppendQuoted(ref sb, ts, @"hh\:mm\:ss");
                return;
            case Guid g:
                AppendQuoted(ref sb, g, "D");
                return;
            default:
                AppendStringLiteral(ref sb, value.ToString() ?? string.Empty);
                return;
        }
    }

    /// <summary>
    /// Formats a value directly into a stack buffer and appends the resulting
    /// characters, avoiding the intermediate <c>string</c> of <c>ToString()</c>.
    /// </summary>
    private static void AppendFormattable<T>(ref ValueStringBuilder sb, T value, ReadOnlySpan<char> format = default)
        where T : ISpanFormattable
    {
        Span<char> buffer = stackalloc char[128];
        if (value.TryFormat(buffer, out int written, format, CultureInfo.InvariantCulture))
        {
            sb.Append(buffer[..written]);
        }
        else
        {
            sb.Append(value.ToString(format.IsEmpty ? null : format.ToString(), CultureInfo.InvariantCulture));
        }
    }

    private static void AppendQuoted<T>(ref ValueStringBuilder sb, T value, ReadOnlySpan<char> format)
        where T : ISpanFormattable
    {
        sb.Append('\'');
        AppendFormattable(ref sb, value, format);
        sb.Append('\'');
    }

    private static void AppendHex(ref ValueStringBuilder sb, ReadOnlySpan<byte> bytes)
    {
        const string hex = "0123456789abcdef";
        Span<char> pair = stackalloc char[2];
        foreach (byte b in bytes)
        {
            pair[0] = hex[b >> 4];
            pair[1] = hex[b & 0x0F];
            sb.Append(pair);
        }
    }

    private static void AppendCharLiteral(ref ValueStringBuilder sb, char c)
    {
        sb.Append('\'');
        if (c == '\'')
            sb.Append("''");
        else
            sb.Append(c);
        sb.Append('\'');
    }

    private static void AppendStringLiteral(ref ValueStringBuilder sb, string s)
    {
        sb.Append('\'');
        int start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\'')
            {
                sb.Append(s.AsSpan(start, i - start));
                sb.Append("''");
                start = i + 1;
            }
        }
        sb.Append(s.AsSpan(start));
        sb.Append('\'');
    }

    internal static string ValueToSqlLiteral(object? value)
    {
        if (value is null || value is DBNull)
            return "NULL";

        return value switch
        {
            bool b => b ? "TRUE" : "FALSE",
            int i => i.ToString(CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture),
            short s => s.ToString(CultureInfo.InvariantCulture),
            byte bt => bt.ToString(CultureInfo.InvariantCulture),
            sbyte sb => sb.ToString(CultureInfo.InvariantCulture),
            ushort us => us.ToString(CultureInfo.InvariantCulture),
            uint ui => ui.ToString(CultureInfo.InvariantCulture),
            ulong ul => ul.ToString(CultureInfo.InvariantCulture),
            float f => f.ToString("G", CultureInfo.InvariantCulture),
            double d => d.ToString("G", CultureInfo.InvariantCulture),
            decimal m => m.ToString(CultureInfo.InvariantCulture),
            string s => FormatStringLiteral(s),
            DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss.ffffff}'",
            DateOnly d => $"'{d:yyyy-MM-dd}'",
            TimeOnly t => $"'{t:HH:mm:ss}'",
            TimeSpan ts => $"'{ts:hh\\:mm\\:ss}'",
            byte[] bytes => FormatByteArray(bytes),
            Guid g => $"'{g:D}'",
            char c => FormatStringLiteral(c.ToString()),
            _ => FormatStringLiteral(value.ToString() ?? string.Empty)
        };
    }

    internal static string FormatStringLiteral(string s)
    {
        return "'" + s.Replace("'", "''") + "'";
    }

    private static string FormatByteArray(byte[] bytes)
    {
        Span<char> initialBuffer = stackalloc char[32];
        var sb = new ValueStringBuilder(initialBuffer);
        sb.Append("x'");
        AppendHex(ref sb, bytes);
        sb.Append('\'');
        return sb.ToString();
    }

    internal static DbType ValueToDbType(object value)
    {
        return value switch
        {
            bool => DbType.Boolean,
            int => DbType.Int32,
            long => DbType.Int64,
            short => DbType.Int16,
            byte => DbType.Byte,
            sbyte => DbType.SByte,
            ushort => DbType.UInt16,
            uint => DbType.UInt32,
            ulong => DbType.UInt64,
            float => DbType.Single,
            double => DbType.Double,
            decimal => DbType.Decimal,
            string => DbType.String,
            DateTime => DbType.DateTime,
            DateOnly => DbType.Date,
            TimeOnly => DbType.Time,
            TimeSpan => DbType.Time,
            byte[] => DbType.Binary,
            Guid => DbType.Guid,
            char => DbType.StringFixedLength,
            _ => DbType.Object
        };
    }
}
