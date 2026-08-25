using Microsoft.EntityFrameworkCore;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Application.Features.Tickets;
using SupportTicketing.Contracts.Reporting;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Identity;
using SupportTicketing.Domain.Tickets;

namespace SupportTicketing.Application.Features.Reporting;

/// <summary>
/// Shared period handling for the reports.
/// </summary>
/// <remarks>
/// Reports take a period but never a scope. Which rows a caller may aggregate comes
/// from their token through <see cref="TicketScope.ForCurrentUser"/>; letting a
/// request widen that would turn every report into a way of reading other people's
/// tickets one summary at a time.
/// </remarks>
internal static class ReportWindow
{
    /// <summary>Two years. Long enough for a year-on-year comparison, short enough to stay bounded.</summary>
    private const int MaxDays = 730;

    private const int DefaultDays = 30;

    internal static (DateTime From, DateTime To, int Days) Resolve(
        DateTime? fromUtc, DateTime? toUtc, DateTime now)
    {
        var to = toUtc ?? now;
        var from = fromUtc ?? to.Date.AddDays(-DefaultDays + 1);

        // A reversed range is a client bug, not a request for zero rows. Swapping is
        // the kinder reading and produces a report rather than a puzzling blank page.
        if (from > to)
        {
            (from, to) = (to, from);
        }

        // Counted in whole days from midnight to midnight, because that is what the
        // daily series iterates over. Deriving it from the raw span instead lets a
        // period ending at noon report one more day than the chart actually plots.
        var days = (to.Date - from.Date).Days + 1;

        if (days > MaxDays)
        {
            from = to.Date.AddDays(-MaxDays + 1);
            days = MaxDays;
        }

        return (from, to, days);
    }

    internal static IQueryable<Ticket> Visible(
        IAppDbContext db, ICurrentUser currentUser, ReportQueryParameters parameters,
        DateTime from, DateTime to)
    {
        currentUser.Require(Permissions.Reports.View);

        if (!currentUser.CanSeeAnyTickets())
        {
            throw new ForbiddenException("You do not have permission to view ticket data.");
        }

        var query = db.Tickets.AsNoTracking()
            .ForCurrentUser(currentUser)
            .Where(t => t.CreatedAtUtc >= from && t.CreatedAtUtc <= to);

        if (parameters.TeamId is { } teamId)
        {
            query = query.Where(t => t.AssignedTeamId == teamId);
        }

        if (parameters.CategoryId is { } categoryId)
        {
            query = query.Where(t => t.CategoryId == categoryId);
        }

        if (parameters.StaffId is { } staffId)
        {
            query = query.Where(t => t.AssignedStaffId == staffId);
        }

        return query;
    }

    internal static async Task<ReportPeriod> DescribeAsync(
        IQueryable<Ticket> visible, ICurrentUser currentUser,
        DateTime from, DateTime to, int days, CancellationToken cancellationToken) =>
        new()
        {
            FromUtc = from,
            ToUtc = to,
            Days = days,
            Scope = currentUser.Scope.ToString(),
            TicketsInScope = await visible.CountAsync(cancellationToken),
        };

    internal static double? Round(double? value, int digits = 1) =>
        value is null ? null : Math.Round(value.Value, digits);
}

// ------------------------------------------------------------ SLA compliance

public sealed record GetSlaComplianceReportQuery(ReportQueryParameters Parameters)
    : IQuery<SlaComplianceReport>;

/// <summary>
/// How often targets were met, broken down three ways.
/// </summary>
/// <remarks>
/// Compliance is measured over <em>settled</em> clocks only. A running clock has not
/// yet failed and counting it as met would make a fresh backlog look like a perfect
/// month; counting it as breached would do the opposite. Tickets with no SLA policy
/// are excluded from the denominator entirely rather than treated as compliant.
/// </remarks>
public sealed class GetSlaComplianceReportQueryHandler(
    IAppDbContext db, ICurrentUser currentUser, IClock clock)
    : IQueryHandler<GetSlaComplianceReportQuery, SlaComplianceReport>
{
    public async Task<SlaComplianceReport> HandleAsync(
        GetSlaComplianceReportQuery query, CancellationToken cancellationToken)
    {
        var (from, to, days) = ReportWindow.Resolve(
            query.Parameters.FromUtc, query.Parameters.ToUtc, clock.UtcNow);

        var visible = ReportWindow.Visible(db, currentUser, query.Parameters, from, to);

        // One pass over the joined rows. Grouping three ways in SQL would mean three
        // round trips returning at most a few hundred rows between them; pulling the
        // narrow projection once and grouping in memory is both fewer queries and
        // less code, and the row count is bounded by the period filter above.
        var rows = await (
            from instance in db.TicketSlaInstances.AsNoTracking()
            join ticket in visible on instance.TicketId equals ticket.Id
            select new SlaRow
            {
                Priority = instance.Priority,
                TeamName = ticket.AssignedTeam == null ? null : ticket.AssignedTeam.Name,
                CategoryName = ticket.Category == null ? null : ticket.Category.Name,
                ResponseState = instance.ResponseState,
                ResolutionState = instance.ResolutionState,
                StartedAtUtc = instance.StartedAtUtc,
                FirstRespondedAtUtc = instance.FirstRespondedAtUtc,
                ResolvedAtUtc = instance.ResolvedAtUtc,
            })
            .ToListAsync(cancellationToken);

        var period = await ReportWindow.DescribeAsync(visible, currentUser, from, to, days, cancellationToken);

        return new SlaComplianceReport
        {
            Period = period,
            Overall = Summarise("All tickets", rows),
            ByPriority =
            [
                .. rows.GroupBy(r => r.Priority)
                    .OrderByDescending(g => g.Key)
                    .Select(g => Summarise(g.Key.ToString(), [.. g]))
            ],
            ByTeam =
            [
                .. rows.GroupBy(r => r.TeamName ?? "Unassigned")
                    .OrderByDescending(g => g.Count())
                    .Select(g => Summarise(g.Key, [.. g]))
            ],
            ByCategory =
            [
                .. rows.GroupBy(r => r.CategoryName ?? "Uncategorised")
                    .OrderByDescending(g => g.Count())
                    .Select(g => Summarise(g.Key, [.. g]))
            ],
        };
    }

    private static SlaComplianceRow Summarise(string label, IReadOnlyList<SlaRow> rows)
    {
        var resolutionMet = rows.Count(r => r.ResolutionState == SlaTimerState.Met);
        var resolutionBreached = rows.Count(r => r.ResolutionState == SlaTimerState.Breached);
        var settled = resolutionMet + resolutionBreached;

        var responded = rows.Where(r => r.FirstRespondedAtUtc is not null).ToList();
        var resolved = rows.Where(r => r.ResolvedAtUtc is not null).ToList();

        return new SlaComplianceRow
        {
            Label = label,
            Tracked = rows.Count,
            ResponseMet = rows.Count(r => r.ResponseState == SlaTimerState.Met),
            ResponseBreached = rows.Count(r => r.ResponseState == SlaTimerState.Breached),
            ResolutionMet = resolutionMet,
            ResolutionBreached = resolutionBreached,
            Unsettled = rows.Count - settled,
            CompliancePercent = settled == 0
                ? null
                : Math.Round((double)resolutionMet / settled * 100, 1),
            AverageResponseMinutes = responded.Count == 0
                ? null
                : Math.Round(responded.Average(r =>
                    (r.FirstRespondedAtUtc!.Value - r.StartedAtUtc).TotalMinutes), 1),
            AverageResolutionMinutes = resolved.Count == 0
                ? null
                : Math.Round(resolved.Average(r =>
                    (r.ResolvedAtUtc!.Value - r.StartedAtUtc).TotalMinutes), 1),
        };
    }

    private sealed class SlaRow
    {
        public PriorityLevel Priority { get; init; }
        public string? TeamName { get; init; }
        public string? CategoryName { get; init; }
        public SlaTimerState ResponseState { get; init; }
        public SlaTimerState ResolutionState { get; init; }
        public DateTime StartedAtUtc { get; init; }
        public DateTime? FirstRespondedAtUtc { get; init; }
        public DateTime? ResolvedAtUtc { get; init; }
    }
}

// --------------------------------------------------------- Staff performance

public sealed record GetStaffPerformanceReportQuery(ReportQueryParameters Parameters)
    : IQuery<StaffPerformanceReport>;

/// <summary>
/// Per-agent throughput and quality.
/// </summary>
/// <remarks>
/// Resolved counts appear beside reopen counts and satisfaction on purpose. Volume
/// alone rewards closing tickets quickly whether or not the problem went away, and a
/// report that measures only what is easy to measure will be optimised for.
/// </remarks>
public sealed class GetStaffPerformanceReportQueryHandler(
    IAppDbContext db, ICurrentUser currentUser, IClock clock)
    : IQueryHandler<GetStaffPerformanceReportQuery, StaffPerformanceReport>
{
    public async Task<StaffPerformanceReport> HandleAsync(
        GetStaffPerformanceReportQuery query, CancellationToken cancellationToken)
    {
        var (from, to, days) = ReportWindow.Resolve(
            query.Parameters.FromUtc, query.Parameters.ToUtc, clock.UtcNow);

        var visible = ReportWindow.Visible(db, currentUser, query.Parameters, from, to);

        // Individual figures are management information. Somebody who can only see
        // their own queue gets the period header and an empty table rather than a
        // 403, so the page still renders and says why it is empty.
        if (!currentUser.Has(Permissions.Tickets.ViewTeam))
        {
            return new StaffPerformanceReport
            {
                Period = await ReportWindow.DescribeAsync(
                    visible, currentUser, from, to, days, cancellationToken),
                Staff = [],
            };
        }

        var assigned = visible.Where(t => t.AssignedStaffId != null);

        var rows = await assigned
            .Select(t => new StaffRow
            {
                StaffId = t.AssignedStaffId!.Value,
                StaffName = t.AssignedStaff!.FirstName + " " + t.AssignedStaff.LastName,
                TeamName = t.AssignedTeam == null ? null : t.AssignedTeam.Name,
                Status = t.Status,
                CreatedAtUtc = t.CreatedAtUtc,
                FirstRespondedAtUtc = t.FirstRespondedAtUtc,
                ResolvedAtUtc = t.ResolvedAtUtc,
                ClosedAtUtc = t.ClosedAtUtc,
                ReopenCount = t.ReopenCount,
            })
            .ToListAsync(cancellationToken);

        var breachedByStaff = await (
            from instance in db.TicketSlaInstances.AsNoTracking()
            join ticket in assigned on instance.TicketId equals ticket.Id
            where instance.ResolutionState == SlaTimerState.Breached
            group ticket by ticket.AssignedStaffId!.Value into g
            select new { StaffId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.StaffId, x => x.Count, cancellationToken);

        var ratingsByStaff = await (
            from rating in db.SatisfactionRatings.AsNoTracking()
            join ticket in assigned on rating.TicketId equals ticket.Id
            group rating by ticket.AssignedStaffId!.Value into g
            select new { StaffId = g.Key, Count = g.Count(), Average = g.Average(r => (double)r.Rating) })
            .ToDictionaryAsync(x => x.StaffId, x => new { x.Count, x.Average }, cancellationToken);

        var staff = rows
            .GroupBy(r => new { r.StaffId, r.StaffName, r.TeamName })
            .Select(g =>
            {
                var responded = g.Where(r => r.FirstRespondedAtUtc is not null).ToList();
                var resolved = g.Where(r => r.ResolvedAtUtc is not null).ToList();

                ratingsByStaff.TryGetValue(g.Key.StaffId, out var csat);

                return new StaffPerformanceRow
                {
                    StaffId = g.Key.StaffId,
                    StaffName = g.Key.StaffName,
                    TeamName = g.Key.TeamName,
                    OpenTickets = g.Count(r =>
                        r.Status != TicketStatus.Closed && r.Status != TicketStatus.Cancelled),
                    ResolvedInPeriod = resolved.Count,
                    ClosedInPeriod = g.Count(r => r.ClosedAtUtc is not null),
                    ReopenedAfterResolution = g.Count(r => r.ReopenCount > 0),
                    SlaBreached = breachedByStaff.GetValueOrDefault(g.Key.StaffId),
                    AverageFirstResponseMinutes = responded.Count == 0
                        ? null
                        : ReportWindow.Round(responded.Average(r =>
                            (r.FirstRespondedAtUtc!.Value - r.CreatedAtUtc).TotalMinutes)),
                    AverageResolutionMinutes = resolved.Count == 0
                        ? null
                        : ReportWindow.Round(resolved.Average(r =>
                            (r.ResolvedAtUtc!.Value - r.CreatedAtUtc).TotalMinutes)),
                    AverageSatisfaction = csat is null ? null : Math.Round(csat.Average, 2),
                    SatisfactionResponses = csat?.Count ?? 0,
                };
            })
            .OrderByDescending(a => a.ResolvedInPeriod)
            .ThenBy(a => a.StaffName)
            .ToList();

        return new StaffPerformanceReport
        {
            Period = await ReportWindow.DescribeAsync(visible, currentUser, from, to, days, cancellationToken),
            Staff = staff,
        };
    }

    private sealed class StaffRow
    {
        public Guid StaffId { get; init; }
        public required string StaffName { get; init; }
        public string? TeamName { get; init; }
        public TicketStatus Status { get; init; }
        public DateTime CreatedAtUtc { get; init; }
        public DateTime? FirstRespondedAtUtc { get; init; }
        public DateTime? ResolvedAtUtc { get; init; }
        public DateTime? ClosedAtUtc { get; init; }
        public int ReopenCount { get; init; }
    }
}

// -------------------------------------------------------------- Volume trend

public sealed record GetVolumeTrendReportQuery(ReportQueryParameters Parameters)
    : IQuery<VolumeTrendReport>;

/// <summary>
/// Raised against resolved over time, with the backlog that results.
/// </summary>
/// <remarks>
/// The backlog line is what makes this report worth reading: raised and resolved can
/// both be climbing while the queue quietly grows, and only the running balance shows
/// it. The balance is anchored to the real open count at the start of the period
/// rather than starting from zero, so day one is not a cliff.
/// </remarks>
public sealed class GetVolumeTrendReportQueryHandler(
    IAppDbContext db, ICurrentUser currentUser, IClock clock)
    : IQueryHandler<GetVolumeTrendReportQuery, VolumeTrendReport>
{
    public async Task<VolumeTrendReport> HandleAsync(
        GetVolumeTrendReportQuery query, CancellationToken cancellationToken)
    {
        var (from, to, days) = ReportWindow.Resolve(
            query.Parameters.FromUtc, query.Parameters.ToUtc, clock.UtcNow);

        var visible = ReportWindow.Visible(db, currentUser, query.Parameters, from, to);

        // Opening backlog ignores the period filter deliberately: it counts what was
        // already outstanding when the period began, which by definition was raised
        // before it. Scope still applies.
        var priorTickets = db.Tickets.AsNoTracking()
            .ForCurrentUser(currentUser)
            .Where(t => t.CreatedAtUtc < from);

        var openingBacklog = await priorTickets.CountAsync(
            t => t.ResolvedAtUtc == null || t.ResolvedAtUtc >= from, cancellationToken);

        var raised = await visible
            .GroupBy(t => t.CreatedAtUtc.Date)
            .Select(g => new { Date = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var resolved = await visible
            .Where(t => t.ResolvedAtUtc != null)
            .GroupBy(t => t.ResolvedAtUtc!.Value.Date)
            .Select(g => new { Date = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var reopened = await visible
            .Where(t => t.ReopenedAtUtc != null)
            .GroupBy(t => t.ReopenedAtUtc!.Value.Date)
            .Select(g => new { Date = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var raisedByDate = raised.ToDictionary(x => x.Date, x => x.Count);
        var resolvedByDate = resolved.ToDictionary(x => x.Date, x => x.Count);
        var reopenedByDate = reopened.ToDictionary(x => x.Date, x => x.Count);

        var points = new List<VolumeTrendPoint>(days);
        var backlog = openingBacklog;

        // Zero-filled so a quiet weekend appears as zero rather than vanishing. A
        // line chart that skips empty days compresses its own x-axis and turns a
        // Sunday into what looks like a collapse in demand.
        for (var date = from.Date; date <= to.Date; date = date.AddDays(1))
        {
            var raisedToday = raisedByDate.GetValueOrDefault(date);
            var resolvedToday = resolvedByDate.GetValueOrDefault(date);

            backlog += raisedToday - resolvedToday;

            points.Add(new VolumeTrendPoint
            {
                Date = date,
                Raised = raisedToday,
                Resolved = resolvedToday,
                Reopened = reopenedByDate.GetValueOrDefault(date),
                Backlog = Math.Max(0, backlog),
            });
        }

        var byCategory = await visible
            .GroupBy(t => t.Category == null ? "Uncategorised" : t.Category.Name)
            .Select(g => new { Label = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(12)
            .ToListAsync(cancellationToken);

        var byType = await visible
            .GroupBy(t => t.Type)
            .Select(g => new { Label = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var bySource = await visible
            .GroupBy(t => t.Source)
            .Select(g => new { Label = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return new VolumeTrendReport
        {
            Period = await ReportWindow.DescribeAsync(visible, currentUser, from, to, days, cancellationToken),
            Days = points,
            OpeningBacklog = openingBacklog,
            ByCategory = [.. byCategory.Select(x => new LabelledCount(x.Label, x.Count))],
            ByType = [.. byType.OrderByDescending(x => x.Count).Select(x => new LabelledCount(x.Label.ToString(), x.Count))],
            BySource = [.. bySource.OrderByDescending(x => x.Count).Select(x => new LabelledCount(x.Label.ToString(), x.Count))],
        };
    }
}

// -------------------------------------------------------------- Satisfaction

public sealed record GetSatisfactionReportQuery(ReportQueryParameters Parameters)
    : IQuery<SatisfactionReport>;

/// <summary>
/// What requesters said about the support they received.
/// </summary>
/// <remarks>
/// The response rate is reported alongside the average because the two are read
/// together or not at all. A 4.8 from six per cent of requesters is a different
/// claim from a 4.2 from sixty, and only one of them is evidence.
/// </remarks>
public sealed class GetSatisfactionReportQueryHandler(
    IAppDbContext db, ICurrentUser currentUser, IClock clock)
    : IQueryHandler<GetSatisfactionReportQuery, SatisfactionReport>
{
    private const int MaxComments = 20;

    public async Task<SatisfactionReport> HandleAsync(
        GetSatisfactionReportQuery query, CancellationToken cancellationToken)
    {
        var (from, to, days) = ReportWindow.Resolve(
            query.Parameters.FromUtc, query.Parameters.ToUtc, clock.UtcNow);

        var visible = ReportWindow.Visible(db, currentUser, query.Parameters, from, to);

        var ratings =
            from rating in db.SatisfactionRatings.AsNoTracking()
            join ticket in visible on rating.TicketId equals ticket.Id
            select new { rating, ticket };

        var responses = await ratings.CountAsync(cancellationToken);

        var eligible = await visible.CountAsync(
            t => t.Status == TicketStatus.Resolved || t.Status == TicketStatus.Closed,
            cancellationToken);

        var distribution = await ratings
            .GroupBy(x => x.rating.Rating)
            .Select(g => new { Score = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var byScore = distribution.ToDictionary(x => x.Score, x => x.Count);

        var byAgent = await ratings
            .Where(x => x.ticket.AssignedStaffId != null)
            .GroupBy(x => new
            {
                StaffId = x.ticket.AssignedStaffId!.Value,
                Name = x.ticket.AssignedStaff!.FirstName + " " + x.ticket.AssignedStaff.LastName,
            })
            .Select(g => new SatisfactionByStaffRow
            {
                StaffId = g.Key.StaffId,
                StaffName = g.Key.Name,
                Responses = g.Count(),
                AverageRating = g.Average(x => (double)x.rating.Rating),
                // IsDetractor is a computed property EF is told to ignore, so the
                // threshold is repeated here rather than silently failing to translate.
                Detractors = g.Count(x => x.rating.Rating <= 3),
            })
            .ToListAsync(cancellationToken);

        var comments = await ratings
            .Where(x => x.rating.Comment != null && x.rating.Comment != "")
            .OrderByDescending(x => x.rating.SubmittedAtUtc)
            .Take(MaxComments)
            .Select(x => new SatisfactionCommentRow
            {
                TicketId = x.ticket.Id,
                TicketNumber = x.ticket.TicketNumber,
                Subject = x.ticket.Subject,
                Rating = x.rating.Rating,
                Comment = x.rating.Comment!,
                SubmittedAtUtc = x.rating.SubmittedAtUtc,
            })
            .ToListAsync(cancellationToken);

        return new SatisfactionReport
        {
            Period = await ReportWindow.DescribeAsync(visible, currentUser, from, to, days, cancellationToken),
            AverageRating = responses == 0
                ? null
                : Math.Round(await ratings.AverageAsync(x => (double)x.rating.Rating, cancellationToken), 2),
            Responses = responses,
            Eligible = eligible,
            ResponsePercent = eligible == 0
                ? null
                : Math.Round((double)responses / eligible * 100, 1),
            Distribution =
            [
                .. Enumerable.Range(1, 5).Select(score =>
                    new LabelledCount(score.ToString(), byScore.GetValueOrDefault(score)))
            ],
            ByStaff =
            [
                .. byAgent
                    .Select(a => a with { AverageRating = Math.Round(a.AverageRating, 2) })
                    .OrderByDescending(a => a.AverageRating)
                    .ThenByDescending(a => a.Responses)
            ],
            RecentComments = comments,
        };
    }
}
