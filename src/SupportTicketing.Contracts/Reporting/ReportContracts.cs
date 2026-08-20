namespace SupportTicketing.Contracts.Reporting;

/// <summary>
/// Period and filters shared by every report.
/// </summary>
/// <remarks>
/// Reports never take a scope parameter. What a caller may see is decided by their
/// token, so widening the period is possible and widening the audience is not.
/// </remarks>
public sealed record ReportQueryParameters
{
    public DateTime? FromUtc { get; init; }
    public DateTime? ToUtc { get; init; }
    public Guid? TeamId { get; init; }
    public Guid? CategoryId { get; init; }
    public Guid? AgentId { get; init; }
}

/// <summary>The period a report actually covered, after clamping.</summary>
public sealed record ReportPeriod
{
    public required DateTime FromUtc { get; init; }
    public required DateTime ToUtc { get; init; }
    public required int Days { get; init; }

    /// <summary>Which data scope produced these figures, so the page can label them honestly.</summary>
    public required string Scope { get; init; }

    /// <summary>Tickets in the period that the caller may see, before any grouping.</summary>
    public required int TicketsInScope { get; init; }
}

// ------------------------------------------------------------- SLA compliance

public sealed record SlaComplianceReport
{
    public required ReportPeriod Period { get; init; }
    public required SlaComplianceRow Overall { get; init; }
    public required IReadOnlyList<SlaComplianceRow> ByPriority { get; init; }
    public required IReadOnlyList<SlaComplianceRow> ByTeam { get; init; }
    public required IReadOnlyList<SlaComplianceRow> ByCategory { get; init; }
}

public sealed record SlaComplianceRow
{
    public required string Label { get; init; }

    /// <summary>Clocks started in the period. Tickets with no SLA policy are excluded, not counted as compliant.</summary>
    public required int Tracked { get; init; }

    public required int ResponseMet { get; init; }
    public required int ResponseBreached { get; init; }
    public required int ResolutionMet { get; init; }
    public required int ResolutionBreached { get; init; }

    /// <summary>Still running or paused, so not yet countable either way.</summary>
    public required int Unsettled { get; init; }

    /// <summary>Met as a percentage of settled resolution clocks. Null while nothing has settled.</summary>
    public double? CompliancePercent { get; init; }

    public double? AverageResponseMinutes { get; init; }
    public double? AverageResolutionMinutes { get; init; }
}

// --------------------------------------------------------- Agent performance

public sealed record AgentPerformanceReport
{
    public required ReportPeriod Period { get; init; }
    public required IReadOnlyList<AgentPerformanceRow> Agents { get; init; }
}

public sealed record AgentPerformanceRow
{
    public required Guid AgentId { get; init; }
    public required string AgentName { get; init; }
    public string? TeamName { get; init; }

    /// <summary>Tickets assigned to this agent that are still open.</summary>
    public required int OpenTickets { get; init; }

    public required int ResolvedInPeriod { get; init; }
    public required int ClosedInPeriod { get; init; }

    /// <summary>Resolved tickets that were later reopened — the counterweight to a high resolved count.</summary>
    public required int ReopenedAfterResolution { get; init; }

    public required int SlaBreached { get; init; }

    public double? AverageFirstResponseMinutes { get; init; }
    public double? AverageResolutionMinutes { get; init; }

    public double? AverageSatisfaction { get; init; }
    public required int SatisfactionResponses { get; init; }
}

// ------------------------------------------------------------- Volume trend

// ------------------------------------------------------------ severity claims

/// <summary>How often requesters ask for more severity than they may declare.</summary>
public sealed record SeverityClaimReport
{
    public required DateTime FromUtc { get; init; }
    public required DateTime ToUtc { get; init; }

    public required int TicketsRaised { get; init; }

    /// <summary>Tickets where the claim was above the cap and was reduced.</summary>
    public required int ClaimsReduced { get; init; }

    public required double ReducedPercent { get; init; }

    /// <summary>Only requesters who over-claimed at least once, worst rate first.</summary>
    public required IReadOnlyList<SeverityClaimRow> Rows { get; init; }
}

public sealed record SeverityClaimRow
{
    public required Guid RequesterId { get; init; }
    public required string RequesterName { get; init; }
    public required string RequesterEmail { get; init; }
    public required int TicketsRaised { get; init; }
    public required int ClaimsReduced { get; init; }

    /// <summary>A rate, because ten in two hundred is not the same problem as four in four.</summary>
    public required double ReducedPercent { get; init; }
}

public sealed record VolumeTrendReport
{
    public required ReportPeriod Period { get; init; }
    public required IReadOnlyList<VolumeTrendPoint> Days { get; init; }
    public required IReadOnlyList<LabelledCount> ByCategory { get; init; }
    public required IReadOnlyList<LabelledCount> ByType { get; init; }
    public required IReadOnlyList<LabelledCount> BySource { get; init; }

    /// <summary>Open tickets at the moment the period began, which anchors the backlog line.</summary>
    public required int OpeningBacklog { get; init; }
}

public sealed record VolumeTrendPoint
{
    public required DateTime Date { get; init; }
    public required int Raised { get; init; }
    public required int Resolved { get; init; }
    public required int Reopened { get; init; }

    /// <summary>Running balance: yesterday's backlog plus today's raised, less today's resolved.</summary>
    public required int Backlog { get; init; }
}

public sealed record LabelledCount(string Label, int Count);

// -------------------------------------------------------------- Satisfaction

public sealed record SatisfactionReport
{
    public required ReportPeriod Period { get; init; }
    public double? AverageRating { get; init; }
    public required int Responses { get; init; }

    /// <summary>Finished tickets in the period that could have been rated.</summary>
    public required int Eligible { get; init; }

    public double? ResponsePercent { get; init; }

    /// <summary>Counts for one through five stars, always five entries including zeroes.</summary>
    public required IReadOnlyList<LabelledCount> Distribution { get; init; }

    public required IReadOnlyList<SatisfactionByAgentRow> ByAgent { get; init; }
    public required IReadOnlyList<SatisfactionCommentRow> RecentComments { get; init; }
}

public sealed record SatisfactionByAgentRow
{
    public required Guid AgentId { get; init; }
    public required string AgentName { get; init; }
    public required int Responses { get; init; }
    public required double AverageRating { get; init; }
    public required int Detractors { get; init; }
}

public sealed record SatisfactionCommentRow
{
    public required Guid TicketId { get; init; }
    public required string TicketNumber { get; init; }
    public required string Subject { get; init; }
    public required int Rating { get; init; }
    public required string Comment { get; init; }
    public required DateTime SubmittedAtUtc { get; init; }
}

// -------------------------------------------------------------------- Export

/// <summary>
/// Asks for a report as CSV. The report name is validated against an allowlist —
/// it names a handler, never a table or a column.
/// </summary>
public sealed record ReportExportRequest
{
    public required string Report { get; init; }
    public DateTime? FromUtc { get; init; }
    public DateTime? ToUtc { get; init; }
    public Guid? TeamId { get; init; }
    public Guid? CategoryId { get; init; }
    public Guid? AgentId { get; init; }
}
