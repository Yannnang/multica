using ChatBi.Application.Common;
using ChatBi.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace ChatBi.Application.Semantic;

/// <summary>语义表管理用例（semantic:manage 写、semantic:read 读；权限在端点层）。</summary>
public class SemanticTableService
{
    private readonly ISemanticTableStore _store;
    private readonly IDataSourceStore _dataSourceStore;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SemanticTableService> _logger;

    public SemanticTableService(
        ISemanticTableStore store,
        IDataSourceStore dataSourceStore,
        TimeProvider timeProvider,
        ILogger<SemanticTableService> logger)
    {
        _store = store;
        _dataSourceStore = dataSourceStore;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<PagedResult<SemanticTableResponse>> ListAsync(int? page, int? pageSize, CancellationToken cancellationToken)
    {
        var result = await _store.GetPageAsync(PageRequest.From(page, pageSize), cancellationToken).ConfigureAwait(false);
        return new PagedResult<SemanticTableResponse>(
            result.Items.Select(ToResponse).ToList(), result.Page, result.PageSize, result.Total);
    }

    public async Task<SemanticTableResponse> GetByIdAsync(long id, CancellationToken cancellationToken)
    {
        var entity = await _store.FindByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new SemanticNotFoundException("语义表", id);
        return ToResponse(entity);
    }

    public async Task<SemanticTableResponse> CreateAsync(SemanticTableRequest request, long operatorUserId, CancellationToken cancellationToken)
    {
        Validate(request, includeRowVersion: false);

        _ = await _dataSourceStore.FindByIdAsync(request.DataSourceId, cancellationToken).ConfigureAwait(false)
            ?? throw new SemanticValidationException(SemanticErrorCodes.FieldInvalid, $"DataSourceId={request.DataSourceId} 不存在。");

        if (await _store.ExistsAsync(request.DataSourceId, request.SchemaName, request.TableName, excludeId: null, cancellationToken).ConfigureAwait(false))
        {
            throw new SemanticDuplicateException(
                $"该数据源下已存在 {request.SchemaName}.{request.TableName} 的语义表定义。");
        }

        var entity = new SemanticTable
        {
            DataSourceId = request.DataSourceId,
            SchemaName = string.IsNullOrWhiteSpace(request.SchemaName) ? "dbo" : request.SchemaName.Trim(),
            TableName = request.TableName.Trim(),
            DisplayName = request.DisplayName.Trim(),
            Description = request.Description,
            IsEnabled = request.IsEnabled,
            CreatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime,
            CreatedByUserId = operatorUserId,
        };

        await _store.AddAsync(entity, cancellationToken).ConfigureAwait(false);
        await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("SemanticTable created: id={TableId} by user={UserId}.", entity.Id, operatorUserId);
        return ToResponse(entity);
    }

    public async Task<SemanticTableResponse> UpdateAsync(long id, SemanticTableRequest request, long operatorUserId, CancellationToken cancellationToken)
    {
        Validate(request, includeRowVersion: true);

        var entity = await _store.FindByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new SemanticNotFoundException("语义表", id);

        _ = await _dataSourceStore.FindByIdAsync(request.DataSourceId, cancellationToken).ConfigureAwait(false)
            ?? throw new SemanticValidationException(SemanticErrorCodes.FieldInvalid, $"DataSourceId={request.DataSourceId} 不存在。");

        if (await _store.ExistsAsync(request.DataSourceId, request.SchemaName, request.TableName, excludeId: id, cancellationToken).ConfigureAwait(false))
        {
            throw new SemanticDuplicateException(
                $"该数据源下已存在 {request.SchemaName}.{request.TableName} 的语义表定义。");
        }

        entity.DataSourceId = request.DataSourceId;
        entity.SchemaName = string.IsNullOrWhiteSpace(request.SchemaName) ? "dbo" : request.SchemaName.Trim();
        entity.TableName = request.TableName.Trim();
        entity.DisplayName = request.DisplayName.Trim();
        entity.Description = request.Description;
        entity.IsEnabled = request.IsEnabled;
        entity.UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        entity.UpdatedByUserId = operatorUserId;

        _store.MarkRowVersion(entity, request.RowVersion!);
        await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("SemanticTable updated: id={TableId} by user={UserId}.", entity.Id, operatorUserId);
        return ToResponse(entity);
    }

    public async Task DeleteAsync(long id, long operatorUserId, CancellationToken cancellationToken)
    {
        var entity = await _store.FindByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new SemanticNotFoundException("语义表", id);

        entity.IsDeleted = true; // 软删；子列保留（随表一起退出 schema linking）。
        entity.UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        entity.UpdatedByUserId = operatorUserId;

        await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("SemanticTable soft-deleted: id={TableId} by user={UserId}.", entity.Id, operatorUserId);
    }

    private static void Validate(SemanticTableRequest request, bool includeRowVersion)
    {
        SemanticGuards.Ensure(request.DataSourceId > 0, "DataSourceId 必填。");
        SemanticGuards.Ensure(request.SchemaName is null || request.SchemaName.Length <= 128, "SchemaName 超长（上限 128）。");
        SemanticGuards.Ensure(!string.IsNullOrWhiteSpace(request.TableName), "TableName 必填。");
        SemanticGuards.Ensure(request.TableName.Trim().Length <= 128, "TableName 超长（上限 128）。");
        SemanticGuards.Ensure(!string.IsNullOrWhiteSpace(request.DisplayName), "DisplayName 必填。");
        SemanticGuards.Ensure(request.DisplayName.Trim().Length <= 128, "DisplayName 超长（上限 128）。");
        SemanticGuards.Ensure(request.Description is null || request.Description.Length <= 1024, "Description 超长（上限 1024）。");
        if (includeRowVersion)
        {
            SemanticGuards.Ensure(request.RowVersion is { Length: > 0 }, "PUT 必须携带并发令牌 RowVersion。");
        }
    }

    internal static SemanticTableResponse ToResponse(SemanticTable entity) => new(
        entity.Id,
        entity.DataSourceId,
        entity.SchemaName,
        entity.TableName,
        entity.DisplayName,
        entity.Description,
        entity.IsEnabled,
        entity.RowVersion,
        entity.CreatedAtUtc,
        entity.UpdatedAtUtc);
}

/// <summary>语义列管理用例（列挂在语义表下；软删不适用，DELETE 为物理删除）。</summary>
public class SemanticColumnService
{
    private readonly ISemanticColumnStore _store;
    private readonly ISemanticTableStore _tableStore;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SemanticColumnService> _logger;

    public SemanticColumnService(
        ISemanticColumnStore store,
        ISemanticTableStore tableStore,
        TimeProvider timeProvider,
        ILogger<SemanticColumnService> logger)
    {
        _store = store;
        _tableStore = tableStore;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<PagedResult<SemanticColumnResponse>> ListAsync(int? page, int? pageSize, CancellationToken cancellationToken)
    {
        var result = await _store.GetPageAsync(PageRequest.From(page, pageSize), cancellationToken).ConfigureAwait(false);
        return new PagedResult<SemanticColumnResponse>(
            result.Items.Select(ToResponse).ToList(), result.Page, result.PageSize, result.Total);
    }

    public async Task<SemanticColumnResponse> GetByIdAsync(long id, CancellationToken cancellationToken)
    {
        var entity = await _store.FindByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new SemanticNotFoundException("语义列", id);
        return ToResponse(entity);
    }

    public async Task<SemanticColumnResponse> CreateAsync(SemanticColumnRequest request, long operatorUserId, CancellationToken cancellationToken)
    {
        Validate(request, includeRowVersion: false);

        _ = await _tableStore.FindByIdAsync(request.SemanticTableId, cancellationToken).ConfigureAwait(false)
            ?? throw new SemanticValidationException(SemanticErrorCodes.FieldInvalid, $"SemanticTableId={request.SemanticTableId} 不存在。");

        if (await _store.ExistsAsync(request.SemanticTableId, request.ColumnName, excludeId: null, cancellationToken).ConfigureAwait(false))
        {
            throw new SemanticDuplicateException($"该语义表下已存在列 {request.ColumnName}。");
        }

        var entity = new SemanticColumn
        {
            SemanticTableId = request.SemanticTableId,
            ColumnName = request.ColumnName.Trim(),
            DisplayName = request.DisplayName.Trim(),
            DbTypeName = request.DbTypeName.Trim(),
            SemanticType = request.SemanticType,
            IsSensitive = request.IsSensitive,
            Description = request.Description,
            CreatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime,
            CreatedByUserId = operatorUserId,
        };

        await _store.AddAsync(entity, cancellationToken).ConfigureAwait(false);
        await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("SemanticColumn created: id={ColumnId} by user={UserId}.", entity.Id, operatorUserId);
        return ToResponse(entity);
    }

    public async Task<SemanticColumnResponse> UpdateAsync(long id, SemanticColumnRequest request, long operatorUserId, CancellationToken cancellationToken)
    {
        Validate(request, includeRowVersion: true);

        var entity = await _store.FindByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new SemanticNotFoundException("语义列", id);

        _ = await _tableStore.FindByIdAsync(request.SemanticTableId, cancellationToken).ConfigureAwait(false)
            ?? throw new SemanticValidationException(SemanticErrorCodes.FieldInvalid, $"SemanticTableId={request.SemanticTableId} 不存在。");

        if (await _store.ExistsAsync(request.SemanticTableId, request.ColumnName, excludeId: id, cancellationToken).ConfigureAwait(false))
        {
            throw new SemanticDuplicateException($"该语义表下已存在列 {request.ColumnName}。");
        }

        entity.SemanticTableId = request.SemanticTableId;
        entity.ColumnName = request.ColumnName.Trim();
        entity.DisplayName = request.DisplayName.Trim();
        entity.DbTypeName = request.DbTypeName.Trim();
        entity.SemanticType = request.SemanticType;
        entity.IsSensitive = request.IsSensitive;
        entity.Description = request.Description;
        entity.UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        entity.UpdatedByUserId = operatorUserId;

        _store.MarkRowVersion(entity, request.RowVersion!);
        await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("SemanticColumn updated: id={ColumnId} by user={UserId}.", entity.Id, operatorUserId);
        return ToResponse(entity);
    }

    public async Task DeleteAsync(long id, long operatorUserId, CancellationToken cancellationToken)
    {
        var entity = await _store.FindByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new SemanticNotFoundException("语义列", id);

        _store.Remove(entity); // 语义列不软删（设计无 IsDeleted），物理删除。
        await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("SemanticColumn deleted: id={ColumnId} by user={UserId}.", id, operatorUserId);
    }

    private static void Validate(SemanticColumnRequest request, bool includeRowVersion)
    {
        SemanticGuards.Ensure(request.SemanticTableId > 0, "SemanticTableId 必填。");
        SemanticGuards.Ensure(!string.IsNullOrWhiteSpace(request.ColumnName), "ColumnName 必填。");
        SemanticGuards.Ensure(request.ColumnName.Trim().Length <= 128, "ColumnName 超长（上限 128）。");
        SemanticGuards.Ensure(!string.IsNullOrWhiteSpace(request.DisplayName), "DisplayName 必填。");
        SemanticGuards.Ensure(request.DisplayName.Trim().Length <= 128, "DisplayName 超长（上限 128）。");
        SemanticGuards.Ensure(!string.IsNullOrWhiteSpace(request.DbTypeName), "DbTypeName 必填。");
        SemanticGuards.Ensure(request.DbTypeName.Trim().Length <= 64, "DbTypeName 超长（上限 64）。");
        SemanticGuards.Ensure(Enum.IsDefined(request.SemanticType), "SemanticType 取值非法。");
        SemanticGuards.Ensure(request.Description is null || request.Description.Length <= 1024, "Description 超长（上限 1024）。");
        if (includeRowVersion)
        {
            SemanticGuards.Ensure(request.RowVersion is { Length: > 0 }, "PUT 必须携带并发令牌 RowVersion。");
        }
    }

    internal static SemanticColumnResponse ToResponse(SemanticColumn entity) => new(
        entity.Id,
        entity.SemanticTableId,
        entity.ColumnName,
        entity.DisplayName,
        entity.DbTypeName,
        entity.SemanticType,
        entity.IsSensitive,
        entity.Description,
        entity.RowVersion,
        entity.CreatedAtUtc,
        entity.UpdatedAtUtc);
}
