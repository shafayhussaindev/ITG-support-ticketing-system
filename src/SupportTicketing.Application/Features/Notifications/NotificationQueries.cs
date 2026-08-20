using Microsoft.EntityFrameworkCore;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Contracts.Notifications;

namespace SupportTicketing.Application.Features.Notifications;

/// <summary>
/// The notification bell.
/// </summary>
/// <remarks>
/// Every query here filters on the caller's own user id. Notifications are personal:
/// there is no permission that grants sight of someone else's, so the recipient
/// filter is the whole access-control story and is applied at the database.
/// </remarks>
public sealed record GetMyNotificationsQuery(bool UnreadOnly, int Take) : IQuery<NotificationSummaryResponse>;

public sealed class GetMyNotificationsQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IQueryHandler<GetMyNotificationsQuery, NotificationSummaryResponse>
{
    public async Task<NotificationSummaryResponse> HandleAsync(
        GetMyNotificationsQuery query, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            throw new ForbiddenException();
        }

        var mine = db.Notifications.AsNoTracking().Where(n => n.RecipientUserId == userId);

        var unreadCount = await mine.CountAsync(n => n.ReadAtUtc == null, cancellationToken);

        var recent = await mine
            .Where(n => !query.UnreadOnly || n.ReadAtUtc == null)
            .OrderByDescending(n => n.CreatedAtUtc)
            .Take(Math.Clamp(query.Take, 1, 100))
            .Select(n => new NotificationResponse
            {
                Id = n.Id,
                EventType = n.EventType.ToString(),
                Title = n.Title,
                Body = n.Body,
                Severity = n.Severity.ToString(),
                Link = n.Link,
                TicketId = n.TicketId,
                TicketNumber = n.TicketNumber,
                ShowAsPopup = n.ShowAsPopup,
                IsRead = n.ReadAtUtc != null,
                ReadAtUtc = n.ReadAtUtc,
                CreatedAtUtc = n.CreatedAtUtc,
            })
            .ToListAsync(cancellationToken);

        return new NotificationSummaryResponse { UnreadCount = unreadCount, Recent = recent };
    }
}

public sealed record MarkNotificationsReadCommand(IReadOnlyList<Guid>? Ids, bool All) : ICommand<int>;

public sealed class MarkNotificationsReadCommandHandler(IAppDbContext db, ICurrentUser currentUser, IClock clock)
    : ICommandHandler<MarkNotificationsReadCommand, int>
{
    public async Task<int> HandleAsync(
        MarkNotificationsReadCommand command, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            throw new ForbiddenException();
        }

        // The recipient predicate is not optional. Without it, a caller could mark
        // another person's notifications read by supplying their identifiers.
        var query = db.Notifications
            .AsTracking()
            .Where(n => n.RecipientUserId == userId && n.ReadAtUtc == null);

        if (!command.All)
        {
            var ids = command.Ids ?? [];
            query = query.Where(n => ids.Contains(n.Id));
        }

        var notifications = await query.ToListAsync(cancellationToken);
        var now = clock.UtcNow;

        foreach (var notification in notifications)
        {
            notification.ReadAtUtc = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        return notifications.Count;
    }
}
