using ChatBi.Application.Common;
using ChatBi.Application.Configuration;
using ChatBi.Domain.Entities;
using ChatBi.Domain.Enums;
using ChatBi.Domain.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChatBi.Application.Auth;

/// <summary>
/// SSO 用例服务：state 校验 → code 换 user_access_token → 拉用户信息 → union_id 映射本地用户
/// （首登自动建档默认角色 BusinessUser）→ 签发本地 JWT。
/// 红线：user_access_token 用后即弃、不持久化、不进日志；业务侧一律本地 JWT。
/// </summary>
public class AuthService
{
    private readonly IFeishuAuthClient _feishu;
    private readonly IAuthStateStore _stateStore;
    private readonly ITokenService _tokenService;
    private readonly IUserAccountRepository _userRepository;
    private readonly FeishuOptions _feishuOptions;
    private readonly AuthRedirectOptions _redirectOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        IFeishuAuthClient feishu,
        IAuthStateStore stateStore,
        ITokenService tokenService,
        IUserAccountRepository userRepository,
        IOptions<FeishuOptions> feishuOptions,
        IOptions<AuthRedirectOptions> redirectOptions,
        TimeProvider timeProvider,
        ILogger<AuthService> logger)
    {
        _feishu = feishu;
        _stateStore = stateStore;
        _tokenService = tokenService;
        _userRepository = userRepository;
        _feishuOptions = feishuOptions.Value;
        _redirectOptions = redirectOptions.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>GET /api/auth/feishu/login：生成一次性 state（服务端存储 5 分钟），返回 302 目标授权页 URL。</summary>
    public async Task<string> BeginLoginAsync(string? redirect, CancellationToken cancellationToken = default)
    {
        var state = await _stateStore.CreateStateAsync(SanitizeRedirect(redirect), cancellationToken).ConfigureAwait(false);
        return _feishu.BuildAuthorizeUrl(state, redirect);
    }

    /// <summary>GET /api/auth/feishu/callback：完整回调链路，成功返回会话信息，失败抛 <see cref="AuthException"/>。</summary>
    public async Task<CallbackSuccess> CompleteLoginAsync(string? code, string? state, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            throw new AuthStateInvalidException();
        }

        var storedRedirect = await _stateStore.ConsumeStateAsync(state, cancellationToken).ConfigureAwait(false);
        if (storedRedirect is null)
        {
            throw new AuthStateInvalidException();
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            throw new AuthCodeInvalidException("缺少授权码。");
        }

        FeishuUserInfo feishuUser;
        try
        {
            var userAccessToken = await _feishu.ExchangeCodeForUserAccessTokenAsync(code, cancellationToken).ConfigureAwait(false);
            feishuUser = await _feishu.GetUserInfoAsync(userAccessToken, cancellationToken).ConfigureAwait(false);
        }
        catch (AuthException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            // 飞书不可用/超时：不泄露内部细节，只记服务端日志。
            _logger.LogError(ex, "Feishu upstream unavailable during code exchange.");
            throw new AuthUpstreamUnavailableException();
        }

        if (string.IsNullOrWhiteSpace(feishuUser.UnionId))
        {
            _logger.LogError("Feishu user info missing union_id.");
            throw new AuthUpstreamUnavailableException();
        }

        var user = await _userRepository.FindByUnionIdAsync(feishuUser.UnionId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            user = await ProvisionUserAsync(feishuUser, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (user.Status != UserStatus.Active)
            {
                throw new AuthUserDisabledException();
            }

            // 通讯录同步只更新 Name/DepartmentPath 等，不改 UnionId（外部键稳定）。
            user.Name = feishuUser.Name;
            user.AvatarUrl = feishuUser.AvatarUrl ?? user.AvatarUrl;
            user.Email = feishuUser.Email ?? user.Email;
            if (!string.IsNullOrWhiteSpace(feishuUser.DepartmentPath))
            {
                user.DepartmentPath = feishuUser.DepartmentPath;
            }

            user.LastLoginAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
            user.UpdatedAtUtc = user.LastLoginAtUtc;
            await _userRepository.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        var roles = await _userRepository.GetRoleCodesAsync(user.Id, cancellationToken).ConfigureAwait(false);
        var tokens = IssueTokens(user, roles);

        return new CallbackSuccess(
            tokens,
            user.Id,
            user.Name,
            roles,
            SanitizeRedirect(storedRedirect));
    }

    /// <summary>POST /api/auth/refresh：校验 refresh token（签名/过期/TokenVersion/账号状态），滑动重签发。</summary>
    public async Task<AuthTokenPair> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        var info = _tokenService.ValidateRefreshToken(refreshToken)
            ?? throw new AuthRefreshInvalidException();

        var user = await _userRepository.FindByIdAsync(info.UserId, cancellationToken).ConfigureAwait(false);
        if (user is null || user.IsDeleted)
        {
            throw new AuthRefreshInvalidException();
        }

        if (user.Status != UserStatus.Active)
        {
            // 越权用例 5：禁用用户持旧 refresh token → 拒绝。
            throw new AuthUserDisabledException();
        }

        if (user.TokenVersion != info.TokenVersion)
        {
            // 登出/禁用/离职 → TokenVersion++，旧 token 全部失效。
            throw new AuthRefreshInvalidException();
        }

        var roles = await _userRepository.GetRoleCodesAsync(user.Id, cancellationToken).ConfigureAwait(false);
        return IssueTokens(user, roles);
    }

    /// <summary>POST /api/auth/logout：TokenVersion++（撤销该用户全部已发 JWT/refresh，不维护黑名单表）。</summary>
    public async Task LogoutAsync(long userId, CancellationToken cancellationToken = default)
    {
        var user = await _userRepository.FindByIdAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return;
        }

        user.TokenVersion++;
        user.UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        await _userRepository.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>回跳路径白名单校验：仅允许配置前缀内的相对路径，防开放重定向。</summary>
    public string SanitizeRedirect(string? redirect)
    {
        if (string.IsNullOrWhiteSpace(redirect))
        {
            return _redirectOptions.DefaultRedirect;
        }

        // 拒绝协议相对（//host）、绝对 URL、反斜杠绕过。
        if (!redirect.StartsWith('/') || redirect.StartsWith("//") || redirect.Contains('\\') || redirect.Contains(':'))
        {
            return _redirectOptions.DefaultRedirect;
        }

        foreach (var prefix in _redirectOptions.AllowedPrefixes)
        {
            if (redirect.StartsWith(prefix, StringComparison.Ordinal))
            {
                return redirect;
            }
        }

        return _redirectOptions.DefaultRedirect;
    }

    private async Task<User> ProvisionUserAsync(FeishuUserInfo feishuUser, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // 首登自动建档：默认角色 BusinessUser；DepartmentPath 拿不到则置 NULL（行级权限退化为 SelfOnly，保守默认）。
        var user = new User
        {
            UnionId = feishuUser.UnionId,
            OpenId = feishuUser.OpenId,
            Name = feishuUser.Name,
            Email = feishuUser.Email,
            AvatarUrl = feishuUser.AvatarUrl,
            DepartmentPath = string.IsNullOrWhiteSpace(feishuUser.DepartmentPath) ? null : feishuUser.DepartmentPath,
            Status = UserStatus.Active,
            TokenVersion = 0,
            LastLoginAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        await _userRepository.AddAsync(user, cancellationToken).ConfigureAwait(false);
        await _userRepository.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _userRepository.GrantRoleAsync(user.Id, RoleCodes.BusinessUser, cancellationToken).ConfigureAwait(false);

        // 首个 SysAdmin 由 <ADMIN_UNION_ID> 配置指定（避免死锁）：命中即授予。
        if (!string.IsNullOrWhiteSpace(_feishuOptions.AdminUnionId)
            && string.Equals(_feishuOptions.AdminUnionId, feishuUser.UnionId, StringComparison.Ordinal))
        {
            await _userRepository.GrantRoleAsync(user.Id, RoleCodes.SysAdmin, cancellationToken).ConfigureAwait(false);
        }

        await _userRepository.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Provisioned new user {UserId} via Feishu SSO.", user.Id);
        return user;
    }

    private AuthTokenPair IssueTokens(User user, IReadOnlyList<string> roles)
    {
        var (accessToken, accessExpires) = _tokenService.CreateAccessToken(user, roles);
        var (refreshToken, refreshExpires) = _tokenService.CreateRefreshToken(user);
        return new AuthTokenPair(accessToken, refreshToken, accessExpires, refreshExpires);
    }
}
