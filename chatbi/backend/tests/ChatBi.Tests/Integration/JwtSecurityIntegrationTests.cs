using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using ChatBi.Application.Auth;
using ChatBi.Domain.Enums;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ChatBi.Tests.Integration;

/// <summary>
/// JWT 用例（集成级）：篡改签名、过期、alg=none 降级、TokenVersion 失效，全部 401；
/// 401（无/坏 token）与 403（token 有效无权限）严格区分。
/// </summary>
[Collection(IntegrationCollection.Name)]
public class JwtSecurityIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly ChatBiWebApplicationFactory _factory;

    public JwtSecurityIntegrationTests(ChatBiWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<(string UnionId, AuthTokenPair Tokens, long UserId)> LoginNewUserAsync()
    {
        var unionId = $"ou_jwt_{Guid.NewGuid():N}"[..24];
        var client = _factory.CreateNoRedirectClient();

        var loginResponse = await client.GetAsync("/api/auth/feishu/login");
        var state = HttpUtility.ParseQueryString(loginResponse.Headers.Location!.Query)["state"]!;

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/auth/feishu/callback?code=mock-{unionId}&state={state}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var callback = await client.SendAsync(request);
        callback.StatusCode.Should().Be(HttpStatusCode.OK);
        var tokens = (await callback.Content.ReadFromJsonAsync<AuthTokenPair>(JsonOptions))!;

        await using var db = _factory.CreateDbContext();
        var userId = (await db.Users.SingleAsync(x => x.UnionId == unionId)).Id;

        return (unionId, tokens, userId);
    }

    private static HttpClient WithBearer(HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task NoToken_401()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/auth/me");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GarbageToken_401()
    {
        var client = WithBearer(_factory.CreateClient(), "this.is.not-a-jwt");
        var response = await client.GetAsync("/api/auth/me");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TamperedSignature_401()
    {
        var (_, tokens, _) = await LoginNewUserAsync();
        var tampered = TestTokenFactory.TamperSignature(tokens.AccessToken);

        var client = WithBearer(_factory.CreateClient(), tampered);
        var response = await client.GetAsync("/api/auth/me");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ExpiredToken_401()
    {
        var (_, _, userId) = await LoginNewUserAsync();
        var expired = TestTokenFactory.CreateExpiredAccess(
            ChatBiWebApplicationFactory.TestSigningKey, userId, 0, "BusinessUser");

        var client = WithBearer(_factory.CreateClient(), expired);
        var response = await client.GetAsync("/api/auth/me");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AlgNoneDowngrade_401()
    {
        var (_, _, userId) = await LoginNewUserAsync();
        var algNone = TestTokenFactory.CreateAlgNone(userId, 0, "SysAdmin");

        var client = WithBearer(_factory.CreateClient(), algNone);
        var response = await client.GetAsync("/api/auth/me");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ForgedWithWrongKey_401()
    {
        var forged = TestTokenFactory.CreateValidAccess(
            new string('k', 48), 1, 0, "SysAdmin");

        var client = WithBearer(_factory.CreateClient(), forged);
        var response = await client.GetAsync("/api/auth/me");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TokenVersionBumped_OldAccess_401()
    {
        var (_, tokens, userId) = await LoginNewUserAsync();

        await using (var db = _factory.CreateDbContext())
        {
            var user = await db.Users.SingleAsync(x => x.Id == userId);
            user.TokenVersion++;
            await db.SaveChangesAsync();
        }

        var client = WithBearer(_factory.CreateClient(), tokens.AccessToken);
        var response = await client.GetAsync("/api/auth/me");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_InvalidatesAccessAndRefresh()
    {
        var (_, tokens, _) = await LoginNewUserAsync();
        var client = WithBearer(_factory.CreateClient(), tokens.AccessToken);

        var logout = await client.PostAsync("/api/auth/logout", content: null);
        logout.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // 旧 access 立即失效（TokenVersion++）。
        var me = await client.GetAsync("/api/auth/me");
        me.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // 旧 refresh 也失效。
        var anonymous = _factory.CreateClient();
        var refresh = await anonymous.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = tokens.RefreshToken });
        refresh.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await refresh.Content.ReadAsStringAsync()).Should().Contain("AUTH_REFRESH_INVALID");
    }

    [Fact]
    public async Task Refresh_Sliding_NewPairWorks_OldAccessAsRefreshRejected()
    {
        var (_, tokens, _) = await LoginNewUserAsync();
        var client = _factory.CreateClient();

        var refreshResponse = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = tokens.RefreshToken });
        refreshResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var newPair = (await refreshResponse.Content.ReadFromJsonAsync<AuthTokenPair>(JsonOptions))!;
        newPair.AccessToken.Should().NotBeNullOrWhiteSpace();

        // 新 access 可用。
        var newClient = WithBearer(_factory.CreateClient(), newPair.AccessToken);
        (await newClient.GetAsync("/api/auth/me")).StatusCode.Should().Be(HttpStatusCode.OK);

        // access token 不能当 refresh 用（typ 校验）。
        var abuse = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = tokens.AccessToken });
        abuse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_GarbageToken_401()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = "garbage" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).Should().Contain("AUTH_REFRESH_INVALID");
    }

    [Fact]
    public async Task DisabledUser_OldAccessToken_401()
    {
        var (unionId, tokens, userId) = await LoginNewUserAsync();

        await using (var db = _factory.CreateDbContext())
        {
            var user = await db.Users.SingleAsync(x => x.Id == userId);
            user.Status = UserStatus.Disabled;
            await db.SaveChangesAsync();
        }

        var client = WithBearer(_factory.CreateClient(), tokens.AccessToken);
        var me = await client.GetAsync("/api/auth/me");

        me.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        unionId.Should().NotBeNull();
    }
}
