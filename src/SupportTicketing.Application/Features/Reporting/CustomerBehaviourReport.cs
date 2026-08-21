using Microsoft.EntityFrameworkCore;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Contracts.Reporting;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Identity;

namespace SupportTicketing.Application.Features.Reporting;

public sealed record GetCustomerBehaviourReportQuery(ReportQueryParameters Parameters)
    : IQuery<CustomerBehaviourReport>;

/// <summary>
/// How individual requesters use the desk.
/// </summary>
/// <remarks>
/// <para>
/// Every other report describes the desk: how fast it answered, how much came in, how
/// well it met its promises. This one describes named people, which is a materially
/// different thing, and that is why it sits behind its own permission held by nobody
/// but the Super Admin. A screen that ranks colleagues is not something to hand out
/// with ordinary reporting access.
/// </para>
/// <para>
/// The figures are shown against the desk's own averages rather than absolute
/// thresholds, because there is no universal number for "too many tickets". Somebody
/// raising three times what everyone else raises is worth a conversation; somebody
/// raising twice as many as a quiet month last year is not.
/// </para>
/// <para>
/// It is a prompt for a conversation, not a verdict. High figures usually mean the
/// person has been handed a system that keeps failing, or that nobody has explained
/// what the impact scale means — both of which are the organization's problem to fix
/// rather than the requester's fault.
/// </para>
/// </remarks>
public sealed class GetCustomerBehaviourReportQueryHandler(
    IAppDbContext db, ICurrentUser currentUser, IClock clock)
    : IQueryHandler<GetCustomerBehaviourReportQuery, CustomerBehaviourReport>
{
    public async Task<CustomerBehaviourReport> HandleAsync(
        GetCustomerBehaviourReportQuery query, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Reports.ViewCustomerBehaviour);

        var (fromUtc, toUtc, _) = ReportWindow.Resolve(
            query.Parameters.FromUtc, query.Parameters.ToUtc, clock.UtcNow);

        var raised = await db.Tickets.AsNoTracking()
            .Where(t => t.CreatedAtUtc >= fromUtc && t.CreatedAtUtc < toUtc)
            .Select(t => new
            {
                t.RequesterId,
                Name = t.Requester!.FirstName + " " + t.Requester.LastName,
                Email = t.Requester.Email,
                Department = t.Department == null ? null : t.Department.Name,
                t.ClaimedImpact,
                t.ClaimedUrgency,
                t.Priority,
                t.Status,
                t.ReopenCount,
                t.CreatedAtUtc,
                t.ResolvedAtUtc,
                t.ClosedAtUtc,
            })
            .ToListAsync(cancellationToken);

        var ratings = await db.SatisfactionRatings.AsNoTracking()
            .Where(r => r.SubmittedAtUtc >= fromUtc && r.SubmittedAtUtc < toUtc)
            .Select(r => new { r.RatedById, r.Rating })
            .ToListAsync(cancellationToken);

        var ratingsByPerson = ratings
            .GroupBy(r => r.RatedById)
            .ToDictionary(g => g.Key, g => g.Average(r => (double)r.Rating));

        var rows = raised
            .GroupBy(t => new { t.RequesterId, t.Name, t.Email, t.Department })
            .Select(g =>
            {
                var total = g.Count();

                // Only tickets they have actually had the chance to confirm. Counting a
                // ticket resolved an hour ago as "unconfirmed" would make a prompt person
                // look negligent.
                var confirmed = g
                    .Where(t => t.ResolvedAtUtc is not null && t.ClosedAtUtc is not null)
                    .Select(t => (t.ClosedAtUtc!.Value - t.ResolvedAtUtc!.Value).TotalHours)
                    .ToList();

                return new CustomerBehaviourRow
                {
                    RequesterId = g.Key.RequesterId,
                    RequesterName = g.Key.Name,
                    RequesterEmail = g.Key.Email,
                    Department = g.Key.Department,

                    TicketsRaised = total,

                    OverClaimedSeverity = g.Count(t => t.ClaimedImpact is not null
                                                       || t.ClaimedUrgency is not null),

                    // Counted per ticket rather than per reopening: somebody who reopened
                    // one ticket four times has one unresolved problem, not four.
                    Reopened = g.Count(t => t.ReopenCount > 0),

                    Cancelled = g.Count(t => t.Status == TicketStatus.Cancelled),

                    HighOrCritical = g.Count(t => t.Priority is PriorityLevel.High
                                                              or PriorityLevel.Critical),

                    AwaitingTheirConfirmation = g.Count(t => t.Status == TicketStatus.Resolved),

                    AverageConfirmationHours = confirmed.Count == 0
                        ? null
                        : Math.Round(confirmed.Average(), 1),

                    AverageSatisfaction = ratingsByPerson.TryGetValue(g.Key.RequesterId, out var score)
                        ? Math.Round(score, 2)
                        : null,
                };
            })
            .OrderByDescending(r => r.TicketsRaised)
            .ThenBy(r => r.RequesterName)
            .ToList();

        var totalRaised = raised.Count;

        return new CustomerBehaviourReport
        {
            FromUtc = fromUtc,
            ToUtc = toUtc,
            Requesters = rows.Count,
            TicketsRaised = totalRaised,

            // The comparison point. Without it every number on the screen is unreadable:
            // eleven tickets means nothing until you know the desk averages three.
            AverageTicketsPerRequester = rows.Count == 0
                ? 0
                : Math.Round((double)totalRaised / rows.Count, 1),

            Rows = rows,
        };
    }
}
