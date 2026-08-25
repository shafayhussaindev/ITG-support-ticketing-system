namespace SupportTicketing.Contracts.Knowledge;

public sealed record ArticleListItemResponse
{
    public required Guid Id { get; init; }
    public required string Slug { get; init; }
    public required string Title { get; init; }
    public required string Summary { get; init; }
    public required string Status { get; init; }
    public required string Visibility { get; init; }
    public string? CategoryName { get; init; }
    public required string AuthorName { get; init; }
    public required int ViewCount { get; init; }
    public required int HelpfulCount { get; init; }
    public required int NotHelpfulCount { get; init; }
    public double? HelpfulRatio { get; init; }
    public DateTime? PublishedAtUtc { get; init; }
    public required DateTime CreatedAtUtc { get; init; }
}

public sealed record ArticleDetailResponse
{
    public required Guid Id { get; init; }
    public required string Slug { get; init; }
    public required string Title { get; init; }
    public required string Summary { get; init; }
    public required string Content { get; init; }
    public required string Status { get; init; }
    public required string Visibility { get; init; }
    public Guid? CategoryId { get; init; }
    public string? CategoryName { get; init; }
    public required string AuthorName { get; init; }
    public required int CurrentVersion { get; init; }
    public Guid? SourceTicketId { get; init; }
    public required int ViewCount { get; init; }
    public required int HelpfulCount { get; init; }
    public required int NotHelpfulCount { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public DateTime? PublishedAtUtc { get; init; }
    public required DateTime CreatedAtUtc { get; init; }
    public DateTime? UpdatedAtUtc { get; init; }

    /// <summary>Whether the caller has already voted, so the UI does not offer it twice.</summary>
    public bool? MyVoteWasHelpful { get; init; }

    /// <summary>Lifecycle transitions this caller may perform on this article.</summary>
    public required IReadOnlyList<string> AvailableActions { get; init; }
}

public sealed record CreateArticleRequest
{
    public required string Title { get; init; }
    public required string Summary { get; init; }
    public required string Content { get; init; }
    public Guid? CategoryId { get; init; }
    public string Visibility { get; init; } = "Organization";
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>Set when the article is written up from a resolved ticket.</summary>
    public Guid? SourceTicketId { get; init; }
}

public sealed record UpdateArticleRequest
{
    public required string Title { get; init; }
    public required string Summary { get; init; }
    public required string Content { get; init; }
    public Guid? CategoryId { get; init; }
    public string? Visibility { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>Recorded on the version row so reviewers can see what changed and why.</summary>
    public string? ChangeNote { get; init; }
}

public sealed record ChangeArticleStatusRequest
{
    public required string Status { get; init; }
    public string? Reason { get; init; }
}

public sealed record ArticleFeedbackRequest
{
    public required bool WasHelpful { get; init; }
    public string? Comment { get; init; }
    public Guid? TicketId { get; init; }
}

public sealed record ArticleVersionResponse
{
    public required int Version { get; init; }
    public required string Title { get; init; }
    public required DateTime ChangedAtUtc { get; init; }
    public string? ChangedByName { get; init; }
    public string? ChangeNote { get; init; }
}

// ------------------------------------------------------------ satisfaction

public sealed record SubmitRatingRequest
{
    /// <summary>Overall satisfaction, 1 to 5.</summary>
    public required int Rating { get; init; }

    public int? ResolutionRating { get; init; }
    public int? StaffRating { get; init; }
    public string? Comment { get; init; }
}

public sealed record SatisfactionRatingResponse
{
    public required Guid Id { get; init; }
    public required Guid TicketId { get; init; }
    public required int Rating { get; init; }
    public int? ResolutionRating { get; init; }
    public int? StaffRating { get; init; }
    public string? Comment { get; init; }
    public required string RatedByName { get; init; }
    public string? RatedStaffName { get; init; }
    public required DateTime SubmittedAtUtc { get; init; }
}
