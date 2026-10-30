namespace ChatBi.Application.Common;

/// <summary>语义层错误码（设计基线错误码表 SEM_* 段）。</summary>
public static class SemanticErrorCodes
{
    /// <summary>404：资源不存在（含已被软删）。</summary>
    public const string NotFound = "SEM_NOT_FOUND";

    /// <summary>409：唯一性冲突（重名/重复定义）。</summary>
    public const string Duplicate = "SEM_DUPLICATE";

    /// <summary>409：RowVersion 并发冲突。</summary>
    public const string ConcurrencyConflict = "SEM_CONCURRENCY_CONFLICT";

    /// <summary>422：通用字段校验失败。</summary>
    public const string FieldInvalid = "SEM_FIELD_INVALID";

    /// <summary>422：请求体出现疑似明文连接串/密码。</summary>
    public const string PlaintextCredential = "SEM_PLAINTEXT_CREDENTIAL";

    /// <summary>422：CredentialRef 不是合法引用名。</summary>
    public const string CredentialRefInvalid = "SEM_CREDENTIAL_REF_INVALID";

    /// <summary>422：Metrics.Expression 未通过结构化白名单。</summary>
    public const string ExpressionInvalid = "SEM_EXPRESSION_INVALID";

    /// <summary>422：OnConditionsJson 未通过结构化校验（含列归属校验）。</summary>
    public const string ConditionsInvalid = "SEM_CONDITIONS_INVALID";

    /// <summary>422：同义词目标不存在。</summary>
    public const string TargetNotFound = "SEM_TARGET_NOT_FOUND";

    /// <summary>422：分页参数非法。</summary>
    public const string PagingInvalid = "SEM_PAGING_INVALID";
}
