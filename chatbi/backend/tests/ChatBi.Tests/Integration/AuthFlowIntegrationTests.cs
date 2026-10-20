using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using ChatBi.Api.Controllers;
using ChatBi.Application.Auth;
using ChatBi.Domain.Entities;
using ChatBi.Domain.Enums;
using ChatBi.Infrastructure.Auth;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ChatBi.Tests.Integration;

/// <summary>SSO 契约集成测试（WebApplicationFactory + 飞书 mock）：建档、再登录、state/code 错误、上游故障、禁用。</summary>
[Collection(IntegrationCollection.Name)]
public class AuthFlowIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly ChatBiWebApplicationFactory _factory;

    public AuthFlowIntegrationTests(ChatBiWebApplicationFactory factory)
    {
        _factory = factory;
    }

    /// <summary>走完整浏览器流：login 拿 state → callback 换 token（JSON 流）。</summary>
    private async Task<(AuthTokenPair Tokens, HttpResponseMessage CallbackResponse)> LoginAsync(
        string code, string redirect = "/chat")
    {
        var client = _factory.CreateNoRedirectClient();

        var loginResponse = await client.GetAsync($"/api/auth/feishu/login?redirect={Uri.EscapeDataString(redirect)}");
        loginResponse.StatusCode.Should().Be(HttpStatusCode.Found);
        var state = ParseState(loginResponse.Headers.Location!);

        var callbackRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/auth/feishu/callback?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state)}");
        callbackRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var callbackResponse = await client.SendAsync(callbackRequest);

        if (callbackResponse.StatusCode != HttpStatusCode.OK)
        {
            return (null!, callbackResponse);
        }

        var tokens = await callbackResponse.Content.ReadFromJsonAsync<AuthTokenPair>(JsonOptions);
        return (tokens!, callbackResponse);
    }

    private static string ParseState(Uri location)
    {
        var query = HttpUtility.ParseQueryString(location.Query);
        var state = query["state"];
        state.Should().NotBeNullOrWhiteSpace("login 必须携带一次性 state");
        return state!;
    }

    [Fact]
    public async Task Login_Returns302_WithState_AndMockAuthorizeUrl()
    {
        var client = _factory.CreateNoRedirectClient();

        var response = await client.GetAsync("/api/auth/feishu/login?redirect=/chat");

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var location = response.Headers.Location!;
        location.Host.Should().Be("mock.feishu.local");
        location.AbsolutePath.Should().Be("/open-apis/authen/v1/authorize");
        HttpUtility.ParseQueryString(location.Query)["client_id"].Should().Be("cli_test_app");
        HttpUtility.ParseQueryString(location.Query)["state"].Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Callback_FirstLogin_CreatesUserWithBusinessUser_AndReturnsTokens()
    {
        var unionId = $"ou_first_{Guid.NewGuid():N}"[..24];
        var (tokens, response) = await LoginAsync($"mock-{unionId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        tokens.AccessToken.Should().NotBeNullOrWhiteSpace();
        tokens.RefreshToken.Should().NotBeNullOrWhiteSpace();
        tokens.AccessExpiresInSeconds.Should().Be(60 * 60);
        tokens.RefreshExpiresInSeconds.Should().Be(7 * 24 * 60 * 60);

        using var db = _factory.CreateDbContext();
        var user = await db.Users.SingleAsync(x => x.UnionId == unionId);
        user.Name.Should().Be($"Mock User {unionId}");
        user.Status.Should().Be(UserStatus.Active);
        user.DepartmentPath.Should().BeNull("mock 用户不带部门 → 行级权限保守退化 SelfOnly");

        var roles = await (from ur in db.UserRoles
                          join r in db.Roles on ur.RoleId equals r.Id
                          where ur.UserId == user.Id
                          select r.Code).ToListAsync();
        roles.Should().Equal("BusinessUser");
    }

    [Fact]
    public async Task Callback_SecondLogin_SameUser_NoDuplicate()
    {
        var unionId = $"ou_again_{Guid.NewGuid():N}"[..24];
        var (first, _) = await LoginAsync($"mock-{unionId}");
        var (second, _) = await LoginAsync($"mock-{unionId}");

        first.Should().NotBeNull();
        second.Should().NotBeNull();

        using var db = _factory.CreateDbContext();
        (await db.Users.CountAsync(x => x.UnionId == unionId)).Should().Be(1);
        var user = await db.Users.SingleAsync(x => x.UnionId == unionId);
        user.LastLoginAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Callback_BrowserFlow_302_ToWhitelistedRelativePath_WithFragmentTokens()
    {
        var unionId = $"ou_frag_{Guid.NewGuid():N}"[..24];
        var client = _factory.CreateNoRedirectClient();
        var loginResponse = await client.GetAsync("/api/auth/feishu/login?redirect=/chat/room1");
        var state = ParseState(loginResponse.Headers.Location!);

        var callback = await client.GetAsync($"/api/auth/feishu/callback?code=mock-{unionId}&state={state}");

        callback.StatusCode.Should().Be(HttpStatusCode.Found);
        var location = callback.Headers.Location!.ToString();
        location.Should().StartWith("/chat/room1#access_token=");
        location.Should().Contain("refresh_token=");
        location.Should().Contain("token_type=Bearer");
    }

    [Fact]
    public async Task Callback_OpenRedirectAttempt_FallsBackToDefaultPath()
    {
        var unionId = $"ou_evil_{Guid.NewGuid():N}"[..24];
        var client = _factory.CreateNoRedirectClient();
        var loginResponse = await client.GetAsync("/api/auth/feishu/login?redirect=https%3A%2F%2Fevil.example.com");
        var state = ParseState(loginResponse.Headers.Location!);

        var callback = await client.GetAsync($"/api/auth/feishu/callback?code=mock-{unionId}&state={state}");

        callback.StatusCode.Should().Be(HttpStatusCode.Found);
        var location = callback.Headers.Location!.ToString();
        location.Should().StartWith("/#access_token=");
        location.Should().NotContain("evil.example.com");
    }

    [Fact]
    public async Task Callback_StateReused_401_AuthStateInvalid()
    {
        var client = _factory.CreateNoRedirectClient();
        var loginResponse = await client.GetAsync("/api/auth/feishu/login");
        var state = ParseState(loginResponse.Headers.Location!);

        var first = await client.GetAsync($"/api/auth/feishu/callback?code=mock-ou_reuse&state={state}");
        first.StatusCode.Should().Be(HttpStatusCode.Found, "首次消费 state 成功（默认浏览器流为 302）");

        var second = await client.GetAsync($"/api/auth/feishu/callback?code=mock-ou_reuse&state={state}");
        second.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var problem = await second.Content.ReadAsStringAsync();
        problem.Should().Contain("AUTH_STATE_INVALID");
    }

    [Fact]
    public async Task Callback_UnknownState_401_AuthStateInvalid()
    {
        var client = _factory.CreateNoRedirectClient();
        var response = await client.GetAsync("/api/auth/feishu/callback?code=mock-ou_x&state=forged-state-value");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).Should().Contain("AUTH_STATE_INVALID");
    }

    [Fact]
    public async Task Callback_MissingState_401_AuthStateInvalid()
    {
        var client = _factory.CreateNoRedirectClient();
        var response = await client.GetAsync("/api/auth/feishu/callback?code=mock-ou_x");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).Should().Contain("AUTH_STATE_INVALID");
    }

    [Fact]
    public async Task Callback_InvalidCode_401_AuthCodeInvalid()
    {
        var client = _factory.CreateNoRedirectClient();
        var loginResponse = await client.GetAsync("/api/auth/feishu/login");
        var state = ParseState(loginResponse.Headers.Location!);

        var response = await client.GetAsync($"/api/auth/feishu/callback?code=not-a-mock-code&state={state}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).Should().Contain("AUTH_CODE_INVALID");
    }

    [Fact]
    public async Task Callback_MissingCode_401_AuthCodeInvalid()
    {
        var client = _factory.CreateNoRedirectClient();
        var loginResponse = await client.GetAsync("/api/auth/feishu/login");
        var state = ParseState(loginResponse.Headers.Location!);

        var response = await client.GetAsync($"/api/auth/feishu/callback?state={state}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).Should().Contain("AUTH_CODE_INVALID");
    }

    [Fact]
    public async Task Callback_UpstreamDown_502_AuthUpstreamUnavailable()
    {
        var client = _factory.CreateNoRedirectClient();
        var loginResponse = await client.GetAsync("/api/auth/feishu/login");
        var state = ParseState(loginResponse.Headers.Location!);

        var response = await client.GetAsync($"/api/auth/feishu/callback?code={MockFeishuAuthClient.UpstreamDownCode}&state={state}");

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await response.Content.ReadAsStringAsync()).Should().Contain("AUTH_UPSTREAM_UNAVAILABLE");
    }

    [Fact]
    public async Task Callback_DisabledUser_403_AuthUserDisabled()
    {
        var unionId = $"ou_disabled_{Guid.NewGuid():N}"[..24];
        await LoginAsync($"mock-{unionId}");

        await using (var db = _factory.CreateDbContext())
        {
            var user = await db.Users.SingleAsync(x => x.UnionId == unionId);
            user.Status = UserStatus.Disabled;
            user.TokenVersion++;
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateNoRedirectClient();
        var loginResponse = await client.GetAsync("/api/auth/feishu/login");
        var state = ParseState(loginResponse.Headers.Location!);
        var response = await client.GetAsync($"/api/auth/feishu/callback?code=mock-{unionId}&state={state}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("AUTH_USER_DISABLED");
    }

    [Fact]
    public async Task AdminUnionId_FirstLogin_GetsSysAdmin()
    {
        var (tokens, response) = await LoginAsync($"mock-{ChatBiWebApplicationFactory.AdminUnionId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var me = await client.GetFromJsonAsync<MeResponse>( "/api/auth/me", JsonOptions);

        me!.Roles.Should().Contain("SysAdmin");
        me.Permissions.Should().Contain("users:manage").And.Contain("audit:read");
    }
}
