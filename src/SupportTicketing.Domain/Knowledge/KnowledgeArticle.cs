using SupportTicketing.Domain.Catalog;
using SupportTicketing.Domain.Common;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Identity;

namespace SupportTicketing.Domain.Knowledge;

/// <summary>
/// A knowledge article: the reusable answer distilled from tickets that keep recurring.
/// </summary>
/// <remarks>
/// Visibility is a first-class field rather than an afterthought. An article written
/// for support staff can contain admin credentials paths, workaround caveats and
/// blunt assessments that must never reach a requester, so the read queries filter on
/// it at the database in the same way ticket internal notes do.
/// </remarks>
public class KnowledgeArticle : TenantEntity, IHasRowVersion
{
    public required string Title { get; set; }

    /// <summary>One or two sentences shown in search results and suggestion lists.</summary>
    public required string Summary { get; set; }

    /// <summary>The full solution. Markdown, rendered by the client.</summary>
    public required string Content { get; set; }

    /// <summary>Stable, human-readable identifier used in links, for example <c>reset-erp-password</c>.</summary>
    public required string Slug { get; set; }

    public Guid? CategoryId { get; set; }
    public Category? Category { get; set; }

    public Guid? ApplicationId { get; set; }
    public Guid? ApplicationModuleId { get; set; }

    public ArticleStatus Status { get; set; } = ArticleStatus.Draft;
    public ArticleVisibility Visibility { get; set; } = ArticleVisibility.Organization;

    public Guid AuthorId { get; set; }
    public User? Author { get; set; }

    /// <summary>Who approved publication. Null while the article has never been published.</summary>
    public Guid? PublishedById { get; set; }
    public DateTime? PublishedAtUtc { get; set; }

    public DateTime? ArchivedAtUtc { get; set; }

    /// <summary>Incremented on every published edit; matches the latest version row.</summary>
    public int CurrentVersion { get; set; } = 1;

    /// <summary>Ticket this article was written from, when it came out of a real resolution.</summary>
    public Guid? SourceTicketId { get; set; }

    // Denormalised counters. Recomputing these from the feedback table on every
    // search result would turn one query into one per row.
    public int ViewCount { get; set; }
    public int HelpfulCount { get; set; }
    public int NotHelpfulCount { get; set; }

    /// <summary>Comma-separated tags. Kept simple: articles are searched, not faceted.</summary>
    public string? Tags { get; set; }

    public byte[]? RowVersion { get; set; }

    public ICollection<KnowledgeArticleVersion> Versions { get; set; } = [];
    public ICollection<KnowledgeFeedback> Feedback { get; set; } = [];

    /// <summary>Only a published article is offered to anyone who did not write it.</summary>
    public bool IsReadable => Status == ArticleStatus.Published;

    /// <summary>Share of readers who found it useful. Null until anyone has voted.</summary>
    public double? HelpfulRatio =>
        HelpfulCount + NotHelpfulCount == 0
            ? null
            : (double)HelpfulCount / (HelpfulCount + NotHelpfulCount);
}

/// <summary>
/// A point-in-time copy of an article, written on every publish.
/// </summary>
/// <remarks>
/// Append-only. A published article is something people acted on, so the wording that
/// was live when a ticket was resolved has to remain recoverable even after the
/// article is rewritten.
/// </remarks>
public class KnowledgeArticleVersion : Entity, IAppendOnly, ITenantOwned
{
    public Guid OrganizationId { get; set; }

    public Guid ArticleId { get; set; }
    public KnowledgeArticle? Article { get; set; }

    public int Version { get; set; }

    public required string Title { get; set; }
    public required string Summary { get; set; }
    public required string Content { get; set; }

    public Guid? ChangedById { get; set; }
    public DateTime ChangedAtUtc { get; set; }

    /// <summary>What changed and why, for reviewers reading the history.</summary>
    public string? ChangeNote { get; set; }
}

/// <summary>
/// One reader's verdict on an article.
/// </summary>
/// <remarks>
/// Unique per reader per article so the counters cannot be inflated by clicking
/// twice; changing your mind updates the existing row rather than adding another.
/// </remarks>
public class KnowledgeFeedback : TenantEntity
{
    public Guid ArticleId { get; set; }
    public KnowledgeArticle? Article { get; set; }

    public Guid UserId { get; set; }
    public User? User { get; set; }

    public bool WasHelpful { get; set; }

    public string? Comment { get; set; }

    /// <summary>Ticket the reader was working on, when the article was suggested from one.</summary>
    public Guid? TicketId { get; set; }
}
