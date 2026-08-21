using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Notifications;

namespace SupportTicketing.Application.Features.Notifications;

/// <summary>A notification to raise, described independently of how it will be delivered.</summary>
public sealed record NotificationRequest
{
    public required Guid OrganizationId { get; init; }
    public required Guid RecipientUserId { get; init; }
    public required NotificationEventType EventType { get; init; }
    public required string Title { get; init; }
    public required string Body { get; init; }

    /// <summary>
    /// Stable key that makes the notification idempotent, for example
    /// sla-warning:{ticketId}:resolution. A unique index on recipient and key means a
    /// job that runs twice cannot tell the same person the same thing twice.
    /// </summary>
    public required string DeduplicationKey { get; init; }

    public NotificationSeverity Severity { get; init; } = NotificationSeverity.Info;

    /// <summary>Interrupt this recipient rather than leaving it in the bell.</summary>
    public bool ShowAsPopup { get; init; }

    /// <summary>
    /// Send this by email as well, overriding what the event type would decide.
    /// </summary>
    /// <remarks>
    /// Null defers to <see cref="EmailPolicy"/>. Set explicitly where the caller knows
    /// something the event type cannot — a breach matters to the person holding the
    /// ticket and belongs in a supervisor's list rather than their inbox, and both are
    /// the same event type.
    /// </remarks>
    public bool? SendEmail { get; init; }
    public string? Link { get; init; }
    public Guid? TicketId { get; init; }
    public string? TicketNumber { get; init; }
}

public interface INotificationService
{
    /// <summary>
    /// Raises a notification and queues it on every channel the recipient has not
    /// disabled. Returns false when an identical notification already existed, which
    /// callers can treat as success.
    /// </summary>
    Task<bool> RaiseAsync(NotificationRequest request, CancellationToken cancellationToken);

    Task<int> RaiseManyAsync(IEnumerable<NotificationRequest> requests, CancellationToken cancellationToken);
}

/// <summary>
/// One way of getting a notification to a person.
/// </summary>
/// <remarks>
/// The abstraction exists so email, Teams, Slack or SMS can be added without touching
/// ticket logic. A channel that throws is recorded as a failed delivery and retried;
/// it never fails the operation that raised the notification, because a ticket being
/// resolved must not depend on a mail server being reachable.
/// </remarks>
public interface INotificationChannel
{
    NotificationChannel Channel { get; }

    /// <summary>Whether this channel is configured well enough to attempt delivery.</summary>
    bool IsEnabled { get; }

    Task SendAsync(Notification notification, CancellationToken cancellationToken);
}
