namespace SupportTicketing.Contracts.Tickets;

public sealed record CreateTicketRequest
{
    public required string Subject { get; init; }
    public required string Description { get; init; }

    /// <summary>Agents may raise a ticket for someone else. Omitted means "on my own behalf".</summary>
    public Guid? RequesterId { get; init; }

    public Guid? CategoryId { get; init; }
    public Guid? SubcategoryId { get; init; }
    public Guid? ApplicationId { get; init; }
    public Guid? ApplicationModuleId { get; init; }
    public Guid? DepartmentId { get; init; }
    public Guid? OfficeId { get; init; }

    public string Type { get; init; } = "Incident";

    /// <summary>
    /// How widely the issue is felt. One axis of the priority matrix — the requester
    /// is never asked for a priority directly.
    /// </summary>
    public required string Impact { get; init; }

    /// <summary>How soon it needs attention. The other matrix axis.</summary>
    public required string Urgency { get; init; }

    public string? ContactEmail { get; init; }
    public string? ContactPhone { get; init; }

    public IReadOnlyList<string>? Tags { get; init; }
    public IReadOnlyList<RelatedRecordRequest>? RelatedRecords { get; init; }
}

public sealed record RelatedRecordRequest
{
    public required string RecordType { get; init; }
    public required string RecordReference { get; init; }
    public string? RecordLabel { get; init; }
    public string? RecordUrl { get; init; }
    public string? SourceSystem { get; init; }
    public string? Notes { get; init; }
}

/// <summary>Row shape for the ticket list. Deliberately lean — no bodies, no history.</summary>
public sealed record TicketListItemResponse
{
    public required Guid Id { get; init; }
    public required string TicketNumber { get; init; }
    public required string Subject { get; init; }
    public required string Status { get; init; }
    public required string Priority { get; init; }
    public required string Type { get; init; }
    public string? CategoryName { get; init; }
    public required string RequesterName { get; init; }
    public string? AssignedAgentName { get; init; }
    public string? AssignedTeamName { get; init; }
    public required DateTime CreatedAtUtc { get; init; }
    public DateTime? UpdatedAtUtc { get; init; }
    public DateTime? ResolvedAtUtc { get; init; }
    public required int CommentCount { get; init; }
    public required int AttachmentCount { get; init; }
}

public sealed record TicketDetailResponse
{
    public required Guid Id { get; init; }
    public required string TicketNumber { get; init; }
    public required string Subject { get; init; }
    public required string Description { get; init; }
    public required string Status { get; init; }
    public required string Type { get; init; }
    public required string Impact { get; init; }
    public required string Urgency { get; init; }

    /// <summary>What the requester asked for, when the organization's cap reduced it.</summary>
    /// <remarks>
    /// Null on almost every ticket. A value means somebody claimed a severity above what
    /// they may declare, and staff can see what they believed before deciding whether
    /// they were right.
    /// </remarks>
    public string? ClaimedImpact { get; init; }

    public string? ClaimedUrgency { get; init; }
    public required string Priority { get; init; }
    public required string SuggestedPriority { get; init; }
    public required string PriorityDecisionSource { get; init; }
    public string? PriorityOverrideReason { get; init; }
    public required string Severity { get; init; }
    public required string Source { get; init; }

    public required Guid RequesterId { get; init; }
    public required string RequesterName { get; init; }
    public string? RequesterEmail { get; init; }
    public string? ContactEmail { get; init; }
    public string? ContactPhone { get; init; }

    public Guid? CategoryId { get; init; }
    public string? CategoryName { get; init; }
    public Guid? SubcategoryId { get; init; }
    public string? SubcategoryName { get; init; }
    public Guid? ApplicationId { get; init; }
    public string? ApplicationName { get; init; }
    public Guid? ApplicationModuleId { get; init; }
    public string? ApplicationModuleName { get; init; }
    public Guid? DepartmentId { get; init; }
    public string? DepartmentName { get; init; }
    public Guid? OfficeId { get; init; }
    public string? OfficeName { get; init; }

    public Guid? AssignedAgentId { get; init; }
    public string? AssignedAgentName { get; init; }
    public Guid? AssignedTeamId { get; init; }
    public string? AssignedTeamName { get; init; }

    public DateTime? AssignedAtUtc { get; init; }
    public DateTime? AcceptedAtUtc { get; init; }
    public DateTime? FirstRespondedAtUtc { get; init; }
    public DateTime? ResolvedAtUtc { get; init; }
    public string? ResolvedByName { get; init; }
    public DateTime? ClosedAtUtc { get; init; }
    public DateTime? ReopenedAtUtc { get; init; }
    public required int ReopenCount { get; init; }

    public string? RootCause { get; init; }
    public string? ResolutionSummary { get; init; }
    public string? WorkPerformed { get; init; }
    public string? ClosureReason { get; init; }

    public required DateTime CreatedAtUtc { get; init; }
    public DateTime? UpdatedAtUtc { get; init; }

    /// <summary>Base64 concurrency token. Send it back on any update to detect a clash.</summary>
    public string? RowVersion { get; init; }

    /// <summary>Statuses this ticket may legally move to next, filtered by the caller's permissions.</summary>
    public required IReadOnlyList<string> AllowedTransitions { get; init; }

    public required IReadOnlyList<string> Tags { get; init; }
    public required IReadOnlyList<RelatedRecordResponse> RelatedRecords { get; init; }
}

public sealed record RelatedRecordResponse
{
    public required Guid Id { get; init; }
    public required string RecordType { get; init; }
    public required string RecordReference { get; init; }
    public string? RecordLabel { get; init; }
    public string? RecordUrl { get; init; }
    public string? SourceSystem { get; init; }
    public string? Notes { get; init; }
}

public sealed record TicketCommentResponse
{
    public required Guid Id { get; init; }

    /// <summary>
    /// <c>PublicReply</c>, <c>InternalNote</c> or <c>SystemEvent</c>. An internal note
    /// never appears in a response to a caller without the internal-note permission —
    /// it is filtered at the database, not hidden by the client.
    /// </summary>
    public required string Type { get; init; }

    public required string Body { get; init; }
    public Guid? AuthorId { get; init; }
    public string? AuthorName { get; init; }
    public Guid? ParentCommentId { get; init; }
    public required bool IsEdited { get; init; }
    public required bool IsFirstResponse { get; init; }
    public required DateTime CreatedAtUtc { get; init; }
    public required IReadOnlyList<AttachmentResponse> Attachments { get; init; }
    public required IReadOnlyList<string> MentionedUserNames { get; init; }
}

public sealed record AttachmentResponse
{
    public required Guid Id { get; init; }
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public required long SizeBytes { get; init; }
    public required string ScanState { get; init; }
    public required bool IsDownloadable { get; init; }
    public required bool IsInternalOnly { get; init; }
    public string? UploadedByName { get; init; }
    public required DateTime CreatedAtUtc { get; init; }
}

public sealed record TicketTimelineEntry
{
    public required string Kind { get; init; }
    public required DateTime OccurredAtUtc { get; init; }
    public string? ActorName { get; init; }
    public required string Summary { get; init; }
    public string? Detail { get; init; }
    public string? DecisionSource { get; init; }
}

// ---------------------------------------------------------------- commands

public sealed record AddCommentRequest
{
    public required string Body { get; init; }

    /// <summary>True writes a staff-only note. Requires the internal-note permission.</summary>
    public bool IsInternal { get; init; }

    public Guid? ParentCommentId { get; init; }
    public IReadOnlyList<Guid>? MentionedUserIds { get; init; }
}

public sealed record AssignTicketRequest
{
    public Guid? AgentId { get; init; }
    public Guid? TeamId { get; init; }
    public string? Reason { get; init; }
}

public sealed record ChangeStatusRequest
{
    public required string Status { get; init; }
    public string? Reason { get; init; }
}

public sealed record ChangePriorityRequest
{
    public required string Impact { get; init; }
    public required string Urgency { get; init; }

    /// <summary>Omit to accept whatever the matrix calculates; supply to override it.</summary>
    public string? Priority { get; init; }

    /// <summary>Mandatory when <see cref="Priority"/> differs from the calculated value.</summary>
    public string? Reason { get; init; }
}

public sealed record ResolveTicketRequest
{
    public required string ResolutionSummary { get; init; }
    public string? RootCause { get; init; }
    public string? WorkPerformed { get; init; }
}

public sealed record CloseTicketRequest
{
    public string? ClosureReason { get; init; }
    public string? Comment { get; init; }
}

public sealed record ReopenTicketRequest
{
    public required string Reason { get; init; }
}

public sealed record LogWorkRequest
{
    public required int MinutesSpent { get; init; }
    public required string Description { get; init; }
    public DateTime? WorkDateUtc { get; init; }
    public bool IsBillable { get; init; }
}

public sealed record TicketListQueryParameters
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
    public string? Search { get; init; }
    public string? Status { get; init; }
    public string? Priority { get; init; }
    public string? Type { get; init; }
    public Guid? CategoryId { get; init; }
    public Guid? AssignedAgentId { get; init; }
    public Guid? AssignedTeamId { get; init; }
    public Guid? RequesterId { get; init; }
    public Guid? DepartmentId { get; init; }
    public bool? Unassigned { get; init; }
    public bool? OpenOnly { get; init; }
    public DateTime? CreatedFromUtc { get; init; }
    public DateTime? CreatedToUtc { get; init; }
    public string? SortBy { get; init; }
    public bool SortDescending { get; init; } = true;
}
