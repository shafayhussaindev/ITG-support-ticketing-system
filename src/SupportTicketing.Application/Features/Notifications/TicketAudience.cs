using Microsoft.EntityFrameworkCore;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Notifications;
using SupportTicketing.Domain.Tickets;

namespace SupportTicketing.Application.Features.Notifications;

/// <summary>
/// Tells the people who have to act on a ticket that it exists, or that it is now theirs.
/// </summary>
/// <remarks>
/// <para>
/// Raising a ticket and assigning one both notified nobody. Work arrived and the only
/// way to find out was to go looking, which is the same failure as the SLA monitor had:
/// the system knew something had changed and kept it to itself.
/// </para>
/// <para>
/// Separate from <see cref="ISlaAudience"/> on purpose. That answers "who is accountable
/// for this being late" and deliberately reaches supervision; this answers "who has to
/// pick this up", which is the assignee or the team the ticket landed in and nobody
/// else. An administrator does not need interrupting every time a ticket is raised.
/// </para>
/// </remarks>
public interface ITicketAudience
{
    /// <summary>Announces a newly raised ticket to whoever it landed on.</summary>
    Task<int> RaisedAsync(Ticket ticket, CancellationToken cancellationToken);

    /// <summary>Announces that a ticket has moved to a new owner or team.</summary>
    Task<int> AssignedAsync(
        Ticket ticket, Guid? previousStaffId, Guid? previousTeamId, CancellationToken cancellationToken);
}

public sealed class TicketAudience(IAppDbContext db, INotificationService notifications, ICurrentUser currentUser)
    : ITicketAudience
{
    public async Task<int> RaisedAsync(Ticket ticket, CancellationToken cancellationToken)
    {
        var recipients = await RecipientsAsync(ticket.AssignedStaffId, ticket.AssignedTeamId, cancellationToken);

        return await FanOutAsync(
            ticket,
            recipients,
            NotificationEventType.TicketAssigned,
            $"New ticket: {ticket.TicketNumber}",
            Describe(ticket),
            $"ticket-raised:{ticket.Id}",
            cancellationToken);
    }

    public async Task<int> AssignedAsync(
        Ticket ticket, Guid? previousStaffId, Guid? previousTeamId, CancellationToken cancellationToken)
    {
        // Nothing moved, so nobody needs telling. Re-saving a ticket without changing
        // its owner is common, and a notification for it would be pure noise.
        if (ticket.AssignedStaffId == previousStaffId && ticket.AssignedTeamId == previousTeamId)
        {
            return 0;
        }

        var recipients = await RecipientsAsync(ticket.AssignedStaffId, ticket.AssignedTeamId, cancellationToken);

        // Only the people it moved *to*. Whoever held it before already knows they no
        // longer do, because they were usually the one who passed it on.
        recipients.Remove(previousStaffId ?? Guid.Empty);

        return await FanOutAsync(
            ticket,
            recipients,
            NotificationEventType.TicketAssigned,
            $"Assigned to you: {ticket.TicketNumber}",
            Describe(ticket),
            // Keyed on the owner as well as the ticket, so a ticket that moves twice
            // announces itself twice rather than being swallowed as a duplicate.
            $"ticket-assigned:{ticket.Id}:{ticket.AssignedStaffId}:{ticket.AssignedTeamId}",
            cancellationToken);
    }

    private static string Describe(Ticket ticket) =>
        $"{ticket.Subject} — {ticket.Priority} priority.";

    /// <summary>
    /// The assignee, or every active member of the team it landed in.
    /// </summary>
    /// <remarks>
    /// A ticket with an owner is that person's problem and nobody else's, so the team is
    /// not told as well. A ticket with only a team belongs to whoever picks it up first,
    /// which means all of them need to see it.
    /// </remarks>
    private async Task<HashSet<Guid>> RecipientsAsync(
        Guid? assigneeId, Guid? teamId, CancellationToken cancellationToken)
    {
        if (assigneeId is { } assignee)
        {
            return [assignee];
        }

        if (teamId is not { } team)
        {
            // Unassigned and unrouted. It sits in the unassigned queue, and the SLA
            // monitor tells supervision if it stays there — announcing it to the whole
            // organization now would be noise nobody could act on.
            return [];
        }

        var members = await db.TeamMembers.AsNoTracking()
            .Where(m => m.TeamId == team && m.IsActive && m.User!.IsActive && !m.User.IsAnonymised)
            .Select(m => m.UserId)
            .ToListAsync(cancellationToken);

        return [.. members];
    }

    private async Task<int> FanOutAsync(
        Ticket ticket,
        HashSet<Guid> recipients,
        NotificationEventType eventType,
        string title,
        string body,
        string key,
        CancellationToken cancellationToken)
    {
        // Never tell somebody about their own action. A staff member raising a ticket on a
        // requester's behalf, or a lead assigning one to themselves, already knows.
        if (currentUser.UserId is { } actor)
        {
            recipients.Remove(actor);
        }

        var raised = 0;

        foreach (var recipientId in recipients)
        {
            var sent = await notifications.RaiseAsync(
                new NotificationRequest
                {
                    OrganizationId = ticket.OrganizationId,
                    RecipientUserId = recipientId,
                    EventType = eventType,
                    Title = title,
                    Body = body,
                    Severity = ticket.Priority == PriorityLevel.Critical
                        ? NotificationSeverity.Critical
                        : NotificationSeverity.Info,
                    Link = $"/tickets/{ticket.Id}",
                    TicketId = ticket.Id,
                    TicketNumber = ticket.TicketNumber,
                    DeduplicationKey = $"{key}:{recipientId}",

                    // Everyone here is being asked to do something, so everyone here is
                    // interrupted. That is the difference between this and supervision.
                    ShowAsPopup = true,
                },
                cancellationToken);

            if (sent)
            {
                raised++;
            }
        }

        return raised;
    }
}
