namespace ChatBi.Domain.Enums;

/// <summary>Users.Status — 用户状态（离职/禁用走 Disabled）。</summary>
public enum UserStatus : byte
{
    Active = 1,
    Disabled = 2,
}

/// <summary>DataSources.DbType — MVP 仅实现 SqlServer，其余预留。</summary>
public enum DataSourceType : byte
{
    SqlServer = 1,
    MySql = 2,
    PostgreSql = 3,
    ClickHouse = 4,
}

/// <summary>DataSources.Status。</summary>
public enum DataSourceStatus : byte
{
    Enabled = 1,
    Disabled = 2,
}

/// <summary>SemanticColumns.SemanticType。</summary>
public enum SemanticColumnType : byte
{
    Dimension = 1,
    Measure = 2,
    Time = 3,
    Identifier = 4,
}

/// <summary>Metrics.AggregationType。</summary>
public enum AggregationType : byte
{
    Sum = 1,
    Count = 2,
    Avg = 3,
    Ratio = 4,
    Custom = 5,
}

/// <summary>SemanticRelations.JoinType。</summary>
public enum JoinType : byte
{
    Inner = 1,
    Left = 2,
}

/// <summary>Synonyms.TargetType（多态目标，无 FK）。</summary>
public enum SynonymTargetType : byte
{
    Metric = 1,
    SemanticTable = 2,
    SemanticColumn = 3,
}

/// <summary>ChatMessages.Role。</summary>
public enum ChatRole : byte
{
    User = 1,
    Assistant = 2,
}

/// <summary>ChatMessages.Status — 问数结果状态。</summary>
public enum AskStatus : byte
{
    Success = 1,
    ClarifyNeeded = 2,
    Rejected = 3,
    Failed = 4,
}

/// <summary>FeedbackAnnotations.Verdict — 准确率闭环标注。</summary>
public enum FeedbackVerdict : byte
{
    Correct = 1,
    IncorrectSql = 2,
    WrongCaliber = 3,
    AmbiguousQuestion = 4,
}

/// <summary>DataPermissionRules.SubjectType（多态主体，无 FK）。</summary>
public enum PermissionSubjectType : byte
{
    Role = 1,
    User = 2,
}

/// <summary>DataPermissionRules.ScopeType — 行级数据范围。</summary>
public enum DataScopeType : byte
{
    All = 1,
    DepartmentAndBelow = 2,
    SelfOnly = 3,
    Custom = 4,
}
