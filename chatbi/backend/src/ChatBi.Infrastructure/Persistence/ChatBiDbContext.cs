using ChatBi.Domain.Common;
using ChatBi.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ChatBi.Infrastructure.Persistence;

/// <summary>
/// 平台库 DbContext（SQL Server，EF Core 8 Code First）。
/// 约定：复数表名、bigint IDENTITY 主键、UTC datetime2(3)、软删全局过滤器、
/// 审计四字段、可编辑配置表 RowVersion 并发令牌。
/// 红线：HasQueryFilter 仅用于平台库软删除；行级数据权限走 Application 层改写器（S4）。
/// </summary>
public class ChatBiDbContext : DbContext
{
    public ChatBiDbContext(DbContextOptions<ChatBiDbContext> options)
        : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<DataPermissionRule> DataPermissionRules => Set<DataPermissionRule>();
    public DbSet<DataSource> DataSources => Set<DataSource>();
    public DbSet<SemanticTable> SemanticTables => Set<SemanticTable>();
    public DbSet<SemanticColumn> SemanticColumns => Set<SemanticColumn>();
    public DbSet<Metric> Metrics => Set<Metric>();
    public DbSet<MetricTable> MetricTables => Set<MetricTable>();
    public DbSet<SemanticRelation> SemanticRelations => Set<SemanticRelation>();
    public DbSet<Synonym> Synonyms => Set<Synonym>();
    public DbSet<ChatSession> ChatSessions => Set<ChatSession>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<FeedbackAnnotation> FeedbackAnnotations => Set<FeedbackAnnotation>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ChatBiDbContext).Assembly);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ApplyAuditTimestamps();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ApplyAuditTimestamps();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void ApplyAuditTimestamps()
    {
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<AuditedEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    if (entry.Entity.CreatedAtUtc == default)
                    {
                        entry.Entity.CreatedAtUtc = now;
                    }

                    break;
                case EntityState.Modified:
                    entry.Entity.UpdatedAtUtc = now;
                    break;
            }
        }
    }
}
