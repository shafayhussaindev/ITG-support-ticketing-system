using Microsoft.EntityFrameworkCore;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Application.Features.Tickets;
using SupportTicketing.Contracts.Sla;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Identity;

namespace SupportTicketing.Application.Features.Sla;

public sealed record GetTicketSlaQuery(Guid TicketId) : IQuery<TicketSlaResponse?>;

public sealed class GetTicketSlaQueryHandler(IAppDbContext db, ICurrentUser currentUser, IClock clock)
    : IQueryHandler<GetTicketSlaQuery, TicketSlaResponse?>
{
    public async Task<TicketSlaResponse?> HandleAsync(
        GetTicketSlaQuery query, CancellationToken cancellationToken)
    {
        // Confirms the caller may see the ticket before revealing anything about its
        // SLA; otherwise this endpoint would be a way around the ticket scope check.
        _ = await TicketScope.FindForCurrentUserAsync(
            db.Tickets.AsNoTracking(), query.TicketId, currentUser, cancellationToken);

        var instance = await db.TicketSlaInstances
            .AsNoTracking()
            .Include(i => i.Policy)
            .FirstOrDefaultAsync(i => i.TicketId == query.TicketId, cancellationToken);

        if (instance is null)
        {
            // No policy matched when the ticket was raised, so no promise exists. That
            // is a legitimate state, reported as absence rather than as zeroes.
            return null;
        }

        var events = await db.SlaEvents
            .AsNoTracking()
            .Where(e => e.SlaInstanceId == instance.Id)
            .OrderBy(e => e.OccurredAtUtc)
            .Select(e => new SlaEventResponse
            {
                EventType = e.EventType.ToString(),
                Level = e.Level,
                OccurredAtUtc = e.OccurredAtUtc,
                Detail = e.Detail,
                Source = e.Source.ToString(),
            })
            .ToListAsync(cancellationToken);

        var now = clock.UtcNow;

        // While paused the countdown freezes, matching how the engine measures it.
        var reference = instance.PausedAtUtc ?? now;

        return new TicketSlaResponse
        {
            TicketId = instance.TicketId,
            PolicyName = instance.Policy?.Name,
            Priority = instance.Priority.ToString(),
            ResponseMinutes = instance.ResponseMinutes,
            ResolutionMinutes = instance.ResolutionMinutes,
            WarningThresholdPercent = instance.WarningThresholdPercent,
            StartedAtUtc = instance.StartedAtUtc,
            ResponseDueAtUtc = instance.ResponseDueAtUtc,
            ResolutionDueAtUtc = instance.ResolutionDueAtUtc,
            FirstRespondedAtUtc = instance.FirstRespondedAtUtc,
            ResolvedAtUtc = instance.ResolvedAtUtc,
            ResponseState = instance.ResponseState.ToString(),
            ResolutionState = instance.ResolutionState.ToString(),
            IsPaused = instance.IsPaused,
            PausedAtUtc = instance.PausedAtUtc,
            TotalPausedMinutes = instance.TotalPausedMinutes,
            ResolutionConsumedPercent = Math.Round(instance.ResolutionConsumedPercent(now), 1),
            ResponseConsumedPercent = Math.Round(instance.ResponseConsumedPercent(now), 1),
            MinutesToResolutionDue = Math.Round((instance.ResolutionDueAtUtc - reference).TotalMinutes, 1),
            HighestEscalationLevel = instance.HighestEscalationLevel,
            Events = events,
        };
    }
}

public sealed record ListEscalationsQuery(bool OpenOnly) : IQuery<IReadOnlyList<EscalationResponse>>;

/// <summary>
/// The escalation queue.
/// </summary>
/// <remarks>
/// Scoped through the ticket, not the escalation. Reading the escalation table
/// directly would expose the existence and subject of tickets the caller cannot
/// otherwise see, so the join is the access control.
/// </remarks>
public sealed class ListEscalationsQueryHandler(IAppDbContext db, ICurrentUser currentUser)
    : IQueryHandler<ListEscalationsQuery, IReadOnlyList<EscalationResponse>>
{
    public async Task<IReadOnlyList<EscalationResponse>> HandleAsync(
        ListEscalationsQuery query, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Escalations.View);

        var visibleTickets = db.Tickets.AsNoTracking().ForCurrentUser(currentUser);

        var escalations =
            from e in db.EscalationHistory.AsNoTracking()
            join t in visibleTickets on e.TicketId equals t.Id
            where !query.OpenOnly
                  || e.State == EscalationState.Raised
                  || e.State == EscalationState.Notified
            orderby e.RaisedAtUtc descending
            select new EscalationResponse
            {
                Id = e.Id,
                TicketId = e.TicketId,
                TicketNumber = t.TicketNumber,
                TicketSubject = t.Subject,
                Priority = t.Priority.ToString(),
                Level = e.Level,
                Trigger = e.Trigger.ToString(),
                State = e.State.ToString(),
                ThresholdPercent = e.ThresholdPercent,
                RecipientName = db.Users
                    .Where(u => u.Id == e.RecipientUserId)
                    .Select(u => u.FirstName + " " + u.LastName)
                    .FirstOrDefault(),
                RaisedAtUtc = e.RaisedAtUtc,
                AcknowledgedAtUtc = e.AcknowledgedAtUtc,
                AcknowledgedByName = db.Users
                    .Where(u => u.Id == e.AcknowledgedById)
                    .Select(u => u.FirstName + " " + u.LastName)
                    .FirstOrDefault(),
                Reason = e.Reason,
            };

        return await escalations.Take(200).ToListAsync(cancellationToken);
    }
}
