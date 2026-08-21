using Microsoft.EntityFrameworkCore;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Notifications;

namespace SupportTicketing.Application.Features.Notifications;

/// <summary>
/// Creates notifications and queues their deliveries.
/// </summary>
/// <remarks>
/// Delivery itself is deliberately not attempted here. Sending an email inline would
/// tie the latency of resolving a ticket to the responsiveness of a mail server, and
/// a transient SMTP failure would roll back the resolution. Rows are queued and a
/// background dispatcher drains them.
/// </remarks>
public sealed class NotificationService(IAppDbContext db, IClock clock) : INotificationService
{
    /// <summary>
    /// In-app is always attempted: it is the record of what the user was told,
    /// independent of whether any external system was reachable. Email is decided per
    /// event and per recipient — see <see cref="EmailPolicy"/>.
    /// </summary>

    public async Task<bool> RaiseAsync(NotificationRequest request, CancellationToken cancellationToken)
    {
        // Checked before insert so the common case avoids a constraint violation, but
        // the unique index remains the real guarantee against two concurrent jobs.
        var alreadyRaised = await db.Notifications
            .IgnoreQueryFilters()
            .AnyAsync(
                n => n.RecipientUserId == request.RecipientUserId
                     && n.DeduplicationKey == request.DeduplicationKey
                     && !n.IsDeleted,
                cancellationToken);

        if (alreadyRaised)
        {
            return false;
        }

        var notification = new Notification
        {
            OrganizationId = request.OrganizationId,
            ShowAsPopup = request.ShowAsPopup,
            RecipientUserId = request.RecipientUserId,
            EventType = request.EventType,
            Title = request.Title,
            Body = request.Body,
            Link = request.Link,
            TicketId = request.TicketId,
            TicketNumber = request.TicketNumber,
            Severity = request.Severity,
            DeduplicationKey = request.DeduplicationKey,
        };

        db.Notifications.Add(notification);

        var disabled = await db.NotificationPreferences
            .IgnoreQueryFilters()
            .Where(p => p.UserId == request.RecipientUserId
                        && p.EventType == request.EventType
                        && !p.IsEnabled
                        && !p.IsDeleted)
            .Select(p => p.Channel)
            .ToListAsync(cancellationToken);

        // In-app always; email only when this event, for this recipient, earns one.
        var channels = new List<NotificationChannel> { NotificationChannel.InApp };

        if (request.SendEmail ?? EmailPolicy.ShouldEmail(request.EventType))
        {
            channels.Add(NotificationChannel.Email);
        }

        foreach (var channel in channels.Where(c => !disabled.Contains(c)))
        {
            db.NotificationDeliveries.Add(new NotificationDelivery
            {
                OrganizationId = request.OrganizationId,
                NotificationId = notification.Id,
                Channel = channel,
                State = NotificationDeliveryState.Pending,
                NextAttemptAtUtc = clock.UtcNow,
            });
        }

        return true;
    }

    public async Task<int> RaiseManyAsync(
        IEnumerable<NotificationRequest> requests, CancellationToken cancellationToken)
    {
        var raised = 0;

        foreach (var request in requests)
        {
            if (await RaiseAsync(request, cancellationToken))
            {
                raised++;
            }
        }

        return raised;
    }
}

/// <summary>
/// The in-app channel. Delivery is a no-op because the notification row itself is the
/// deliverable; the client reads it from the notifications endpoint.
/// </summary>
/// <remarks>
/// Implemented as a channel rather than special-cased so that delivery state,
/// retries and reporting work identically across every channel.
/// </remarks>
public sealed class InAppNotificationChannel : INotificationChannel
{
    public NotificationChannel Channel => NotificationChannel.InApp;

    public bool IsEnabled => true;

    public Task SendAsync(Notification notification, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
