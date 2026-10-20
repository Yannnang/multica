namespace ChatBi.Application.Common;

/// <summary>统一错误体（错误走 ProblemDetails + code 字段）。</summary>
public record ApiError(string Code, string Message);

/// <summary>机器可读错误码常量（登录失败错误码见设计基线 4.6）。</summary>
public static class ErrorCodes
{
    public const string AuthStateInvalid = "AUTH_STATE_INVALID";
    public const string AuthCodeInvalid = "AUTH_CODE_INVALID";
    public const string AuthUpstreamUnavailable = "AUTH_UPSTREAM_UNAVAILABLE";
    public const string AuthUserDisabled = "AUTH_USER_DISABLED";
    public const string AuthRefreshInvalid = "AUTH_REFRESH_INVALID";
}

/// <summary>认证/授权流程异常基类：携带机器可读错误码与 HTTP 状态。</summary>
public abstract class AuthException : Exception
{
    protected AuthException(string code, int httpStatus, string message)
        : base(message)
    {
        Code = code;
        HttpStatus = httpStatus;
    }

    public string Code { get; }

    public int HttpStatus { get; }
}

/// <summary>state 缺失/过期/重用 → 401 AUTH_STATE_INVALID。</summary>
public sealed class AuthStateInvalidException : AuthException
{
    public AuthStateInvalidException()
        : base(ErrorCodes.AuthStateInvalid, StatusCodes401, "登录状态校验失败，请重新发起登录。")
    {
    }

    private const int StatusCodes401 = 401;
}

/// <summary>飞书 code 无效/过期 → 401 AUTH_CODE_INVALID。</summary>
public sealed class AuthCodeInvalidException : AuthException
{
    public AuthCodeInvalidException(string? detail = null)
        : base(ErrorCodes.AuthCodeInvalid, 401, detail ?? "登录授权码无效，请重新登录。")
    {
    }
}

/// <summary>飞书不可用/超时 → 502 AUTH_UPSTREAM_UNAVAILABLE（不提供本地账号密码兜底）。</summary>
public sealed class AuthUpstreamUnavailableException : AuthException
{
    public AuthUpstreamUnavailableException(string message = "登录服务暂不可用，请稍后重试。")
        : base(ErrorCodes.AuthUpstreamUnavailable, 502, message)
    {
    }
}

/// <summary>禁用/离职用户 → 403 AUTH_USER_DISABLED。</summary>
public sealed class AuthUserDisabledException : AuthException
{
    public AuthUserDisabledException()
        : base(ErrorCodes.AuthUserDisabled, 403, "账号已被禁用，请联系管理员。")
    {
    }
}

/// <summary>refresh token 无效/过期/已撤销 → 401 AUTH_REFRESH_INVALID。</summary>
public sealed class AuthRefreshInvalidException : AuthException
{
    public AuthRefreshInvalidException()
        : base(ErrorCodes.AuthRefreshInvalid, 401, "会话已失效，请重新登录。")
    {
    }
}
