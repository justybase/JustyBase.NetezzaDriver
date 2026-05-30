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
        var sb = new System.Text.StringBuilder(bytes.Length * 2 + 2);
        sb.Append("x'");
        foreach (var b in bytes)
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
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
