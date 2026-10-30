using ChatBi.Application.Common;
using ChatBi.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace ChatBi.Application.Semantic;

/// <summary>
/// 表关联管理用例。OnConditionsJson 只收结构化列 id 对（白名单登记的 SemanticColumns.Id），
/// 并校验列归属两侧表——执行期由 S4 映射物理列名拼 JOIN，绝不收列名文本。
/// </summary>
public class SemanticRelationService
{
    private readonly ISemanticRelationStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SemanticRelationService> _logger;

    public SemanticRelationService(ISemanticRelationStore store, TimeProvider timeProvider, ILogger<SemanticRelationService> logger)
    {
        _store = store;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<PagedResult<SemanticRelationResponse>> ListAsync(int? page, int? pageSize, CancellationToken cancellationToken)
    {
        var result = await _store.GetPageAsync(PageRequest.From(page, pageSize), cancellationToken).ConfigureAwait(false);
        return new PagedResult<SemanticRelationResponse>(
            result.Items.Select(ToResponse).ToList(), result.Page, result.PageSize, result.Total);
    }

    public async Task<SemanticRelationResponse> GetByIdAsync(long id, CancellationToken cancellationToken)
    {
        var entity = await _store.FindByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new SemanticNotFoundException("表关联", id);
        return ToResponse(entity);
    }

    public async Task<SemanticRelationResponse> CreateAsync(SemanticRelationRequest request, long operatorUserId, CancellationToken cancellationToken)
    {
        var (conditions, canonicalJson) = await ValidateAsync(request, includeRowVersion: false, cancellationToken).ConfigureAwait(false);

        if (await _store.ExistsAsync(request.LeftSemanticTableId, request.RightSemanticTableId, excludeId: null, cancellationToken).ConfigureAwait(false))
        {
            throw new SemanticDuplicateException("这两张表之间已存在关联定义。");
        }

        var entity = new SemanticRelation
        {
            LeftSemanticTableId = request.LeftSemanticTableId,
            RightSemanticTableId = request.RightSemanticTableId,
            JoinType = request.JoinType,
            OnConditionsJson = canonicalJson,
            CreatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime,
            CreatedByUserId = operatorUserId,
        };

        await _store.AddAsync(entity, cancellationToken).ConfigureAwait(false);
        await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("SemanticRelation created: id={RelationId} by user={UserId}.", entity.Id, operatorUserId);
        return ToResponse(entity);
    }

    public async Task<SemanticRelationResponse> UpdateAsync(long id, SemanticRelationRequest request, long operatorUserId, CancellationToken cancellationToken)
    {
        var (conditions, canonicalJson) = await ValidateAsync(request, includeRowVersion: true, cancellationToken).ConfigureAwait(false);

        var entity = await _store.FindByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new SemanticNotFoundException("表关联", id);

        if (await _store.ExistsAsync(request.LeftSemanticTableId, request.RightSemanticTableId, excludeId: id, cancellationToken).ConfigureAwait(false))
        {
            throw new SemanticDuplicateException("这两张表之间已存在关联定义。");
        }

        entity.LeftSemanticTableId = request.LeftSemanticTableId;
        entity.RightSemanticTableId = request.RightSemanticTableId;
        entity.JoinType = request.JoinType;
        entity.OnConditionsJson = canonicalJson;
        entity.UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        entity.UpdatedByUserId = operatorUserId;

        _store.MarkRowVersion(entity, request.RowVersion!);
        await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("SemanticRelation updated: id={RelationId} by user={UserId}.", entity.Id, operatorUserId);
        return ToResponse(entity);
    }

    public async Task DeleteAsync(long id, long operatorUserId, CancellationToken cancellationToken)
    {
        var entity = await _store.FindByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new SemanticNotFoundException("表关联", id);

        _store.Remove(entity); // 关联定义不软删（设计无 IsDeleted），物理删除。
        await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("SemanticRelation deleted: id={RelationId} by user={UserId}.", id, operatorUserId);
    }

    /// <summary>结构校验 + 引用校验（两侧表存在、条件列归属对应表），返回条件与规范化 JSON。</summary>
    private async Task<(IReadOnlyList<StructureValidators.JoinCondition> Conditions, string CanonicalJson)> ValidateAsync(
        SemanticRelationRequest request,
        bool includeRowVersion,
        CancellationToken cancellationToken)
    {
        SemanticGuards.Ensure(request.LeftSemanticTableId > 0, "LeftSemanticTableId 必填。");
        SemanticGuards.Ensure(request.RightSemanticTableId > 0, "RightSemanticTableId 必填。");
        SemanticGuards.Ensure(request.LeftSemanticTableId != request.RightSemanticTableId, "左右表不能相同。");
        SemanticGuards.Ensure(Enum.IsDefined(request.JoinType), "JoinType 取值非法。");
        if (includeRowVersion)
        {
            SemanticGuards.Ensure(request.RowVersion is { Length: > 0 }, "PUT 必须携带并发令牌 RowVersion。");
        }

        var conditions = StructureValidators.ParseJoinConditionsJson(request.OnConditionsJson);

        var existingTables = await _store.GetExistingTableIdsAsync(
            new[] { request.LeftSemanticTableId, request.RightSemanticTableId }, cancellationToken).ConfigureAwait(false);
        SemanticGuards.Ensure(existingTables.Contains(request.LeftSemanticTableId), $"LeftSemanticTableId={request.LeftSemanticTableId} 不存在。");
        SemanticGuards.Ensure(existingTables.Contains(request.RightSemanticTableId), $"RightSemanticTableId={request.RightSemanticTableId} 不存在。");

        var leftColumnIds = conditions.Select(x => x.LeftColumnId).Distinct().ToList();
        var rightColumnIds = conditions.Select(x => x.RightColumnId).Distinct().ToList();
        var leftMatched = await _store.CountColumnsInTableAsync(leftColumnIds, request.LeftSemanticTableId, cancellationToken).ConfigureAwait(false);
        if (leftMatched != leftColumnIds.Count)
        {
            throw new SemanticValidationException(
                SemanticErrorCodes.ConditionsInvalid, "OnConditionsJson 存在不属于左表的 leftColumnId。");
        }

        var rightMatched = await _store.CountColumnsInTableAsync(rightColumnIds, request.RightSemanticTableId, cancellationToken).ConfigureAwait(false);
        if (rightMatched != rightColumnIds.Count)
        {
            throw new SemanticValidationException(
                SemanticErrorCodes.ConditionsInvalid, "OnConditionsJson 存在不属于右表的 rightColumnId。");
        }

        var canonicalJson = System.Text.Json.JsonSerializer.Serialize(conditions.Select(x => new
        {
            leftColumnId = x.LeftColumnId,
            rightColumnId = x.RightColumnId,
        }));

        return (conditions, canonicalJson);
    }

    internal static SemanticRelationResponse ToResponse(SemanticRelation entity) => new(
        entity.Id,
        entity.LeftSemanticTableId,
        entity.RightSemanticTableId,
        entity.JoinType,
        entity.OnConditionsJson,
        entity.RowVersion,
        entity.CreatedAtUtc,
        entity.UpdatedAtUtc);
}

/// <summary>同义词管理用例（多态目标：Metric/SemanticTable/SemanticColumn；无并发令牌，PUT 幂等覆盖）。</summary>
public class SynonymService
{
    private readonly ISynonymStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SynonymService> _logger;

    public SynonymService(ISynonymStore store, TimeProvider timeProvider, ILogger<SynonymService> logger)
    {
        _store = store;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<PagedResult<SynonymResponse>> ListAsync(int? page, int? pageSize, CancellationToken cancellationToken)
    {
        var result = await _store.GetPageAsync(PageRequest.From(page, pageSize), cancellationToken).ConfigureAwait(false);
        return new PagedResult<SynonymResponse>(
            result.Items.Select(ToResponse).ToList(), result.Page, result.PageSize, result.Total);
    }

    public async Task<SynonymResponse> GetByIdAsync(long id, CancellationToken cancellationToken)
    {
        var entity = await _store.FindByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new SemanticNotFoundException("同义词", id);
        return ToResponse(entity);
    }

    public async Task<SynonymResponse> CreateAsync(SynonymRequest request, long operatorUserId, CancellationToken cancellationToken)
    {
        await ValidateAsync(request, cancellationToken).ConfigureAwait(false);

        if (await _store.ExistsAsync(request.Term, request.TargetType, request.TargetId, excludeId: null, cancellationToken).ConfigureAwait(false))
        {
            throw new SemanticDuplicateException($"同义词“{request.Term}”已指向该目标。");
        }

        var entity = new Synonym
        {
            Term = request.Term.Trim(),
            TargetType = request.TargetType,
            TargetId = request.TargetId,
            CreatedByUserId = operatorUserId,
            CreatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime,
        };

        await _store.AddAsync(entity, cancellationToken).ConfigureAwait(false);
        await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Synonym created: id={SynonymId} by user={UserId}.", entity.Id, operatorUserId);
        return ToResponse(entity);
    }

    public async Task<SynonymResponse> UpdateAsync(long id, SynonymRequest request, long operatorUserId, CancellationToken cancellationToken)
    {
        await ValidateAsync(request, cancellationToken).ConfigureAwait(false);

        var entity = await _store.FindByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new SemanticNotFoundException("同义词", id);

        if (await _store.ExistsAsync(request.Term, request.TargetType, request.TargetId, excludeId: id, cancellationToken).ConfigureAwait(false))
        {
            throw new SemanticDuplicateException($"同义词“{request.Term}”已指向该目标。");
        }

        entity.Term = request.Term.Trim();
        entity.TargetType = request.TargetType;
        entity.TargetId = request.TargetId;

        await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Synonym updated: id={SynonymId} by user={UserId}.", entity.Id, operatorUserId);
        return ToResponse(entity);
    }

    public async Task DeleteAsync(long id, long operatorUserId, CancellationToken cancellationToken)
    {
        var entity = await _store.FindByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new SemanticNotFoundException("同义词", id);

        _store.Remove(entity); // 同义词不软删（设计无 IsDeleted），物理删除。
        await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Synonym deleted: id={SynonymId} by user={UserId}.", id, operatorUserId);
    }

    private async Task ValidateAsync(SynonymRequest request, CancellationToken cancellationToken)
    {
        SemanticGuards.Ensure(!string.IsNullOrWhiteSpace(request.Term), "Term 必填。");
        SemanticGuards.Ensure(request.Term.Trim().Length <= 128, "Term 超长（上限 128）。");
        SemanticGuards.Ensure(Enum.IsDefined(request.TargetType), "TargetType 取值非法。");
        SemanticGuards.Ensure(request.TargetId > 0, "TargetId 必填。");

        if (!await _store.TargetExistsAsync(request.TargetType, request.TargetId, cancellationToken).ConfigureAwait(false))
        {
            throw new SemanticValidationException(
                SemanticErrorCodes.TargetNotFound, $"TargetType={request.TargetType} 下 TargetId={request.TargetId} 不存在。");
        }
    }

    internal static SynonymResponse ToResponse(Synonym entity) => new(
        entity.Id,
        entity.Term,
        entity.TargetType,
        entity.TargetId,
        entity.CreatedByUserId,
        entity.CreatedAtUtc);
}
