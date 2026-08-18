using Microsoft.EntityFrameworkCore;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Contracts.Knowledge;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Identity;
using SupportTicketing.Domain.Knowledge;

namespace SupportTicketing.Application.Features.Knowledge;

/// <summary>
/// Restricts article queries to what the caller may read.
/// </summary>
/// <remarks>
/// Two independent gates. Status: a draft is visible only to its author and to
/// editors, because half-written instructions are worse than none. Visibility: an
/// internal article can name admin paths and blunt workarounds, so it is filtered at
/// the database for anyone without staff-level ticket access rather than hidden by
/// the client.
/// </remarks>
public static class ArticleScope
{
    public static IQueryable<KnowledgeArticle> Readable(
        this IQueryable<KnowledgeArticle> query, ICurrentUser user)
    {
        var isStaff = user.Has(Permissions.Tickets.ViewTeam);
        var canEdit = user.Has(Permissions.Knowledge.Edit);
        var userId = user.UserId ?? Guid.Empty;

        return query.Where(a =>
            (a.Status == ArticleStatus.Published || canEdit || a.AuthorId == userId)
            && (isStaff || a.Visibility != ArticleVisibility.Internal));
    }
}

public sealed record SearchArticlesQuery(string? Search, Guid? CategoryId, string? Status, int Page, int PageSize)
    : IQuery<PagedResult<ArticleListItemResponse>>;

public sealed class SearchArticlesQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IQueryHandler<SearchArticlesQuery, PagedResult<ArticleListItemResponse>>
{
    public async Task<PagedResult<ArticleListItemResponse>> HandleAsync(
        SearchArticlesQuery query, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Knowledge.View);

        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = Math.Clamp(query.PageSize, 1, 50);

        var articles = db.KnowledgeArticles.AsNoTracking().Readable(currentUser);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();

            // Parameterised by EF, so the term cannot alter the query shape.
            articles = articles.Where(a =>
                a.Title.Contains(term) || a.Summary.Contains(term)
                || a.Content.Contains(term) || (a.Tags != null && a.Tags.Contains(term)));
        }

        if (query.CategoryId is { } categoryId)
        {
            articles = articles.Where(a => a.CategoryId == categoryId);
        }

        if (Enum.TryParse<ArticleStatus>(query.Status, true, out var status))
        {
            articles = articles.Where(a => a.Status == status);
        }

        var total = await articles.CountAsync(cancellationToken);

        var items = await articles
            // Most-read first: an article people actually use is more likely to be the
            // one being looked for than the newest draft.
            .OrderByDescending(a => a.Status == ArticleStatus.Published)
            .ThenByDescending(a => a.ViewCount)
            .ThenByDescending(a => a.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new ArticleListItemResponse
            {
                Id = a.Id,
                Slug = a.Slug,
                Title = a.Title,
                Summary = a.Summary,
                Status = a.Status.ToString(),
                Visibility = a.Visibility.ToString(),
                CategoryName = a.Category!.Name,
                AuthorName = a.Author!.FirstName + " " + a.Author.LastName,
                ViewCount = a.ViewCount,
                HelpfulCount = a.HelpfulCount,
                NotHelpfulCount = a.NotHelpfulCount,
                HelpfulRatio = a.HelpfulCount + a.NotHelpfulCount == 0
                    ? null
                    : (double)a.HelpfulCount / (a.HelpfulCount + a.NotHelpfulCount),
                PublishedAtUtc = a.PublishedAtUtc,
                CreatedAtUtc = a.CreatedAtUtc,
            })
            .ToListAsync(cancellationToken);

        return new PagedResult<ArticleListItemResponse>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = total,
        };
    }
}

public sealed record GetArticleQuery(Guid Id) : IQuery<ArticleDetailResponse>;

public sealed class GetArticleQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IQueryHandler<GetArticleQuery, ArticleDetailResponse>
{
    public async Task<ArticleDetailResponse> HandleAsync(
        GetArticleQuery query, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Knowledge.View);

        var myId = currentUser.UserId;

        var article = await db.KnowledgeArticles
            .AsNoTracking()
            .Readable(currentUser)
            .Where(a => a.Id == query.Id)
            .Select(a => new
            {
                Article = a,
                CategoryName = a.Category!.Name,
                AuthorName = a.Author!.FirstName + " " + a.Author.LastName,
                MyVote = a.Feedback
                    .Where(f => f.UserId == myId)
                    .Select(f => (bool?)f.WasHelpful)
                    .FirstOrDefault(),
            })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("Article", query.Id);

        var found = article.Article;

        return new ArticleDetailResponse
        {
            Id = found.Id,
            Slug = found.Slug,
            Title = found.Title,
            Summary = found.Summary,
            Content = found.Content,
            Status = found.Status.ToString(),
            Visibility = found.Visibility.ToString(),
            CategoryId = found.CategoryId,
            CategoryName = article.CategoryName,
            AuthorName = article.AuthorName,
            CurrentVersion = found.CurrentVersion,
            SourceTicketId = found.SourceTicketId,
            ViewCount = found.ViewCount,
            HelpfulCount = found.HelpfulCount,
            NotHelpfulCount = found.NotHelpfulCount,
            Tags = string.IsNullOrWhiteSpace(found.Tags)
                ? []
                : found.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            PublishedAtUtc = found.PublishedAtUtc,
            CreatedAtUtc = found.CreatedAtUtc,
            UpdatedAtUtc = found.UpdatedAtUtc,
            MyVoteWasHelpful = article.MyVote,
            AvailableActions = AvailableActions(found.Status, currentUser),
        };
    }

    /// <summary>
    /// Lifecycle moves this caller may make. Returned so the client renders only
    /// buttons that will work; the commands re-check independently.
    /// </summary>
    private static IReadOnlyList<string> AvailableActions(ArticleStatus status, ICurrentUser user)
    {
        var actions = new List<string>();

        if (user.Has(Permissions.Knowledge.Edit))
        {
            actions.Add("Edit");
        }

        if (status == ArticleStatus.Draft && user.Has(Permissions.Knowledge.Edit))
        {
            actions.Add("InReview");
        }

        // Publishing is a separate permission from editing on purpose: writing an
        // article and deciding the organization stands behind it are different acts.
        if ((status == ArticleStatus.Draft || status == ArticleStatus.InReview
             || status == ArticleStatus.Archived)
            && user.Has(Permissions.Knowledge.Publish))
        {
            actions.Add("Published");
        }

        if (status == ArticleStatus.Published && user.Has(Permissions.Knowledge.Archive))
        {
            actions.Add("Archived");
        }

        return actions;
    }
}

/// <summary>
/// Articles worth offering while a requester is describing a problem.
/// </summary>
/// <remarks>
/// Deliberately keyword matching rather than anything cleverer. A wrong suggestion at
/// ticket-creation time trains people to ignore the panel entirely, so precision
/// matters more than recall here. Only published articles the caller may see are
/// offered, and the AI-assisted version arrives in a later phase behind a feature flag.
/// </remarks>
public sealed record SuggestArticlesQuery(string? Text, Guid? CategoryId, int Take)
    : IQuery<IReadOnlyList<ArticleListItemResponse>>;

public sealed class SuggestArticlesQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IQueryHandler<SuggestArticlesQuery, IReadOnlyList<ArticleListItemResponse>>
{
    private static readonly char[] Separators = [' ', ',', '.', ';', ':', '!', '?', '\n', '\r', '\t'];

    public async Task<IReadOnlyList<ArticleListItemResponse>> HandleAsync(
        SuggestArticlesQuery query, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Knowledge.View);

        // Short words carry almost no signal and match nearly everything, so they are
        // dropped rather than allowed to flood the results.
        var terms = (query.Text ?? string.Empty)
            .Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 4)
            .Select(w => w.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();

        if (terms.Count == 0 && query.CategoryId is null)
        {
            return [];
        }

        var articles = db.KnowledgeArticles
            .AsNoTracking()
            .Readable(currentUser)
            .Where(a => a.Status == ArticleStatus.Published);

        if (query.CategoryId is { } categoryId)
        {
            articles = articles.Where(a => a.CategoryId == categoryId);
        }

        if (terms.Count > 0)
        {
            // Any term matching is enough. Requiring all of them returns nothing for a
            // normally worded problem description.
            articles = articles.Where(a =>
                terms.Any(t => a.Title.Contains(t) || a.Summary.Contains(t)
                               || (a.Tags != null && a.Tags.Contains(t))));
        }

        return await articles
            .OrderByDescending(a => a.HelpfulCount - a.NotHelpfulCount)
            .ThenByDescending(a => a.ViewCount)
            .Take(Math.Clamp(query.Take, 1, 10))
            .Select(a => new ArticleListItemResponse
            {
                Id = a.Id,
                Slug = a.Slug,
                Title = a.Title,
                Summary = a.Summary,
                Status = a.Status.ToString(),
                Visibility = a.Visibility.ToString(),
                CategoryName = a.Category!.Name,
                AuthorName = a.Author!.FirstName + " " + a.Author.LastName,
                ViewCount = a.ViewCount,
                HelpfulCount = a.HelpfulCount,
                NotHelpfulCount = a.NotHelpfulCount,
                HelpfulRatio = null,
                PublishedAtUtc = a.PublishedAtUtc,
                CreatedAtUtc = a.CreatedAtUtc,
            })
            .ToListAsync(cancellationToken);
    }
}

public sealed record GetArticleVersionsQuery(Guid ArticleId) : IQuery<IReadOnlyList<ArticleVersionResponse>>;

/// <summary>
/// The article's revision history.
/// </summary>
/// <remarks>
/// Readability is checked against the article first, so the version endpoint cannot
/// be used to read the content of a draft or an internal article the caller would be
/// refused through the normal route.
/// </remarks>
public sealed class GetArticleVersionsQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IQueryHandler<GetArticleVersionsQuery, IReadOnlyList<ArticleVersionResponse>>
{
    public async Task<IReadOnlyList<ArticleVersionResponse>> HandleAsync(
        GetArticleVersionsQuery query, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Knowledge.View);

        var readable = await db.KnowledgeArticles
            .AsNoTracking()
            .Readable(currentUser)
            .AnyAsync(a => a.Id == query.ArticleId, cancellationToken);

        if (!readable)
        {
            throw new NotFoundException("Article", query.ArticleId);
        }

        return await db.KnowledgeArticleVersions
            .AsNoTracking()
            .Where(v => v.ArticleId == query.ArticleId)
            .OrderByDescending(v => v.Version)
            .Select(v => new ArticleVersionResponse
            {
                Version = v.Version,
                Title = v.Title,
                ChangedAtUtc = v.ChangedAtUtc,
                ChangedByName = db.Users
                    .Where(u => u.Id == v.ChangedById)
                    .Select(u => u.FirstName + " " + u.LastName)
                    .FirstOrDefault(),
                ChangeNote = v.ChangeNote,
            })
            .ToListAsync(cancellationToken);
    }
}
