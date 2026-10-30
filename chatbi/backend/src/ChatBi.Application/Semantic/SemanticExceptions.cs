using ChatBi.Application.Common;

namespace ChatBi.Application.Semantic;

/// <summary>
/// 语义层用例异常基类：携带机器可读错误码（SEM_*）与 HTTP 状态，
/// 由 GlobalExceptionHandler 统一转 ProblemDetails。消息面向调用方，不回显敏感字段值。
/// </summary>
public abstract class SemanticException : Exception
{
    protected SemanticException(string code, int httpStatus, string message)
        : base(message)
    {
        Code = code;
        HttpStatus = httpStatus;
    }

    public string Code { get; }

    public int HttpStatus { get; }
}

/// <summary>404 SEM_NOT_FOUND：资源不存在（含已被软删）。</summary>
public sealed class SemanticNotFoundException : SemanticException
{
    public SemanticNotFoundException(string resource, long id)
        : base(SemanticErrorCodes.NotFound, 404, $"{resource}（id={id}）不存在。")
    {
    }
}

/// <summary>409 SEM_DUPLICATE：唯一性冲突（重名/重复定义）。</summary>
public sealed class SemanticDuplicateException : SemanticException
{
    public SemanticDuplicateException(string message)
        : base(SemanticErrorCodes.Duplicate, 409, message)
    {
    }
}

/// <summary>409 SEM_CONCURRENCY_CONFLICT：RowVersion 并发冲突。</summary>
public sealed class SemanticConcurrencyException : SemanticException
{
    public SemanticConcurrencyException()
        : base(SemanticErrorCodes.ConcurrencyConflict, 409, "资源已被他人修改（RowVersion 不一致），请刷新后重试。")
    {
    }
}

/// <summary>422：字段/结构校验失败。message 面向调用方，禁止回显敏感字段值（如明文凭证）。</summary>
public sealed class SemanticValidationException : SemanticException
{
    public SemanticValidationException(string code, string message)
        : base(code, 422, message)
    {
    }
}
