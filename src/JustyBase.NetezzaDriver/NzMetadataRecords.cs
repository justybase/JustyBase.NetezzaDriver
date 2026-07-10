namespace JustyBase.NetezzaDriver;

public sealed record NzTableInfo(
    string Schema,
    string TableName,
    string? Owner,
    string? ObjType,
    long? ObjId,
    long? RowCount);

public sealed record NzColumnInfo(
    string ColumnName,
    int Ordinal,
    string DataType,
    bool IsNullable,
    long? ObjId,
    string? Description = null,
    string? DefaultValue = null);

public sealed record NzViewInfo(
    string Schema,
    string ViewName,
    string? Owner,
    long? ObjId,
    string? Definition);

public sealed record NzProcedureInfo(
    string Schema,
    string ProcName,
    string? Owner,
    long? ObjId,
    string? Signature,
    string? Returns,
    bool? IsBuiltin,
    string? Source,
    bool? ExecutedAsOwner = null,
    string? Arguments = null,
    string? Description = null);

public sealed record NzTableSizeInfo(
    string Schema,
    string TableName,
    long? UsedBytes,
    long? AllocatedBytes,
    long? SizeMb,
    double? Skew);

public sealed record NzSessionInfo(
    long SessionId,
    string? Username,
    string? Database,
    DateTime? ConnectTime,
    string? Priority,
    string? Status,
    string? ClientType,
    string? ClientOsUser);

public sealed record NzObjectInfo(
    string Schema,
    string Name,
    string Type,
    string? Owner,
    long? ObjId);

// ── New record types for enhanced schema search ──

public sealed record NzDatabaseInfo(
    string DatabaseName,
    string? Owner,
    string? DefaultSchema);

public sealed record NzFunctionInfo(
    string Schema,
    string FunctionName,
    string? Owner,
    long? ObjId,
    string? Signature,
    string? Returns,
    string? Language,
    bool? IsFluid);

public sealed record NzSynonymInfo(
    string Schema,
    string SynonymName,
    string? RefObjectName,
    string? RefDatabase,
    string? RefSchema,
    string? Description);

public sealed record NzConstraintInfo(
    string Schema,
    string TableName,
    string ConstraintName,
    char ConstraintType,        // 'p' = PK, 'f' = FK, 'u' = UQ
    string ColumnName,
    string? PkDatabase,
    string? PkSchema,
    string? PkTable,
    string? PkColumn,
    string? UpdateType,
    string? DeleteType);

public sealed record NzDistKeyInfo(
    string Schema,
    string TableName,
    string ColumnName,
    int DistAttNum);

public sealed record NzOrganizeKeyInfo(
    string Schema,
    string TableName,
    string ColumnName,
    int AttNum);

public sealed record NzObjectDetailInfo(
    string Schema,
    string ObjectName,
    string ObjectType,
    string? Owner,
    long? ObjId,
    string? Description,
    DateTime? CreatedDate);
