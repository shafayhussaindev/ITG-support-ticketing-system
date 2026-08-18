namespace SupportTicketing.Contracts.Reporting;

/// <summary>
/// The dashboard payload.
/// </summary>
/// <remarks>
/// Every figure is computed over the tickets the caller is entitled to see, so a
/// requester, an agent and a manager asking for "open tickets" legitimately get
/// different numbers from the same endpoint. The scope is taken from the token.
/// </remarks>
public sealed record DashboardResponse
{
    /// <summary>Which data scope produced these figures, so the UI can label them honestly.</summary>
    public required string Scope { get; init; }

    public required DashboardKpis Kpis { get; init; }
    public required IReadOnlyList<TimeSeriesPoint> VolumeByDay { get; init; }
    public required IReadOnlyList<CategoryCount> ByStatus { get; init; }
    public required IReadOnlyList<CategoryCount> ByPriority { get; init; }
    public required IReadOnlyList<CategoryCount> ByCategory { get; init; }
    public required IReadOnlyList<AgentWorkload> AgentWorkload { get; init; }
}

public sealed record DashboardKpis
{
    public required int TotalOpen { get; init; }
    public required int NewToday { get; init; }
    public required int CriticalOpen { get; init; }
    public required int Unassigned { get; init; }
    public required int ResolvedToday { get; init; }

    /// <summary>Open tickets past the warning threshold but not yet breached.</summary>
    public required int ApproachingBreach { get; init; }

    public required int Breached { get; init; }

    /// <summary>Percentage of settled clocks that met their resolution target. Null when none have settled.</summary>
    public double? SlaCompliancePercent { get; init; }

    /// <summary>Mean minutes from creation to first reply, over tickets that have one.</summary>
    public double? AverageFirstResponseMinutes { get; init; }

    public double? AverageResolutionMinutes { get; init; }

    public required int ReopenedCount { get; init; }

    /// <summary>Mean satisfaction score out of five. Null until anyone has rated.</summary>
    public double? AverageSatisfaction { get; init; }

    public required int SatisfactionResponses { get; init; }
}

public sealed record TimeSeriesPoint
{
    public required DateTime Date { get; init; }
    public required int Raised { get; init; }
    public required int Resolved { get; init; }
}

public sealed record CategoryCount
{
    public required string Label { get; init; }
    public required int Count { get; init; }

    /// <summary>Query string that reproduces this slice on the ticket list, for drill-down.</summary>
    public string? DrillDownQuery { get; init; }
}

public sealed record AgentWorkload
{
    public required Guid AgentId { get; init; }
    public required string AgentName { get; init; }
    public required int OpenTickets { get; init; }
    public required int CriticalTickets { get; init; }
    public required int BreachedTickets { get; init; }

    /// <summary>
    /// Priority-weighted load rather than a raw count. Ten low-priority questions are
    /// not the same burden as ten critical outages, and ranking by count alone pushes
    /// work towards whoever happens to hold the easy tickets.
    /// </summary>
    public required int WeightedScore { get; init; }
}
