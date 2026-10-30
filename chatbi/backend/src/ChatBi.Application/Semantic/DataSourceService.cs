using ChatBi.Application.Common;
using ChatBi.Domain.Entities;
using ChatBi.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace ChatBi.Application.Semantic;

/// <summary>
/// 数据源管理用例。红线：凭证只收引用名（CredentialRef），疑似明文连接串/密码 → 422 拒绝；
/// 日志只落资源 id，不落 CredentialRef 或请求体内容。
/// 权限在端点层：读 semantic:read（所有已认证角色）、写 datasource:manage（仅 SysAdmin）。
/// </summary>
public class DataSourceService
{
    private readonly IDataSourceStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DataSourceService> _logger;

    public DataSourceService(IDataSourceStore store, TimeProvider timeProvider, ILogger<DataSourceService> logger)
    {
        _store = store;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<PagedResult<DataSourceResponse>> ListAsync(int? page, int? pageSize, CancellationToken cancellationToken)
    {
        var result = await _store.GetPageAsync(PageRequest.From(page, pageSize), cancellationToken).ConfigureAwait(false);
        return new PagedResult<DataSourceResponse>(
            result.Items.Select(ToResponse).ToList(), result.Page, result.PageSize, result.Total);
    }

    public async Task<DataSourceResponse> GetByIdAsync(long id, CancellationToken cancellationToken)
    {
        var entity = await _store.FindByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new SemanticNotFoundException("数据源", id);
        return ToResponse(entity);
    }

    public async Task<DataSourceResponse> CreateAsync(DataSourceRequest request, long operatorUserId, CancellationToken cancellationToken)
    {
        Validate(request, includeRowVersion: false);
        StructureValidators.ValidateCredentialRef(request.CredentialRef);

        if (await _store.NameExistsAsync(request.Name, excludeId: null, cancellationToken).ConfigureAwait(false))
        {
            throw new SemanticDuplicateException($"数据源名称“{request.Name}”已存在。");
        }

        var entity = new DataSource
        {
            Name = request.Name.Trim(),
            DbType = request.DbType,
            Host = request.Host.Trim(),
            Port = request.Port,
            DatabaseName = request.DatabaseName.Trim(),
            CredentialRef = request.CredentialRef,
            QueryTimeoutSeconds = request.QueryTimeoutSeconds,
            MaxRowsPerQuery = request.MaxRowsPerQuery,
            Status = request.Status,
            CreatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime,
            CreatedByUserId = operatorUserId,
        };

        await _store.AddAsync(entity, cancellationToken).ConfigureAwait(false);
        await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // 脱敏：日志只记录资源 id 与操作者；CredentialRef 及请求体内容一律不落日志。
        _logger.LogInformation("DataSource created: id={DataSourceId} by user={UserId}.", entity.Id, operatorUserId);
        return ToResponse(entity);
    }

    public async Task<DataSourceResponse> UpdateAsync(long id, DataSourceRequest request, long operatorUserId, CancellationToken cancellationToken)
    {
        Validate(request, includeRowVersion: true);
        StructureValidators.ValidateCredentialRef(request.CredentialRef);

        var entity = await _store.FindByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new SemanticNotFoundException("数据源", id);

        if (await _store.NameExistsAsync(request.Name, excludeId: id, cancellationToken).ConfigureAwait(false))
        {
            throw new SemanticDuplicateException($"数据源名称“{request.Name}”已存在。");
        }

        entity.Name = request.Name.Trim();
        entity.DbType = request.DbType;
        entity.Host = request.Host.Trim();
        entity.Port = request.Port;
        entity.DatabaseName = request.DatabaseName.Trim();
        entity.CredentialRef = request.CredentialRef;
        entity.QueryTimeoutSeconds = request.QueryTimeoutSeconds;
        entity.MaxRowsPerQuery = request.MaxRowsPerQuery;
        entity.Status = request.Status;
        entity.UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        entity.UpdatedByUserId = operatorUserId;

        _store.MarkRowVersion(entity, request.RowVersion!);
        await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("DataSource updated: id={DataSourceId} by user={UserId}.", entity.Id, operatorUserId);
        return ToResponse(entity);
    }

    public async Task DeleteAsync(long id, long operatorUserId, CancellationToken cancellationToken)
    {
        var entity = await _store.FindByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new SemanticNotFoundException("数据源", id);

        entity.IsDeleted = true; // 软删：记录保留，但不再出现在本 API 与 schema linking。
        entity.UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        entity.UpdatedByUserId = operatorUserId;

        await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("DataSource soft-deleted: id={DataSourceId} by user={UserId}.", entity.Id, operatorUserId);
    }

    private static void Validate(DataSourceRequest request, bool includeRowVersion)
    {
        SemanticGuards.Ensure(!string.IsNullOrWhiteSpace(request.Name), "Name 必填。");
        SemanticGuards.Ensure(request.Name.Trim().Length <= 128, "Name 超长（上限 128）。");
        SemanticGuards.Ensure(Enum.IsDefined(request.DbType), "DbType 取值非法。");
        SemanticGuards.Ensure(!string.IsNullOrWhiteSpace(request.Host), "Host 必填。");
        SemanticGuards.Ensure(request.Host.Trim().Length <= 256, "Host 超长（上限 256）。");
        SemanticGuards.Ensure(request.Port is >= 1 and <= 65535, "Port 取值 1~65535。");
        SemanticGuards.Ensure(!string.IsNullOrWhiteSpace(request.DatabaseName), "DatabaseName 必填。");
        SemanticGuards.Ensure(request.DatabaseName.Trim().Length <= 128, "DatabaseName 超长（上限 128）。");
        SemanticGuards.Ensure(request.QueryTimeoutSeconds is >= 1 and <= 600, "QueryTimeoutSeconds 取值 1~600（默认 30）。");
        SemanticGuards.Ensure(request.MaxRowsPerQuery is >= 1 and <= 1_000_000, "MaxRowsPerQuery 取值 1~1000000（默认 5000）。");
        SemanticGuards.Ensure(Enum.IsDefined(request.Status), "Status 取值非法。");
        if (includeRowVersion)
        {
            SemanticGuards.Ensure(request.RowVersion is { Length: > 0 }, "PUT 必须携带并发令牌 RowVersion。");
        }
    }

    internal static DataSourceResponse ToResponse(DataSource entity) => new(
        entity.Id,
        entity.Name,
        entity.DbType,
        entity.Host,
        entity.Port,
        entity.DatabaseName,
        entity.CredentialRef,
        entity.QueryTimeoutSeconds,
        entity.MaxRowsPerQuery,
        entity.Status,
        entity.RowVersion,
        entity.CreatedAtUtc,
        entity.UpdatedAtUtc);
}
