using ChatBi.Application.Common;
using ChatBi.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace ChatBi.Application.Semantic;

/// <summary>
/// 指标口径管理用例（含 MetricTables 关联的整体替换）。
/// Expression 做结构化白名单校验（禁裸 SQL 片段），见 <see cref="StructureValidators.ValidateMetricExpression"/>。
/// </summary>
public class MetricService
{
    private readonly IMetricStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MetricService> _logger;

    public MetricService(IMetricStore store, TimeProvider timeProvider, ILogger<MetricService> logger)
    {
        _store = store;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<PagedResult<MetricResponse>> ListAsync(int? page, int? pageSize, CancellationToken cancellationToken)
    {
        var result = await _store.GetPageAsync(PageRequest.From(page, pageSize), cancellationToken).ConfigureAwait(false);
        var tableIdsByMetric = await LoadTableIdsAsync(result.Items.Select(x => x.Id).ToList(), cancellationToken).ConfigureAwait(false);
        return new PagedResult<MetricResponse>(
            result.Items.Select(x => ToResponse(x, tableIdsByMetric)).ToList(), result.Page, result.PageSize, result.Total);
    }

    public async Task<MetricResponse> GetByIdAsync(long id, CancellationToken cancellationToken)
    {
        var entity = await _store.FindByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new SemanticNotFoundException("指标", id);
        var tableIds = await _store.GetTableIdsAsync(id, cancellationToken).ConfigureAwait(false);
        return ToResponse(entity, new Dictionary<long, IReadOnlyList<long>> { [id] = tableIds });
    }

    public async Task<MetricResponse> CreateAsync(MetricRequest request, long operatorUserId, CancellationToken cancellationToken)
    {
        Validate(request, includeRowVersion: false);
        StructureValidators.ValidateMetricExpression(request.Expression);
        var tableIds = await ValidateReferencesAsync(request, cancellationToken).ConfigureAwait(false);

        if (await _store.CodeExistsAsync(request.Code, excludeId: null, cancellationToken).ConfigureAwait(false))
        {
            throw new SemanticDuplicateException($"指标 Code“{request.Code}”已存在。");
        }

        var entity = new Metric
        {
            Code = request.Code.Trim(),
            DisplayName = request.DisplayName.Trim(),
            Description = request.Description,
            Expression = request.Expression.Trim(),
            AggregationType = request.AggregationType,
            Unit = request.Unit,
            DefaultTimeColumnId = request.DefaultTimeColumnId,
            IsEnabled = request.IsEnabled,
            OwnerUserId = request.OwnerUserId,
            CreatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime,
            CreatedByUserId = operatorUserId,
        };

        await _store.AddAsync(entity, cancellationToken).ConfigureAwait(false);
        await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false); // 先落库取得自增 Id，再补 MetricTables 关联。
        await _store.SetTableIdsAsync(entity.Id, tableIds, cancellationToken).ConfigureAwait(false);
        await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Metric created: id={MetricId} by user={UserId}.", entity.Id, operatorUserId);
        return ToResponse(entity, new Dictionary<long, IReadOnlyList<long>> { [entity.Id] = tableIds });
    }

    public async Task<MetricResponse> UpdateAsync(long id, MetricRequest request, long operatorUserId, CancellationToken cancellationToken)
    {
        Validate(request, includeRowVersion: true);
        StructureValidators.ValidateMetricExpression(request.Expression);
        var tableIds = await ValidateReferencesAsync(request, cancellationToken).ConfigureAwait(false);

        var entity = await _store.FindByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new SemanticNotFoundException("指标", id);

        if (await _store.CodeExistsAsync(request.Code, excludeId: id, cancellationToken).ConfigureAwait(false))
        {
            throw new SemanticDuplicateException($"指标 Code“{request.Code}”已存在。");
        }

        entity.Code = request.Code.Trim();
        entity.DisplayName = request.DisplayName.Trim();
        entity.Description = request.Description;
        entity.Expression = request.Expression.Trim();
        entity.AggregationType = request.AggregationType;
        entity.Unit = request.Unit;
        entity.DefaultTimeColumnId = request.DefaultTimeColumnId;
        entity.IsEnabled = request.IsEnabled;
        entity.OwnerUserId = request.OwnerUserId;
        entity.UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        entity.UpdatedByUserId = operatorUserId;

        await _store.SetTableIdsAsync(id, tableIds, cancellationToken).ConfigureAwait(false);
        _store.MarkRowVersion(entity, request.RowVersion!);
        await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Metric updated: id={MetricId} by user={UserId}.", entity.Id, operatorUserId);
        return ToResponse(entity, new Dictionary<long, IReadOnlyList<long>> { [entity.Id] = tableIds });
    }

    public async Task DeleteAsync(long id, long operatorUserId, CancellationToken cancellationToken)
    {
        var entity = await _store.FindByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new SemanticNotFoundException("指标", id);

        entity.IsDeleted = true; // 软删；MetricTables 关联行保留（指标不再出现在查询中）。
        entity.UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        entity.UpdatedByUserId = operatorUserId;

        await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Metric soft-deleted: id={MetricId} by user={UserId}.", entity.Id, operatorUserId);
    }

    private async Task<IReadOnlyList<long>> ValidateReferencesAsync(MetricRequest request, CancellationToken cancellationToken)
    {
        var tableIds = (request.TableIds ?? Array.Empty<long>())
            .Where(x => x > 0)
            .Distinct()
            .ToList();

        var existingTables = await _store.GetExistingTableIdsAsync(tableIds, cancellationToken).ConfigureAwait(false);
        var missingTables = tableIds.Where(x => !existingTables.Contains(x)).ToList();
        SemanticGuards.Ensure(missingTables.Count == 0, $"TableIds 引用了不存在的语义表：{string.Join(',', missingTables)}。");

        if (request.DefaultTimeColumnId is { } columnId)
        {
            var existingColumns = await _store.GetExistingColumnIdsAsync(new[] { columnId }, cancellationToken).ConfigureAwait(false);
            SemanticGuards.Ensure(existingColumns.Contains(columnId), $"DefaultTimeColumnId={columnId} 不存在。");
        }

        if (request.OwnerUserId is { } ownerId)
        {
            SemanticGuards.Ensure(await _store.UserExistsAsync(ownerId, cancellationToken).ConfigureAwait(false), $"OwnerUserId={ownerId} 不存在。");
        }

        return tableIds;
    }

    /// <summary>整页指标的 TableIds 一次批量加载（列表页无 N+1）。</summary>
    private async Task<Dictionary<long, IReadOnlyList<long>>> LoadTableIdsAsync(IReadOnlyList<long> metricIds, CancellationToken cancellationToken)
    {
        var mapping = new Dictionary<long, IReadOnlyList<long>>();
        foreach (var metricId in metricIds)
        {
            mapping[metricId] = Array.Empty<long>();
        }

        if (metricIds.Count > 0)
        {
            foreach (var (metricId, tableIds) in await _store.GetTableIdsBatchAsync(metricIds, cancellationToken).ConfigureAwait(false))
            {
                mapping[metricId] = tableIds;
            }
        }

        return mapping;
    }

    private static void Validate(MetricRequest request, bool includeRowVersion)
    {
        SemanticGuards.Ensure(!string.IsNullOrWhiteSpace(request.Code), "Code 必填。");
        SemanticGuards.Ensure(request.Code.Trim().Length <= 64, "Code 超长（上限 64）。");
        SemanticGuards.Ensure(!string.IsNullOrWhiteSpace(request.DisplayName), "DisplayName 必填。");
        SemanticGuards.Ensure(request.DisplayName.Trim().Length <= 128, "DisplayName 超长（上限 128）。");
        SemanticGuards.Ensure(request.Description is null || request.Description.Length <= 1024, "Description 超长（上限 1024）。");
        SemanticGuards.Ensure(Enum.IsDefined(request.AggregationType), "AggregationType 取值非法。");
        SemanticGuards.Ensure(request.Unit is null || request.Unit.Length <= 32, "Unit 超长（上限 32）。");
        if (includeRowVersion)
        {
            SemanticGuards.Ensure(request.RowVersion is { Length: > 0 }, "PUT 必须携带并发令牌 RowVersion。");
        }
    }

    internal static MetricResponse ToResponse(Metric entity, IReadOnlyDictionary<long, IReadOnlyList<long>> tableIdsByMetric) => new(
        entity.Id,
        entity.Code,
        entity.DisplayName,
        entity.Description,
        entity.Expression,
        entity.AggregationType,
        entity.Unit,
        entity.DefaultTimeColumnId,
        entity.IsEnabled,
        entity.OwnerUserId,
        tableIdsByMetric.GetValueOrDefault(entity.Id) ?? Array.Empty<long>(),
        entity.RowVersion,
        entity.CreatedAtUtc,
        entity.UpdatedAtUtc);
}
