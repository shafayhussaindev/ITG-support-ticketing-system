using Microsoft.EntityFrameworkCore;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Contracts.Reporting;
using SupportTicketing.Domain.Identity;

namespace SupportTicketing.Application.Features.Reporting;

public sealed record GetSeverityClaimReportQuery(ReportQueryParameters Parameters)
    : IQuery<SeverityClaimReport>;

/// <summary>
/// Who asks for more severity than they are allowed, and how often.
/// </summary>
/// <remarks>
/// <para>
/// The cap already stops an inflated claim reaching the priority. This exists because
/// stopping it is not the same as knowing about it: a requester who marks every ticket
/// Critical is telling you something — either that they misunderstand the scale, or that
/// their work genuinely is urgent and the categories are wrong for them. Both are worth
/// a conversation, and neither is visible from the tickets alone.
/// </para>
/// <para>
/// Reported as a rate rather than a count, because somebody who raises two hundred
/// tickets and over-claims on ten is not the problem, and somebody who raises four and
/// over-claims on all four is.
/// </para>
/// </remarks>
public sealed class GetSeverityClaimReportQueryHandler(
    IAppDbContext db, ICurrentUser currentUser, IClock clock)
    : IQueryHandler<GetSeverityClaimReportQuery, SeverityClaimReport>
{
    public async Task<SeverityClaimReport> HandleAsync(
        GetSeverityClaimReportQuery query, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Reports.View);

        var (fromUtc, toUtc, _) = ReportWindow.Resolve(
            query.Parameters.FromUtc, query.Parameters.ToUtc, clock.UtcNow);

        var raised = await db.Tickets.AsNoTracking()
            .Where(t => t.CreatedAtUtc >= fromUtc && t.CreatedAtUtc < toUtc)
            .Select(t => new
            {
                t.RequesterId,
                RequesterName = t.Requester!.FirstName + " " + t.Requester.LastName,
                RequesterEmail = t.Requester.Email,
                t.ClaimedImpact,
                t.ClaimedUrgency,
            })
            .ToListAsync(cancellationToken);

        var rows = raised
            .GroupBy(t => new { t.RequesterId, t.RequesterName, t.RequesterEmail })
            .Select(g =>
            {
                var total = g.Count();
                var reduced = g.Count(t => t.ClaimedImpact is not null || t.ClaimedUrgency is not null);

                return new SeverityClaimRow
                {
                    RequesterId = g.Key.RequesterId,
                    RequesterName = g.Key.RequesterName,
                    RequesterEmail = g.Key.RequesterEmail,
                    TicketsRaised = total,
                    ClaimsReduced = reduced,
                    ReducedPercent = total == 0 ? 0 : Math.Round(reduced * 100.0 / total, 1),
                };
            })
            // Only people who actually over-claimed. A list padded with everyone who
            // never did buries the handful worth talking to.
            .Where(r => r.ClaimsReduced > 0)
            .OrderByDescending(r => r.ReducedPercent)
            .ThenByDescending(r => r.ClaimsReduced)
            .ToList();

        var totalRaised = raised.Count;
        var totalReduced = raised.Count(t => t.ClaimedImpact is not null || t.ClaimedUrgency is not null);

        return new SeverityClaimReport
        {
            FromUtc = fromUtc,
            ToUtc = toUtc,
            TicketsRaised = totalRaised,
            ClaimsReduced = totalReduced,
            ReducedPercent = totalRaised == 0 ? 0 : Math.Round(totalReduced * 100.0 / totalRaised, 1),
            Rows = rows,
        };
    }
}
