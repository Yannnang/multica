namespace ChatBi.Domain.Security;

/// <summary>操作权限码（资源:操作）。未列出的操作一律拒绝（默认拒绝 Policy）。</summary>
public static class Permissions
{
    public const string ChatAsk = "chat:ask";
    public const string SqlView = "sql:view";
    public const string SemanticRead = "semantic:read";
    public const string SemanticManage = "semantic:manage";
    public const string DataPermManage = "dataperm:manage";
    public const string DataSourceManage = "datasource:manage";
    public const string UsersManage = "users:manage";
    public const string AuditRead = "audit:read";

    public static readonly IReadOnlyList<string> All = new[]
    {
        ChatAsk, SqlView, SemanticRead, SemanticManage,
        DataPermManage, DataSourceManage, UsersManage, AuditRead,
    };
}

/// <summary>内置角色 Code（种子数据，IsSystem=1 不可删改）。</summary>
public static class RoleCodes
{
    public const string BusinessUser = "BusinessUser";
    public const string DataSteward = "DataSteward";
    public const string SysAdmin = "SysAdmin";

    public static readonly IReadOnlyList<string> All = new[] { BusinessUser, DataSteward, SysAdmin };
}

/// <summary>
/// 角色 × 操作矩阵（设计基线 v1.0 第 3.2 节）。
/// 唯一权威来源：RolePermissions 种子数据与授权 Handler 均由此类派生，避免双写漂移。
/// </summary>
public static class PermissionMatrix
{
    private static readonly Dictionary<string, IReadOnlySet<string>> Matrix = new()
    {
        [RoleCodes.BusinessUser] = new HashSet<string>(StringComparer.Ordinal)
        {
            Permissions.ChatAsk, Permissions.SqlView, Permissions.SemanticRead,
        },
        [RoleCodes.DataSteward] = new HashSet<string>(StringComparer.Ordinal)
        {
            Permissions.ChatAsk, Permissions.SqlView, Permissions.SemanticRead,
            Permissions.SemanticManage, Permissions.DataPermManage,
        },
        [RoleCodes.SysAdmin] = new HashSet<string>(StringComparer.Ordinal)
        {
            Permissions.ChatAsk, Permissions.SqlView, Permissions.SemanticRead,
            Permissions.SemanticManage, Permissions.DataPermManage,
            Permissions.DataSourceManage, Permissions.UsersManage, Permissions.AuditRead,
        },
    };

    /// <summary>角色是否拥有指定操作权限；未知角色一律 false（默认拒绝）。</summary>
    public static bool HasPermission(string roleCode, string permission) =>
        Matrix.TryGetValue(roleCode, out var permissions) && permissions.Contains(permission);

    /// <summary>角色的全部操作权限（用于签发 JWT perm claim / 种子）。</summary>
    public static IReadOnlyList<string> ForRole(string roleCode) =>
        Matrix.TryGetValue(roleCode, out var permissions)
            ? permissions.OrderBy(p => p, StringComparer.Ordinal).ToList()
            : Array.Empty<string>();

    /// <summary>全部 (角色, 权限) 对，供 RolePermissions HasData 种子使用。</summary>
    public static IEnumerable<(string RoleCode, string Permission)> AllPairs()
    {
        foreach (var roleCode in RoleCodes.All)
        {
            foreach (var permission in ForRole(roleCode))
            {
                yield return (roleCode, permission);
            }
        }
    }
}
