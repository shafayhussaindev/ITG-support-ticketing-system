namespace SupportTicketing.Contracts.Sla;

/// <summary>The live SLA position for one ticket, as the interface needs to draw it.</summary>
public sealed record TicketSlaResponse
{
    public required Guid TicketId { get; init; }
    public string? PolicyName { get; init; }
    public required string Priority { get; init; }

    public required int ResponseMinutes { get; init; }
    public required int ResolutionMinutes { get; init; }
    public required int WarningThresholdPercent { get; init; }

    public required DateTime StartedAtUtc { get; init; }
    public required DateTime ResponseDueAtUtc { get; init; }
    public required DateTime ResolutionDueAtUtc { get; init; }

    public DateTime? FirstRespondedAtUtc { get; init; }
    public DateTime? ResolvedAtUtc { get; init; }

    public required string ResponseState { get; init; }
    public required string ResolutionState { get; init; }

    public required bool IsPaused { get; init; }
    public DateTime? PausedAtUtc { get; init; }
    public required int TotalPausedMinutes { get; init; }

    /// <summary>Percentage of the resolution budget consumed, frozen while paused.</summary>
    public required double ResolutionConsumedPercent { get; init; }
    public required double ResponseConsumedPercent { get; init; }

    /// <summary>Minutes left before the resolution deadline. Negative once breached.</summary>
    public required double MinutesToResolutionDue { get; init; }

    public required int HighestEscalationLevel { get; init; }
    public required IReadOnlyList<SlaEventResponse> Events { get; init; }
}

public sealed record SlaEventResponse
{
    public required string EventType { get; init; }
    public required int Level { get; init; }
    public required DateTime OccurredAtUtc { get; init; }
    public string? Detail { get; init; }
    public required string Source { get; init; }
}

public sealed record AcknowledgeEscalationRequest
{
    /// <summary>Optional: what you are doing about it, kept alongside why it was raised.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// The state of the escalation queue as a whole.
/// </summary>
/// <remarks>
/// Counted on the server rather than derived from the listing, because the listing is
/// capped at 200 rows. Counting in the browser would under-report at exactly the moment
/// the queue is worst, which is the one moment the number matters.
/// </remarks>
public sealed record EscalationSummaryResponse
{
    /// <summary>Raised or notified: nobody has picked these up.</summary>
    public required int Unacknowledged { get; init; }

    /// <summary>Somebody owns it, but the ticket is still not fixed.</summary>
    public required int Acknowledged { get; init; }

    /// <summary>Everything still open, whether or not anyone has taken it on.</summary>
    public required int Open { get; init; }

    /// <summary>Age in hours of the longest-standing unacknowledged escalation.</summary>
    public double? OldestUnacknowledgedHours { get; init; }

    /// <summary>Open escalations that have gone past the first rung.</summary>
    public required int BeyondFirstLevel { get; init; }

    /// <summary>Settled in the last seven days, as the counterweight to the backlog.</summary>
    public required int SettledLastWeek { get; init; }
}

public sealed record EscalationResponse
{
    public required Guid Id { get; init; }
    public required Guid TicketId { get; init; }
    public required string TicketNumber { get; init; }
    public required string TicketSubject { get; init; }
    public required string Priority { get; init; }
    public required int Level { get; init; }
    public required string Trigger { get; init; }
    public required string State { get; init; }
    public required int ThresholdPercent { get; init; }
    public string? RecipientName { get; init; }
    public required DateTime RaisedAtUtc { get; init; }
    public DateTime? AcknowledgedAtUtc { get; init; }
    public string? AcknowledgedByName { get; init; }
    public string? Reason { get; init; }
}
