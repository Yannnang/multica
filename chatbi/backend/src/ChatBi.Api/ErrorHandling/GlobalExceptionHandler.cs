using ChatBi.Application.Common;
using ChatBi.Application.Semantic;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace ChatBi.Api.ErrorHandling;

/// <summary>
/// 全局异常处理：AuthException / SemanticException → 对应 HTTP 状态 + ProblemDetails(code)；
/// 未知异常 → 500 INTERNAL（细节只进服务端日志，不外泄）。
/// 注意：日志只落错误码与请求路径，不落异常消息里的业务字段（如凭证）。
/// </summary>
public class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, code, message) = exception switch
        {
            AuthException authEx => (authEx.HttpStatus, authEx.Code, authEx.Message),
            SemanticException semanticEx => (semanticEx.HttpStatus, semanticEx.Code, semanticEx.Message),
            _ => (StatusCodes.Status500InternalServerError, "INTERNAL", "服务器内部错误，请稍后重试。"),
        };

        if (status == StatusCodes.Status500InternalServerError)
        {
            _logger.LogError(exception, "Unhandled exception processing {Method} {Path}.", httpContext.Request.Method, httpContext.Request.Path);
        }
        else
        {
            _logger.LogWarning("Request rejected {Code} on {Method} {Path}.", code, httpContext.Request.Method, httpContext.Request.Path);
        }

        if (httpContext.Response.HasStarted)
        {
            return false;
        }

        httpContext.Response.Clear();
        httpContext.Response.StatusCode = status;
        httpContext.Response.ContentType = "application/problem+json";

        var problem = new ProblemDetails
        {
            Status = status,
            Title = code,
            Detail = message,
            Type = $"https://chatbi.local/errors/{code.ToLowerInvariant()}",
        };
        problem.Extensions["code"] = code;

        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
