using ChatBi.Domain.Common;
using ChatBi.Domain.Enums;

namespace ChatBi.Domain.Entities;

/// <summary>会话。FK→Users Restrict（离职禁用不删会话，审计需要）。</summary>
public class ChatSession : SoftDeletableConcurrentEntity
{
    public long UserId { get; set; }

    /// <summary>首问截断生成，可改。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>冗余字段，会话列表分页排序用（OrderBy LastMessageAtUtc DESC）。</summary>
    public DateTime LastMessageAtUtc { get; set; }

    public ICollection<ChatMessage> Messages { get; set; } = new List<ChatMessage>();
}

/// <summary>问数消息（只增；结果集明细不落库）。</summary>
public class ChatMessage
{
    public long Id { get; set; }

    /// <summary>FK→ChatSessions，Cascade。</summary>
    public long SessionId { get; set; }

    public ChatSession Session { get; set; } = null!;

    public ChatRole Role { get; set; }

    /// <summary>Role=User 时必填（CHECK 约束）。</summary>
    public string? QuestionText { get; set; }

    /// <summary>Role=Assistant 时必填。</summary>
    public AskStatus? Status { get; set; }

    /// <summary>最终执行 SQL（已注入权限过滤）。</summary>
    public string? GeneratedSql { get; set; }

    /// <summary>ClarifyNeeded 时的选项。</summary>
    public string? ClarifyOptionsJson { get; set; }

    /// <summary>图表规格（类型/轴/数据，采样 ≤200 行）。</summary>
    public string? ChartSpecJson { get; set; }

    /// <summary>文字结论。</summary>
    public string? Narrative { get; set; }

    public int? RowCountReturned { get; set; }

    /// <summary>端到端耗时（验收 p95 依据）。</summary>
    public int? ElapsedMs { get; set; }

    /// <summary>机器可读错误码。</summary>
    public string? ErrorCode { get; set; }

    /// <summary>用户可见文案，不含敏感细节。</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>客户端生成；UQ (SessionId, IdempotencyKey) WHERE NOT NULL，24h 窗口由应用层判定。</summary>
    public string? IdempotencyKey { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>反馈标注（准确率闭环，few-shot 素材回流；不软删）。</summary>
public class FeedbackAnnotation
{
    public long Id { get; set; }

    /// <summary>FK→ChatMessages，Cascade。</summary>
    public long ChatMessageId { get; set; }

    public FeedbackVerdict Verdict { get; set; }

    /// <summary>人工修正 SQL，作为 few-shot 素材。</summary>
    public string? CorrectedSql { get; set; }

    public string? Note { get; set; }

    /// <summary>FK→Users，Restrict。UQ (ChatMessageId, AnnotatedByUserId)。</summary>
    public long AnnotatedByUserId { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>审计日志（只增不改不删；DetailJson 脱敏，禁止结果集明细与 token；季度归档）。</summary>
public class AuditLog
{
    public long Id { get; set; }

    /// <summary>无 FK（写入热点，避免级联锁；用户删除后保留审计）。</summary>
    public long? UserId { get; set; }

    /// <summary>login / logout / ask / semantic.update / permission.grant / datasource.update…</summary>
    public string Action { get; set; } = string.Empty;

    public string? ResourceType { get; set; }

    public string? ResourceId { get; set; }

    /// <summary>脱敏后上下文（问题文本、生成 SQL 摘要）。</summary>
    public string? DetailJson { get; set; }

    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
