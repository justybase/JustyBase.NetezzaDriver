using System.Data.Common;

namespace JustyBase.NetezzaDriver;

public sealed class NzDbColumn : DbColumn
{
    public int ProviderType { get; internal set; }
    public int TypeModifier { get; internal set; }
    public string? DeclaredTypeName { get; internal set; }

    internal NzDbColumn(
        string? columnName,
        int? columnOrdinal,
        int providerType,
        int typeModifier,
        Type? dataType,
        string? dataTypeName,
        int? columnSize,
        int? numericPrecision,
        int? numericScale,
        bool? allowDBNull,
        string? baseColumnName,
        string? baseServerName,
        string? declaredTypeName)
    {
        ColumnName = columnName;
        ColumnOrdinal = columnOrdinal;
        ProviderType = providerType;
        TypeModifier = typeModifier;
        DataType = dataType;
#pragma warning disable CS8601 // Possible null reference assignment
        DataTypeName = dataTypeName ?? dataType?.Name;
#pragma warning restore CS8601
        ColumnSize = columnSize;
        NumericPrecision = numericPrecision;
        NumericScale = numericScale;
        AllowDBNull = allowDBNull;
        BaseColumnName = baseColumnName;
        BaseServerName = baseServerName;
        DeclaredTypeName = declaredTypeName;
        IsAutoIncrement = false;
        IsIdentity = false;
        IsLong = false;
        IsReadOnly = false;
        IsUnique = false;
    }
}
