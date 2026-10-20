using ChatBi.Domain.Common;
using ChatBi.Domain.Enums;

namespace ChatBi.Domain.Entities;

/// <summary>用户（身份映射外部唯一键为飞书 union_id）。</summary>
public class User : SoftDeletableConcurrentEntity
{
    /// <summary>飞书 union_id，外部唯一键（UQ_Users_UnionId）。</summary>
    public string UnionId { get; set; } = string.Empty;

    /// <summary>飞书 open_id（本应用内），仅备查。</summary>
    public string? OpenId { get; set; }

    /// <summary>显示名。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>飞书企业邮箱（可能拿不到，需 scope）。</summary>
    public string? Email { get; set; }

    public string? AvatarUrl { get; set; }

    /// <summary>如 /总部/技术部/后端组，行级权限依据；拿不到则为 NULL（行级权限退化为 SelfOnly）。</summary>
    public string? DepartmentPath { get; set; }

    public UserStatus Status { get; set; } = UserStatus.Active;

    /// <summary>自增即撤销该用户全部已发 JWT/refresh（撤销通道，不维护黑名单）。</summary>
    public int TokenVersion { get; set; }

    public DateTime? LastLoginAtUtc { get; set; }
}

/// <summary>角色（种子数据走迁移 HasData，不手改；无角色继承）。</summary>
public class Role
{
    public long Id { get; set; }

    /// <summary>BusinessUser / DataSteward / SysAdmin，UQ。</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>1=内置不可删改。</summary>
    public bool IsSystem { get; set; }
}

/// <summary>用户-角色关联（复合主键，无角色继承，多角色授予代替）。</summary>
public class UserRole
{
    public long UserId { get; set; }

    public long RoleId { get; set; }

    /// <summary>不建 FK（避免环）。</summary>
    public long? GrantedByUserId { get; set; }

    public DateTime GrantedAtUtc { get; set; }
}

/// <summary>角色-操作权限（复合主键；种子数据与 <see cref="Security.PermissionMatrix"/> 保持一致）。</summary>
public class RolePermission
{
    public long RoleId { get; set; }

    /// <summary>资源:操作码，如 chat:ask、semantic:manage。</summary>
    public string Resource { get; set; } = string.Empty;
}

/// <summary>
/// 行级/列级数据权限规则。CustomPredicateJson 为结构化 JSON，
/// 服务端白名单校验后转参数化 WHERE；严禁存 SQL 片段（防注入红线）。
/// </summary>
public class DataPermissionRule : ConcurrentAuditedEntity
{
    public PermissionSubjectType SubjectType { get; set; }

    /// <summary>RoleId 或 UserId（不建 FK，多态）。</summary>
    public long SubjectId { get; set; }

    /// <summary>NULL=对该主体全局默认；FK→SemanticTables，Restrict。</summary>
    public long? SemanticTableId { get; set; }

    public DataScopeType ScopeType { get; set; }

    /// <summary>ScopeType=DepartmentAndBelow 时用。</summary>
    public string? DepartmentPathPrefix { get; set; }

    /// <summary>ScopeType=Custom 时用，结构化 JSON：[{"column":"dept_path","op":"eq","value":"…"}]。</summary>
    public string? CustomPredicateJson { get; set; }

    public bool Enabled { get; set; } = true;
}
