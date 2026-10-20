using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatBi.Application.Auth;
using ChatBi.Application.Common;
using ChatBi.Application.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChatBi.Infrastructure.Auth;

/// <summary>
/// 飞书开放平台客户端（企业自建应用、中国大陆域名）。
/// 接口路径按设计文档标注「需查证」项，以官方文档当前版本实现，S0 后台配置完成后实测锁定：
///   - 授权页：GET {AuthorizeBaseUrl}/open-apis/authen/v1/authorize（authorization_code）
///   - code 换 user_access_token：POST {OpenApiBaseUrl}/open-apis/authen/v2/oauth/token
///     （v2 端点直接用 client_id + client_secret，无需先取 app_access_token——实测锁定后回填 MYIT-15）
///   - 用户信息：GET {OpenApiBaseUrl}/open-apis/authen/v1/user_info（Bearer user_access_token）
/// 红线：user_access_token 用后即弃，不持久化、不进日志。
/// </summary>
public class FeishuAuthClient : IFeishuAuthClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _httpClient;
    private readonly FeishuOptions _options;
    private readonly ILogger<FeishuAuthClient> _logger;

    public FeishuAuthClient(HttpClient httpClient, IOptions<FeishuOptions> options, ILogger<FeishuAuthClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public string BuildAuthorizeUrl(string state, string? redirect)
    {
        var query = new List<string>
        {
            $"client_id={Uri.EscapeDataString(_options.AppId)}",
            $"redirect_uri={Uri.EscapeDataString(_options.RedirectUri)}",
            $"state={Uri.EscapeDataString(state)}",
            "response_type=code",
        };
        if (!string.IsNullOrWhiteSpace(_options.ScopeList))
        {
            query.Add($"scope={Uri.EscapeDataString(_options.ScopeList)}");
        }

        if (!string.IsNullOrWhiteSpace(redirect))
        {
            // 回跳相对路径经 state 暂存传递，不进飞书 URL（防开放重定向由服务端白名单控制）。
            query.Add($"chatbi_redirect={Uri.EscapeDataString(redirect)}");
        }

        return $"{_options.AuthorizeBaseUrl.TrimEnd('/')}/open-apis/authen/v1/authorize?{string.Join('&', query)}";
    }

    public async Task<string> ExchangeCodeForUserAccessTokenAsync(string code, CancellationToken cancellationToken = default)
    {
        var request = new
        {
            grant_type = "authorization_code",
            client_id = _options.AppId,
            client_secret = _options.AppSecret,
            code,
            redirect_uri = _options.RedirectUri,
        };

        var response = await SendAsHttpErrorAsync(
            () => _httpClient.PostAsJsonAsync("/open-apis/authen/v2/oauth/token", request, JsonOptions, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        var payload = await response.Content.ReadFromJsonAsync<FeishuTokenResponse>(JsonOptions, cancellationToken).ConfigureAwait(false);
        if (payload is null || string.IsNullOrWhiteSpace(payload.AccessToken))
        {
            // 只记业务码不记响应体（响应体可能含 token）。
            _logger.LogWarning("Feishu token exchange failed. code_hint={CodeHint}", payload?.Error ?? payload?.Code?.ToString());
            throw new AuthCodeInvalidException();
        }

        return payload.AccessToken;
    }

    public async Task<FeishuUserInfo> GetUserInfoAsync(string userAccessToken, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/open-apis/authen/v1/user_info");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userAccessToken);

        var response = await SendAsHttpErrorAsync(() => _httpClient.SendAsync(request, cancellationToken), cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadFromJsonAsync<FeishuUserInfoResponse>(JsonOptions, cancellationToken).ConfigureAwait(false);

        if (payload?.Data is null || string.IsNullOrWhiteSpace(payload.Data.UnionId))
        {
            _logger.LogWarning("Feishu user_info returned no union_id. code_hint={CodeHint}", payload?.Code);
            throw new HttpRequestException("Feishu user_info payload invalid.");
        }

        var data = payload.Data;
        return new FeishuUserInfo(
            data.UnionId,
            data.OpenId,
            string.IsNullOrWhiteSpace(data.Name) ? data.UnionId : data.Name,
            string.IsNullOrWhiteSpace(data.Email) ? data.EnterpriseEmail : data.Email,
            data.AvatarUrl,
            DepartmentPath: null); // 部门路径 user_info 不带出；拿不到置 NULL，行级权限退化 SelfOnly（设计基线 4.4）
    }

    /// <summary>把超时/传输失败统一为 HttpRequestException，由 AuthService 映射 AUTH_UPSTREAM_UNAVAILABLE。</summary>
    private async Task<HttpResponseMessage> SendAsHttpErrorAsync(
        Func<Task<HttpResponseMessage>> send,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await send().ConfigureAwait(false);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException("Feishu request timed out.", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Feishu upstream returned {StatusCode} for {Path}.", (int)response.StatusCode, response.RequestMessage?.RequestUri?.AbsolutePath);
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new AuthCodeInvalidException();
            }

            throw new HttpRequestException($"Feishu upstream returned {(int)response.StatusCode}.");
        }

        return response;
    }

    private sealed record FeishuTokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("token_type")] string? TokenType,
        [property: JsonPropertyName("expires_in")] int? ExpiresIn,
        [property: JsonPropertyName("code")] int? Code,
        [property: JsonPropertyName("error")] string? Error);

    private sealed record FeishuUserInfoResponse(
        [property: JsonPropertyName("code")] int? Code,
        [property: JsonPropertyName("msg")] string? Msg,
        [property: JsonPropertyName("data")] FeishuUserData? Data);

    private sealed record FeishuUserData(
        [property: JsonPropertyName("union_id")] string? UnionId,
        [property: JsonPropertyName("open_id")] string? OpenId,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("avatar_url")] string? AvatarUrl,
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("enterprise_email")] string? EnterpriseEmail);
}

/// <summary>
/// 飞书 mock 客户端（配置开关 Feishu:UseMock=true 启用）：供契约测试与 S0 未完成时开发。
/// code 约定：
///   - <c>mock-{unionId}</c>：成功，返回该 union_id 的确定性用户（Name=Mock User {unionId}）；
///     unionId 形如 <c>dept-{n}</c> 时附带 DepartmentPath（模拟可拿到部门的场景）。
///   - <c>mock-upstream-down</c>：模拟飞书不可用（HttpRequestException → AUTH_UPSTREAM_UNAVAILABLE）。
///   - 其他：AUTH_CODE_INVALID。
/// </summary>
public class MockFeishuAuthClient : IFeishuAuthClient
{
    public const string UpstreamDownCode = "mock-upstream-down";
    private const string MockCodePrefix = "mock-";

    private readonly FeishuOptions _options;

    public MockFeishuAuthClient(IOptions<FeishuOptions> options)
    {
        _options = options.Value;
    }

    public string BuildAuthorizeUrl(string state, string? redirect)
    {
        var url = $"https://mock.feishu.local/open-apis/authen/v1/authorize?client_id={Uri.EscapeDataString(_options.AppId)}&state={Uri.EscapeDataString(state)}";
        if (!string.IsNullOrWhiteSpace(redirect))
        {
            url += $"&chatbi_redirect={Uri.EscapeDataString(redirect)}";
        }

        return url;
    }

    public Task<string> ExchangeCodeForUserAccessTokenAsync(string code, CancellationToken cancellationToken = default)
    {
        if (code == UpstreamDownCode)
        {
            throw new HttpRequestException("Mock Feishu upstream unavailable.");
        }

        if (string.IsNullOrWhiteSpace(code) || !code.StartsWith(MockCodePrefix, StringComparison.Ordinal) || code.Length == MockCodePrefix.Length)
        {
            throw new AuthCodeInvalidException();
        }

        var unionId = code[MockCodePrefix.Length..];
        return Task.FromResult($"mock-user-access-token::{unionId}");
    }

    public Task<FeishuUserInfo> GetUserInfoAsync(string userAccessToken, CancellationToken cancellationToken = default)
    {
        const string tokenPrefix = "mock-user-access-token::";
        if (!userAccessToken.StartsWith(tokenPrefix, StringComparison.Ordinal))
        {
            throw new HttpRequestException("Mock user access token invalid.");
        }

        var unionId = userAccessToken[tokenPrefix.Length..];
        var departmentPath = unionId.StartsWith("dept-", StringComparison.Ordinal) ? "/总部/技术部/后端组" : null;

        return Task.FromResult(new FeishuUserInfo(
            unionId,
            $"mock-open-{unionId}",
            $"Mock User {unionId}",
            $"{unionId}@mock.feishu.local",
            "https://mock.feishu.local/avatar.png",
            departmentPath));
    }
}
