using SupportTicketing.Domain.Common;
using SupportTicketing.Domain.Enums;

namespace SupportTicketing.Domain.Auditing;

/// <summary>
/// Immutable record of a security- or business-significant action.
/// </summary>
/// <remarks>
/// Implements <see cref="IAppendOnly"/>: the persistence interceptor throws if a row
/// is ever modified or deleted, and the application's database login is denied
/// UPDATE and DELETE on this table. Passwords, tokens, API keys, message bodies and
/// attachment contents are never written here.
/// </remarks>
public class AuditLog : Entity, IAppendOnly, ITenantOwned
{
    public Guid OrganizationId { get; set; }

    public AuditAction Action { get; set; }

    /// <summary>Entity type affected, for example <c>Ticket</c> or <c>User</c>.</summary>
    public required string EntityType { get; set; }

    public Guid? EntityId { get; set; }

    /// <summary>Human-readable identifier such as a ticket number, for fast audit search.</summary>
    public string? EntityReference { get; set; }

    public Guid? ActorId { get; set; }
    public string? ActorName { get; set; }
    public string? ActorEmail { get; set; }

    /// <summary>Whether a person, a rule, AI, or a background job performed the action.</summary>
    public DecisionSource Source { get; set; } = DecisionSource.Human;

    public DateTime OccurredAtUtc { get; set; }

    /// <summary>Changed fields as JSON: <c>[{"field":"Status","from":"New","to":"Assigned"}]</c>.</summary>
    public string? ChangesJson { get; set; }

    public string? Reason { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }

    /// <summary>Ties every log line and audit row produced by one request together.</summary>
    public Guid? CorrelationId { get; set; }

    /// <summary>True when the action was denied. Failed attempts are as important as successes.</summary>
    public bool IsFailure { get; set; }

    public string? FailureReason { get; set; }
}

/// <summary>Runtime configuration, editable by an administrator without a deployment.</summary>
public class SystemSetting : AuditableEntity
{
    /// <summary>Null for a global default; set for a per-organization override.</summary>
    public Guid? OrganizationId { get; set; }

    public required string Key { get; set; }
    public required string Value { get; set; }
    public required string ValueType { get; set; }
    public string? Description { get; set; }
    public string? Category { get; set; }

    /// <summary>Masked in API responses and logs. Used for integration credentials.</summary>
    public bool IsSensitive { get; set; }

    /// <summary>Editable only by Super Admin.</summary>
    public bool IsSystemManaged { get; set; }
}
