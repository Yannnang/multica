using ChatBi.Domain.Entities;

namespace ChatBi.Application.Auth;

/// <summary>飞书用户信息最小集（S1 按最小集设计，字段可得性依赖实际开通 scope，S0 后实测锁定）。</summary>
public record FeishuUserInfo(
    string UnionId,
    string? OpenId,
    string Name,
    string? Email,
    string? AvatarUrl,
    string? DepartmentPath);

/// <summary>本地签发的 token 对（业务会话唯一凭证是本地 JWT；user_access_token 用后即弃不外发）。</summary>
public record AuthTokenPair(
    string AccessToken,
    string RefreshToken,
    int AccessExpiresInSeconds,
    int RefreshExpiresInSeconds);

/// <summary>登录成功后返回给调用方的会话信息。</summary>
public record CallbackSuccess(
    AuthTokenPair Tokens,
    long UserId,
    string DisplayName,
    IReadOnlyList<string> Roles,
    string RedirectPath);

/// <summary>refresh token 载荷（验证通过后用于重签发）。</summary>
public record RefreshTokenInfo(long UserId, int TokenVersion);

/// <summary>飞书开放平台客户端抽象。真实实现走 IHttpClientFactory + Polly；mock 实现供契约测试与 S0 未完成时开发。</summary>
public interface IFeishuAuthClient
{
    /// <summary>构造授权页 URL（含 client_id、redirect_uri、state、scope）。</summary>
    string BuildAuthorizeUrl(string state, string? redirect);

    /// <summary>code 换 user_access_token【接口路径以官方文档实测为准，S0 后锁定】。</summary>
    Task<string> ExchangeCodeForUserAccessTokenAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>拉取用户信息（union_id、name、avatar…）。user_access_token 仅用后即弃，不持久化、不进日志。</summary>
    Task<FeishuUserInfo> GetUserInfoAsync(string userAccessToken, CancellationToken cancellationToken = default);
}

/// <summary>state 存储：随机、服务端存储、5 分钟一次性（缺失/过期/重用即拒绝）。</summary>
public interface IAuthStateStore
{
    /// <summary>创建一次性 state，并暂存登录完成后的回跳相对路径。</summary>
    Task<string> CreateStateAsync(string? redirect, CancellationToken cancellationToken = default);

    /// <summary>校验并消费 state（一次性）；返回暂存的回跳路径。无效/过期/重用时返回 null。</summary>
    Task<string?> ConsumeStateAsync(string state, CancellationToken cancellationToken = default);
}

/// <summary>本地 JWT 签发与校验（HS256；密钥仅来自环境变量 JWT_SIGNING_KEY）。</summary>
public interface ITokenService
{
    (string Token, int ExpiresInSeconds) CreateAccessToken(User user, IReadOnlyList<string> roles);

    (string Token, int ExpiresInSeconds) CreateRefreshToken(User user);

    /// <summary>校验 refresh token（签名/Issuer/Audience/过期/typ）；失败返回 null。</summary>
    RefreshTokenInfo? ValidateRefreshToken(string refreshToken);
}

/// <summary>用户账号仓储（Infrastructure EF 实现）。</summary>
public interface IUserAccountRepository
{
    Task<User?> FindByUnionIdAsync(string unionId, CancellationToken cancellationToken = default);

    Task<User?> FindByIdAsync(long userId, CancellationToken cancellationToken = default);

    Task AddAsync(User user, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetRoleCodesAsync(long userId, CancellationToken cancellationToken = default);

    /// <summary>授予角色（幂等）。</summary>
    Task GrantRoleAsync(long userId, string roleCode, CancellationToken cancellationToken = default);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
