namespace ChatBi.Domain.Common;

/// <summary>
/// 审计四字段（每表都有，见《数据库表设计 v1.0》第 0 节）。
/// 时间一律 UTC，datetime2(3)，字段以 AtUtc 结尾。
/// </summary>
public abstract class AuditedEntity
{
    public long Id { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }

    public long? CreatedByUserId { get; set; }

    public long? UpdatedByUserId { get; set; }
}

/// <summary>软删除实体基类（平台库软删除走 EF 全局查询过滤器）。</summary>
public abstract class SoftDeletableEntity : AuditedEntity
{
    public bool IsDeleted { get; set; }
}

/// <summary>可编辑配置类表：软删 + RowVersion 并发令牌。</summary>
public abstract class SoftDeletableConcurrentEntity : SoftDeletableEntity
{
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

/// <summary>可编辑配置类表（不软删）：审计 + RowVersion 并发令牌。</summary>
public abstract class ConcurrentAuditedEntity : AuditedEntity
{
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
