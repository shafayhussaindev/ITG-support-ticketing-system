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
