using System.Globalization;
using System.Text;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Application.Features.Tickets;
using SupportTicketing.Contracts.Reporting;
using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Identity;

namespace SupportTicketing.Application.Features.Reporting;

/// <summary>A generated file, ready to be streamed to the caller.</summary>
public sealed record ExportedFile(string FileName, string ContentType, byte[] Content);

public sealed record ExportReportCommand(ReportExportRequest Request) : ICommand<ExportedFile>;

/// <summary>
/// Renders a report as CSV.
/// </summary>
/// <remarks>
/// <para>
/// The report name selects a handler from a fixed list. It never reaches a table
/// name, a column list or an <c>ORDER BY</c>, so no value a client can send widens
/// what the export reads — the same scope filter that governs the on-screen report
/// governs the file.
/// </para>
/// <para>
/// Exporting is audited. It is the point at which data leaves the system's own access
/// controls and becomes a spreadsheet on somebody's laptop, which is exactly the event
/// an investigation later needs to find.
/// </para>
/// </remarks>
public sealed class ExportReportCommandHandler(
    IAppDbContext db,
    ICurrentUser currentUser,
    IDispatcher dispatcher,
    IAuditWriter audit,
    IClock clock)
    : ICommandHandler<ExportReportCommand, ExportedFile>
{
    private const int MaxTicketRows = 20_000;

    private static readonly string[] KnownReports =
        ["tickets", "sla-compliance", "agent-performance", "satisfaction"];

    public async Task<ExportedFile> HandleAsync(
        ExportReportCommand command, CancellationToken cancellationToken)
    {
        currentUser.Require(Permissions.Reports.Export);

        var report = (command.Request.Report ?? string.Empty).Trim().ToLowerInvariant();

        if (!KnownReports.Contains(report))
        {
            throw new ValidationException(
            [
                new ValidationFailure(
                    "report",
                    $"'{command.Request.Report}' is not an exportable report. "
                    + $"Choose one of: {string.Join(", ", KnownReports)}.")
            ]);
        }

        var parameters = new ReportQueryParameters
        {
            FromUtc = command.Request.FromUtc,
            ToUtc = command.Request.ToUtc,
            TeamId = command.Request.TeamId,
            CategoryId = command.Request.CategoryId,
            AgentId = command.Request.AgentId,
        };

        var (csv, rowCount) = report switch
        {
            "tickets" => await ExportTicketsAsync(parameters, cancellationToken),
            "sla-compliance" => await ExportSlaComplianceAsync(parameters, cancellationToken),
            "agent-performance" => await ExportAgentPerformanceAsync(parameters, cancellationToken),
            _ => await ExportSatisfactionAsync(parameters, cancellationToken),
        };

        await audit.WriteAsync(
            AuditAction.Exported,
            entityType: "Report",
            entityId: null,
            entityReference: report,
            changes: new
            {
                Report = report,
                command.Request.FromUtc,
                command.Request.ToUtc,
                Rows = rowCount,
            },
            cancellationToken: cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        var stamp = clock.UtcNow.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);

        return new ExportedFile(
            $"{report}-{stamp}.csv",
            "text/csv",
            // A byte-order mark, because Excel opens a UTF-8 CSV as the local
            // codepage without one and mangles every accented name in the file.
            Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray());
    }

    private async Task<(string Csv, int Rows)> ExportTicketsAsync(
        ReportQueryParameters parameters, CancellationToken cancellationToken)
    {
        var (from, to, _) = ReportWindow.Resolve(parameters.FromUtc, parameters.ToUtc, clock.UtcNow);
        var visible = ReportWindow.Visible(db, currentUser, parameters, from, to);

        var rows = await visible
            .OrderByDescending(t => t.CreatedAtUtc)
            .Take(MaxTicketRows)
            .Select(t => new
            {
                t.TicketNumber,
                t.Subject,
                Type = t.Type.ToString(),
                Status = t.Status.ToString(),
                Priority = t.Priority.ToString(),
                Impact = t.Impact.ToString(),
                Urgency = t.Urgency.ToString(),
                Category = t.Category == null ? null : t.Category.Name,
                Requester = t.Requester == null ? null : t.Requester.FirstName + " " + t.Requester.LastName,
                Agent = t.AssignedAgent == null ? null : t.AssignedAgent.FirstName + " " + t.AssignedAgent.LastName,
                Team = t.AssignedTeam == null ? null : t.AssignedTeam.Name,
                t.CreatedAtUtc,
                t.FirstRespondedAtUtc,
                t.ResolvedAtUtc,
                t.ClosedAtUtc,
                t.ReopenCount,
                Source = t.Source.ToString(),
            })
            .ToListAsync(cancellationToken);

        var csv = new CsvBuilder(
            "Ticket", "Subject", "Type", "Status", "Priority", "Impact", "Urgency", "Category",
            "Requester", "Agent", "Team", "Raised (UTC)", "First response (UTC)",
            "Resolved (UTC)", "Closed (UTC)", "Reopened", "Source");

        foreach (var r in rows)
        {
            csv.AddRow(
                r.TicketNumber, r.Subject, r.Type, r.Status, r.Priority, r.Impact, r.Urgency,
                r.Category, r.Requester, r.Agent, r.Team,
                Stamp(r.CreatedAtUtc), Stamp(r.FirstRespondedAtUtc), Stamp(r.ResolvedAtUtc),
                Stamp(r.ClosedAtUtc), r.ReopenCount.ToString(CultureInfo.InvariantCulture), r.Source);
        }

        return (csv.Build(), rows.Count);
    }

    private async Task<(string Csv, int Rows)> ExportSlaComplianceAsync(
        ReportQueryParameters parameters, CancellationToken cancellationToken)
    {
        var report = await dispatcher.QueryAsync(
            new GetSlaComplianceReportQuery(parameters), cancellationToken);

        var csv = new CsvBuilder(
            "Grouping", "Label", "Tracked", "Response met", "Response breached",
            "Resolution met", "Resolution breached", "Unsettled", "Compliance %",
            "Avg response (min)", "Avg resolution (min)");

        var groups = new (string Grouping, IReadOnlyList<SlaComplianceRow> Rows)[]
        {
            ("Overall", [report.Overall]),
            ("Priority", report.ByPriority),
            ("Team", report.ByTeam),
            ("Category", report.ByCategory),
        };

        var count = 0;

        foreach (var (grouping, rows) in groups)
        {
            foreach (var row in rows)
            {
                csv.AddRow(
                    grouping, row.Label,
                    Number(row.Tracked), Number(row.ResponseMet), Number(row.ResponseBreached),
                    Number(row.ResolutionMet), Number(row.ResolutionBreached), Number(row.Unsettled),
                    Number(row.CompliancePercent), Number(row.AverageResponseMinutes),
                    Number(row.AverageResolutionMinutes));

                count++;
            }
        }

        return (csv.Build(), count);
    }

    private async Task<(string Csv, int Rows)> ExportAgentPerformanceAsync(
        ReportQueryParameters parameters, CancellationToken cancellationToken)
    {
        var report = await dispatcher.QueryAsync(
            new GetAgentPerformanceReportQuery(parameters), cancellationToken);

        var csv = new CsvBuilder(
            "Agent", "Team", "Open", "Resolved", "Closed", "Reopened", "SLA breached",
            "Avg first response (min)", "Avg resolution (min)", "Satisfaction", "Responses");

        foreach (var a in report.Agents)
        {
            csv.AddRow(
                a.AgentName, a.TeamName, Number(a.OpenTickets), Number(a.ResolvedInPeriod),
                Number(a.ClosedInPeriod), Number(a.ReopenedAfterResolution), Number(a.SlaBreached),
                Number(a.AverageFirstResponseMinutes), Number(a.AverageResolutionMinutes),
                Number(a.AverageSatisfaction), Number(a.SatisfactionResponses));
        }

        return (csv.Build(), report.Agents.Count);
    }

    private async Task<(string Csv, int Rows)> ExportSatisfactionAsync(
        ReportQueryParameters parameters, CancellationToken cancellationToken)
    {
        var report = await dispatcher.QueryAsync(
            new GetSatisfactionReportQuery(parameters), cancellationToken);

        var csv = new CsvBuilder("Agent", "Responses", "Average rating", "Detractors");

        foreach (var a in report.ByAgent)
        {
            csv.AddRow(a.AgentName, Number(a.Responses), Number(a.AverageRating), Number(a.Detractors));
        }

        return (csv.Build(), report.ByAgent.Count);
    }

    private static string? Stamp(DateTime? value) =>
        value?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string? Number(double? value) =>
        value?.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Minimal RFC 4180 CSV writer.
/// </summary>
/// <remarks>
/// <para>
/// Hand-rolled rather than taken from a package: the format is small, the rules are
/// short, and a dependency here would have to be justified for the rest of its life.
/// </para>
/// <para>
/// Values beginning <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, tab or carriage return are
/// prefixed with an apostrophe. Spreadsheet applications treat those as the start of a
/// formula, so a ticket subject like <c>=cmd|'/c calc'!A1</c> becomes executable the
/// moment somebody opens the export. Quoting alone does not prevent it.
/// </para>
/// </remarks>
internal sealed class CsvBuilder(params string[] headers)
{
    private readonly StringBuilder _builder = new();
    private bool _headerWritten;

    internal void AddRow(params string?[] values)
    {
        if (!_headerWritten)
        {
            _builder.AppendLine(string.Join(',', headers.Select(Escape)));
            _headerWritten = true;
        }

        _builder.AppendLine(string.Join(',', values.Select(Escape)));
    }

    internal string Build()
    {
        if (!_headerWritten)
        {
            _builder.AppendLine(string.Join(',', headers.Select(Escape)));
            _headerWritten = true;
        }

        return _builder.ToString();
    }

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var text = value;

        if (text[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            text = "'" + text;
        }

        if (text.Contains('"') || text.Contains(',') || text.Contains('\n') || text.Contains('\r'))
        {
            text = "\"" + text.Replace("\"", "\"\"") + "\"";
        }

        return text;
    }
}
