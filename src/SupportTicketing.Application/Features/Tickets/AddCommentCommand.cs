using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Contracts.Tickets;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Identity;
using SupportTicketing.Application.Features.Sla;
using SupportTicketing.Domain.Tickets;
using SupportTicketing.Application.Features.Notifications;

namespace SupportTicketing.Application.Features.Tickets;

public sealed record AddCommentCommand(Guid TicketId, AddCommentRequest Request)
    : ICommand<TicketCommentResponse>;

public sealed class AddCommentCommandValidator : AbstractValidator<AddCommentCommand>
{
    public AddCommentCommandValidator()
    {
        RuleFor(x => x.Request.Body).NotEmpty().MaximumLength(20_000);
        RuleFor(x => x.Request.MentionedUserIds)
            .Must(m => m is null || m.Count <= 25)
            .WithMessage("You can mention at most 25 people in one comment.");
    }
}

/// <summary>
/// Posts a reply or an internal note.
/// </summary>
/// <remarks>
/// The distinction between the two is enforced here and at the read side, never in
/// the client. Writing an internal note requires <c>ticket.internal_note</c>, and the
/// read projection excludes internal notes from anyone without that same permission
/// at the database level. A requester therefore cannot see a note even if a
/// serialisation bug or a future export tried to include one.
/// </remarks>
public sealed class AddCommentCommandHandler(
    IAppDbContext db, ICurrentUser currentUser, ISlaEngine slaEngine,
    IRequesterAudience requesterAudience, IAuditWriter audit, IClock clock)
    : ICommandHandler<AddCommentCommand, TicketCommentResponse>
{
    public async Task<TicketCommentResponse> HandleAsync(
        AddCommentCommand command, CancellationToken cancellationToken)
    {
        var request = command.Request;

        currentUser.Require(request.IsInternal
            ? Permissions.Tickets.InternalNote
            : Permissions.Tickets.PublicReply);

        var ticket = await TicketMutation.LoadForUpdateAsync(
            db, command.TicketId, currentUser, cancellationToken, allowClosed: true);

        if (ticket.IsClosed && !currentUser.Has(Permissions.Tickets.InternalNote))
        {
            throw new Domain.Common.BusinessRuleException(
                "ticket.closed",
                $"Ticket {ticket.TicketNumber} is closed. Reopen it to continue the conversation.");
        }

        var now = clock.UtcNow;
        var isSupportReply = !request.IsInternal && ticket.RequesterId != currentUser.UserId;

        // The response clock stops at the first public reply from support, not at an
        // internal note and not at the requester adding more detail to their own ticket.
        var isFirstResponse = isSupportReply && !ticket.HasFirstResponse;

        if (isFirstResponse)
        {
            ticket.FirstRespondedAtUtc = now;
        }

        var comment = new TicketComment
        {
            OrganizationId = ticket.OrganizationId,
            TicketId = ticket.Id,
            Type = request.IsInternal ? CommentType.InternalNote : CommentType.PublicReply,
            AuthorId = currentUser.UserId,
            Body = request.Body.Trim(),
            ParentCommentId = await ResolveParentAsync(request.ParentCommentId, ticket.Id, cancellationToken),
            IsFirstResponse = isFirstResponse,
        };

        db.TicketComments.Add(comment);

        await AddMentionsAsync(comment, request.MentionedUserIds, cancellationToken);

        // The response clock stops at the first public reply from support, never
        // at an internal note and never at the requester adding more detail.
        if (isFirstResponse)
        {
            await slaEngine.RecordFirstResponseAsync(ticket, now, cancellationToken);
        }

        // A public reply from support on a ticket that was waiting for the requester
        // hands the ball back to them; a reply from the requester returns it to support.
        if (!request.IsInternal)
        {
            AdvanceWaitingStatus(ticket, isSupportReply, now);
        }

        // The body is deliberately absent from the audit record. Audit captures that a
        // comment was made, by whom and of what kind — not its contents, which may hold
        // personal or commercially sensitive information.
        await audit.WriteAsync(
            AuditAction.Updated, nameof(TicketComment), comment.Id, ticket.TicketNumber,
            changes: new
            {
                CommentType = comment.Type.ToString(),
                comment.IsFirstResponse,
                BodyLength = comment.Body.Length,
                MentionCount = request.MentionedUserIds?.Count ?? 0,
            },
            reason: request.IsInternal ? "Internal note added." : "Public reply added.",
            cancellationToken: cancellationToken);

        // The audience refuses anything that is not a public reply, so an internal
        // note cannot leave the building even if this is ever called with one.
        await requesterAudience.RepliedAsync(
            ticket, comment.Id, comment.Type, comment.Body, currentUser.FullName ?? "The support desk",
            cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        var authorName = await db.Users
            .Where(u => u.Id == currentUser.UserId)
            .Select(u => u.FirstName + " " + u.LastName)
            .FirstOrDefaultAsync(cancellationToken);

        return new TicketCommentResponse
        {
            Id = comment.Id,
            Type = comment.Type.ToString(),
            Body = comment.Body,
            AuthorId = comment.AuthorId,
            AuthorName = authorName,
            ParentCommentId = comment.ParentCommentId,
            IsEdited = false,
            IsFirstResponse = comment.IsFirstResponse,
            CreatedAtUtc = now,
            Attachments = [],
            MentionedUserNames = [],
        };
    }

    /// <summary>Rejects a parent comment from a different ticket, which would splice two conversations.</summary>
    private async Task<Guid?> ResolveParentAsync(
        Guid? parentId, Guid ticketId, CancellationToken cancellationToken)
    {
        if (parentId is null)
        {
            return null;
        }

        var belongs = await db.TicketComments
            .AnyAsync(c => c.Id == parentId && c.TicketId == ticketId, cancellationToken);

        return belongs ? parentId : throw new NotFoundException("Comment", parentId.Value);
    }

    private async Task AddMentionsAsync(
        TicketComment comment, IReadOnlyList<Guid>? userIds, CancellationToken cancellationToken)
    {
        if (userIds is null || userIds.Count == 0)
        {
            return;
        }

        var distinct = userIds.Distinct().ToList();

        // Filtered through the tenant-scoped Users set, so a mention cannot name
        // someone in another organization.
        var valid = await db.Users
            .Where(u => distinct.Contains(u.Id) && u.IsActive)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);

        foreach (var userId in valid)
        {
            db.TicketCommentMentions.Add(new TicketCommentMention
            {
                CommentId = comment.Id,
                MentionedUserId = userId,
            });
        }
    }

    private static void AdvanceWaitingStatus(Ticket ticket, bool isSupportReply, DateTime now)
    {
        if (isSupportReply && ticket.Status == TicketStatus.New)
        {
            return;
        }

        if (!isSupportReply && ticket.Status == TicketStatus.WaitingForRequester)
        {
            // The requester answered, so work can resume and the SLA clock restarts.
            ticket.Status = TicketStatus.InProgress;
            ticket.UpdatedAtUtc = now;
        }
    }
}
