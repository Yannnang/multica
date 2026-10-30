using ChatBi.Application.Common;
using ChatBi.Domain.Entities;
using ChatBi.Domain.Enums;

namespace ChatBi.Application.Semantic;

/// <summary>
/// 语义层仓储接口（Application 定义，Infrastructure 以 EF 实现）。
/// 约定：查询不带跟踪；分页显式 OrderBy(Id)（稳定排序）；软删由 EF 全局过滤器承担。
/// </summary>
public interface IDataSourceStore
{
    Task<DataSource?> FindByIdAsync(long id, CancellationToken cancellationToken);
    Task<bool> NameExistsAsync(string name, long? excludeId, CancellationToken cancellationToken);
    Task<PagedResult<DataSource>> GetPageAsync(PageRequest page, CancellationToken cancellationToken);
    Task AddAsync(DataSource entity, CancellationToken cancellationToken);
    /// <summary>把客户端 RowVersion 注入为并发令牌原值（SQL Server rowversion WHERE 子句依据）。</summary>
    void MarkRowVersion(DataSource entity, byte[] rowVersion);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public interface ISemanticTableStore
{
    Task<SemanticTable?> FindByIdAsync(long id, CancellationToken cancellationToken);
    Task<bool> ExistsAsync(long dataSourceId, string schemaName, string tableName, long? excludeId, CancellationToken cancellationToken);
    Task<PagedResult<SemanticTable>> GetPageAsync(PageRequest page, CancellationToken cancellationToken);
    Task AddAsync(SemanticTable entity, CancellationToken cancellationToken);
    void MarkRowVersion(SemanticTable entity, byte[] rowVersion);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public interface ISemanticColumnStore
{
    Task<SemanticColumn?> FindByIdAsync(long id, CancellationToken cancellationToken);
    Task<bool> ExistsAsync(long semanticTableId, string columnName, long? excludeId, CancellationToken cancellationToken);
    Task<PagedResult<SemanticColumn>> GetPageAsync(PageRequest page, CancellationToken cancellationToken);
    Task AddAsync(SemanticColumn entity, CancellationToken cancellationToken);
    /// <summary>语义列不软删，物理删除。</summary>
    void Remove(SemanticColumn entity);
    void MarkRowVersion(SemanticColumn entity, byte[] rowVersion);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public interface IMetricStore
{
    Task<Metric?> FindByIdAsync(long id, CancellationToken cancellationToken);
    Task<bool> CodeExistsAsync(string code, long? excludeId, CancellationToken cancellationToken);
    Task<PagedResult<Metric>> GetPageAsync(PageRequest page, CancellationToken cancellationToken);
    Task AddAsync(Metric entity, CancellationToken cancellationToken);
    Task<IReadOnlyList<long>> GetTableIdsAsync(long metricId, CancellationToken cancellationToken);
    /// <summary>批量取一组指标的表关联（列表页一次查询，避免 N+1）。</summary>
    Task<IReadOnlyDictionary<long, IReadOnlyList<long>>> GetTableIdsBatchAsync(IReadOnlyList<long> metricIds, CancellationToken cancellationToken);
    /// <summary>整体替换 MetricTables 关联（本变更随 SaveChangesAsync 一次提交）。</summary>
    Task SetTableIdsAsync(long metricId, IReadOnlyList<long> tableIds, CancellationToken cancellationToken);
    /// <summary>批量取存在的表 id（校验 TableIds 引用）。</summary>
    Task<HashSet<long>> GetExistingTableIdsAsync(IReadOnlyList<long> tableIds, CancellationToken cancellationToken);
    /// <summary>批量取存在的列 id（校验 DefaultTimeColumnId 引用）。</summary>
    Task<HashSet<long>> GetExistingColumnIdsAsync(IReadOnlyList<long> columnIds, CancellationToken cancellationToken);
    Task<bool> UserExistsAsync(long userId, CancellationToken cancellationToken);
    void MarkRowVersion(Metric entity, byte[] rowVersion);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public interface ISemanticRelationStore
{
    Task<SemanticRelation?> FindByIdAsync(long id, CancellationToken cancellationToken);
    Task<bool> ExistsAsync(long leftTableId, long rightTableId, long? excludeId, CancellationToken cancellationToken);
    Task<PagedResult<SemanticRelation>> GetPageAsync(PageRequest page, CancellationToken cancellationToken);
    Task AddAsync(SemanticRelation entity, CancellationToken cancellationToken);
    /// <summary>关联定义不软删，物理删除。</summary>
    void Remove(SemanticRelation entity);
    Task<HashSet<long>> GetExistingTableIdsAsync(IReadOnlyList<long> tableIds, CancellationToken cancellationToken);
    /// <summary>返回 columnIds 中属于 tableId 的数量（校验 JOIN 条件列归属）。</summary>
    Task<int> CountColumnsInTableAsync(IReadOnlyList<long> columnIds, long tableId, CancellationToken cancellationToken);
    void MarkRowVersion(SemanticRelation entity, byte[] rowVersion);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public interface ISynonymStore
{
    Task<Synonym?> FindByIdAsync(long id, CancellationToken cancellationToken);
    Task<bool> ExistsAsync(string term, SynonymTargetType targetType, long targetId, long? excludeId, CancellationToken cancellationToken);
    Task<PagedResult<Synonym>> GetPageAsync(PageRequest page, CancellationToken cancellationToken);
    Task AddAsync(Synonym entity, CancellationToken cancellationToken);
    /// <summary>同义词不软删，物理删除。</summary>
    void Remove(Synonym entity);
    /// <summary>多态目标存在性（Metric/SemanticTable/SemanticColumn，仅计未软删行）。</summary>
    Task<bool> TargetExistsAsync(SynonymTargetType targetType, long targetId, CancellationToken cancellationToken);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
