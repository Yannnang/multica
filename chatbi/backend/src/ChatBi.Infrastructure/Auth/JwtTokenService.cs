using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using ChatBi.Application.Auth;
using ChatBi.Application.Configuration;
using ChatBi.Domain.Entities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace ChatBi.Infrastructure.Auth;

/// <summary>JWT claim 常量（设计基线 3.4：sub/union_id/role[]/dept_path/token_ver/exp）。</summary>
public static class ChatBiClaims
{
    public const string Subject = "sub";
    public const string UnionId = "union_id";
    public const string Name = "name";
    public const string Role = "role";
    public const string DepartmentPath = "dept_path";
    public const string TokenVersion = "token_ver";
    public const string TokenUse = "typ";

    public const string TokenUseAccess = "access";
    public const string TokenUseRefresh = "refresh";
}

/// <summary>
/// 本地自签 JWT（HS256）。红线：密钥仅来自环境变量 JWT_SIGNING_KEY，
/// 不落 appsettings、不落仓库、不进日志；必验签名/Issuer/Audience/过期/TokenVersion。
/// </summary>
public class JwtTokenService : ITokenService
{
    private const int MinimumSigningKeyLength = 32;

    private readonly JwtOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SymmetricSecurityKey _signingKey;
    private readonly SigningCredentials _signingCredentials;

    public JwtTokenService(IOptions<JwtOptions> options, TimeProvider timeProvider)
    {
        _options = options.Value;
        _timeProvider = timeProvider;

        if (string.IsNullOrWhiteSpace(_options.SigningKey))
        {
            throw new InvalidOperationException(
                $"JWT signing key is not configured. Set environment variable {JwtOptions.SigningKeyEnvironmentVariable}.");
        }

        if (_options.SigningKey.Length < MinimumSigningKeyLength)
        {
            throw new InvalidOperationException(
                $"JWT signing key must be at least {MinimumSigningKeyLength} characters (HS256).");
        }

        _signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        _signingCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256);
    }

    /// <summary>构造 TokenValidationParameters：仅允许 HS256（拒 alg=none 与算法混淆降级），全项校验。</summary>
    public TokenValidationParameters BuildTokenValidationParameters(bool validateLifetime) => new()
    {
        ValidateIssuer = true,
        ValidIssuer = _options.Issuer,
        ValidateAudience = true,
        ValidAudience = _options.Audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = _signingKey,
        ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },
        RequireSignedTokens = true,
        ValidateLifetime = validateLifetime,
        ClockSkew = TimeSpan.FromSeconds(30),
        RoleClaimType = ChatBiClaims.Role,
        NameClaimType = ChatBiClaims.Subject,
    };

    public (string Token, int ExpiresInSeconds) CreateAccessToken(User user, IReadOnlyList<string> roles)
    {
        var expiresInSeconds = _options.AccessTokenMinutes * 60;
        var claims = new List<Claim>
        {
            new(ChatBiClaims.Subject, user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new(ChatBiClaims.UnionId, user.UnionId),
            new(ChatBiClaims.Name, user.Name),
            new(ChatBiClaims.DepartmentPath, user.DepartmentPath ?? string.Empty),
            new(ChatBiClaims.TokenVersion, user.TokenVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new(ChatBiClaims.TokenUse, ChatBiClaims.TokenUseAccess),
        };
        claims.AddRange(roles.Select(r => new Claim(ChatBiClaims.Role, r)));

        return (CreateToken(claims, expiresInSeconds), expiresInSeconds);
    }

    public (string Token, int ExpiresInSeconds) CreateRefreshToken(User user)
    {
        var expiresInSeconds = _options.RefreshTokenDays * 24 * 60 * 60;
        var claims = new List<Claim>
        {
            new(ChatBiClaims.Subject, user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new(ChatBiClaims.TokenVersion, user.TokenVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new(ChatBiClaims.TokenUse, ChatBiClaims.TokenUseRefresh),
        };

        return (CreateToken(claims, expiresInSeconds), expiresInSeconds);
    }

    public RefreshTokenInfo? ValidateRefreshToken(string refreshToken)
    {
        try
        {
            // 保持字面 claim 名（sub 不被重映射为 ClaimTypes.NameIdentifier）。
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var principal = handler.ValidateToken(refreshToken, BuildTokenValidationParameters(validateLifetime: true), out _);

            var use = principal.FindFirst(ChatBiClaims.TokenUse)?.Value;
            if (use != ChatBiClaims.TokenUseRefresh)
            {
                return null; // access token 不能当 refresh 用
            }

            var sub = principal.FindFirst(ChatBiClaims.Subject)?.Value;
            var version = principal.FindFirst(ChatBiClaims.TokenVersion)?.Value;
            if (!long.TryParse(sub, out var userId) || !int.TryParse(version, out var tokenVersion))
            {
                return null;
            }

            return new RefreshTokenInfo(userId, tokenVersion);
        }
        catch (SecurityTokenException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>供测试构造过期/篡改 token 的辅助：以自定义过期时间签发。</summary>
    public string CreateTokenForTesting(IReadOnlyList<Claim> claims, DateTimeOffset expiresAtUtc)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = expiresAtUtc.AddHours(-2).UtcDateTime,
            NotBefore = expiresAtUtc.AddHours(-1).UtcDateTime, // 允许构造已过期的 token
            Expires = expiresAtUtc.UtcDateTime,
            SigningCredentials = _signingCredentials,
        };
        return new JwtSecurityTokenHandler().CreateToken(descriptor) is JwtSecurityToken jwt
            ? jwt.RawData
            : throw new InvalidOperationException("Failed to create test token.");
    }

    private string CreateToken(IReadOnlyList<Claim> claims, int expiresInSeconds)
    {
        var now = _timeProvider.GetUtcNow();
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = now.Add(TimeSpan.FromSeconds(expiresInSeconds)).UtcDateTime,
            SigningCredentials = _signingCredentials,
        };

        var token = new JwtSecurityTokenHandler().CreateToken(descriptor);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

/// <summary>
/// state 存储：随机、服务端存储、5 分钟一次性（IMemoryCache 实现，单机；多实例部署换分布式缓存，接口不变）。
/// </summary>
public class MemoryAuthStateStore : IAuthStateStore
{
    private const string CacheKeyPrefix = "auth-state:";
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(5);

    private readonly IMemoryCache _cache;

    public MemoryAuthStateStore(IMemoryCache cache)
    {
        _cache = cache;
    }

    public Task<string> CreateStateAsync(string? redirect, CancellationToken cancellationToken = default)
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var state = Base64UrlEncoder.Encode(bytes);

        _cache.Set(CacheKeyPrefix + state, redirect ?? string.Empty, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = StateLifetime,
        });

        return Task.FromResult(state);
    }

    public Task<string?> ConsumeStateAsync(string state, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            return Task.FromResult<string?>(null);
        }

        var key = CacheKeyPrefix + state;
        if (!_cache.TryGetValue(key, out string? redirect))
        {
            return Task.FromResult<string?>(null); // 缺失/过期/已重用
        }

        _cache.Remove(key); // 一次性：立即消费
        return Task.FromResult<string?>(redirect ?? string.Empty);
    }
}
