using Microsoft.EntityFrameworkCore;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Application.Features.Tickets;
using SupportTicketing.Contracts.Sla;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Identity;

namespace SupportTicketing.Application.Features.Escalations;

public sealed record GetEscalationSummaryQuery : IQuery<EscalationSummaryResponse>;

/// <summary>
/// The shape of the escalation queue, for somebody watching it rather than working it.
/// </summary>
/// <remarks>
/// <para>
/// An administrator or super admin opening this screen is asking a different question
/// from the staff member who owns one of the tickets. They want to know whether the desk is
/// keeping up, and a flat list of two hundred rows does not answer that.
/// </para>
/// <para>
/// Scoped through ticket visibility like the listing itself, so the counts a person
/// sees are counts of things they are entitled to know about. A summary that quietly
/// totalled everything would leak how much work exists on tickets the caller cannot
/// open.
/// </para>
/// </remarks>
public sealed class GetEscalationSummaryQueryHandler(
    IAppDbContext db, ICurrentUser currentUser, IClock clock)
    : IQueryHandler<GetEscalationSummaryQuery, EscalationSummaryResponse>
{
    public async Task<EscalationSummaryResponse> HandleAsync(
        GetEscalationSummaryQuery query, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Escalations.View);

        var visibleTickets = db.Tickets.AsNoTracking().ForCurrentUser(currentUser);

        var mine = db.EscalationHistory.AsNoTracking()
            .Where(e => visibleTickets.Any(t => t.Id == e.TicketId));

        var now = clock.UtcNow;
        var weekAgo = now.AddDays(-7);

        // One round trip. Six separate counts would each re-run the visibility join,
        // which on a busy desk is the same work six times over.
        var counts = await mine
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Unacknowledged = g.Count(e => e.State == EscalationState.Raised
                                              || e.State == EscalationState.Notified),

                Acknowledged = g.Count(e => e.State == EscalationState.Acknowledged),

                BeyondFirstLevel = g.Count(e => e.Level > 1
                                                && (e.State == EscalationState.Raised
                                                    || e.State == EscalationState.Notified
                                                    || e.State == EscalationState.Acknowledged)),

                SettledLastWeek = g.Count(e => (e.State == EscalationState.Resolved
                                                || e.State == EscalationState.Cancelled)
                                               && e.ResolvedAtUtc != null
                                               && e.ResolvedAtUtc >= weekAgo),

                OldestUnacknowledged = g
                    .Where(e => e.State == EscalationState.Raised
                                || e.State == EscalationState.Notified)
                    .Min(e => (DateTime?)e.RaisedAtUtc),
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (counts is null)
        {
            return new EscalationSummaryResponse
            {
                Unacknowledged = 0,
                Acknowledged = 0,
                Open = 0,
                BeyondFirstLevel = 0,
                SettledLastWeek = 0,
            };
        }

        return new EscalationSummaryResponse
        {
            Unacknowledged = counts.Unacknowledged,
            Acknowledged = counts.Acknowledged,
            Open = counts.Unacknowledged + counts.Acknowledged,
            BeyondFirstLevel = counts.BeyondFirstLevel,
            SettledLastWeek = counts.SettledLastWeek,

            // Rounded to a tenth of an hour. Reporting an age to the millisecond
            // implies a precision the sweep interval does not have.
            OldestUnacknowledgedHours = counts.OldestUnacknowledged is { } oldest
                ? Math.Round((now - oldest).TotalHours, 1)
                : null,
        };
    }
}
