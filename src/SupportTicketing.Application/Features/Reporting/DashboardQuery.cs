using Microsoft.EntityFrameworkCore;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Application.Features.Tickets;
using SupportTicketing.Contracts.Reporting;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Identity;
using SupportTicketing.Domain.Tickets;

namespace SupportTicketing.Application.Features.Reporting;

public sealed record GetDashboardQuery(int Days) : IQuery<DashboardResponse>;

/// <summary>
/// Builds the dashboard from the tickets the caller may see.
/// </summary>
/// <remarks>
/// <para>
/// There is one dashboard endpoint rather than one per role. The figures differ
/// because the scope filter differs, not because the code branches on a job title —
/// which means a new role gets a correct dashboard without a code change, and no
/// role can be given a dashboard that quietly exceeds its data scope.
/// </para>
/// <para>
/// Every aggregate is computed in SQL. Pulling tickets into memory to count them
/// would work for the demo dataset and fall over on a real one.
/// </para>
/// </remarks>
public sealed class GetDashboardQueryHandler(IAppDbContext db, ICurrentUser currentUser, IClock clock)
    : IQueryHandler<GetDashboardQuery, DashboardResponse>
{
    /// <summary>
    /// Relative cost of a ticket by priority, used for the weighted workload score.
    /// A critical outage is not one unit of work in the way a password reset is.
    /// </summary>
    private const int CriticalWeight = 8;
    private const int HighWeight = 4;
    private const int MediumWeight = 2;
    private const int LowWeight = 1;

    public async Task<DashboardResponse> HandleAsync(
        GetDashboardQuery query, CancellationToken cancellationToken)
    {
        if (!currentUser.CanSeeAnyTickets())
        {
            throw new ForbiddenException("You do not have permission to view ticket data.");
        }

        var now = clock.UtcNow;
        var today = now.Date;
        var days = Math.Clamp(query.Days, 7, 180);
        var from = today.AddDays(-days + 1);

        var visible = db.Tickets.AsNoTracking().ForCurrentUser(currentUser);

        var open = visible.Where(t =>
            t.Status != TicketStatus.Closed && t.Status != TicketStatus.Cancelled);

        var kpis = await BuildKpisAsync(visible, open, today, now, from, cancellationToken);
        var volume = await BuildVolumeAsync(visible, from, cancellationToken);
        var byStatus = await BuildByStatusAsync(open, cancellationToken);
        var byPriority = await BuildByPriorityAsync(open, cancellationToken);
        var byCategory = await BuildByCategoryAsync(open, cancellationToken);

        // Only meaningful to somebody who can see beyond their own queue.
        var workload = currentUser.Has(Permissions.Tickets.ViewTeam)
            ? await BuildWorkloadAsync(open, cancellationToken)
            : [];

        return new DashboardResponse
        {
            Scope = currentUser.Scope.ToString(),
            Kpis = kpis,
            VolumeByDay = volume,
            ByStatus = byStatus,
            ByPriority = byPriority,
            ByCategory = byCategory,
            AgentWorkload = workload,
        };
    }

    private async Task<DashboardKpis> BuildKpisAsync(
        IQueryable<Ticket> visible, IQueryable<Ticket> open, DateTime today, DateTime now,
        DateTime windowStart, CancellationToken cancellationToken)
    {
        var totalOpen = await open.CountAsync(cancellationToken);
        var newToday = await visible.CountAsync(t => t.CreatedAtUtc >= today, cancellationToken);
        var criticalOpen = await open.CountAsync(t => t.Priority == PriorityLevel.Critical, cancellationToken);
        var unassigned = await open.CountAsync(t => t.AssignedAgentId == null, cancellationToken);
        var resolvedToday = await visible.CountAsync(t => t.ResolvedAtUtc >= today, cancellationToken);
        var reopened = await visible.CountAsync(t => t.ReopenCount > 0, cancellationToken);

        // SLA figures join through the instance so a ticket with no policy is excluded
        // rather than counted as compliant, which would flatter the numbers.
        var slaForVisible =
            from instance in db.TicketSlaInstances.AsNoTracking()
            join ticket in visible on instance.TicketId equals ticket.Id
            select instance;

        var breached = await slaForVisible
            .CountAsync(i => i.ResolutionState == SlaTimerState.Breached, cancellationToken);

        var approaching = await slaForVisible
            .CountAsync(
                i => i.ResolutionState == SlaTimerState.Running
                     && i.PausedAtUtc == null
                     && i.ResolutionDueAtUtc > now
                     && i.ResolutionWarningRaised,
                cancellationToken);

        var settled = await slaForVisible
            .CountAsync(
                i => i.ResolutionState == SlaTimerState.Met || i.ResolutionState == SlaTimerState.Breached,
                cancellationToken);

        var met = await slaForVisible
            .CountAsync(i => i.ResolutionState == SlaTimerState.Met, cancellationToken);

        double? compliance = settled == 0 ? null : Math.Round((double)met / settled * 100, 1);

        // Averaged over tickets that actually carry the timestamp. Treating a missing
        // first response as zero would make an unanswered backlog look instantaneous.
        //
        // The subtraction happens in memory rather than through EF.Functions.DateDiff,
        // which is SQL Server specific and would drag the provider into the Application
        // layer — something the architecture tests forbid. Only two datetime columns
        // per row cross the wire, and the window below bounds how many rows that is.
        var responseWindow = await visible
            .Where(t => t.FirstRespondedAtUtc != null && t.CreatedAtUtc >= windowStart)
            .Select(t => new { t.CreatedAtUtc, Responded = t.FirstRespondedAtUtc!.Value })
            .ToListAsync(cancellationToken);

        double? avgResponse = responseWindow.Count == 0
            ? null
            : responseWindow.Average(x => (x.Responded - x.CreatedAtUtc).TotalMinutes);

        var resolutionWindow = await visible
            .Where(t => t.ResolvedAtUtc != null && t.CreatedAtUtc >= windowStart)
            .Select(t => new { t.CreatedAtUtc, Resolved = t.ResolvedAtUtc!.Value })
            .ToListAsync(cancellationToken);

        double? avgResolution = resolutionWindow.Count == 0
            ? null
            : resolutionWindow.Average(x => (x.Resolved - x.CreatedAtUtc).TotalMinutes);

        var ratings =
            from rating in db.SatisfactionRatings.AsNoTracking()
            join ticket in visible on rating.TicketId equals ticket.Id
            select rating.Rating;

        var responses = await ratings.CountAsync(cancellationToken);
        double? csat = responses == 0 ? null : Math.Round(await ratings.AverageAsync(cancellationToken), 2);

        return new DashboardKpis
        {
            TotalOpen = totalOpen,
            NewToday = newToday,
            CriticalOpen = criticalOpen,
            Unassigned = unassigned,
            ResolvedToday = resolvedToday,
            ApproachingBreach = approaching,
            Breached = breached,
            SlaCompliancePercent = compliance,
            AverageFirstResponseMinutes = avgResponse is null ? null : Math.Round(avgResponse.Value, 1),
            AverageResolutionMinutes = avgResolution is null ? null : Math.Round(avgResolution.Value, 1),
            ReopenedCount = reopened,
            AverageSatisfaction = csat,
            SatisfactionResponses = responses,
        };
    }

    /// <summary>
    /// Daily raised and resolved counts.
    /// </summary>
    /// <remarks>
    /// Grouped in SQL, then zero-filled in memory so quiet days appear as zero rather
    /// than vanishing. A line chart that silently skips empty days compresses the
    /// x-axis and makes a weekend lull look like a sudden collapse in volume.
    /// </remarks>
    private async Task<IReadOnlyList<TimeSeriesPoint>> BuildVolumeAsync(
        IQueryable<Ticket> visible, DateTime from, CancellationToken cancellationToken)
    {
        var raised = await visible
            .Where(t => t.CreatedAtUtc >= from)
            .GroupBy(t => t.CreatedAtUtc.Date)
            .Select(g => new { Date = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var resolved = await visible
            .Where(t => t.ResolvedAtUtc != null && t.ResolvedAtUtc >= from)
            .GroupBy(t => t.ResolvedAtUtc!.Value.Date)
            .Select(g => new { Date = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var raisedByDate = raised.ToDictionary(x => x.Date, x => x.Count);
        var resolvedByDate = resolved.ToDictionary(x => x.Date, x => x.Count);

        var points = new List<TimeSeriesPoint>();
        var today = DateTime.UtcNow.Date;

        for (var date = from; date <= today; date = date.AddDays(1))
        {
            points.Add(new TimeSeriesPoint
            {
                Date = date,
                Raised = raisedByDate.GetValueOrDefault(date),
                Resolved = resolvedByDate.GetValueOrDefault(date),
            });
        }

        return points;
    }

    private static async Task<IReadOnlyList<CategoryCount>> BuildByStatusAsync(
        IQueryable<Ticket> open, CancellationToken cancellationToken)
    {
        var rows = await open
            .GroupBy(t => t.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return rows
            .OrderByDescending(r => r.Count)
            .Select(r => new CategoryCount
            {
                Label = Humanise(r.Status.ToString()),
                Count = r.Count,
                // Drill-down reuses the ticket list's own filter contract, so a chart
                // click lands on exactly the rows the segment counted.
                DrillDownQuery = $"status={r.Status}&openOnly=true",
            })
            .ToList();
    }

    private static async Task<IReadOnlyList<CategoryCount>> BuildByPriorityAsync(
        IQueryable<Ticket> open, CancellationToken cancellationToken)
    {
        var rows = await open
            .GroupBy(t => t.Priority)
            .Select(g => new { Priority = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        // Ordered by severity rather than by count, because a priority axis that
        // reorders itself as the data changes is hard to read at a glance.
        return rows
            .OrderByDescending(r => r.Priority)
            .Select(r => new CategoryCount
            {
                Label = r.Priority.ToString(),
                Count = r.Count,
                DrillDownQuery = $"priority={r.Priority}&openOnly=true",
            })
            .ToList();
    }

    private static async Task<IReadOnlyList<CategoryCount>> BuildByCategoryAsync(
        IQueryable<Ticket> open, CancellationToken cancellationToken)
    {
        var rows = await open
            .GroupBy(t => new { t.CategoryId, Name = t.Category!.Name })
            .Select(g => new { g.Key.CategoryId, g.Key.Name, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .Take(10)
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new CategoryCount
            {
                Label = r.Name ?? "Uncategorised",
                Count = r.Count,
                DrillDownQuery = r.CategoryId is null ? null : $"categoryId={r.CategoryId}&openOnly=true",
            })
            .ToList();
    }

    private async Task<IReadOnlyList<AgentWorkload>> BuildWorkloadAsync(
        IQueryable<Ticket> open, CancellationToken cancellationToken)
    {
        var breachedTicketIds = db.TicketSlaInstances
            .AsNoTracking()
            .Where(i => i.ResolutionState == SlaTimerState.Breached)
            .Select(i => i.TicketId);

        var rows = await open
            .Where(t => t.AssignedAgentId != null)
            .GroupBy(t => new
            {
                AgentId = t.AssignedAgentId!.Value,
                Name = t.AssignedAgent!.FirstName + " " + t.AssignedAgent.LastName,
            })
            .Select(g => new
            {
                g.Key.AgentId,
                g.Key.Name,
                Open = g.Count(),
                Critical = g.Count(t => t.Priority == PriorityLevel.Critical),
                Breached = g.Count(t => breachedTicketIds.Contains(t.Id)),

                // Weighted in SQL so ordering happens in the database rather than
                // after pulling every agent's rows back.
                Weighted = g.Sum(t =>
                    t.Priority == PriorityLevel.Critical ? CriticalWeight
                    : t.Priority == PriorityLevel.High ? HighWeight
                    : t.Priority == PriorityLevel.Medium ? MediumWeight
                    : LowWeight),
            })
            .OrderByDescending(g => g.Weighted)
            .Take(15)
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new AgentWorkload
            {
                AgentId = r.AgentId,
                AgentName = r.Name,
                OpenTickets = r.Open,
                CriticalTickets = r.Critical,
                BreachedTickets = r.Breached,
                WeightedScore = r.Weighted,
            })
            .ToList();
    }

    /// <summary>Turns PascalCase enum names into readable labels.</summary>
    private static string Humanise(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, "([a-z])([A-Z])", "$1 $2");
}

internal static class DashboardPermissionExtensions
{
    public static bool CanSeeAnyTickets(this ICurrentUser user) =>
        user.Has(Permissions.Tickets.ViewOwn)
        || user.Has(Permissions.Tickets.ViewAssigned)
        || user.Has(Permissions.Tickets.ViewTeam)
        || user.Has(Permissions.Tickets.ViewDepartment)
        || user.Has(Permissions.Tickets.ViewOrganization)
        || user.Has(Permissions.Tickets.ViewAll);
}
