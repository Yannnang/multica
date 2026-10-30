using ChatBi.Domain.Enums;

namespace ChatBi.Application.Semantic;

// ---- 请求 DTO（record）。RowVersion 为 PUT 并发令牌（JSON 侧 base64 字符串），POST 置空。 ----

public record DataSourceRequest(
    string Name,
    DataSourceType DbType,
    string Host,
    int Port,
    string DatabaseName,
    string CredentialRef,
    int QueryTimeoutSeconds = 30,
    int MaxRowsPerQuery = 5000,
    DataSourceStatus Status = DataSourceStatus.Enabled,
    byte[]? RowVersion = null);

public record SemanticTableRequest(
    long DataSourceId,
    string SchemaName,
    string TableName,
    string DisplayName,
    string? Description,
    bool IsEnabled = true,
    byte[]? RowVersion = null);

public record SemanticColumnRequest(
    long SemanticTableId,
    string ColumnName,
    string DisplayName,
    string DbTypeName,
    SemanticColumnType SemanticType,
    bool IsSensitive = false,
    string? Description = null,
    byte[]? RowVersion = null);

public record MetricRequest(
    string Code,
    string DisplayName,
    string? Description,
    string Expression,
    AggregationType AggregationType,
    string? Unit,
    long? DefaultTimeColumnId,
    bool IsEnabled = true,
    long? OwnerUserId = null,
    IReadOnlyList<long>? TableIds = null,
    byte[]? RowVersion = null);

public record SemanticRelationRequest(
    long LeftSemanticTableId,
    long RightSemanticTableId,
    JoinType JoinType,
    string OnConditionsJson,
    byte[]? RowVersion = null);

/// <summary>Synonym 无并发令牌（设计只建 CreatedByUserId/CreatedAtUtc），PUT 为幂等覆盖。</summary>
public record SynonymRequest(string Term, SynonymTargetType TargetType, long TargetId);

// ---- 响应 DTO ----

public record DataSourceResponse(
    long Id,
    string Name,
    DataSourceType DbType,
    string Host,
    int Port,
    string DatabaseName,
    string CredentialRef,
    int QueryTimeoutSeconds,
    int MaxRowsPerQuery,
    DataSourceStatus Status,
    byte[] RowVersion,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc);

public record SemanticTableResponse(
    long Id,
    long DataSourceId,
    string SchemaName,
    string TableName,
    string DisplayName,
    string? Description,
    bool IsEnabled,
    byte[] RowVersion,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc);

public record SemanticColumnResponse(
    long Id,
    long SemanticTableId,
    string ColumnName,
    string DisplayName,
    string DbTypeName,
    SemanticColumnType SemanticType,
    bool IsSensitive,
    string? Description,
    byte[] RowVersion,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc);

public record MetricResponse(
    long Id,
    string Code,
    string DisplayName,
    string? Description,
    string Expression,
    AggregationType AggregationType,
    string? Unit,
    long? DefaultTimeColumnId,
    bool IsEnabled,
    long? OwnerUserId,
    IReadOnlyList<long> TableIds,
    byte[] RowVersion,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc);

public record SemanticRelationResponse(
    long Id,
    long LeftSemanticTableId,
    long RightSemanticTableId,
    JoinType JoinType,
    string OnConditionsJson,
    byte[] RowVersion,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc);

public record SynonymResponse(
    long Id,
    string Term,
    SynonymTargetType TargetType,
    long TargetId,
    long? CreatedByUserId,
    DateTime CreatedAtUtc);
