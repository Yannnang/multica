using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using ChatBi.Application.Configuration;
using ChatBi.Domain.Entities;
using ChatBi.Domain.Enums;
using ChatBi.Domain.Security;
using ChatBi.Infrastructure.Auth;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace ChatBi.Tests.Unit;

public class JwtTokenServiceTests
{
    private const string TestKey = "unit-test-signing-key-0123456789abcdef"; // 仅测试用，非真实密钥

    private static JwtTokenService CreateService(int accessMinutes = 60, int refreshDays = 7) =>
        new(
            Options.Create(new JwtOptions
            {
                SigningKey = TestKey,
                Issuer = "chatbi",
                Audience = "chatbi-web",
                AccessTokenMinutes = accessMinutes,
                RefreshTokenDays = refreshDays,
            }),
            TimeProvider.System);

    private static User TestUser(int tokenVersion = 0) => new()
    {
        Id = 7,
        UnionId = "ou_test_1",
        Name = "测试用户",
        Status = UserStatus.Active,
        TokenVersion = tokenVersion,
        DepartmentPath = "/总部/技术部",
    };

    [Fact]
    public void MissingSigningKey_Throws()
    {
        var act = () => new JwtTokenService(
            Options.Create(new JwtOptions { SigningKey = string.Empty }),
            TimeProvider.System);

        act.Should().Throw<InvalidOperationException>().WithMessage("*JWT_SIGNING_KEY*");
    }

    [Fact]
    public void ShortSigningKey_Throws()
    {
        var act = () => new JwtTokenService(
            Options.Create(new JwtOptions { SigningKey = "too-short" }),
            TimeProvider.System);

        act.Should().Throw<InvalidOperationException>().WithMessage("*32*");
    }

    [Fact]
    public void AccessToken_ContainsDesignClaims_AndValidates()
    {
        var service = CreateService();
        var user = TestUser(tokenVersion: 3);

        var (token, expires) = service.CreateAccessToken(user, new[] { RoleCodes.BusinessUser });

        expires.Should().Be(60 * 60);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        jwt.Header.Alg.Should().Be(SecurityAlgorithms.HmacSha256);
        jwt.Claims.First(c => c.Type == ChatBiClaims.Subject).Value.Should().Be("7");
        jwt.Claims.First(c => c.Type == ChatBiClaims.UnionId).Value.Should().Be("ou_test_1");
        jwt.Claims.First(c => c.Type == ChatBiClaims.Name).Value.Should().Be("测试用户");
        jwt.Claims.First(c => c.Type == ChatBiClaims.DepartmentPath).Value.Should().Be("/总部/技术部");
        jwt.Claims.First(c => c.Type == ChatBiClaims.TokenVersion).Value.Should().Be("3");
        jwt.Claims.First(c => c.Type == ChatBiClaims.TokenUse).Value.Should().Be(ChatBiClaims.TokenUseAccess);
        jwt.Claims.Where(c => c.Type == ChatBiClaims.Role).Select(c => c.Value).Should().Contain(RoleCodes.BusinessUser);

        var principal = new JwtSecurityTokenHandler()
            .ValidateToken(token, service.BuildTokenValidationParameters(validateLifetime: true), out _);
        principal.Identity!.IsAuthenticated.Should().BeTrue();
    }

    [Fact]
    public void TamperedSignature_Rejected()
    {
        var service = CreateService();
        var (token, _) = service.CreateAccessToken(TestUser(), new[] { RoleCodes.BusinessUser });

        var parts = token.Split('.');
        var signature = parts[2].ToCharArray();
        signature[0] = signature[0] == 'A' ? 'B' : 'A'; // 篡改签名
        var tampered = $"{parts[0]}.{parts[1]}.{new string(signature)}";

        var act = () => new JwtSecurityTokenHandler()
            .ValidateToken(tampered, service.BuildTokenValidationParameters(validateLifetime: true), out _);

        act.Should().Throw<SecurityTokenSignatureKeyNotFoundException>();
    }

    [Fact]
    public void ExpiredToken_Rejected()
    {
        var service = CreateService();
        var expired = service.CreateTokenForTesting(
            new[]
            {
                new Claim(ChatBiClaims.Subject, "7"),
                new Claim(ChatBiClaims.TokenVersion, "0"),
                new Claim(ChatBiClaims.TokenUse, ChatBiClaims.TokenUseAccess),
            },
            DateTimeOffset.UtcNow.AddMinutes(-10));

        var act = () => new JwtSecurityTokenHandler()
            .ValidateToken(expired, service.BuildTokenValidationParameters(validateLifetime: true), out _);

        act.Should().Throw<SecurityTokenExpiredException>();
    }

    [Fact]
    public void AlgNoneToken_Rejected()
    {
        var service = CreateService();
        var header = Base64UrlEncoder.Encode("""{"alg":"none","typ":"JWT"}""");
        var payload = Base64UrlEncoder.Encode($$"""
            {"sub":"7","token_ver":"0","typ":"access","iss":"chatbi","aud":"chatbi-web","exp":{{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}}}
            """);
        var algNone = $"{header}.{payload}.";

        var act = () => new JwtSecurityTokenHandler()
            .ValidateToken(algNone, service.BuildTokenValidationParameters(validateLifetime: true), out _);

        act.Should().Throw<SecurityTokenException>();
    }

    [Fact]
    public void WrongIssuerOrAudience_Rejected()
    {
        var service = CreateService();
        var (token, _) = service.CreateAccessToken(TestUser(), new[] { RoleCodes.BusinessUser });

        var badIssuer = service.BuildTokenValidationParameters(validateLifetime: true);
        badIssuer.ValidIssuer = "someone-else";
        var act1 = () => new JwtSecurityTokenHandler().ValidateToken(token, badIssuer, out _);
        act1.Should().Throw<SecurityTokenInvalidIssuerException>();

        var badAudience = service.BuildTokenValidationParameters(validateLifetime: true);
        badAudience.ValidAudience = "someone-else";
        var act2 = () => new JwtSecurityTokenHandler().ValidateToken(token, badAudience, out _);
        act2.Should().Throw<SecurityTokenInvalidAudienceException>();
    }

    [Fact]
    public void RefreshToken_Validates_AndAccessAsRefresh_Rejected()
    {
        var service = CreateService();
        var user = TestUser(tokenVersion: 5);

        var (refresh, refreshExpires) = service.CreateRefreshToken(user);
        refreshExpires.Should().Be(7 * 24 * 60 * 60);

        var info = service.ValidateRefreshToken(refresh);
        info.Should().NotBeNull();
        info!.UserId.Should().Be(7);
        info.TokenVersion.Should().Be(5);

        // access token 不能当 refresh 用（typ 校验）。
        var (access, _) = service.CreateAccessToken(user, new[] { RoleCodes.BusinessUser });
        service.ValidateRefreshToken(access).Should().BeNull();
    }

    [Fact]
    public void RefreshToken_TamperedOrGarbage_ReturnsNull()
    {
        var service = CreateService();
        service.ValidateRefreshToken("not-a-jwt").Should().BeNull();
        service.ValidateRefreshToken(string.Empty).Should().BeNull();

        var (refresh, _) = service.CreateRefreshToken(TestUser());
        var parts = refresh.Split('.');
        service.ValidateRefreshToken($"{parts[0]}.{parts[1]}.AAAA").Should().BeNull();
    }

    [Fact]
    public void TokenSignedWithDifferentKey_Rejected()
    {
        var service = CreateService();
        var other = new JwtTokenService(
            Options.Create(new JwtOptions { SigningKey = new string('x', 40) }),
            TimeProvider.System);
        var (foreignToken, _) = other.CreateAccessToken(TestUser(), new[] { RoleCodes.SysAdmin });

        var act = () => new JwtSecurityTokenHandler()
            .ValidateToken(foreignToken, service.BuildTokenValidationParameters(validateLifetime: true), out _);

        act.Should().Throw<SecurityTokenSignatureKeyNotFoundException>();
    }

    [Fact]
    public void ValidationParameters_OnlyAllowHmacSha256()
    {
        var parameters = CreateService().BuildTokenValidationParameters(validateLifetime: true);
        parameters.ValidAlgorithms.Should().Equal(SecurityAlgorithms.HmacSha256);
        parameters.RequireSignedTokens.Should().BeTrue();
    }
}

public class MemoryAuthStateStoreTests
{
    private static MemoryAuthStateStore CreateStore() => new(new MemoryCache(new MemoryCacheOptions()));

    [Fact]
    public async Task CreatedState_IsConsumableOnce()
    {
        var store = CreateStore();
        var state = await store.CreateStateAsync("/chat");

        state.Should().NotBeNullOrWhiteSpace();
        (await store.ConsumeStateAsync(state)).Should().Be("/chat");
        (await store.ConsumeStateAsync(state)).Should().BeNull("state 一次性，重用必须拒绝");
    }

    [Fact]
    public async Task UnknownOrEmptyState_ReturnsNull()
    {
        var store = CreateStore();
        (await store.ConsumeStateAsync("never-issued")).Should().BeNull();
        (await store.ConsumeStateAsync(string.Empty)).Should().BeNull();
    }

    [Fact]
    public async Task TwoStates_AreDistinct()
    {
        var store = CreateStore();
        var s1 = await store.CreateStateAsync(null);
        var s2 = await store.CreateStateAsync(null);

        s1.Should().NotBe(s2);
        (await store.ConsumeStateAsync(s1)).Should().Be(string.Empty);
        (await store.ConsumeStateAsync(s2)).Should().Be(string.Empty);
    }
}
