using SupportTicketing.Application.Abstractions;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Notifications;
using SupportTicketing.Domain.Tickets;

namespace SupportTicketing.Application.Features.Notifications;

/// <summary>
/// Keeps the person who raised the ticket informed.
/// </summary>
/// <remarks>
/// <para>
/// Nothing in this system told a requester anything. Not that their ticket had been
/// received, not that somebody had replied, not that it had been fixed. They found out
/// by signing in and looking, which is the opposite of what a support desk is for.
/// </para>
/// <para>
/// Four moments matter to them and no others: it arrived, somebody replied, we think it
/// is fixed, it is closed. Everything else — assignment, escalation, an SLA the desk set
/// for itself — is the organization's business, and telling a customer you missed your
/// own target invites a complaint you would otherwise have handled quietly.
/// </para>
/// <para>
/// Only public replies are ever sent. An internal note reaching a requester would be the
/// worst failure this system could have, and email is a new way for one to escape, so
/// the caller passes the comment type and this refuses anything else.
/// </para>
/// </remarks>
public interface IRequesterAudience
{
    Task AcknowledgeAsync(Ticket ticket, CancellationToken cancellationToken);

    /// <summary>Sends a reply on to the requester. Silently ignores an internal note.</summary>
    Task RepliedAsync(Ticket ticket, CommentType commentType, string body, string authorName,
        CancellationToken cancellationToken);

    Task ResolvedAsync(Ticket ticket, string? resolutionSummary, CancellationToken cancellationToken);

    Task ClosedAsync(Ticket ticket, CancellationToken cancellationToken);
}

public sealed class RequesterAudience(INotificationService notifications, ICurrentUser currentUser)
    : IRequesterAudience
{
    public Task AcknowledgeAsync(Ticket ticket, CancellationToken cancellationToken) =>
        RaiseAsync(
            ticket,
            NotificationEventType.TicketCreated,
            $"We have your request: {ticket.TicketNumber}",
            $"\"{ticket.Subject}\" has been logged and is waiting to be picked up. "
            + "You will hear from us here as it moves.",
            $"requester-created:{ticket.Id}",
            cancellationToken);

    public Task RepliedAsync(
        Ticket ticket, CommentType commentType, string body, string authorName,
        CancellationToken cancellationToken)
    {
        // The guard that matters. Anything that is not a public reply stays inside.
        if (commentType != CommentType.PublicReply)
        {
            return Task.CompletedTask;
        }

        return RaiseAsync(
            ticket,
            NotificationEventType.TicketReplied,
            $"Reply on {ticket.TicketNumber}",
            $"{authorName} replied:\n\n{body}",
            // Keyed on the comment, not the ticket, or only the first reply is ever sent.
            $"requester-replied:{ticket.Id}:{body.GetHashCode()}",
            cancellationToken);
    }

    public Task ResolvedAsync(Ticket ticket, string? resolutionSummary, CancellationToken cancellationToken) =>
        RaiseAsync(
            ticket,
            NotificationEventType.TicketResolved,
            $"We think this is fixed: {ticket.TicketNumber}",
            (string.IsNullOrWhiteSpace(resolutionSummary)
                ? $"\"{ticket.Subject}\" has been marked resolved."
                : $"\"{ticket.Subject}\" has been resolved:\n\n{resolutionSummary}")
            + "\n\nIf that is not right, reopen the ticket and tell us what is still wrong.",
            $"requester-resolved:{ticket.Id}",
            cancellationToken);

    public Task ClosedAsync(Ticket ticket, CancellationToken cancellationToken) =>
        RaiseAsync(
            ticket,
            NotificationEventType.TicketClosed,
            $"Closed: {ticket.TicketNumber}",
            $"\"{ticket.Subject}\" is now closed. If you have a moment, tell us how it went.",
            $"requester-closed:{ticket.Id}",
            cancellationToken);

    private async Task RaiseAsync(
        Ticket ticket, NotificationEventType eventType, string title, string body,
        string key, CancellationToken cancellationToken)
    {
        // A requester acting on their own ticket — closing it, replying to themselves —
        // does not need telling what they just did.
        if (ticket.RequesterId == currentUser.UserId)
        {
            return;
        }

        await notifications.RaiseAsync(
            new NotificationRequest
            {
                OrganizationId = ticket.OrganizationId,
                RecipientUserId = ticket.RequesterId,
                EventType = eventType,
                Title = title,
                Body = body,
                Severity = NotificationSeverity.Info,
                Link = $"/tickets/{ticket.Id}",
                TicketId = ticket.Id,
                TicketNumber = ticket.TicketNumber,
                DeduplicationKey = key,

                // Never a popup. A requester is not sitting in the application waiting to
                // be interrupted; the email is the delivery, and the entry in their list
                // is there for when they do sign in.
                ShowAsPopup = false,
                SendEmail = true,
            },
            cancellationToken);
    }
}
