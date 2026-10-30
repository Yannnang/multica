using ChatBi.Application.Semantic;

namespace ChatBi.Application.Common;

/// <summary>分页响应（设计基线 1.5 契约）。</summary>
public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

/// <summary>分页约定：page 从 1；pageSize 默认 20、上限 100。越界即 422（SEM_PAGING_INVALID）。</summary>
public record PageRequest(int Page, int PageSize)
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    public int Skip => (Page - 1) * PageSize;

    public static PageRequest From(int? page, int? pageSize)
    {
        var effectivePage = page ?? 1;
        var effectivePageSize = pageSize ?? DefaultPageSize;

        if (effectivePage < 1 || effectivePageSize < 1 || effectivePageSize > MaxPageSize)
        {
            throw new SemanticValidationException(
                SemanticErrorCodes.PagingInvalid,
                $"分页参数非法：page 从 1 起，pageSize 取 1~{MaxPageSize}（默认 {DefaultPageSize}）。");
        }

        return new PageRequest(effectivePage, effectivePageSize);
    }
}
