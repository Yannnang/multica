namespace ChatBi.Application.Configuration;

/// <summary>
/// JWT 配置（假设值，配置化）。红线：SigningKey 仅来自环境变量 JWT_SIGNING_KEY，
/// 不落 appsettings、不落仓库、不进日志。
/// </summary>
public class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>环境变量名——唯一合法密钥来源。</summary>
    public const string SigningKeyEnvironmentVariable = "JWT_SIGNING_KEY";

    public string SigningKey { get; set; } = string.Empty;

    public string Issuer { get; set; } = "chatbi";

    public string Audience { get; set; } = "chatbi-web";

    /// <summary>access 60min（假设值，配置化）。</summary>
    public int AccessTokenMinutes { get; set; } = 60;

    /// <summary>refresh 7d 滑动（假设值，配置化）。</summary>
    public int RefreshTokenDays { get; set; } = 7;
}

/// <summary>飞书开放平台配置（全占位符，路径以官方文档实测为准，S0 后锁定）。</summary>
public class FeishuOptions
{
    public const string SectionName = "Feishu";

    /// <summary>true=使用进程内 mock 客户端（契约测试与 S0 未完成时开发）；false=真实飞书。</summary>
    public bool UseMock { get; set; }

    public string AppId { get; set; } = string.Empty;

    /// <summary>仅来自环境变量/密钥管理，不落仓库。</summary>
    public string AppSecret { get; set; } = string.Empty;

    public string RedirectUri { get; set; } = string.Empty;

    /// <summary>空格分隔 scope 列表。</summary>
    public string ScopeList { get; set; } = string.Empty;

    /// <summary>授权页域名（企业自建应用、中国大陆飞书；商店应用/Lark 海外域名不同，S0 纠正）。</summary>
    public string AuthorizeBaseUrl { get; set; } = "https://accounts.feishu.cn";

    /// <summary>OpenAPI 域名。</summary>
    public string OpenApiBaseUrl { get; set; } = "https://open.feishu.cn";

    /// <summary>首个 SysAdmin 的 union_id（&lt;ADMIN_UNION_ID&gt;，避免管理员死锁；登录建档时授予）。</summary>
    public string? AdminUnionId { get; set; }
}

/// <summary>登录回跳白名单（防开放重定向：仅允许配置前缀内的相对路径）。</summary>
public class AuthRedirectOptions
{
    public const string SectionName = "AuthRedirect";

    public string DefaultRedirect { get; set; } = "/";

    public IList<string> AllowedPrefixes { get; set; } = new List<string> { "/" };
}
