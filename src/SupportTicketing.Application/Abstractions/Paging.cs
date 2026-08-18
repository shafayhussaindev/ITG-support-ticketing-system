namespace SupportTicketing.Application.Abstractions;

/// <summary>
/// Standard paged envelope. Every list endpoint returns this shape so clients can
/// share pagination code.
/// </summary>
public sealed class PagedResult<T>
{
    public required IReadOnlyList<T> Items { get; init; }
    public required int Page { get; init; }
    public required int PageSize { get; init; }
    public required int TotalCount { get; init; }

    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < TotalPages;

    public static PagedResult<T> Empty(int page, int pageSize) =>
        new() { Items = [], Page = page, PageSize = pageSize, TotalCount = 0 };
}

/// <summary>
/// Base for paged queries. <see cref="PageSize"/> is clamped so a client cannot
/// request an unbounded result set and exhaust server memory.
/// </summary>
public abstract class PagedQuery
{
    public const int MaxPageSize = 100;
    public const int DefaultPageSize = 25;

    private int _page = 1;
    private int _pageSize = DefaultPageSize;

    public int Page
    {
        get => _page;
        set => _page = value < 1 ? 1 : value;
    }

    public int PageSize
    {
        get => _pageSize;
        set => _pageSize = value switch
        {
            < 1 => DefaultPageSize,
            > MaxPageSize => MaxPageSize,
            _ => value
        };
    }

    /// <summary>Field to sort by. Handlers map this against an allowlist; arbitrary values are rejected.</summary>
    public string? SortBy { get; set; }

    public bool SortDescending { get; set; } = true;

    public int Skip => (Page - 1) * PageSize;
}
