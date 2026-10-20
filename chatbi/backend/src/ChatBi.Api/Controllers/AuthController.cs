using System.Security.Claims;
using ChatBi.Api.Authorization;
using ChatBi.Application.Auth;
using ChatBi.Application.Common;
using ChatBi.Domain.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChatBi.Api.Controllers;

/// <summary>refresh 请求体。</summary>
public record RefreshRequest(string RefreshToken);

/// <summary>会话信息（GET /api/auth/me）。</summary>
public record MeResponse(long UserId, string Name, IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions);

/// <summary>
/// 飞书 SSO 与会话端点：login（302+state）、callback（state→code→token→userinfo→建档→本地 JWT）、
/// refresh（7d 滑动）、logout（TokenVersion++）。
/// 错误码：AUTH_STATE_INVALID(401)、AUTH_CODE_INVALID(401)、AUTH_UPSTREAM_UNAVAILABLE(502)、AUTH_USER_DISABLED(403)。
/// </summary>
[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly AuthService _authService;
    private readonly ILogger<AuthController> _logger;

    public AuthController(AuthService authService, ILogger<AuthController> logger)
    {
        _authService = authService;
        _logger = logger;
    }

    /// <summary>GET /api/auth/feishu/login?redirect=/chat → 302 飞书授权页（state 服务端存储 5min 一次性）。</summary>
    [HttpGet("feishu/login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromQuery] string? redirect, CancellationToken cancellationToken)
    {
        var authorizeUrl = await _authService.BeginLoginAsync(redirect, cancellationToken).ConfigureAwait(false);
        return Redirect(authorizeUrl);
    }

    /// <summary>
    /// GET /api/auth/feishu/callback?code&amp;state。
    /// 浏览器流（默认）：成功后 302 回跳白名单相对路径，token 走 URL fragment 交给 SPA；
    /// JSON 流（Accept: application/json）：直接返回 token 对（契约测试与 fetch 流用）。
    /// </summary>
    [HttpGet("feishu/callback")]
    [AllowAnonymous]
    public async Task<IActionResult> Callback([FromQuery] string? code, [FromQuery] string? state, CancellationToken cancellationToken)
    {
        var result = await _authService.CompleteLoginAsync(code, state, cancellationToken).ConfigureAwait(false);

        if (WantsJson())
        {
            return Ok(result.Tokens);
        }

        var fragment = string.Join('&', new[]
        {
            $"access_token={Uri.EscapeDataString(result.Tokens.AccessToken)}",
            $"refresh_token={Uri.EscapeDataString(result.Tokens.RefreshToken)}",
            "token_type=Bearer",
            $"expires_in={result.Tokens.AccessExpiresInSeconds}",
        });
        return Redirect($"{result.RedirectPath}#{fragment}");
    }

    /// <summary>POST /api/auth/refresh → 校验 refresh token（签名/过期/TokenVersion/账号状态）后滑动重签发。</summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            throw new AuthRefreshInvalidException();
        }

        var tokens = await _authService.RefreshAsync(request.RefreshToken, cancellationToken).ConfigureAwait(false);
        return Ok(tokens);
    }

    /// <summary>POST /api/auth/logout → TokenVersion++（该用户全部已发 token 即刻失效）。</summary>
    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        await _authService.LogoutAsync(userId, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("User {UserId} logged out (TokenVersion bumped).", userId);
        return NoContent();
    }

    /// <summary>GET /api/auth/me → 当前会话用户与权限（供 SPA 渲染与契约验证）。</summary>
    [HttpGet("me")]
    [Authorize]
    public IActionResult Me()
    {
        var userId = GetCurrentUserId();
        var name = User.FindFirstValue(ChatBiClaimTypes.Name) ?? string.Empty;
        var roles = User.FindAll(ChatBiClaimTypes.Role).Select(c => c.Value).Where(v => v is not null).Cast<string>().ToList();
        var permissions = roles
            .SelectMany(PermissionMatrix.ForRole)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        return Ok(new MeResponse(userId, name, roles, permissions));
    }

    private long GetCurrentUserId()
    {
        var sub = User.FindFirstValue(ChatBiClaimTypes.Subject);
        if (!long.TryParse(sub, out var userId))
        {
            // 通过认证的 token 一定带合法 sub；否则视为服务器错误。
            throw new InvalidOperationException("Authenticated principal has no valid sub claim.");
        }

        return userId;
    }

    private bool WantsJson() =>
        Request.Headers.Accept.ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase);
}
