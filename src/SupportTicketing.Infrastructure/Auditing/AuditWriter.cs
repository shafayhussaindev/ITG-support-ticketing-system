using System.Text.Json;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Domain.Auditing;
using SupportTicketing.Domain.Enums;

namespace SupportTicketing.Infrastructure.Auditing;

/// <summary>
/// Appends immutable audit rows.
/// </summary>
/// <remarks>
/// Rows are added to the change tracker rather than saved immediately, so the audit
/// entry commits inside the same transaction as the change it describes. An audit
/// row therefore cannot survive a rolled-back operation, and a committed operation
/// cannot be missing its audit row.
/// </remarks>
public sealed class AuditWriter(IAppDbContext db, ICurrentUser currentUser, IClock clock) : IAuditWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public Task WriteAsync(
        AuditAction action,
        string entityType,
        Guid? entityId,
        string? entityReference = null,
        object? changes = null,
        string? reason = null,
        DecisionSource source = DecisionSource.Human,
        bool isFailure = false,
        string? failureReason = null,
        Guid? organizationIdOverride = null,
        CancellationToken cancellationToken = default)
    {
        var organizationId = organizationIdOverride ?? currentUser.OrganizationId ?? Guid.Empty;

        var entry = new AuditLog
        {
            OrganizationId = organizationId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            EntityReference = entityReference,
            ActorId = currentUser.UserId,
            ActorName = currentUser.FullName,
            ActorEmail = currentUser.Email,
            Source = source,
            OccurredAtUtc = clock.UtcNow,
            ChangesJson = changes is null ? null : JsonSerializer.Serialize(changes, SerializerOptions),
            Reason = Truncate(reason, 1000),
            IpAddress = currentUser.IpAddress,
            UserAgent = Truncate(currentUser.UserAgent, 512),
            CorrelationId = currentUser.CorrelationId,
            IsFailure = isFailure,
            FailureReason = Truncate(failureReason, 500)
        };

        db.AuditLogs.Add(entry);
        return Task.CompletedTask;
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}
