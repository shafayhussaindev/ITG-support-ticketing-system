using System.Text.RegularExpressions;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Contracts.Knowledge;
using SupportTicketing.Domain.Common;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Identity;
using SupportTicketing.Domain.Knowledge;

namespace SupportTicketing.Application.Features.Knowledge;

public sealed record CreateArticleCommand(CreateArticleRequest Request) : ICommand<ArticleDetailResponse>;

public sealed class CreateArticleCommandValidator : AbstractValidator<CreateArticleCommand>
{
    public CreateArticleCommandValidator()
    {
        RuleFor(x => x.Request.Title).NotEmpty().MaximumLength(250);
        RuleFor(x => x.Request.Summary).NotEmpty().MaximumLength(600);
        RuleFor(x => x.Request.Content).NotEmpty().MaximumLength(100_000);
        RuleFor(x => x.Request.Visibility)
            .Must(v => Enum.TryParse<ArticleVisibility>(v, true, out _))
            .WithMessage("Visibility must be Internal, Organization or Public.");
    }
}

public sealed class CreateArticleCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IAuditWriter audit, IClock clock)
    : ICommandHandler<CreateArticleCommand, ArticleDetailResponse>
{
    public async Task<ArticleDetailResponse> HandleAsync(
        CreateArticleCommand command, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Knowledge.Create);

        var request = command.Request;
        var organizationId = currentUser.OrganizationId ?? throw new ForbiddenException();
        var now = clock.UtcNow;

        var article = new KnowledgeArticle
        {
            OrganizationId = organizationId,
            Title = request.Title.Trim(),
            Summary = request.Summary.Trim(),
            Content = request.Content.Trim(),
            Slug = await UniqueSlugAsync(request.Title, cancellationToken),
            CategoryId = request.CategoryId,
            Visibility = Enum.Parse<ArticleVisibility>(request.Visibility, true),
            // Always starts as a draft. Publishing is a separate, separately permitted
            // act, so nothing reaches readers just because someone hit save.
            Status = ArticleStatus.Draft,
            AuthorId = currentUser.UserId ?? throw new ForbiddenException(),
            SourceTicketId = request.SourceTicketId,
            Tags = request.Tags is null ? null : string.Join(',', request.Tags.Select(t => t.Trim())),
            CurrentVersion = 1,
        };

        db.KnowledgeArticles.Add(article);

        db.KnowledgeArticleVersions.Add(new KnowledgeArticleVersion
        {
            OrganizationId = organizationId,
            ArticleId = article.Id,
            Version = 1,
            Title = article.Title,
            Summary = article.Summary,
            Content = article.Content,
            ChangedById = currentUser.UserId,
            ChangedAtUtc = now,
            ChangeNote = "Initial draft.",
        });

        await audit.WriteAsync(
            AuditAction.Created, nameof(KnowledgeArticle), article.Id, article.Slug,
            changes: new { article.Title, Visibility = article.Visibility.ToString() },
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        return await Reload(db, currentUser, article.Id, cancellationToken);
    }

    /// <summary>
    /// Builds a URL-safe slug, appending a counter when the natural one is taken.
    /// </summary>
    /// <remarks>
    /// The unique index remains the real guarantee; this only avoids the common
    /// collision so an author is not shown a constraint error for picking a sensible
    /// title someone else already used.
    /// </remarks>
    private async Task<string> UniqueSlugAsync(string title, CancellationToken cancellationToken)
    {
        var baseSlug = Regex.Replace(title.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');

        if (baseSlug.Length == 0)
        {
            baseSlug = "article";
        }

        if (baseSlug.Length > 140)
        {
            baseSlug = baseSlug[..140].Trim('-');
        }

        var slug = baseSlug;

        for (var suffix = 2; await db.KnowledgeArticles.AnyAsync(a => a.Slug == slug, cancellationToken); suffix++)
        {
            slug = $"{baseSlug}-{suffix}";
        }

        return slug;
    }

    internal static async Task<ArticleDetailResponse> Reload(
        IAppDbContext db, ICurrentUser user, Guid id, CancellationToken cancellationToken) =>
        await new GetArticleQueryHandler(db, user).HandleAsync(new GetArticleQuery(id), cancellationToken);
}

public sealed record UpdateArticleCommand(Guid Id, UpdateArticleRequest Request) : ICommand<ArticleDetailResponse>;

public sealed class UpdateArticleCommandValidator : AbstractValidator<UpdateArticleCommand>
{
    public UpdateArticleCommandValidator()
    {
        RuleFor(x => x.Request.Title).NotEmpty().MaximumLength(250);
        RuleFor(x => x.Request.Summary).NotEmpty().MaximumLength(600);
        RuleFor(x => x.Request.Content).NotEmpty().MaximumLength(100_000);
        RuleFor(x => x.Request.ChangeNote).MaximumLength(1000);
    }
}

/// <summary>
/// Edits an article and snapshots the previous wording.
/// </summary>
/// <remarks>
/// A new version row is written on every edit, not only on publish. People act on
/// what an article said at the time they read it, so the text that was live when a
/// ticket was resolved has to stay recoverable after a rewrite.
/// </remarks>
public sealed class UpdateArticleCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IAuditWriter audit, IClock clock)
    : ICommandHandler<UpdateArticleCommand, ArticleDetailResponse>
{
    public async Task<ArticleDetailResponse> HandleAsync(
        UpdateArticleCommand command, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Knowledge.Edit);

        var article = await db.KnowledgeArticles
            .AsTracking()
            .FirstOrDefaultAsync(a => a.Id == command.Id, cancellationToken)
            ?? throw new NotFoundException("Article", command.Id);

        var request = command.Request;
        var now = clock.UtcNow;

        article.Title = request.Title.Trim();
        article.Summary = request.Summary.Trim();
        article.Content = request.Content.Trim();
        article.CategoryId = request.CategoryId;
        article.Tags = request.Tags is null ? null : string.Join(',', request.Tags.Select(t => t.Trim()));

        if (Enum.TryParse<ArticleVisibility>(request.Visibility, true, out var visibility))
        {
            article.Visibility = visibility;
        }

        article.CurrentVersion++;

        db.KnowledgeArticleVersions.Add(new KnowledgeArticleVersion
        {
            OrganizationId = article.OrganizationId,
            ArticleId = article.Id,
            Version = article.CurrentVersion,
            Title = article.Title,
            Summary = article.Summary,
            Content = article.Content,
            ChangedById = currentUser.UserId,
            ChangedAtUtc = now,
            ChangeNote = request.ChangeNote,
        });

        await audit.WriteAsync(
            AuditAction.Updated, nameof(KnowledgeArticle), article.Id, article.Slug,
            changes: new { article.CurrentVersion, Visibility = article.Visibility.ToString() },
            reason: request.ChangeNote,
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        return await CreateArticleCommandHandler.Reload(db, currentUser, article.Id, cancellationToken);
    }
}

public sealed record ChangeArticleStatusCommand(Guid Id, ChangeArticleStatusRequest Request)
    : ICommand<ArticleDetailResponse>;

/// <summary>
/// Moves an article through draft, review, published and archived.
/// </summary>
/// <remarks>
/// Publishing and archiving carry their own permissions because they change what the
/// organization is telling people. Archiving does not delete: an article that turned
/// out to be wrong is still evidence of what was advised, and the tickets that cite
/// it must not end up pointing at nothing.
/// </remarks>
public sealed class ChangeArticleStatusCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IAuditWriter audit, IClock clock)
    : ICommandHandler<ChangeArticleStatusCommand, ArticleDetailResponse>
{
    public async Task<ArticleDetailResponse> HandleAsync(
        ChangeArticleStatusCommand command, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<ArticleStatus>(command.Request.Status, true, out var target))
        {
            throw new BusinessRuleException("knowledge.unknown_status", "That is not a recognised article status.");
        }

        currentUser.Require(target switch
        {
            ArticleStatus.Published => Permissions.Knowledge.Publish,
            ArticleStatus.Archived => Permissions.Knowledge.Archive,
            _ => Permissions.Knowledge.Edit,
        });

        var article = await db.KnowledgeArticles
            .AsTracking()
            .FirstOrDefaultAsync(a => a.Id == command.Id, cancellationToken)
            ?? throw new NotFoundException("Article", command.Id);

        if (article.Status == target)
        {
            return await CreateArticleCommandHandler.Reload(db, currentUser, article.Id, cancellationToken);
        }

        var from = article.Status;
        var now = clock.UtcNow;

        article.Status = target;

        switch (target)
        {
            case ArticleStatus.Published:
                article.PublishedAtUtc = now;
                article.PublishedById = currentUser.UserId;
                article.ArchivedAtUtc = null;
                break;

            case ArticleStatus.Archived:
                article.ArchivedAtUtc = now;
                break;
        }

        await audit.WriteAsync(
            AuditAction.StatusChanged, nameof(KnowledgeArticle), article.Id, article.Slug,
            changes: new { From = from.ToString(), To = target.ToString() },
            reason: command.Request.Reason,
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        return await CreateArticleCommandHandler.Reload(db, currentUser, article.Id, cancellationToken);
    }
}

public sealed record RecordArticleFeedbackCommand(Guid ArticleId, ArticleFeedbackRequest Request) : ICommand<bool>;

/// <summary>
/// Records whether a reader found an article useful.
/// </summary>
/// <remarks>
/// One verdict per reader per article. Changing your mind updates the existing row
/// and adjusts both counters, so the helpful ratio cannot be inflated by clicking
/// repeatedly, and an author cannot vote their own article up more than once either.
/// </remarks>
public sealed class RecordArticleFeedbackCommandHandler(IAppDbContext db, ICurrentUser currentUser)
    : ICommandHandler<RecordArticleFeedbackCommand, bool>
{
    public async Task<bool> HandleAsync(
        RecordArticleFeedbackCommand command, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Knowledge.View);

        var userId = currentUser.UserId ?? throw new ForbiddenException();

        var article = await db.KnowledgeArticles
            .AsTracking()
            .Readable(currentUser)
            .FirstOrDefaultAsync(a => a.Id == command.ArticleId, cancellationToken)
            ?? throw new NotFoundException("Article", command.ArticleId);

        var existing = await db.KnowledgeFeedback
            .AsTracking()
            .FirstOrDefaultAsync(f => f.ArticleId == article.Id && f.UserId == userId, cancellationToken);

        var wasHelpful = command.Request.WasHelpful;

        if (existing is null)
        {
            db.KnowledgeFeedback.Add(new KnowledgeFeedback
            {
                OrganizationId = article.OrganizationId,
                ArticleId = article.Id,
                UserId = userId,
                WasHelpful = wasHelpful,
                Comment = command.Request.Comment,
                TicketId = command.Request.TicketId,
            });

            if (wasHelpful)
            {
                article.HelpfulCount++;
            }
            else
            {
                article.NotHelpfulCount++;
            }
        }
        else if (existing.WasHelpful != wasHelpful)
        {
            // Move the vote rather than adding a second one.
            existing.WasHelpful = wasHelpful;
            existing.Comment = command.Request.Comment;

            if (wasHelpful)
            {
                article.HelpfulCount++;
                article.NotHelpfulCount = Math.Max(0, article.NotHelpfulCount - 1);
            }
            else
            {
                article.NotHelpfulCount++;
                article.HelpfulCount = Math.Max(0, article.HelpfulCount - 1);
            }
        }
        else
        {
            return false;
        }

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}

public sealed record RecordArticleViewCommand(Guid ArticleId) : ICommand<bool>;

/// <summary>
/// Increments the view counter.
/// </summary>
/// <remarks>
/// Applied as a set-based update rather than load-modify-save. Two people opening the
/// same article at once would otherwise read the same count and write it back twice,
/// losing one of the views.
/// </remarks>
public sealed class RecordArticleViewCommandHandler(IAppDbContext db, ICurrentUser currentUser)
    : ICommandHandler<RecordArticleViewCommand, bool>
{
    public async Task<bool> HandleAsync(
        RecordArticleViewCommand command, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Knowledge.View);

        var updated = await db.KnowledgeArticles
            .Readable(currentUser)
            .Where(a => a.Id == command.ArticleId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(a => a.ViewCount, a => a.ViewCount + 1),
                cancellationToken);

        return updated > 0;
    }
}
