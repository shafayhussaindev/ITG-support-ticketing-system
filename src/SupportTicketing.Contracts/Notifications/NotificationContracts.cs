namespace SupportTicketing.Contracts.Notifications;

public sealed record NotificationResponse
{
    public required Guid Id { get; init; }
    public required string EventType { get; init; }
    public required string Title { get; init; }
    public required string Body { get; init; }
    public required string Severity { get; init; }
    public string? Link { get; init; }
    public Guid? TicketId { get; init; }
    public string? TicketNumber { get; init; }
    /// <summary>Interrupt the reader with this rather than leaving it in the bell.</summary>
    /// <remarks>
    /// True for the person holding the ticket, false for supervisors watching the
    /// pattern. Set per recipient, so two people can receive the same event and only
    /// one of them is interrupted.
    /// </remarks>
    public required bool ShowAsPopup { get; init; }

    public required bool IsRead { get; init; }
    public DateTime? ReadAtUtc { get; init; }
    public required DateTime CreatedAtUtc { get; init; }
}

public sealed record NotificationSummaryResponse
{
    public required int UnreadCount { get; init; }
    public required IReadOnlyList<NotificationResponse> Recent { get; init; }
}
