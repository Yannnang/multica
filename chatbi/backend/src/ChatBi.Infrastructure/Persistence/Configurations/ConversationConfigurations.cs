using ChatBi.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChatBi.Infrastructure.Persistence.Configurations;

internal sealed class ChatSessionConfiguration : IEntityTypeConfiguration<ChatSession>
{
    public void Configure(EntityTypeBuilder<ChatSession> builder)
    {
        builder.ToTable("ChatSessions");
        builder.UseUtcAuditColumns().UseSoftDeleteFilter();
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Title).HasMaxLength(256).IsRequired();
        builder.Property(x => x.LastMessageAtUtc).IsUtcDateTime();
        builder.Property(x => x.RowVersion).IsRowVersion();

        // 离职禁用不删会话（审计需要）→ Restrict。
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_ChatSessions_Users");

        // 对应查询：我的会话列表分页（显式 OrderBy LastMessageAtUtc DESC）。
        builder.HasIndex(x => new { x.UserId, x.LastMessageAtUtc })
            .IsDescending(false, true)
            .HasDatabaseName("IX_ChatSessions_User");
    }
}

internal sealed class ChatMessageConfiguration : IEntityTypeConfiguration<ChatMessage>
{
    public void Configure(EntityTypeBuilder<ChatMessage> builder)
    {
        // CHECK：Role=User(1) 必有 QuestionText；Role=Assistant(2) 必有 Status（数据库表设计 4.2）。
        builder.ToTable("ChatMessages", t => t.HasCheckConstraint(
            "CK_ChatMessages_Role_Fields",
            "([Role] = 1 AND [QuestionText] IS NOT NULL) OR ([Role] = 2 AND [Status] IS NOT NULL)"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Role).IsRequired();
        builder.Property(x => x.QuestionText).HasMaxLength(-1);
        builder.Property(x => x.GeneratedSql).HasMaxLength(-1);
        builder.Property(x => x.ClarifyOptionsJson).HasMaxLength(-1);
        builder.Property(x => x.ChartSpecJson).HasMaxLength(-1);
        builder.Property(x => x.Narrative).HasMaxLength(2048);
        builder.Property(x => x.ErrorCode).HasMaxLength(64);
        builder.Property(x => x.ErrorMessage).HasMaxLength(1024); // 用户可见文案，不含敏感细节
        builder.Property(x => x.IdempotencyKey).HasMaxLength(64);
        builder.Property(x => x.CreatedAtUtc).IsUtcDateTime();

        builder.HasOne(x => x.Session).WithMany(s => s.Messages).HasForeignKey(x => x.SessionId)
            .OnDelete(DeleteBehavior.Cascade).HasConstraintName("FK_ChatMessages_ChatSessions");

        // 24h 幂等窗口由应用层判定；过滤唯一索引只约束非 NULL 键。
        builder.HasIndex(x => new { x.SessionId, x.IdempotencyKey })
            .IsUnique()
            .HasFilter("[IdempotencyKey] IS NOT NULL")
            .HasDatabaseName("UQ_ChatMessages_Idem");
        builder.HasIndex(x => new { x.SessionId, x.CreatedAtUtc })
            .IsDescending(false, true)
            .HasDatabaseName("IX_ChatMessages_Session");
    }
}

internal sealed class FeedbackAnnotationConfiguration : IEntityTypeConfiguration<FeedbackAnnotation>
{
    public void Configure(EntityTypeBuilder<FeedbackAnnotation> builder)
    {
        builder.ToTable("FeedbackAnnotations");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Verdict).IsRequired();
        builder.Property(x => x.Note).HasMaxLength(1024);
        builder.Property(x => x.CreatedAtUtc).IsUtcDateTime();

        builder.HasOne<ChatMessage>().WithMany().HasForeignKey(x => x.ChatMessageId)
            .OnDelete(DeleteBehavior.Cascade).HasConstraintName("FK_FeedbackAnnotations_ChatMessages");
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.AnnotatedByUserId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_FeedbackAnnotations_Users");

        builder.HasIndex(x => new { x.ChatMessageId, x.AnnotatedByUserId })
            .IsUnique()
            .HasDatabaseName("UQ_FeedbackAnnotations_Message_Annotator");
    }
}

internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("AuditLogs");
        builder.HasKey(x => x.Id);

        // 只增不改不删：无软删、无 RowVersion；UserId 无 FK（写入热点，用户删除后保留审计）。
        builder.Property(x => x.Action).HasMaxLength(64).IsRequired();
        builder.Property(x => x.ResourceType).HasMaxLength(64);
        builder.Property(x => x.ResourceId).HasMaxLength(64);
        builder.Property(x => x.DetailJson).HasMaxLength(-1); // 脱敏后上下文；禁止结果集明细、token
        builder.Property(x => x.IpAddress).HasMaxLength(64);
        builder.Property(x => x.UserAgent).HasMaxLength(256);
        builder.Property(x => x.CreatedAtUtc).IsUtcDateTime();

        builder.HasIndex(x => new { x.UserId, x.CreatedAtUtc }).HasDatabaseName("IX_AuditLogs_User");
        builder.HasIndex(x => x.CreatedAtUtc).HasDatabaseName("IX_AuditLogs_Created");
    }
}
