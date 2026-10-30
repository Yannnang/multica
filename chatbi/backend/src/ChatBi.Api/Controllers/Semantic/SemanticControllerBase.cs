using System.Security.Claims;
using ChatBi.Api.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChatBi.Api.Controllers.Semantic;

/// <summary>语义层控制器公共基座：取当前用户 id（sub claim）。</summary>
[ApiController]
public abstract class SemanticControllerBase : ControllerBase
{
    protected long GetCurrentUserId()
    {
        var sub = User.FindFirstValue(ChatBiClaimTypes.Subject);
        if (!long.TryParse(sub, out var userId))
        {
            // 通过认证的 token 一定带合法 sub；否则视为服务器错误。
            throw new InvalidOperationException("Authenticated principal has no valid sub claim.");
        }

        return userId;
    }
}
