namespace McpDatabaseQueryApp.Core.Providers;

public sealed record SchemaInfo(string Name, string? Owner);

public sealed record TableInfo(
    string Schema,
    string Name,
    string Kind,
    long? RowEstimate,
    string? Comment);

public sealed record ColumnInfo(
    string Name,
    string DataType,
    bool IsNullable,
    string? Default,
    bool IsPrimaryKey,
    bool IsIdentity,
    int? OrdinalPosition,
    int? MaxLength,
    int? NumericPrecision,
    int? NumericScale,
    string? Comment);

public sealed record IndexInfo(
    string Name,
    bool IsUnique,
    bool IsPrimaryKey,
    IReadOnlyList<string> Columns,
    string? Method);

public sealed record ForeignKeyInfo(
    string Name,
    IReadOnlyList<string> Columns,
    string ReferencedSchema,
    string ReferencedTable,
    IReadOnlyList<string> ReferencedColumns,
    string OnUpdate,
    string OnDelete);

public sealed record TableDetails(
    TableInfo Table,
    IReadOnlyList<ColumnInfo> Columns,
    IReadOnlyList<IndexInfo> Indexes,
    IReadOnlyList<ForeignKeyInfo> ForeignKeys);

public sealed record RoleInfo(
    string Name,
    bool IsLogin,
    bool IsSuperuser,
    IReadOnlyList<string> MemberOf);

public sealed record DatabaseInfo(
    string Name,
    string? Owner,
    string? Encoding,
    long? SizeBytes);

public sealed record PageRequest(int Offset, int Limit, string? Filter = null);

public sealed record QueryColumn(string Name, string DataType, int Ordinal);

public sealed record QueryRequest(
    string Sql,
    IReadOnlyDictionary<string, object?>? Parameters,
    int? Limit,
    int? TimeoutSeconds);

public sealed record NonQueryRequest(
    string Sql,
    IReadOnlyDictionary<string, object?>? Parameters,
    int? TimeoutSeconds);

public sealed record QueryResult(
    IReadOnlyList<QueryColumn> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    int RowCount,
    bool Truncated,
    long TotalRowsAvailable,
    long ExecutionMs);

public sealed record ExplainResult(string Format, string Plan);
