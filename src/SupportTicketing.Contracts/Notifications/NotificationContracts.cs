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
    public required bool IsRead { get; init; }
    public DateTime? ReadAtUtc { get; init; }
    public required DateTime CreatedAtUtc { get; init; }
}

public sealed record NotificationSummaryResponse
{
    public required int UnreadCount { get; init; }
    public required IReadOnlyList<NotificationResponse> Recent { get; init; }
}
