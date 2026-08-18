namespace SupportTicketing.Contracts.Auditing;

/// <summary>
/// Filters for the audit log. Every field is optional; an unfiltered request returns
/// the most recent page for the caller's organization.
/// </summary>
public sealed record AuditLogQueryParameters
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;

    public DateTime? FromUtc { get; init; }
    public DateTime? ToUtc { get; init; }

    /// <summary>Matched against the <c>AuditAction</c> name. An unknown value is rejected, not ignored.</summary>
    public string? Action { get; init; }

    public string? EntityType { get; init; }

    /// <summary>A ticket number, article title or similar human-readable identifier.</summary>
    public string? EntityReference { get; init; }

    public Guid? EntityId { get; init; }
    public Guid? ActorId { get; init; }

    /// <summary>Human, Rule, Ai or System.</summary>
    public string? Source { get; init; }

    /// <summary>Restricts to denied or failed actions.</summary>
    public bool? FailuresOnly { get; init; }

    /// <summary>Every row produced by one request shares this identifier.</summary>
    public Guid? CorrelationId { get; init; }

    /// <summary>Free text across the entity reference, actor name and actor email.</summary>
    public string? Search { get; init; }
}

public sealed record AuditLogResponse
{
    public required Guid Id { get; init; }
    public required string Action { get; init; }
    public required string EntityType { get; init; }
    public Guid? EntityId { get; init; }
    public string? EntityReference { get; init; }

    public Guid? ActorId { get; init; }

    /// <summary>Null for actions taken by a background job or before sign-in completed.</summary>
    public string? ActorName { get; init; }

    public string? ActorEmail { get; init; }

    /// <summary>Whether a person, a rule, AI or a background job did this.</summary>
    public required string Source { get; init; }

    public required DateTime OccurredAtUtc { get; init; }

    /// <summary>Flattened from the stored JSON. Empty when the action recorded no field values.</summary>
    public required IReadOnlyList<AuditFieldChange> Changes { get; init; }

    public string? Reason { get; init; }
    public string? IpAddress { get; init; }
    public Guid? CorrelationId { get; init; }

    public required bool IsFailure { get; init; }
    public string? FailureReason { get; init; }
}

/// <summary>One field recorded alongside an audited action.</summary>
public sealed record AuditFieldChange(string Field, string? Value);

/// <summary>
/// The distinct values actually present in this organization's log, for populating
/// filter controls. Offering the full enum would list actions that have never
/// occurred and invite fruitless searches.
/// </summary>
public sealed record AuditFilterOptions
{
    public required IReadOnlyList<string> Actions { get; init; }
    public required IReadOnlyList<string> EntityTypes { get; init; }
    public required IReadOnlyList<AuditActorOption> Actors { get; init; }
    public required DateTime? EarliestEntryUtc { get; init; }
    public required int TotalEntries { get; init; }
}

public sealed record AuditActorOption(Guid Id, string Name, int EntryCount);
