using System.Security.Claims;
using ChatBi.Api.Authorization;
using ChatBi.Domain.Enums;
using ChatBi.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;

namespace ChatBi.Api.Auth;

/// <summary>
/// access token 验证后置检查：typ 必须为 access；加载库中用户并校验
/// 存在/未软删/Active/TokenVersion 一致（撤销通道：登出/禁用/离职 → TokenVersion++ 全部会话即刻失效）。
/// </summary>
public static class TokenVersionValidator
{
    public static async Task ValidateAsync(TokenValidatedContext context)
    {
        var tokenUse = context.Principal?.FindFirstValue(ChatBiClaimTypes.TokenUse);
        if (tokenUse != ChatBiClaimTypes.TokenUseAccess)
        {
            context.Fail("Token is not an access token.");
            return;
        }

        var sub = context.Principal?.FindFirstValue(ChatBiClaimTypes.Subject);
        if (!long.TryParse(sub, out var userId))
        {
            context.Fail("Token has no valid sub claim.");
            return;
        }

        var tokenVersionClaim = context.Principal?.FindFirstValue(ChatBiClaimTypes.TokenVersion);
        if (!int.TryParse(tokenVersionClaim, out var tokenVersion))
        {
            context.Fail("Token has no valid token_ver claim.");
            return;
        }

        var dbContext = context.HttpContext.RequestServices.GetRequiredService<ChatBiDbContext>();
        var user = await dbContext.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == userId, context.HttpContext.RequestAborted)
            .ConfigureAwait(false);

        if (user is null)
        {
            context.Fail("User no longer exists.");
            return;
        }

        if (user.Status != UserStatus.Active)
        {
            // 越权用例：禁用用户持旧 token → 401。
            context.Fail("User account is disabled.");
            return;
        }

        if (user.TokenVersion != tokenVersion)
        {
            context.Fail("Token version is stale (revoked).");
        }
    }
}
