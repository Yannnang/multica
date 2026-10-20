using ChatBi.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChatBi.Infrastructure.Persistence.Configurations;

internal sealed class DataSourceConfiguration : IEntityTypeConfiguration<DataSource>
{
    public void Configure(EntityTypeBuilder<DataSource> builder)
    {
        builder.ToTable("DataSources");
        builder.UseUtcAuditColumns().UseSoftDeleteFilter();
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(128).IsRequired();
        builder.Property(x => x.DbType).IsRequired();
        builder.Property(x => x.Host).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Port).IsRequired();
        builder.Property(x => x.DatabaseName).HasMaxLength(128).IsRequired();

        // 凭证只存引用名（环境变量/Key Vault），库内不落明文连接串。
        builder.Property(x => x.CredentialRef).HasMaxLength(256).IsRequired();
        builder.Property(x => x.QueryTimeoutSeconds).IsRequired().HasDefaultValue(30);
        builder.Property(x => x.MaxRowsPerQuery).IsRequired().HasDefaultValue(5000);
        builder.Property(x => x.Status).IsRequired();
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasIndex(x => x.Name).IsUnique().HasDatabaseName("UQ_DataSources_Name");
    }
}

internal sealed class SemanticTableConfiguration : IEntityTypeConfiguration<SemanticTable>
{
    public void Configure(EntityTypeBuilder<SemanticTable> builder)
    {
        builder.ToTable("SemanticTables");
        builder.UseUtcAuditColumns().UseSoftDeleteFilter();
        builder.HasKey(x => x.Id);
        builder.Property(x => x.SchemaName).HasMaxLength(128).IsRequired().HasDefaultValue("dbo");
        builder.Property(x => x.TableName).HasMaxLength(128).IsRequired();
        builder.Property(x => x.DisplayName).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(1024);
        builder.Property(x => x.IsEnabled).IsRequired().HasDefaultValue(true);
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasOne<DataSource>().WithMany().HasForeignKey(x => x.DataSourceId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_SemanticTables_DataSources");

        // SQL Server 过滤唯一索引：软删后可复用同名。
        builder.HasIndex(x => new { x.DataSourceId, x.SchemaName, x.TableName })
            .IsUnique()
            .HasFilter("[IsDeleted] = 0")
            .HasDatabaseName("UQ_SemanticTables_DataSource_Schema_Table");
        builder.HasIndex(x => x.DataSourceId).HasDatabaseName("IX_SemanticTables_DataSourceId");
    }
}

internal sealed class SemanticColumnConfiguration : IEntityTypeConfiguration<SemanticColumn>
{
    public void Configure(EntityTypeBuilder<SemanticColumn> builder)
    {
        builder.ToTable("SemanticColumns");
        builder.UseUtcAuditColumns();
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ColumnName).HasMaxLength(128).IsRequired();
        builder.Property(x => x.DisplayName).HasMaxLength(128).IsRequired();
        builder.Property(x => x.DbTypeName).HasMaxLength(64).IsRequired();
        builder.Property(x => x.SemanticType).IsRequired();
        builder.Property(x => x.IsSensitive).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(1024);
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasOne<SemanticTable>().WithMany().HasForeignKey(x => x.SemanticTableId)
            .OnDelete(DeleteBehavior.Cascade).HasConstraintName("FK_SemanticColumns_SemanticTables");

        builder.HasIndex(x => new { x.SemanticTableId, x.ColumnName })
            .IsUnique()
            .HasDatabaseName("UQ_SemanticColumns_Table_Column");
        builder.HasIndex(x => x.SemanticTableId).HasDatabaseName("IX_SemanticColumns_Table");
    }
}

internal sealed class MetricConfiguration : IEntityTypeConfiguration<Metric>
{
    public void Configure(EntityTypeBuilder<Metric> builder)
    {
        builder.ToTable("Metrics");
        builder.UseUtcAuditColumns().UseSoftDeleteFilter();
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();
        builder.Property(x => x.DisplayName).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(1024);
        builder.Property(x => x.Expression).HasMaxLength(1024).IsRequired();
        builder.Property(x => x.AggregationType).IsRequired();
        builder.Property(x => x.Unit).HasMaxLength(32);
        builder.Property(x => x.IsEnabled).IsRequired().HasDefaultValue(true);
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasIndex(x => x.Code).IsUnique().HasDatabaseName("UQ_Metrics_Code");

        builder.HasOne<SemanticColumn>().WithMany().HasForeignKey(x => x.DefaultTimeColumnId)
            .OnDelete(DeleteBehavior.SetNull).HasConstraintName("FK_Metrics_SemanticColumns");
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.OwnerUserId)
            .OnDelete(DeleteBehavior.SetNull).HasConstraintName("FK_Metrics_Users");
    }
}

internal sealed class MetricTableConfiguration : IEntityTypeConfiguration<MetricTable>
{
    public void Configure(EntityTypeBuilder<MetricTable> builder)
    {
        builder.ToTable("MetricTables");
        builder.HasKey(x => new { x.MetricId, x.SemanticTableId });

        builder.HasOne<Metric>().WithMany().HasForeignKey(x => x.MetricId)
            .OnDelete(DeleteBehavior.Cascade).HasConstraintName("FK_MetricTables_Metrics");
        builder.HasOne<SemanticTable>().WithMany().HasForeignKey(x => x.SemanticTableId)
            .OnDelete(DeleteBehavior.Cascade).HasConstraintName("FK_MetricTables_SemanticTables");
    }
}

internal sealed class SemanticRelationConfiguration : IEntityTypeConfiguration<SemanticRelation>
{
    public void Configure(EntityTypeBuilder<SemanticRelation> builder)
    {
        builder.ToTable("SemanticRelations");
        builder.UseUtcAuditColumns();
        builder.HasKey(x => x.Id);
        builder.Property(x => x.JoinType).IsRequired();
        builder.Property(x => x.OnConditionsJson).HasMaxLength(-1).IsRequired(); // nvarchar(max) 结构化 JSON
        builder.Property(x => x.RowVersion).IsRowVersion();

        // 设计文档标注双 Cascade；SQL Server 禁止同一父表的多级联路径（Msg 1785），
        // 且 SemanticTables 为软删（不物理删除），故两侧均取 Restrict——偏离已在 PR 说明。
        builder.HasOne<SemanticTable>().WithMany().HasForeignKey(x => x.LeftSemanticTableId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_SemanticRelations_LeftSemanticTables");
        builder.HasOne<SemanticTable>().WithMany().HasForeignKey(x => x.RightSemanticTableId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_SemanticRelations_RightSemanticTables");

        builder.HasIndex(x => new { x.LeftSemanticTableId, x.RightSemanticTableId })
            .IsUnique()
            .HasDatabaseName("UQ_SemanticRelations_Left_Right");
    }
}

internal sealed class SynonymConfiguration : IEntityTypeConfiguration<Synonym>
{
    public void Configure(EntityTypeBuilder<Synonym> builder)
    {
        builder.ToTable("Synonyms");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Term).HasMaxLength(128).IsRequired();
        builder.Property(x => x.TargetType).IsRequired();
        builder.Property(x => x.TargetId).IsRequired(); // 多态，无 FK
        builder.Property(x => x.CreatedAtUtc).IsUtcDateTime();

        builder.HasIndex(x => new { x.Term, x.TargetType, x.TargetId })
            .IsUnique()
            .HasDatabaseName("UQ_Synonyms_Term_Target");
        builder.HasIndex(x => x.Term).HasDatabaseName("IX_Synonyms_Term");
    }
}
