using SupportTicketing.Domain.Common;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Identity;

namespace SupportTicketing.Domain.Notifications;

/// <summary>
/// Something a user should be told about, independent of how it reaches them.
/// </summary>
/// <remarks>
/// The notification and its delivery attempts are separate records. One notification
/// may be delivered in-app immediately and by email on retry, and an email that fails
/// three times must not make the in-app copy disappear.
/// </remarks>
public class Notification : TenantEntity
{
    public Guid RecipientUserId { get; set; }
    public User? Recipient { get; set; }

    public NotificationEventType EventType { get; set; }

    public required string Title { get; set; }
    public required string Body { get; set; }

    /// <summary>Relative path the client navigates to, for example /tickets/{id}.</summary>
    public string? Link { get; set; }

    public Guid? TicketId { get; set; }
    public string? TicketNumber { get; set; }

    /// <summary>Drives the visual treatment in the notification list.</summary>
    public NotificationSeverity Severity { get; set; } = NotificationSeverity.Info;

    /// <summary>Whether this should interrupt the recipient rather than wait in the bell.</summary>
    /// <remarks>
    /// <para>
    /// The same event reaches different people for different reasons. The person holding
    /// the ticket needs to know now, because they are the one who can still act on it;
    /// a supervisor needs it on a list they review, because their job is the pattern
    /// rather than the individual ticket.
    /// </para>
    /// <para>
    /// Set per recipient, not per event, which is why it lives here and not on the
    /// notification's type. Interrupting everybody would train all of them to dismiss it.
    /// </para>
    /// </remarks>
    public bool ShowAsPopup { get; set; }

    public DateTime? ReadAtUtc { get; set; }
    public bool IsRead => ReadAtUtc.HasValue;

    /// <summary>
    /// Stable key that makes creation idempotent.
    /// </summary>
    /// <remarks>
    /// Composed from the event and the thing it concerns, for example
    /// sla-warning:{ticketId}:resolution. A unique index on recipient and key means a
    /// background job that runs twice cannot notify the same person twice about the
    /// same fact, which is the difference between a useful inbox and an ignored one.
    /// </remarks>
    public required string DeduplicationKey { get; set; }

    public ICollection<NotificationDelivery> Deliveries { get; set; } = [];
}

public enum NotificationSeverity
{
    Info = 1,
    Success = 2,
    Warning = 3,
    Critical = 4,
}

/// <summary>One attempt to deliver a notification through one channel.</summary>
public class NotificationDelivery : TenantEntity
{
    public Guid NotificationId { get; set; }
    public Notification? Notification { get; set; }

    public NotificationChannel Channel { get; set; }
    public NotificationDeliveryState State { get; set; } = NotificationDeliveryState.Pending;

    public int AttemptCount { get; set; }
    public DateTime? LastAttemptAtUtc { get; set; }
    public DateTime? DeliveredAtUtc { get; set; }

    /// <summary>Why the last attempt failed. Never contains the message body or a credential.</summary>
    public string? FailureReason { get; set; }

    /// <summary>
    /// When to try again, backing off exponentially. Null once delivered or dead-lettered.
    /// </summary>
    public DateTime? NextAttemptAtUtc { get; set; }
}

/// <summary>
/// Per-user opt-out for one event type on one channel.
/// </summary>
/// <remarks>
/// Absence of a row means the channel default applies, so a new event type reaches
/// people without every user having to opt in first.
/// </remarks>
public class NotificationPreference : TenantEntity
{
    public Guid UserId { get; set; }
    public User? User { get; set; }

    public NotificationEventType EventType { get; set; }
    public NotificationChannel Channel { get; set; }

    public bool IsEnabled { get; set; } = true;
}
