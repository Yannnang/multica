using ChatBi.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChatBi.Infrastructure.Persistence.Configurations;

/// <summary>通用映射帮助方法（datetime2(3)、RowVersion、软删过滤器）。</summary>
internal static class MappingExtensions
{
    internal static EntityTypeBuilder<TEntity> UseUtcAuditColumns<TEntity>(this EntityTypeBuilder<TEntity> builder)
        where TEntity : AuditedEntity
    {
        builder.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(3)");
        builder.Property(x => x.UpdatedAtUtc).HasColumnType("datetime2(3)");
        return builder;
    }

    internal static EntityTypeBuilder<TEntity> UseRowVersion<TEntity>(this EntityTypeBuilder<TEntity> builder)
        where TEntity : class
    {
        builder.Property(nameof(ConcurrentAuditedEntity.RowVersion)).IsRowVersion();
        return builder;
    }

    internal static EntityTypeBuilder<TEntity> UseSoftDeleteFilter<TEntity>(this EntityTypeBuilder<TEntity> builder)
        where TEntity : SoftDeletableEntity
    {
        builder.HasQueryFilter(x => !x.IsDeleted);
        return builder;
    }

    internal static PropertyBuilder<DateTime> IsUtcDateTime(this PropertyBuilder<DateTime> builder) =>
        builder.HasColumnType("datetime2(3)");

    internal static PropertyBuilder<DateTime?> IsUtcDateTime(this PropertyBuilder<DateTime?> builder) =>
        builder.HasColumnType("datetime2(3)");
}
