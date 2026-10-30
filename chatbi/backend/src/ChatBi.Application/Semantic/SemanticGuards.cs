using ChatBi.Application.Common;

namespace ChatBi.Application.Semantic;

/// <summary>语义层用例的字段断言（失败 → 422 SEM_FIELD_INVALID）。</summary>
internal static class SemanticGuards
{
    internal static void Ensure(bool condition, string message)
    {
        if (!condition)
        {
            throw new SemanticValidationException(SemanticErrorCodes.FieldInvalid, message);
        }
    }
}
