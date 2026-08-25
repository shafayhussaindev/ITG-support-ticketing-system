using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Application.Features.Tickets;
using SupportTicketing.Contracts.Knowledge;
using SupportTicketing.Domain.Common;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Feedback;
using SupportTicketing.Domain.Identity;

namespace SupportTicketing.Application.Features.Feedback;

public sealed record SubmitRatingCommand(Guid TicketId, SubmitRatingRequest Request)
    : ICommand<SatisfactionRatingResponse>;

public sealed class SubmitRatingCommandValidator : AbstractValidator<SubmitRatingCommand>
{
    public SubmitRatingCommandValidator()
    {
        RuleFor(x => x.Request.Rating).InclusiveBetween(1, 5);
        RuleFor(x => x.Request.ResolutionRating).InclusiveBetween(1, 5)
            .When(x => x.Request.ResolutionRating.HasValue);
        RuleFor(x => x.Request.StaffRating).InclusiveBetween(1, 5)
            .When(x => x.Request.StaffRating.HasValue);
        RuleFor(x => x.Request.Comment).MaximumLength(2000);
    }
}

/// <summary>
/// Records the requester's satisfaction with a finished ticket.
/// </summary>
/// <remarks>
/// Three rules make the score mean something. Only the requester may rate, because
/// nobody else experienced the support. Only a resolved or closed ticket may be
/// rated, because a score given mid-flight measures impatience rather than outcome.
/// And only once: re-rating would let a score be lobbied upward after a
/// disagreement, so a second submission is refused rather than quietly replacing the
/// first.
/// </remarks>
public sealed class SubmitRatingCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, IAuditWriter audit, IClock clock)
    : ICommandHandler<SubmitRatingCommand, SatisfactionRatingResponse>
{
    public async Task<SatisfactionRatingResponse> HandleAsync(
        SubmitRatingCommand command, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId ?? throw new ForbiddenException();

        var ticket = await TicketScope.FindForCurrentUserAsync(
            db.Tickets.AsNoTracking(), command.TicketId, currentUser, cancellationToken);

        if (ticket.RequesterId != userId)
        {
            throw new ForbiddenException(
                "Only the person who raised a ticket can rate the support they received.");
        }

        if (ticket.Status is not (TicketStatus.Resolved or TicketStatus.Closed))
        {
            throw new BusinessRuleException(
                "feedback.ticket_not_finished",
                "You can rate a ticket once it has been resolved or closed.");
        }

        var alreadyRated = await db.SatisfactionRatings
            .AnyAsync(r => r.TicketId == ticket.Id, cancellationToken);

        if (alreadyRated)
        {
            throw new ConflictException(
                "feedback.already_submitted",
                "This ticket has already been rated. Ratings cannot be changed once submitted.");
        }

        var now = clock.UtcNow;
        var request = command.Request;

        var rating = new SatisfactionRating
        {
            OrganizationId = ticket.OrganizationId,
            TicketId = ticket.Id,
            RatedById = userId,
            Rating = request.Rating,
            ResolutionRating = request.ResolutionRating,
            StaffRating = request.StaffRating,
            Comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim(),
            // Copied now so staff reporting survives a later reassignment of the ticket.
            RatedStaffId = ticket.AssignedStaffId,
            TeamId = ticket.AssignedTeamId,
            SubmittedAtUtc = now,
        };

        db.SatisfactionRatings.Add(rating);

        // The comment is deliberately absent from the audit entry: it is the
        // requester's own words about a named colleague, and the rating row is
        // already the system of record for it.
        await audit.WriteAsync(
            AuditAction.Created, nameof(SatisfactionRating), rating.Id, ticket.TicketNumber,
            changes: new { rating.Rating, rating.ResolutionRating, rating.StaffRating, rating.IsDetractor },
            reason: "Satisfaction rating submitted.",
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        var raterName = await db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.FirstName + " " + u.LastName)
            .FirstOrDefaultAsync(cancellationToken) ?? "Unknown";

        string? agentName = null;
        if (rating.RatedStaffId is { } staffId)
        {
            agentName = await db.Users
                .Where(u => u.Id == staffId)
                .Select(u => u.FirstName + " " + u.LastName)
                .FirstOrDefaultAsync(cancellationToken);
        }

        return new SatisfactionRatingResponse
        {
            Id = rating.Id,
            TicketId = rating.TicketId,
            Rating = rating.Rating,
            ResolutionRating = rating.ResolutionRating,
            StaffRating = rating.StaffRating,
            Comment = rating.Comment,
            RatedByName = raterName,
            RatedStaffName = agentName,
            SubmittedAtUtc = rating.SubmittedAtUtc,
        };
    }
}

public sealed record GetTicketRatingQuery(Guid TicketId) : IQuery<SatisfactionRatingResponse?>;

public sealed class GetTicketRatingQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IQueryHandler<GetTicketRatingQuery, SatisfactionRatingResponse?>
{
    public async Task<SatisfactionRatingResponse?> HandleAsync(
        GetTicketRatingQuery query, CancellationToken cancellationToken)
    {
        // Scoped through the ticket, so a rating is visible to exactly the people who
        // can already see the ticket it belongs to.
        _ = await TicketScope.FindForCurrentUserAsync(
            db.Tickets.AsNoTracking(), query.TicketId, currentUser, cancellationToken);

        return await db.SatisfactionRatings
            .AsNoTracking()
            .Where(r => r.TicketId == query.TicketId)
            .Select(r => new SatisfactionRatingResponse
            {
                Id = r.Id,
                TicketId = r.TicketId,
                Rating = r.Rating,
                ResolutionRating = r.ResolutionRating,
                StaffRating = r.StaffRating,
                Comment = r.Comment,
                RatedByName = r.RatedBy!.FirstName + " " + r.RatedBy.LastName,
                RatedStaffName = db.Users
                    .Where(u => u.Id == r.RatedStaffId)
                    .Select(u => u.FirstName + " " + u.LastName)
                    .FirstOrDefault(),
                SubmittedAtUtc = r.SubmittedAtUtc,
            })
            .FirstOrDefaultAsync(cancellationToken);
    }
}
