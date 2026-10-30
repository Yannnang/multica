using ChatBi.Application.Common;
using ChatBi.Application.Semantic;
using ChatBi.Domain.Entities;
using ChatBi.Domain.Enums;
using ChatBi.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ChatBi.Infrastructure.Repositories;

/// <summary>
/// 语义层 EF 仓储实现。约定：查询 AsNoTracking；分页显式 OrderBy(Id)（稳定排序）；
/// SaveChanges 统一翻译唯一键冲突 → 409 SEM_DUPLICATE、并发冲突 → 409 SEM_CONCURRENCY_CONFLICT。
/// </summary>
internal static class EfSaveChangesExtensions
{
    internal static async Task SaveSemanticChangesAsync(this ChatBiDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new SemanticConcurrencyException();
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // 预检查之外的并发插入撞唯一索引（软删行占用名等）→ 409。
            throw new SemanticDuplicateException("保存冲突：唯一性约束校验失败（可能已有同名/同定义记录）。");
        }
    }
}

/// <summary>数据源仓储（EF 实现；软删过滤器已在全局配置）。</summary>
public class EfDataSourceStore : IDataSourceStore
{
    private readonly ChatBiDbContext _db;

    public EfDataSourceStore(ChatBiDbContext db)
    {
        _db = db;
    }

    public Task<DataSource?> FindByIdAsync(long id, CancellationToken cancellationToken) =>
        _db.DataSources.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public Task<bool> NameExistsAsync(string name, long? excludeId, CancellationToken cancellationToken) =>
        _db.DataSources.AnyAsync(x => x.Name == name && (excludeId == null || x.Id != excludeId), cancellationToken);

    public async Task<PagedResult<DataSource>> GetPageAsync(PageRequest page, CancellationToken cancellationToken)
    {
        var query = _db.DataSources.AsNoTracking().OrderBy(x => x.Id);
        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await query.Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new PagedResult<DataSource>(items, page.Page, page.PageSize, total);
    }

    public async Task AddAsync(DataSource entity, CancellationToken cancellationToken) =>
        await _db.DataSources.AddAsync(entity, cancellationToken).ConfigureAwait(false);

    public void MarkRowVersion(DataSource entity, byte[] rowVersion) =>
        _db.Entry(entity).Property(x => x.RowVersion).OriginalValue = rowVersion;

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        _db.SaveSemanticChangesAsync(cancellationToken);
}

/// <summary>语义表仓储（EF 实现）。</summary>
public class EfSemanticTableStore : ISemanticTableStore
{
    private readonly ChatBiDbContext _db;

    public EfSemanticTableStore(ChatBiDbContext db)
    {
        _db = db;
    }

    public Task<SemanticTable?> FindByIdAsync(long id, CancellationToken cancellationToken) =>
        _db.SemanticTables.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public Task<bool> ExistsAsync(long dataSourceId, string schemaName, string tableName, long? excludeId, CancellationToken cancellationToken) =>
        _db.SemanticTables.AnyAsync(
            x => x.DataSourceId == dataSourceId && x.SchemaName == schemaName && x.TableName == tableName && (excludeId == null || x.Id != excludeId),
            cancellationToken);

    public async Task<PagedResult<SemanticTable>> GetPageAsync(PageRequest page, CancellationToken cancellationToken)
    {
        var query = _db.SemanticTables.AsNoTracking().OrderBy(x => x.Id);
        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await query.Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new PagedResult<SemanticTable>(items, page.Page, page.PageSize, total);
    }

    public async Task AddAsync(SemanticTable entity, CancellationToken cancellationToken) =>
        await _db.SemanticTables.AddAsync(entity, cancellationToken).ConfigureAwait(false);

    public void MarkRowVersion(SemanticTable entity, byte[] rowVersion) =>
        _db.Entry(entity).Property(x => x.RowVersion).OriginalValue = rowVersion;

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        _db.SaveSemanticChangesAsync(cancellationToken);
}

/// <summary>语义列仓储（EF 实现；不含软删，物理删除）。</summary>
public class EfSemanticColumnStore : ISemanticColumnStore
{
    private readonly ChatBiDbContext _db;

    public EfSemanticColumnStore(ChatBiDbContext db)
    {
        _db = db;
    }

    public Task<SemanticColumn?> FindByIdAsync(long id, CancellationToken cancellationToken) =>
        _db.SemanticColumns.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public Task<bool> ExistsAsync(long semanticTableId, string columnName, long? excludeId, CancellationToken cancellationToken) =>
        _db.SemanticColumns.AnyAsync(
            x => x.SemanticTableId == semanticTableId && x.ColumnName == columnName && (excludeId == null || x.Id != excludeId),
            cancellationToken);

    public async Task<PagedResult<SemanticColumn>> GetPageAsync(PageRequest page, CancellationToken cancellationToken)
    {
        // 列的列表查询按所属表过滤语义表的软删（JOIN 一次完成，无 N+1）。
        var query = from column in _db.SemanticColumns.AsNoTracking()
                    join table in _db.SemanticTables.AsNoTracking() on column.SemanticTableId equals table.Id
                    orderby column.Id
                    select column;
        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await query.Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new PagedResult<SemanticColumn>(items, page.Page, page.PageSize, total);
    }

    public async Task AddAsync(SemanticColumn entity, CancellationToken cancellationToken) =>
        await _db.SemanticColumns.AddAsync(entity, cancellationToken).ConfigureAwait(false);

    public void Remove(SemanticColumn entity) =>
        _db.SemanticColumns.Remove(entity);

    public void MarkRowVersion(SemanticColumn entity, byte[] rowVersion) =>
        _db.Entry(entity).Property(x => x.RowVersion).OriginalValue = rowVersion;

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        _db.SaveSemanticChangesAsync(cancellationToken);
}

/// <summary>指标仓储（EF 实现，含 MetricTables 关联维护）。</summary>
public class EfMetricStore : IMetricStore
{
    private readonly ChatBiDbContext _db;

    public EfMetricStore(ChatBiDbContext db)
    {
        _db = db;
    }

    public Task<Metric?> FindByIdAsync(long id, CancellationToken cancellationToken) =>
        _db.Metrics.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public Task<bool> CodeExistsAsync(string code, long? excludeId, CancellationToken cancellationToken) =>
        _db.Metrics.AnyAsync(x => x.Code == code && (excludeId == null || x.Id != excludeId), cancellationToken);

    public async Task<PagedResult<Metric>> GetPageAsync(PageRequest page, CancellationToken cancellationToken)
    {
        var query = _db.Metrics.AsNoTracking().OrderBy(x => x.Id);
        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await query.Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new PagedResult<Metric>(items, page.Page, page.PageSize, total);
    }

    public async Task AddAsync(Metric entity, CancellationToken cancellationToken) =>
        await _db.Metrics.AddAsync(entity, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<long>> GetTableIdsAsync(long metricId, CancellationToken cancellationToken) =>
        await _db.MetricTables
            .Where(x => x.MetricId == metricId)
            .OrderBy(x => x.SemanticTableId)
            .Select(x => x.SemanticTableId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyDictionary<long, IReadOnlyList<long>>> GetTableIdsBatchAsync(IReadOnlyList<long> metricIds, CancellationToken cancellationToken)
    {
        var rows = await _db.MetricTables
            .Where(x => metricIds.Contains(x.MetricId))
            .OrderBy(x => x.MetricId)
            .ThenBy(x => x.SemanticTableId)
            .Select(x => new { x.MetricId, x.SemanticTableId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var mapping = new Dictionary<long, IReadOnlyList<long>>();
        foreach (var row in rows)
        {
            if (!mapping.TryGetValue(row.MetricId, out var list))
            {
                list = new List<long>();
                mapping[row.MetricId] = list;
            }

            ((List<long>)list).Add(row.SemanticTableId);
        }

        return mapping;
    }

    public async Task SetTableIdsAsync(long metricId, IReadOnlyList<long> tableIds, CancellationToken cancellationToken)
    {
        var existing = await _db.MetricTables
            .Where(x => x.MetricId == metricId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var row in existing.Where(row => !tableIds.Contains(row.SemanticTableId)))
        {
            _db.MetricTables.Remove(row);
        }

        var existingTableIds = existing.Select(row => row.SemanticTableId).ToHashSet();
        foreach (var tableId in tableIds.Where(tableId => !existingTableIds.Contains(tableId)))
        {
            await _db.MetricTables.AddAsync(new MetricTable { MetricId = metricId, SemanticTableId = tableId }, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<HashSet<long>> GetExistingTableIdsAsync(IReadOnlyList<long> tableIds, CancellationToken cancellationToken)
    {
        if (tableIds.Count == 0)
        {
            return new HashSet<long>();
        }

        return (await _db.SemanticTables
            .AsNoTracking()
            .Where(x => tableIds.Contains(x.Id))
            .Select(x => x.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false)).ToHashSet();
    }

    public async Task<HashSet<long>> GetExistingColumnIdsAsync(IReadOnlyList<long> columnIds, CancellationToken cancellationToken)
    {
        if (columnIds.Count == 0)
        {
            return new HashSet<long>();
        }

        return (await _db.SemanticColumns
            .AsNoTracking()
            .Where(x => columnIds.Contains(x.Id))
            .Select(x => x.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false)).ToHashSet();
    }

    public Task<bool> UserExistsAsync(long userId, CancellationToken cancellationToken) =>
        _db.Users.AnyAsync(x => x.Id == userId, cancellationToken);

    public void MarkRowVersion(Metric entity, byte[] rowVersion) =>
        _db.Entry(entity).Property(x => x.RowVersion).OriginalValue = rowVersion;

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        _db.SaveSemanticChangesAsync(cancellationToken);
}

/// <summary>表关联仓储（EF 实现）。</summary>
public class EfSemanticRelationStore : ISemanticRelationStore
{
    private readonly ChatBiDbContext _db;

    public EfSemanticRelationStore(ChatBiDbContext db)
    {
        _db = db;
    }

    public Task<SemanticRelation?> FindByIdAsync(long id, CancellationToken cancellationToken) =>
        _db.SemanticRelations.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public Task<bool> ExistsAsync(long leftTableId, long rightTableId, long? excludeId, CancellationToken cancellationToken) =>
        _db.SemanticRelations.AnyAsync(
            x => x.LeftSemanticTableId == leftTableId && x.RightSemanticTableId == rightTableId && (excludeId == null || x.Id != excludeId),
            cancellationToken);

    public async Task<PagedResult<SemanticRelation>> GetPageAsync(PageRequest page, CancellationToken cancellationToken)
    {
        var query = _db.SemanticRelations.AsNoTracking().OrderBy(x => x.Id);
        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await query.Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new PagedResult<SemanticRelation>(items, page.Page, page.PageSize, total);
    }

    public async Task AddAsync(SemanticRelation entity, CancellationToken cancellationToken) =>
        await _db.SemanticRelations.AddAsync(entity, cancellationToken).ConfigureAwait(false);

    public void Remove(SemanticRelation entity) =>
        _db.SemanticRelations.Remove(entity);

    public async Task<HashSet<long>> GetExistingTableIdsAsync(IReadOnlyList<long> tableIds, CancellationToken cancellationToken)
    {
        if (tableIds.Count == 0)
        {
            return new HashSet<long>();
        }

        return (await _db.SemanticTables
            .AsNoTracking()
            .Where(x => tableIds.Contains(x.Id))
            .Select(x => x.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false)).ToHashSet();
    }

    public Task<int> CountColumnsInTableAsync(IReadOnlyList<long> columnIds, long tableId, CancellationToken cancellationToken) =>
        _db.SemanticColumns.CountAsync(x => columnIds.Contains(x.Id) && x.SemanticTableId == tableId, cancellationToken);

    public void MarkRowVersion(SemanticRelation entity, byte[] rowVersion) =>
        _db.Entry(entity).Property(x => x.RowVersion).OriginalValue = rowVersion;

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        _db.SaveSemanticChangesAsync(cancellationToken);
}

/// <summary>同义词仓储（EF 实现；多态目标存在性在单查询内完成）。</summary>
public class EfSynonymStore : ISynonymStore
{
    private readonly ChatBiDbContext _db;

    public EfSynonymStore(ChatBiDbContext db)
    {
        _db = db;
    }

    public Task<Synonym?> FindByIdAsync(long id, CancellationToken cancellationToken) =>
        _db.Synonyms.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public Task<bool> ExistsAsync(string term, SynonymTargetType targetType, long targetId, long? excludeId, CancellationToken cancellationToken) =>
        _db.Synonyms.AnyAsync(
            x => x.Term == term && x.TargetType == targetType && x.TargetId == targetId && (excludeId == null || x.Id != excludeId),
            cancellationToken);

    public async Task<PagedResult<Synonym>> GetPageAsync(PageRequest page, CancellationToken cancellationToken)
    {
        var query = _db.Synonyms.AsNoTracking().OrderBy(x => x.Id);
        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await query.Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new PagedResult<Synonym>(items, page.Page, page.PageSize, total);
    }

    public async Task AddAsync(Synonym entity, CancellationToken cancellationToken) =>
        await _db.Synonyms.AddAsync(entity, cancellationToken).ConfigureAwait(false);

    public void Remove(Synonym entity) =>
        _db.Synonyms.Remove(entity);

    public Task<bool> TargetExistsAsync(SynonymTargetType targetType, long targetId, CancellationToken cancellationToken) =>
        targetType switch
        {
            SynonymTargetType.Metric => _db.Metrics.AnyAsync(x => x.Id == targetId, cancellationToken),
            SynonymTargetType.SemanticTable => _db.SemanticTables.AnyAsync(x => x.Id == targetId, cancellationToken),
            SynonymTargetType.SemanticColumn => _db.SemanticColumns.AnyAsync(x => x.Id == targetId, cancellationToken),
            _ => Task.FromResult(false),
        };

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        _db.SaveSemanticChangesAsync(cancellationToken);
}
