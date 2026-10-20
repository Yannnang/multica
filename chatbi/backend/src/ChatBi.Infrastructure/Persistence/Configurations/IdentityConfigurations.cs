using ChatBi.Domain.Entities;
using ChatBi.Domain.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChatBi.Infrastructure.Persistence.Configurations;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users");
        builder.UseUtcAuditColumns().UseSoftDeleteFilter();
        builder.HasKey(x => x.Id);
        builder.Property(x => x.UnionId).HasMaxLength(64).IsRequired();
        builder.Property(x => x.OpenId).HasMaxLength(64);
        builder.Property(x => x.Name).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Email).HasMaxLength(256);
        builder.Property(x => x.AvatarUrl).HasMaxLength(512);
        builder.Property(x => x.DepartmentPath).HasMaxLength(512);
        builder.Property(x => x.Status).IsRequired();
        builder.Property(x => x.TokenVersion).IsRequired();
        builder.Property(x => x.LastLoginAtUtc).IsUtcDateTime();
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasIndex(x => x.UnionId).IsUnique().HasDatabaseName("UQ_Users_UnionId");
        builder.HasIndex(x => x.Status).HasDatabaseName("IX_Users_Status");
    }
}

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("Roles");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(64).IsRequired();
        builder.Property(x => x.IsSystem).IsRequired();
        builder.HasIndex(x => x.Code).IsUnique().HasDatabaseName("UQ_Roles_Code");

        // 种子数据走迁移 HasData，不手改（IsSystem=1 内置不可删改）。
        builder.HasData(
            new Role { Id = 1, Code = RoleCodes.BusinessUser, Name = "业务用户", IsSystem = true },
            new Role { Id = 2, Code = RoleCodes.DataSteward, Name = "数据管理员", IsSystem = true },
            new Role { Id = 3, Code = RoleCodes.SysAdmin, Name = "系统管理员", IsSystem = true });
    }
}

internal sealed class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> builder)
    {
        builder.ToTable("UserRoles");
        builder.HasKey(x => new { x.UserId, x.RoleId });
        builder.Property(x => x.GrantedAtUtc).IsUtcDateTime();

        builder.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("FK_UserRoles_Users");
        builder.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("FK_UserRoles_Roles");
        // GrantedByUserId 不建 FK（避免环）。

        builder.HasIndex(x => x.RoleId).HasDatabaseName("IX_UserRoles_RoleId");
    }
}

internal sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> builder)
    {
        builder.ToTable("RolePermissions");
        builder.HasKey(x => new { x.RoleId, x.Resource });
        builder.Property(x => x.Resource).HasMaxLength(128).IsRequired();

        builder.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("FK_RolePermissions_Roles");

        // 种子数据与 Domain.PermissionMatrix 单一权威来源保持一致（角色 Id：1=BusinessUser 2=DataSteward 3=SysAdmin）。
        var roleIdByCode = new Dictionary<string, long>
        {
            [RoleCodes.BusinessUser] = 1,
            [RoleCodes.DataSteward] = 2,
            [RoleCodes.SysAdmin] = 3,
        };
        builder.HasData(PermissionMatrix.AllPairs()
            .Select(pair => new RolePermission { RoleId = roleIdByCode[pair.RoleCode], Resource = pair.Permission })
            .ToArray());
    }
}

internal sealed class DataPermissionRuleConfiguration : IEntityTypeConfiguration<DataPermissionRule>
{
    public void Configure(EntityTypeBuilder<DataPermissionRule> builder)
    {
        builder.ToTable("DataPermissionRules");
        builder.UseUtcAuditColumns();
        builder.HasKey(x => x.Id);
        builder.Property(x => x.SubjectType).IsRequired();
        builder.Property(x => x.SubjectId).IsRequired(); // 多态，不建 FK
        builder.Property(x => x.ScopeType).IsRequired();
        builder.Property(x => x.DepartmentPathPrefix).HasMaxLength(512);
        builder.Property(x => x.CustomPredicateJson).HasMaxLength(-1); // nvarchar(max)：结构化 JSON，严禁 SQL 片段
        builder.Property(x => x.Enabled).IsRequired().HasDefaultValue(true);
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasOne<SemanticTable>().WithMany().HasForeignKey(x => x.SemanticTableId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_DataPermissionRules_SemanticTables");

        builder.HasIndex(x => new { x.SubjectType, x.SubjectId }).HasDatabaseName("IX_DataPermissionRules_Subject");
        builder.HasIndex(x => x.SemanticTableId).HasDatabaseName("IX_DataPermissionRules_Table");
    }
}
