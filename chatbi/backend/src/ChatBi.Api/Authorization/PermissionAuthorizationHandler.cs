using ChatBi.Domain.Security;
using Microsoft.AspNetCore.Authorization;

namespace ChatBi.Api.Authorization;

/// <summary>操作权限要求（Policy-based Authorization，默认拒绝；未列出的操作一律拒绝）。</summary>
public class OperationAuthorizationRequirement : IAuthorizationRequirement
{
    public OperationAuthorizationRequirement(string permission)
    {
        Permission = permission;
    }

    public string Permission { get; }
}

/// <summary>
/// 权限 Handler：角色来自 JWT role claim，矩阵唯一权威来源 <see cref="PermissionMatrix"/>
/// （与 RolePermissions 种子同源）。无状态，注册为 Singleton。
/// </summary>
public class PermissionAuthorizationHandler : AuthorizationHandler<OperationAuthorizationRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, OperationAuthorizationRequirement requirement)
    {
        foreach (var roleClaim in context.User.FindAll(ChatBiClaimTypes.Role))
        {
            if (PermissionMatrix.HasPermission(roleClaim.Value ?? string.Empty, requirement.Permission))
            {
                context.Succeed(requirement);
                break;
            }
        }

        return Task.CompletedTask;
    }
}

/// <summary>与 JWT 签发一致的 claim 名（Infrastructure.Auth.ChatBiClaims 的 Api 侧镜像，避免 Api 依赖 Infrastructure 内部类型）。</summary>
public static class ChatBiClaimTypes
{
    public const string Subject = "sub";
    public const string Name = "name";
    public const string Role = "role";
    public const string UnionId = "union_id";
    public const string DepartmentPath = "dept_path";
    public const string TokenVersion = "token_ver";
    public const string TokenUse = "typ";
    public const string TokenUseAccess = "access";
}

/// <summary>Policy 名 = 权限码（chat:ask 等），端点上写 [Authorize(Policy = Permissions.Xxx)]。</summary>
public static class AuthorizationPolicies
{
    public static Microsoft.Extensions.DependencyInjection.IServiceCollection AddChatBiPolicies(
        this Microsoft.Extensions.DependencyInjection.IServiceCollection services)
    {
        var builder = services.AddAuthorizationBuilder();

        // 默认拒绝：未认证请求一律 401（Default + Fallback 双保险）。
        builder.SetDefaultPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        builder.SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        foreach (var permission in Permissions.All)
        {
            builder.AddPolicy(permission, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new OperationAuthorizationRequirement(permission)));
        }

        return services;
    }
}
