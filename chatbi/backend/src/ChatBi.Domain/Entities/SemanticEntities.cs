using ChatBi.Domain.Common;
using ChatBi.Domain.Enums;

namespace ChatBi.Domain.Entities;

/// <summary>数据源。凭证只存引用名（环境变量/Key Vault），库内不落明文连接串。</summary>
public class DataSource : SoftDeletableConcurrentEntity
{
    /// <summary>UQ。</summary>
    public string Name { get; set; } = string.Empty;

    public DataSourceType DbType { get; set; }

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; }

    public string DatabaseName { get; set; } = string.Empty;

    /// <summary>环境变量/Key Vault 引用名（如 BIZ_DB_READONLY_CONNECTION）。</summary>
    public string CredentialRef { get; set; } = string.Empty;

    /// <summary>执行超时上限，默认 30s。</summary>
    public int QueryTimeoutSeconds { get; set; } = 30;

    /// <summary>单次返回行数上限，默认 5000。</summary>
    public int MaxRowsPerQuery { get; set; } = 5000;

    public DataSourceStatus Status { get; set; } = DataSourceStatus.Enabled;
}

/// <summary>语义表。IsEnabled=0 即不参与 schema linking。</summary>
public class SemanticTable : SoftDeletableConcurrentEntity
{
    /// <summary>FK→DataSources，Restrict。</summary>
    public long DataSourceId { get; set; }

    public string SchemaName { get; set; } = "dbo";

    public string TableName { get; set; } = string.Empty;

    /// <summary>业务名（给 LLM 与人）。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>业务口径描述，进 prompt。</summary>
    public string? Description { get; set; }

    public bool IsEnabled { get; set; } = true;
}

/// <summary>语义列。IsSensitive=1 为列级受限：未授权用户生成 SQL 时剔除该列。</summary>
public class SemanticColumn : ConcurrentAuditedEntity
{
    /// <summary>FK→SemanticTables，Cascade。</summary>
    public long SemanticTableId { get; set; }

    /// <summary>物理列名。</summary>
    public string ColumnName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>物理类型（用于生成合法 SQL）。</summary>
    public string DbTypeName { get; set; } = string.Empty;

    public SemanticColumnType SemanticType { get; set; }

    public bool IsSensitive { get; set; }

    /// <summary>口径/枚举值说明，进 prompt。</summary>
    public string? Description { get; set; }
}

/// <summary>指标口径。Expression 为聚合表达式模板（{t} 代表基表别名），由 DataSteward 维护并审核。</summary>
public class Metric : SoftDeletableConcurrentEntity
{
    /// <summary>UQ，如 approval_pass_rate。</summary>
    public string Code { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string Expression { get; set; } = string.Empty;

    public AggregationType AggregationType { get; set; }

    /// <summary>%、元、件…</summary>
    public string? Unit { get; set; }

    /// <summary>FK→SemanticColumns，SetNull；默认时间维度。</summary>
    public long? DefaultTimeColumnId { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>FK→Users，SetNull；口径负责人。</summary>
    public long? OwnerUserId { get; set; }
}

/// <summary>指标可基于哪些表（复合主键，双向 Cascade）。</summary>
public class MetricTable
{
    public long MetricId { get; set; }

    public long SemanticTableId { get; set; }
}

/// <summary>表关联（供多表查询）。OnConditionsJson 结构化，列名来自白名单登记，非用户输入。</summary>
public class SemanticRelation : ConcurrentAuditedEntity
{
    /// <summary>FK→SemanticTables，Cascade。</summary>
    public long LeftSemanticTableId { get; set; }

    /// <summary>FK→SemanticTables，Cascade。</summary>
    public long RightSemanticTableId { get; set; }

    public JoinType JoinType { get; set; }

    /// <summary>结构化 [{"leftColumnId":1,"rightColumnId":2}]。</summary>
    public string OnConditionsJson { get; set; } = "[]";
}

/// <summary>同义词（提升 schema linking 召回）。多态目标，无 FK；仅建审计字段。</summary>
public class Synonym
{
    public long Id { get; set; }

    /// <summary>如「通过率」。UQ_Synonyms (Term, TargetType, TargetId)。</summary>
    public string Term { get; set; } = string.Empty;

    public SynonymTargetType TargetType { get; set; }

    public long TargetId { get; set; }

    public long? CreatedByUserId { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
